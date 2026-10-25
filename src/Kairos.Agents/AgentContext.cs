using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents;

/// <summary>Optional model settings for one call. Anything unset is left to the model client.</summary>
public sealed record AgentChatOptions(float? Temperature = null, int? MaxOutputTokens = null, long? Seed = null);

public sealed record AgentUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);

/// <summary>A model's answer as it was recorded. Replaying a workflow returns the same reply without calling the model.</summary>
public sealed class AgentReply
{
    internal AgentReply(IReadOnlyList<ChatMessage> messages, AgentUsage? usage, string? finishReason, string? modelId)
    {
        Messages = messages;
        Usage = usage;
        FinishReason = finishReason;
        ModelId = modelId;
        ToolCalls = [.. messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()];
        Text = string.Concat(messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text));
    }

    /// <summary>The messages to append to the conversation (usually one assistant message).</summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    public string Text { get; }

    /// <summary>The tools the model asked for. Empty when it gave a final answer.</summary>
    public IReadOnlyList<FunctionCallContent> ToolCalls { get; }

    /// <summary>Tokens used, as reported by the model. Recorded so spend can be read back from history.</summary>
    public AgentUsage? Usage { get; }

    public string? FinishReason { get; }

    public string? ModelId { get; }
}

public enum ToolStepOutcome
{
    /// <summary>The tool ran and returned a result.</summary>
    Completed = 0,

    /// <summary>The tool reported an error, or kept failing after its retries. The model is told.</summary>
    Failed = 1,

    /// <summary>Policy refused the call.</summary>
    Denied = 2,

    /// <summary>A person rejected the call.</summary>
    Rejected = 3,

    /// <summary>Nobody decided before the approval timed out.</summary>
    Expired = 4,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Message">The tool-role message to append to the conversation, whatever the outcome.</param>
/// <param name="Approval">The approval decision, when the call needed one.</param>
/// <param name="Reason">Why the call did not run, when it did not.</param>
public sealed record ToolStepResult(ToolStepOutcome Outcome, ChatMessage Message, ApprovalDecision? Approval, string? Reason);

/// <summary>
/// The durable steps an agent workflow is made of. Every method is recorded in history: replay returns what was recorded and never calls
/// the model or a tool again.
/// </summary>
public interface IAgentContext
{
    /// <summary>The underlying workflow context, for timers, signals and plain activities.</summary>
    IWorkflowContext Workflow { get; }

    /// <summary>Who the agent acts for. Used as the requester of approvals and recorded on tool credentials. Set it from the workflow input.</summary>
    string? OnBehalfOf { get; set; }

    /// <summary>Chooses the tools a model call may use.</summary>
    AgentToolSet Tools(params string[] names);

    /// <summary>Calls a model. The reply is recorded; a replay does not call the model again.</summary>
    Task<AgentReply> ChatAsync(
        string model, IReadOnlyList<ChatMessage> messages, AgentToolSet? tools = null, AgentChatOptions? options = null);

    /// <summary>
    /// Runs one tool call as a durable step with an idempotency key. Policy and approval apply. The returned message is the tool's answer
    /// for the model, including when the call was refused.
    /// </summary>
    Task<ChatMessage> InvokeToolAsync(FunctionCallContent toolCall);

    /// <summary>Like <see cref="InvokeToolAsync"/> but also says what happened, so the workflow can react to a rejection.</summary>
    Task<ToolStepResult> InvokeToolWithOutcomeAsync(FunctionCallContent toolCall);

    /// <summary>Asks a person to decide and waits, durably, for as long as it takes. The decision carries who made it.</summary>
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request);

    /// <summary>A saga whose agent steps notice a cancel request and whose approvals end when the run is cancelled.</summary>
    Task<T> RunSagaAsync<T>(Func<ISaga, Task<T>> body, SagaOptions? options = null);
}

internal sealed class AgentContext(AgentsRuntime runtime, IWorkflowContext workflow) : IAgentContext
{
    private SagaScope? _saga;

    public IWorkflowContext Workflow => workflow;

