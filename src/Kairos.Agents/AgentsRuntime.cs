using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents;

/// <summary>The activity types the agent steps use. Register them on every worker that runs agent workflows (<see cref="AgentsRuntime.Activities"/>).</summary>
public static class AgentActivityNames
{
    public const string Chat = "kairos.agent.chat";
    public const string Tool = "kairos.agent.tool";
    public const string Policy = "kairos.agent.policy";
    public const string ApprovalRequest = "kairos.agent.approval.request";
    public const string ApprovalVerify = "kairos.agent.approval.verify";
    public const string SagaMark = "kairos.saga.mark";
}

/// <summary>Shows a person or a UI the text of a reply while the model is still writing it. Only the final reply is recorded.</summary>
public interface IAgentObserver
{
    ValueTask OnTextAsync(RunKey run, int stepId, string delta, CancellationToken cancellationToken);
}

/// <summary>Collects what an agent application needs: models, tools, a policy and credentials. <see cref="Build"/> makes the runtime.</summary>
public sealed class AgentsBuilder
{
    private readonly Dictionary<string, Func<RunKey, IChatClient>> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentTool> _tools = new(StringComparer.Ordinal);

    internal IToolPolicy Policy { get; private set; } = new BaselineToolPolicy();

    internal IToolCredentialIssuer? Issuer { get; private set; }

    internal IModelCallGuard? Guard { get; private set; }

    internal IAgentObserver? Observer { get; private set; }

    internal IApprovalNotifier? Notifier { get; private set; }

    internal IApproverVerifier? Verifier { get; private set; }

    internal Func<string, string>? Redactor { get; private set; }

    internal TimeProvider Time { get; private set; } = TimeProvider.System;

    internal TimeSpan ModelTimeout { get; private set; } = TimeSpan.FromMinutes(2);

    internal TimeSpan CredentialLifetime { get; private set; } = TimeSpan.FromMinutes(5);

    internal RetryPolicy ModelRetry { get; private set; } = new()
    {
        MaximumAttempts = 3,
        InitialInterval = TimeSpan.FromSeconds(2),
        MaximumInterval = TimeSpan.FromSeconds(30),
        NonRetryableErrorTypes = [nameof(UnknownModelException), nameof(UnknownToolException), nameof(NotSupportedException)],
    };

    internal RetryPolicy ToolRetry { get; private set; } = new()
    {
        MaximumAttempts = 3,
    };

    internal Dictionary<string, Func<RunKey, IChatClient>> Models => _models;

    internal Dictionary<string, AgentTool> Tools => _tools;

    /// <summary>
    /// Makes <paramref name="client"/> available to workflows as <paramref name="alias"/>. Pass the provider's own client, not one wrapped in
    /// automatic function invocation: tools must run through <see cref="IAgentContext.InvokeToolAsync"/> to be durable.
    /// </summary>
    public AgentsBuilder AddModel(string alias, IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return AddModel(alias, _ => client);
    }

    /// <summary>Resolves the model per run, for example a different deployment per namespace.</summary>
    public AgentsBuilder AddModel(string alias, Func<RunKey, IChatClient> resolver)
    {
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentNullException.ThrowIfNull(resolver);
        _models[alias] = resolver;
        return this;
    }

    public AgentsBuilder AddTool(AgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
        return this;
    }

    /// <summary>Replaces the baseline policy (read-only tools run; the rest wait for approval).</summary>
    public AgentsBuilder UsePolicy(IToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        return this;
    }

    /// <summary>Adjusts the baseline policy: which write tools are allowed, denied, or need particular approvers.</summary>
    public AgentsBuilder ConfigureBaselinePolicy(Action<BaselineToolPolicy> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var baseline = new BaselineToolPolicy();
        configure(baseline);
        Policy = baseline;
        return this;
    }

    /// <summary>
    /// Asks <paramref name="guard"/> before every model request (to reserve what it may cost, or refuse it) and tells it afterwards what it cost. See <see cref="IModelCallGuard"/>.
    /// </summary>
    public AgentsBuilder UseModelCallGuard(IModelCallGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Guard = guard;
        return this;
    }