    public string? OnBehalfOf { get; set; }

    public AgentToolSet Tools(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (string name in names)
        {
            if (!runtime.Config.Tools.ContainsKey(name))
            {
                throw new UnknownToolException($"No tool is registered as '{name}'.");
            }
        }

        return new AgentToolSet([.. names]);
    }

    public async Task<AgentReply> ChatAsync(string model, IReadOnlyList<ChatMessage> messages, AgentToolSet? tools = null, AgentChatOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(messages);
        _saga?.ThrowIfCancelled();

        Func<string, string>? redact = runtime.Config.Redactor;
        var request = new ChatStepRequest
        {
            Model = model,
            Messages = [.. messages.Select(m => WireCodec.ToWire(m, redact))],
            Tools = [.. tools?.Names ?? []],
            Options = options is null ? null : new WireChatOptions(options.Temperature, options.MaxOutputTokens, options.Seed),
        };

        byte[] result = await workflow.ExecuteActivityAsync(AgentActivityNames.Chat, WireCodec.Write(request, AgentJson.Default.ChatStepRequest), runtime.Config.ModelRetry);
        ChatStepResponse response = WireCodec.Read(result, AgentJson.Default.ChatStepResponse);
        return new AgentReply(
            [.. response.Messages.Select(WireCodec.FromWire)],
            response.Usage is { } u ? new AgentUsage(u.Input, u.Output, u.Total) : null,
            response.FinishReason,
            response.ModelId);
    }

    public async Task<ChatMessage> InvokeToolAsync(FunctionCallContent toolCall) => (await InvokeToolWithOutcomeAsync(toolCall)).Message;

    public async Task<ToolStepResult> InvokeToolWithOutcomeAsync(FunctionCallContent toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        _saga?.ThrowIfCancelled();

        string callId = string.IsNullOrEmpty(toolCall.CallId) ? workflow.NewGuid().ToString("N") : toolCall.CallId;
        Func<string, string>? redact = runtime.Config.Redactor;
        string argumentsJson = WireCodec.ArgumentsToJson(toolCall.Arguments);

        ToolStepResponse first;
        try
        {
            first = await RunToolAsync(toolCall.Name, callId, argumentsJson, approval: null);
        }
        catch (ActivityFailureException ex)
        {
            return Reply(callId, ToolStepOutcome.Failed, $"The tool '{toolCall.Name}' failed ({ex.ErrorType}).", null, ex.ErrorType);
        }

        switch (first.Outcome)
        {
            case ToolOutcomes.Denied:
                return Reply(callId, ToolStepOutcome.Denied, $"The call was refused by policy: {first.Reason}.", null, first.Reason);

            case ToolOutcomes.ApprovalRequired:
                {
                    ApprovalDecision decision = await RequestApprovalAsync(new ApprovalRequest
                    {
                        Action = $"Call tool '{toolCall.Name}'",
                        Details = redact is null ? argumentsJson : redact(argumentsJson),
                        Tool = toolCall.Name,
                        Requirement = AgentsRuntime.FromWire(first.Requirement ?? new WireApprovalRequirement()),
                    });
                    if (!decision.IsApproved)
                    {
                        string who = decision.Approver is { } a ? $" by {a.Subject}" : "";
                        return Reply(
                            callId, decision.Outcome == ApprovalOutcome.Expired ? ToolStepOutcome.Expired : ToolStepOutcome.Rejected,
                            decision.Outcome == ApprovalOutcome.Expired
                                ? "Nobody approved the call in time, so it was not made."
                                : $"A person rejected the call{who}, so it was not made.",
                            decision, decision.Comment);
                    }

                    ToolStepResponse second;
                    try
                    {
                        second = await RunToolAsync(toolCall.Name, callId, argumentsJson, decision);
                    }
                    catch (ActivityFailureException ex)
                    {
                        return Reply(callId, ToolStepOutcome.Failed, $"The tool '{toolCall.Name}' failed ({ex.ErrorType}).", decision, ex.ErrorType);
                    }

                    return Finish(callId, second, decision);
                }

            default:
                return Finish(callId, first, null);
        }
    }

    private Task<ToolStepResponse> RunToolAsync(string name, string callId, string argumentsJson, ApprovalDecision? approval) =>
        RunToolCoreAsync(
            new ToolStepRequest
            {
                Name = name,
                CallId = callId,
                ArgumentsJson = argumentsJson,
                OnBehalfOf = OnBehalfOf,
                Approval = approval is null ? null : AgentsRuntime.ToWire(approval),
            });

    private async Task<ToolStepResponse> RunToolCoreAsync(ToolStepRequest request)
    {
        byte[] result = await workflow.ExecuteActivityAsync(
            AgentActivityNames.Tool, WireCodec.Write(request, AgentJson.Default.ToolStepRequest), runtime.Config.ToolRetry);
        return WireCodec.Read(result, AgentJson.Default.ToolStepResponse);
    }

    private static ToolStepResult Finish(string callId, ToolStepResponse response, ApprovalDecision? approval) => response.Outcome switch
    {
        ToolOutcomes.Denied => Reply(callId, ToolStepOutcome.Denied, $"The call was refused by policy: {response.Reason}.", approval, response.Reason),
        ToolOutcomes.ApprovalRequired => Reply(callId, ToolStepOutcome.Denied, "The tool still needs approval after it was approved; the call was not made.", approval, response.Reason),
        _ => Reply(callId, response.IsError ? ToolStepOutcome.Failed : ToolStepOutcome.Completed, response.Content ?? "", approval, null),
    };

    private static ToolStepResult Reply(string callId, ToolStepOutcome outcome, string content, ApprovalDecision? approval, string? reason) =>
        new(outcome, new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, content)]), approval, reason);

    public async Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _saga?.ThrowIfCancelled();

        string approvalId = workflow.NewGuid().ToString("N");
        Func<string, string>? redact = runtime.Config.Redactor;
        var wire = new WireApprovalRequest
        {
            ApprovalId = approvalId,
            Action = request.Action,
            Details = request.Details is null ? null : redact is null ? request.Details : redact(request.Details),
            Tool = request.Tool,
            RequestedBy = OnBehalfOf,
            Requirement = AgentsRuntime.ToWire(request.Requirement),
            RequestedAt = workflow.Now,
            RunId = workflow.RunId.ToString("N"),
        };

        // This step is the record an approvals inbox reads, and the hook that tells people. A notifier that is down must not
        // fail the run: the request stays open and visible.
        try
        {
            await workflow.ExecuteActivityAsync(
                AgentActivityNames.ApprovalRequest, WireCodec.Write(wire, AgentJson.Default.WireApprovalRequest), RetryPolicy.Default);
        }
        catch (ActivityFailureException)
        {
            // keep waiting
        }

        string signal = AgentClient.ApprovalSignalName(approvalId);
        Task? timeout = request.Requirement.Timeout is { } t ? workflow.DelayAsync(t) : null;
        Task<byte[]>? cancel = _saga?.CancelTask;
        while (true)
        {
            Task<byte[]> next = workflow.WaitForSignalAsync(signal);
            var waiting = new List<Task>(3) { next };
            if (timeout is not null)
            {
                waiting.Add(timeout);
            }

            if (cancel is not null)
            {
                waiting.Add(cancel);
            }

            Task winner = await Task.WhenAny(waiting);
            if (cancel is not null && winner == cancel)
            {
                _saga!.ThrowIfCancelled();
            }

            if (timeout is not null && winner == timeout)
            {
                return new ApprovalDecision(approvalId, ApprovalOutcome.Expired, null, null, workflow.Now);
            }

            WireApprovalDecision? claimed = ApprovalRules.Prefilter(wire, next.Result);
            if (claimed is not null)
            {
                // Who really decided is established by a recorded step, never by what the signal says about itself.
                ApprovalVerifyResult? verified = null;
                try
                {
                    byte[] checkedResult = await workflow.ExecuteActivityAsync(
                        AgentActivityNames.ApprovalVerify,
                        WireCodec.Write(new ApprovalVerifyRequest { Request = wire, Decision = claimed, ReceivedAt = workflow.Now }, AgentJson.Default.ApprovalVerifyRequest),
                        RetryPolicy.Default);
                    verified = WireCodec.Read(checkedResult, AgentJson.Default.ApprovalVerifyResult);
                }
                catch (ActivityFailureException)
                {
                    // the verifier is unavailable: the decision is not accepted, and the request stays open
                }

                if (verified is { Ok: true })
                {
                    return new ApprovalDecision(
                        approvalId, claimed.Approved ? ApprovalOutcome.Approved : ApprovalOutcome.Rejected,
                        new ApproverIdentity(verified.Subject, claimed.Approver.Name, verified.Roles, claimed.Approver.Evidence), claimed.Comment, claimed.DecidedAt);
                }
            }

            // A decision from someone who may not decide (or garbage) is ignored; it stays in history for the audit trail.
        }
    }

    public Task<T> RunSagaAsync<T>(Func<ISaga, Task<T>> body, SagaOptions? options = null) =>
        SagaScope.RunAsync(workflow, body, options ?? new SagaOptions(), saga => _saga = saga);
}