    /// <summary>Gives every tool call a short-lived credential limited to that run and tool.</summary>
    public AgentsBuilder UseCredentials(IToolCredentialIssuer issuer, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        Issuer = issuer;
        if (lifetime is { } l)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(l, TimeSpan.Zero);
            CredentialLifetime = l;
        }

        return this;
    }

    public AgentsBuilder UseObserver(IAgentObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        Observer = observer;
        return this;
    }

    /// <summary>
    /// Proves who decided each approval. Without a verifier, a decision is accepted only when the request names no approvers and no roles
    /// (anyone may decide); a request that names approvers or roles refuses every decision, because nothing could prove the claim.
    /// </summary>
    public AgentsBuilder UseApproverVerifier(IApproverVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        Verifier = verifier;
        return this;
    }

    public AgentsBuilder UseApprovalNotifier(IApprovalNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        Notifier = notifier;
        return this;
    }

    /// <summary>Applied to every piece of text before it is recorded (messages, tool arguments and results, approval details).</summary>
    public AgentsBuilder UseRedactor(Func<string, string> redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        Redactor = redactor;
        return this;
    }

    public AgentsBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        Time = time;
        return this;
    }

    public AgentsBuilder WithModelRetry(RetryPolicy retry, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(retry);
        ModelRetry = retry;
        if (timeout is { } t)
        {
            ModelTimeout = t;
        }

        return this;
    }

    public AgentsBuilder WithToolRetry(RetryPolicy retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        ToolRetry = retry;
        return this;
    }

    public AgentsRuntime Build() => new(this);
}

/// <summary>A model alias that no client was registered for.</summary>
public sealed class UnknownModelException : Exception
{
    public UnknownModelException()
        : base("Unknown model alias.")
    {
    }

    public UnknownModelException(string message)
        : base(message)
    {
    }

    public UnknownModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A tool name that no tool was registered under.</summary>
public sealed class UnknownToolException : Exception
{
    public UnknownToolException()
        : base("Unknown tool.")
    {
    }

    public UnknownToolException(string message)
        : base(message)
    {
    }

    public UnknownToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The configured agent layer. <see cref="Activities"/> is what workers register; <see cref="CreateContext"/> is what workflow code uses.
/// </summary>
public sealed class AgentsRuntime
{
    /// <summary>Passed to the model client in <see cref="ChatOptions.AdditionalProperties"/>; stable across retries of one step.</summary>
    public const string IdempotencyKeyProperty = "kairos.idempotency_key";

    private readonly AgentsBuilder _config;
    private readonly Dictionary<string, ActivityFunc> _activities;

    internal AgentsRuntime(AgentsBuilder config)
    {
        _config = config;
        _activities = new Dictionary<string, ActivityFunc>(StringComparer.Ordinal)
        {
            [AgentActivityNames.Chat] = ChatStepAsync,
            [AgentActivityNames.Tool] = ToolStepAsync,
            [AgentActivityNames.Policy] = PolicyStepAsync,
            [AgentActivityNames.ApprovalRequest] = ApprovalRequestStepAsync,
            [AgentActivityNames.ApprovalVerify] = ApprovalVerifyStepAsync,
            [AgentActivityNames.SagaMark] = (_, input, _) => Task.FromResult(input),
        };
    }

    /// <summary>Register these on every worker that can run the workflows (<c>options.AddActivities(agents.Activities)</c> in embedded mode).</summary>
    public IReadOnlyDictionary<string, ActivityFunc> Activities => _activities;

    internal AgentsBuilder Config => _config;

    /// <summary>Puts the agent activities into a registry.</summary>
    public void RegisterInto(ActivityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach ((string name, ActivityFunc activity) in _activities)
        {
            registry.Register(name, activity);
        }
    }

    /// <summary>Wraps workflow code that wants the agent steps.</summary>
    public IAgentContext CreateContext(IWorkflowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AgentContext(this, context);
    }