/// <summary>
/// The rules that decide whether an approval signal counts. The workflow only checks that a signal is well formed (cheap, so junk costs
/// nothing); who decided is established on the worker by <see cref="EvaluateAsync"/> and recorded as a step.
/// </summary>
internal static class ApprovalRules
{
    public static WireApprovalDecision? Prefilter(WireApprovalRequest request, ReadOnlySpan<byte> payload)
    {
        WireApprovalDecision? decision;
        try
        {
            decision = JsonSerializer.Deserialize(payload, AgentJson.Default.WireApprovalDecision);
        }
        catch (JsonException)
        {
            return null;
        }

        return decision is not null && string.Equals(decision.ApprovalId, request.ApprovalId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(decision.Approver.Subject)
            ? decision
            : null;
    }

    public static async ValueTask<ApprovalVerifyResult> EvaluateAsync(
        ApprovalVerifyRequest check, RunKey run, IApproverVerifier? verifier, CancellationToken cancellationToken)
    {
        WireApprovalRequest request = check.Request;
        WireApprovalDecision decision = check.Decision;
        WireApprovalRequirement need = request.Requirement;
        bool restricted = need.Approvers.Length > 0 || need.Roles.Length > 0;

        string subject = decision.Approver.Subject;
        string[] roles = decision.Approver.Roles;
        if (verifier is not null)
        {
            VerifiedApprover? proven = await verifier.VerifyAsync(
                new ApproverEvidenceContext(run, decision.ApprovalId, decision.Approved, subject, decision.Approver.Evidence, check.ReceivedAt), cancellationToken).ConfigureAwait(false);
            if (proven is null)
            {
                return Refuse("the approver could not be verified");
            }

            subject = proven.Subject;
            roles = [.. proven.Roles];
        }
        else if (restricted)
        {
            return Refuse("this approval names approvers or roles and no approver verifier is configured");
        }

        if (need.ForbidSelfApproval && request.RequestedBy is { Length: > 0 } requester && string.Equals(requester, subject, StringComparison.Ordinal))
        {
            return Refuse("the person the agent acts for may not approve their own request");
        }

        if (restricted && !need.Approvers.Contains(subject, StringComparer.Ordinal) && !roles.Any(r => need.Roles.Contains(r, StringComparer.Ordinal)))
        {
            return Refuse("the approver is not allowed to decide this approval");
        }

        return new ApprovalVerifyResult { Ok = true, Subject = subject, Roles = roles };
    }

    private static ApprovalVerifyResult Refuse(string reason) => new() { Ok = false, Reason = reason };
}

public static class AgentContextExtensions
{
    /// <inheritdoc cref="IAgentContext.RunSagaAsync{T}"/>
    public static async Task RunSagaAsync(this IAgentContext context, Func<ISaga, Task> body, SagaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        await context.RunSagaAsync(async saga =>
        {
            await body(saga);
            return 0;
        }, options);
    }
}