    /// <summary>Turns <paramref name="body"/> into a registered workflow: <c>options.AddWorkflow("support", agents.Workflow(...))</c>.</summary>
    public WorkflowFunc Workflow(Func<IAgentContext, byte[], Task<byte[]>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return (context, input) => body(CreateContext(context), input);
    }

    // ---- worker side -----------------------------------------------------------------------------------------

    private async Task<byte[]> ChatStepAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        ChatStepRequest request = WireCodec.Read(input, AgentJson.Default.ChatStepRequest);
        using Activity? span = AgentTelemetry.StartChat(context, request);
        long started = AgentTelemetry.Now();
        try
        {
            byte[] reply = await ChatCoreAsync(context, request, span, cancellationToken).ConfigureAwait(false);
            AgentTelemetry.Finish("chat", request.Model, span, started, error: null);
            return reply;
        }
        catch (Exception ex)
        {
            AgentTelemetry.Finish("chat", request.Model, span, started, ex);
            throw;
        }
    }

    private async Task<byte[]> ChatCoreAsync(ActivityContext context, ChatStepRequest request, Activity? span, CancellationToken cancellationToken)
    {
        if (!_config.Models.TryGetValue(request.Model, out Func<RunKey, IChatClient>? resolve))
        {
            throw new UnknownModelException($"No model is registered as '{request.Model}'.");
        }

        ModelCallInfo? call = null;
        ModelCallPermit? permit = null;
        if (_config.Guard is { } guard)
        {
            call = new ModelCallInfo(context.Run, context.ActivityId, context.Attempt, context.IdempotencyKey, request.Model, request.Options?.MaxOutputTokens, request.BudgetAllowance);
            permit = await guard.ReserveAsync(call, cancellationToken).ConfigureAwait(false); // throws when it cannot decide: the step is retried, and the model is not called unguarded
            if (!permit.Allowed)
            {
                return WireCodec.Write(
                    new ChatStepResponse
                    {
                        Outcome = BudgetOutcomes.Exceeded,
                        BudgetLimit = permit.Limit,
                        OnExceed = permit.OnExceed,
                        BudgetApproval = permit.OnExceed == "pause" ? ToWire(permit.Approval ?? new ApprovalRequirement()) : null,
                    },
                    AgentJson.Default.ChatStepResponse);
            }
        }

        IChatClient client = resolve(context.Run);
        AgentTelemetry.Provider(span, client);
        var tools = new List<AITool>(request.Tools.Length);
        foreach (string name in request.Tools)
        {
            tools.Add(_config.Tools.TryGetValue(name, out AgentTool? tool) ? tool.Declaration : throw new UnknownToolException($"No tool is registered as '{name}'."));
        }

        var messages = new List<ChatMessage>(request.Messages.Length);
        foreach (WireMessage m in request.Messages)
        {
            messages.Add(WireCodec.FromWire(m));
        }

        var options = new ChatOptions
        {
            Tools = tools.Count == 0 ? null : tools,
            Temperature = request.Options?.Temperature,
            MaxOutputTokens = request.Options?.MaxOutputTokens,
            Seed = request.Options?.Seed,
            AdditionalProperties = new AdditionalPropertiesDictionary { [IdempotencyKeyProperty] = context.IdempotencyKey },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.ModelTimeout);
        ChatResponse response;
        try
        {
            response = _config.Observer is null
                ? await client.GetResponseAsync(messages, options, timeout.Token).ConfigureAwait(false)
                : await StreamAsync(client, messages, options, context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The model '{request.Model}' did not answer within {_config.ModelTimeout.TotalSeconds:0} seconds.");
        }

        AgentTelemetry.Response(span, request.Model, response);
        if (response.Usage is { } used)
        {
            // Recorded with the step's completion, once: the figure a price table is applied to later.
            context.ReportUsage(new UsageRecord(
                used.InputTokenCount ?? 0, used.OutputTokenCount ?? 0, Math.Min(used.CachedInputTokenCount ?? 0, used.InputTokenCount ?? 0), response.ModelId ?? request.Model));
        }

        if (_config.Guard is { } settling && call is not null && permit is not null)
        {
            await SettleAsync(settling, call, permit, context.Usage, cancellationToken).ConfigureAwait(false);
        }

        var reply = new ChatStepResponse
        {
            Messages = [.. response.Messages.Select(m => WireCodec.ResponseToWire(m, _config.Redactor))],
            FinishReason = response.FinishReason?.Value,
            ModelId = response.ModelId,
            Usage = response.Usage is { } u ? new WireUsage(u.InputTokenCount, u.OutputTokenCount, u.TotalTokenCount) : null,
        };
        return WireCodec.Write(reply, AgentJson.Default.ChatStepResponse);
    }

#pragma warning disable CA1031 // The call has been made and paid for; a ledger that cannot settle now is reconciled from the recorded usage, and must not make the step run the model again.
    private static async Task SettleAsync(IModelCallGuard guard, ModelCallInfo call, ModelCallPermit permit, UsageRecord? usage, CancellationToken cancellationToken)
    {
        try
        {
            await guard.SettleAsync(call, permit, usage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Model call guard could not settle: {ex.GetType().Name}");
        }
    }
#pragma warning restore CA1031

    private async Task<ChatResponse> StreamAsync(
        IChatClient client, List<ChatMessage> messages, ChatOptions options, ActivityContext context, CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
            {
                await NotifyObserverAsync(context, update.Text, cancellationToken).ConfigureAwait(false);
            }
        }

        return updates.ToChatResponse();
    }

#pragma warning disable CA1031 // A broken UI observer must never fail a model step.
    private async Task NotifyObserverAsync(ActivityContext context, string delta, CancellationToken cancellationToken)
    {
        try
        {
            await _config.Observer!.OnTextAsync(context.Run, context.ActivityId, delta, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Agent observer failed: {ex.GetType().Name}");
        }
    }
#pragma warning restore CA1031

    private async Task<byte[]> ToolStepAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        ToolStepRequest request = WireCodec.Read(input, AgentJson.Default.ToolStepRequest);
        using Activity? span = AgentTelemetry.StartTool(context, request);
        long started = AgentTelemetry.Now();
        try
        {
            byte[] reply = await ToolCoreAsync(context, request, cancellationToken).ConfigureAwait(false);
            AgentTelemetry.ToolOutcome(span, reply);
            AgentTelemetry.Finish("execute_tool", request.Name, span, started, error: null);
            return reply;
        }
        catch (Exception ex)
        {
            AgentTelemetry.Finish("execute_tool", request.Name, span, started, ex);
            throw;
        }
    }

    private async Task<byte[]> ToolCoreAsync(ActivityContext context, ToolStepRequest request, CancellationToken cancellationToken)
    {
        if (!_config.Tools.TryGetValue(request.Name, out AgentTool? tool))
        {
            // A model asking for a tool that does not exist is the model's mistake: tell it, do not retry.
            return Reply(new ToolStepResponse { Outcome = ToolOutcomes.Completed, IsError = true, Content = $"There is no tool named '{request.Name}'." });
        }

        ApprovalDecision? approval = request.Approval is null ? null : ToDecision(request.Approval);
        ToolDecision decision;
        if (request.Decision is { } recorded)
        {
            // Decided and recorded in the policy step before this one: honoured as recorded, never evaluated again, and only for the arguments it was made for.
            decision = Honour(recorded, request.ArgumentsJson, approval);
        }
        else
        {
            decision = await _config.Policy
                .EvaluateAsync(new ToolPolicyContext(context.Run, tool, request.ArgumentsJson, approval) { StartedBy = request.StartedBy ?? "", Now = _config.Time.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
        }

        switch (decision.Verdict)
        {
            case ToolVerdict.Deny:
                return Reply(new ToolStepResponse { Outcome = ToolOutcomes.Denied, Reason = decision.Reason ?? "denied by policy" });
            case ToolVerdict.RequireApproval:
                return Reply(new ToolStepResponse
                {
                    Outcome = ToolOutcomes.ApprovalRequired,
                    Reason = decision.Reason,
                    Requirement = ToWire(decision.Approval ?? new ApprovalRequirement()),
                });
        }

        ToolCredential? credential = null;
        if (_config.Issuer is { } issuer)
        {
            // Read-only tools get a token that can only read, whatever their code tries; write scope exists only because the policy said Auto.
            string[] scopes = tool.Access == ToolAccess.ReadOnly ? [$"{tool.Name}:read"] : [$"{tool.Name}:read", $"{tool.Name}:write"];
            credential = await issuer.IssueAsync(
                new ToolCredentialRequest(context.Run, tool.Name, scopes, _config.CredentialLifetime, request.OnBehalfOf),
                _config.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        using JsonDocument arguments = JsonDocument.Parse(request.ArgumentsJson);
        var invocation = new ToolInvocation(
            context.Run, tool.Name, request.CallId, arguments.RootElement.Clone(), context.IdempotencyKey, credential, approval, request.OnBehalfOf);

        string content;
        bool isError = false;
        try
        {
            content = await tool.Handler(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            content = ex.Message;
            isError = true;
        }

        if (credential is not null)
        {
            content = content.Replace(credential.Token, "[credential removed]", StringComparison.Ordinal); // a token must never reach history
        }

        if (_config.Redactor is { } redact)
        {
            content = redact(content);
        }

        WireCredentialGrant? grant = credential is null ? null : new WireCredentialGrant { Scopes = [.. credential.Scopes], ExpiresAt = credential.ExpiresAt, OnBehalfOf = request.OnBehalfOf };
        return Reply(new ToolStepResponse { Outcome = ToolOutcomes.Completed, Content = content, IsError = isError, Credential = grant });
    }

    private static ToolDecision Honour(WirePolicyDecision recorded, string argumentsJson, ApprovalDecision? approval)
    {
        if (!string.Equals(recorded.ArgumentsDigest, ArgumentsDigest(argumentsJson), StringComparison.Ordinal))
        {
            return ToolDecision.Refuse("the arguments are not the ones the policy decided on");
        }

        return recorded.Effect switch
        {
            "allow" => ToolDecision.Allow(),
            "require_approval" when approval is { IsApproved: true } => ToolDecision.Allow(),
            "require_approval" => ToolDecision.NeedsApproval("the policy requires approval"),
            _ => ToolDecision.Refuse("the policy denied the call"),
        };
    }

    /// <summary>SHA-256 of the arguments JSON as the workflow passed it, lower-case hexadecimal: what a recorded decision is bound to.</summary>
    internal static string ArgumentsDigest(string argumentsJson) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(argumentsJson)));

    private async Task<byte[]> PolicyStepAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        ToolStepRequest request = WireCodec.Read(input, AgentJson.Default.ToolStepRequest);
        if (!_config.Tools.TryGetValue(request.Name, out AgentTool? tool))
        {
            return WireCodec.Write(new PolicyStepResponse { Outcome = PolicyOutcomes.Allowed }, AgentJson.Default.PolicyStepResponse); // the tool step reports the unknown tool
        }

        DateTimeOffset now = _config.Time.GetUtcNow();
        ToolDecision decision = await _config.Policy
            .EvaluateAsync(new ToolPolicyContext(context.Run, tool, request.ArgumentsJson, request.Approval is null ? null : ToDecision(request.Approval)) { StartedBy = request.StartedBy ?? "", Now = now }, cancellationToken)
            .ConfigureAwait(false);

        PolicyDecisionRecord record = decision.Record ?? new PolicyDecisionRecord("(unrecorded)", 0, "", decision.Verdict switch { ToolVerdict.Deny => "deny", ToolVerdict.RequireApproval => "require_approval", _ => "allow" }, []);
        WirePolicyDecision wire = new()
        {
            RuleId = Clip(record.RuleId),
            PolicyVersion = record.PolicyVersion,
            PolicyHash = Clip(record.PolicyHash),
            Effect = Clip(record.Effect),
            MatchedFields = [.. record.MatchedFields.Select(Clip)],
            EvaluatedAt = now,
            Starter = string.IsNullOrEmpty(request.StartedBy) ? null : request.StartedBy,
            ArgumentsDigest = ArgumentsDigest(request.ArgumentsJson),
        };
        return WireCodec.Write(
            decision.Verdict switch
            {
                ToolVerdict.Deny => new PolicyStepResponse { Outcome = ToolOutcomes.Denied, Reason = decision.Reason ?? "denied by policy", Decision = wire },
                ToolVerdict.RequireApproval => new PolicyStepResponse
                {
                    Outcome = ToolOutcomes.ApprovalRequired, Reason = decision.Reason, Requirement = ToWire(decision.Approval ?? new ApprovalRequirement()), Decision = wire,
                },
                _ => new PolicyStepResponse { Outcome = PolicyOutcomes.Allowed, Decision = wire },
            },
            AgentJson.Default.PolicyStepResponse);

        static string Clip(string s) => s.Length <= PolicyDecisionRecord.MaxLength ? s : s[..PolicyDecisionRecord.MaxLength];
    }

    private async Task<byte[]> ApprovalRequestStepAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        if (_config.Notifier is { } notifier)
        {
            WireApprovalRequest wire = WireCodec.Read(input, AgentJson.Default.WireApprovalRequest);
            var request = new ApprovalRequest
            {
                Action = wire.Action,
                Details = wire.Details,
                Tool = wire.Tool,
                Requirement = FromWire(wire.Requirement),
            };
            await notifier.NotifyAsync(new ApprovalNotice(context.Run, wire.ApprovalId, request, wire.RequestedBy, wire.RequestedAt), cancellationToken).ConfigureAwait(false);
        }

        return input;
    }

    private async Task<byte[]> ApprovalVerifyStepAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        ApprovalVerifyRequest request = WireCodec.Read(input, AgentJson.Default.ApprovalVerifyRequest);
        ApprovalVerifyResult result = await ApprovalRules.EvaluateAsync(
            request, context.Run, _config.Verifier, cancellationToken, context.IdempotencyKey).ConfigureAwait(false);
        return WireCodec.Write(result, AgentJson.Default.ApprovalVerifyResult);
    }

    private static byte[] Reply(ToolStepResponse response) => WireCodec.Write(response, AgentJson.Default.ToolStepResponse);

    internal static WireApprovalRequirement ToWire(ApprovalRequirement r) => new()
    {
        Approvers = [.. r.Approvers ?? []],
        Roles = [.. r.Roles ?? []],
        TimeoutSeconds = r.Timeout?.TotalSeconds,
        ForbidSelfApproval = r.ForbidSelfApproval,
        Count = Math.Clamp(r.Count, 1, ApprovalRequirement.MaxCount),
    };

    internal static ApprovalRequirement FromWire(WireApprovalRequirement w) =>
        new(w.Approvers, w.Roles, w.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null, w.ForbidSelfApproval, Math.Clamp(w.Count, 1, ApprovalRequirement.MaxCount));

    internal static ApprovalDecision ToDecision(WireApprovalDecision w) => new(
        w.ApprovalId, w.Approved ? ApprovalOutcome.Approved : ApprovalOutcome.Rejected,
        new ApproverIdentity(w.Approver.Subject, w.Approver.Name, w.Approver.Roles, w.Approver.Evidence), w.Comment, w.DecidedAt);

    internal static WireApprovalDecision ToWire(ApprovalDecision d) => new()
    {
        ApprovalId = d.ApprovalId,
        Approved = d.IsApproved,
        Approver = new WireApprover
        {
            Subject = d.Approver?.Subject ?? "",
            Name = d.Approver?.Name,
            Roles = [.. d.Approver?.Roles ?? []],
            Evidence = d.Approver?.Evidence,
        },
        Comment = d.Comment,
        DecidedAt = d.DecidedAt ?? default,
    };
}
