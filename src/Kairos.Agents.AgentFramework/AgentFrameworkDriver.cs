using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>
/// The workflow that runs one conversation with an agent. It is ordinary Kairos workflow code and obeys the same rules: it only awaits context calls, so it replays from history and never runs Agent Framework code.
/// The agent runs <b>inside activities</b> (one model call each); the tool calls, approvals and budgets between them are the same steps native Kairos agents use.
/// </summary>
internal sealed class AgentFrameworkDriver(AgentFrameworkRuntime runtime)
{
    private const string ChildMarker = "kairos.af.child/";

    /// <summary>How many message ids a conversation remembers to recognise a message that arrives twice.</summary>
    private const int RecentTurns = 64;

    /// <summary>Where a conversation stands while the driver works on it.</summary>
    private sealed class Conversation
    {
        public JsonElement? Snapshot { get; set; }

        /// <summary>Set when the run uses <see cref="AfProtocol.Referenced"/>: the session is then followed here and <see cref="Snapshot"/> is not used.</summary>
        public SessionChain? Chain { get; set; }

        public string? Hash { get; set; }

        public string LastText { get; set; } = "";
    }

    public async Task<byte[]> RunAsync(IAgentContext ctx, byte[] inputBytes)
    {
        AfDriverInput input = AfCodec.Read(inputBytes, AfJson.Default.AfDriverInput);
        IWorkflowContext wf = ctx.Workflow;
        AgentDefinition definition = runtime.Definition(input.Agent); // a worker without the agent does not register this agent's workflow type, so the engine leaves the run for one that has it

        ctx.OnBehalfOf = input.OnBehalfOf;
        var state = new Conversation { Snapshot = input.Snapshot, Hash = input.Hash };
        if (wf.IsPatched(AfProtocol.Patch)) // runs recorded before this have no marker and keep the first protocol, in which every step carries the whole session
        {
            state.Chain = new SessionChain();
            if (input.Snapshot is { } held)
            {
                state.Chain.StartFrom(held);
            }
        }

        var pending = new Queue<AfTurn>(input.Pending);
        var recent = new List<string>(input.Recent);
        Task<byte[]> close = wf.WaitForSignalAsync(AgentFrameworkNames.CloseSignal);

        try
        {
            while (true)
            {
                if (pending.Count == 0)
                {
                    AfTurn? next = await NextAsync(wf, close, input.IdleSeconds);
                    if (next is null)
                    {
                        break; // told to stop, or nobody wrote for too long
                    }

                    pending.Enqueue(next);
                }

                if (recent.Contains(pending.Peek().TurnId, StringComparer.Ordinal))
                {
                    pending.Dequeue(); // already answered, or being answered: the same message arrived twice
                    continue;
                }

                if (wf.ShouldContinueAsNew || (input.ContinueAfterEvents is { } limit && wf.HistoryLength >= limit))
                {
                    if (close.IsCompleted)
                    {
                        break;
                    }

                    // Messages that arrived while this one was being taken must travel too: they are in this run's history and the next run cannot see it.
                    while (true)
                    {
                        Task<byte[]> more = wf.WaitForSignalAsync(AgentFrameworkNames.MessageSignal);
                        if (!more.IsCompleted)
                        {
                            break;
                        }

                        pending.Enqueue(AfCodec.Read(more.Result, AfJson.Default.AfTurn));
                    }

                    throw wf.ContinueAsNew(AfCodec.Write(input with { Snapshot = state.Chain is { IsEmpty: false } chain ? chain.Materialize() : state.Snapshot, Hash = state.Hash, Pending = [.. pending], Recent = [.. recent] }, AfJson.Default.AfDriverInput));
                }

                AfTurn turn = pending.Dequeue();
                recent.Add(turn.TurnId);
                if (recent.Count > RecentTurns)
                {
                    recent.RemoveAt(0);
                }

                await TurnAsync(ctx, definition, state, turn);
                if (input.Parent is { } parent)
                {
                    await NotifyAsync(wf, parent, new AfChildAnswer { Ok = true, Text = state.LastText });
                    break; // a nested agent answers one question
                }
            }
        }
        catch (Exception ex) when (input.Parent is { } parent && ex is not ContinueAsNewException)
        {
            // The parent is waiting for this answer: tell it the nested agent failed (the kind of failure only, never what the conversation held).
            await NotifyAsync(wf, parent, new AfChildAnswer { Ok = false, Text = ex.GetType().Name });
            throw;
        }

        return [];
    }

    private static async Task<AfTurn?> NextAsync(IWorkflowContext wf, Task<byte[]> close, double? idleSeconds)
    {
        Task<byte[]> message = wf.WaitForSignalAsync(AgentFrameworkNames.MessageSignal);
        Task? idle = idleSeconds is { } s ? wf.DelayAsync(TimeSpan.FromSeconds(s)) : null;
        Task winner = idle is null ? await Task.WhenAny(message, close) : await Task.WhenAny(message, close, idle);
        return winner == message ? AfCodec.Read(message.Result, AfJson.Default.AfTurn) : null;
    }

    private async Task TurnAsync(IAgentContext ctx, AgentDefinition definition, Conversation state, AfTurn turn)
    {
        JsonElement messages = runtime.Redactor is { } redact ? SnapshotRedactor.RedactMessages(turn.Messages, redact) : turn.Messages;
        JsonElement? newMessages = messages;
        AfToolResult[] results = [];
        AfApprovalResponse[] responses = [];
        for (int step = 0; ; step++)
        {
            if (step >= runtime.Options.MaxStepsPerTurn)
            {
                throw new AgentStepLimitException($"Agent '{definition.Name}' used more than {runtime.Options.MaxStepsPerTurn} model calls for one message (MaxStepsPerTurn).");
            }

            AfStepResult result = await StepAsync(ctx, definition, state, turn.TurnId, newMessages, results, responses);
            if (state.Chain is { } chain)
            {
                chain.Record(result);
            }
            else
            {
                state.Snapshot = result.Snapshot ?? state.Snapshot;
            }

            state.Hash = result.Hash ?? state.Hash;
            if (result.Final)
            {
                state.LastText = result.Messages is { } produced ? AssistantText(produced) : "";
                return;
            }

            newMessages = null;

            // Several calls in one answer run at the same time: they are independent steps, and parallel tool calls stay parallel.
            Task<AfToolResult>[] tools = [.. result.ToolCalls.Select(call => RunToolAsync(ctx, definition, call))];
            Task<AfApprovalResponse>[] approvals = [.. result.ApprovalRequests.Select(request => DecideAsync(ctx, definition, request))];
            await Task.WhenAll(tools.Cast<Task>().Concat(approvals));
            results = [.. tools.Select(t => t.Result)];
            responses = [.. approvals.Select(t => t.Result)];
        }
    }

    private async Task<AfStepResult> StepAsync(
        IAgentContext ctx, AgentDefinition definition, Conversation state, string turnId, JsonElement? messages, AfToolResult[] results, AfApprovalResponse[] responses)
    {
        IWorkflowContext wf = ctx.Workflow;
        string? allowance = null;
        int overrides = 0;
        bool sendWhole = false;
        while (true)
        {
            SessionChain? chain = state.Chain;
            var request = new AfStepRequest
            {
                Agent = definition.Name,
                Hash = state.Hash,
                TurnId = turnId,
                Protocol = chain is null ? 0 : AfProtocol.Referenced,
                Snapshot = chain is null ? state.Snapshot : sendWhole && !chain.IsEmpty ? chain.Materialize() : null,
                SnapshotHash = chain?.Hash,
                SnapshotFrom = chain?.From,
                StepsSinceFull = chain?.StepsSinceFull ?? 0,
                Messages = messages,
                ToolResults = results,
                ApprovalResponses = responses,
                BudgetAllowance = allowance,
            };
            byte[] bytes = await wf.ExecuteActivityAsync(
                definition.Stepped ? AgentFrameworkNames.Step : AgentFrameworkNames.Turn, AfCodec.Write(request, AfJson.Default.AfStepRequest), runtime.Agents.Config.ModelRetry);
            AfStepResult result = AfCodec.Read(bytes, AfJson.Default.AfStepResult);

            switch (result.Outcome)
            {
                case AfOutcomes.NeedSnapshot:
                    // The worker that took the step does not hold the session (another worker ran the last one, or it was dropped from the worker's cache): send it once and ask again. Nothing was called.
                    if (sendWhole || chain is null)
                    {
                        throw new SnapshotIntegrityException("A worker did not take the session it was sent.");
                    }

                    sendWhole = true;
                    continue;

                case AfOutcomes.DefinitionChanged:
                    if (++overrides > 3)
                    {
                        throw new DefinitionChangedException($"Agent '{definition.Name}' changed again and again while the run waited.");
                    }

                    state.Hash = await AllowDefinitionChangeAsync(ctx, definition, result);
                    continue;

                case AfOutcomes.BudgetExceeded:
                    {
                        string limit = result.BudgetLimit ?? "budget";
                        if (allowance is not null || result.OnExceed != "pause")
                        {
                            throw new BudgetExceededException(allowance is null ? $"The budget '{limit}' was reached." : $"The budget '{limit}' was reached again.") { Limit = limit };
                        }

                        ApprovalDecision raise = await ctx.RequestApprovalAsync(new ApprovalRequest
                        {
                            Action = $"Raise the budget '{limit}' for one more model call",
                            Requirement = AgentsRuntime.FromWire(result.BudgetApproval ?? new WireApprovalRequirement()),
                        });
                        if (!raise.IsApproved)
                        {
                            throw new BudgetExceededException($"The budget '{limit}' was reached and nobody raised it.") { Limit = limit };
                        }

                        allowance = raise.ApprovalId;
                        continue;
                    }

                default:
                    return result;
            }
        }
    }

    /// <summary>
    /// The worker has another definition of the agent than the run started with. A person decides, as an approval: the verified approver is recorded in history, and an override mark makes the decision easy to find
    /// (<see cref="AgentFrameworkAudit"/>). Returns the definition to pin the run to from now on.
    /// </summary>
    private static async Task<string> AllowDefinitionChangeAsync(IAgentContext ctx, AgentDefinition definition, AfStepResult changed)
    {
        string from = changed.PinnedHash ?? "";
        string to = changed.Hash ?? "";
        ApprovalDecision decision = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action = $"Continue this run on a changed definition of agent '{definition.Name}'",
            Details = $"The run started on definition {Short(from)}; the worker now has {Short(to)} (instructions, tools or model changed).",
            Requirement = definition.DefinitionChangeApproval,
        });
        if (!decision.IsApproved)
        {
            throw new DefinitionChangedException($"The definition of agent '{definition.Name}' changed since the run started and the change was not approved.");
        }

        await ctx.Workflow.ExecuteActivityAsync(
            AgentFrameworkNames.Override,
            AfCodec.Write(
                new AfOverrideMark
                {
                    Agent = definition.Name,
                    FromHash = from,
                    ToHash = to,
                    ApprovalId = decision.ApprovalId,
                    Approver = decision.Approver?.Subject ?? "",
                    Roles = [.. decision.Approver?.Roles ?? []],
                },
                AfJson.Default.AfOverrideMark));
        return to;

        static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];
    }

    private async Task<AfToolResult> RunToolAsync(IAgentContext ctx, AgentDefinition definition, AfToolCall call)
    {
        var content = new FunctionCallContent(call.CallId, call.Name, WireCodec.JsonToArguments(call.ArgumentsJson));
        ToolStepResult step = await ctx.InvokeToolWithOutcomeAsync(content);
        var answer = (FunctionResultContent)step.Message.Contents[0];
        string text = answer.Result as string ?? "";

        if (step.Outcome == ToolStepOutcome.Completed && definition.Tools.Any(t => t.Name == call.Name && t.ChildAgent is not null) && text.StartsWith(ChildMarker, StringComparison.Ordinal))
        {
            text = await AwaitChildAsync(ctx.Workflow, text[ChildMarker.Length..]);
        }

        return new AfToolResult { CallId = answer.CallId, Content = text };
    }

    /// <summary>Waits for a nested agent's answer without holding compute: for its signal, and now and then (growing to a quarter of an hour) for a look at whether the child run still exists.</summary>
    private async Task<string> AwaitChildAsync(IWorkflowContext wf, string marker)
    {
        int slash = marker.IndexOf('/', StringComparison.Ordinal);
        string signal = marker[..slash];
        var run = Guid.ParseExact(marker[(slash + 1)..], "N");
        Task<byte[]> answer = wf.WaitForSignalAsync(signal);
        TimeSpan wait = TimeSpan.FromSeconds(15);
        while (true)
        {
            Task timer = wf.DelayAsync(wait);
            Task winner = await Task.WhenAny(answer, timer);
            if (winner == answer || answer.IsCompleted)
            {
                AfChildAnswer reply = AfCodec.Read(answer.Result, AfJson.Default.AfChildAnswer);
                return reply.Ok ? reply.Text : $"The nested agent failed ({reply.Text}).";
            }

            byte[] checkedState = await wf.ExecuteActivityAsync(
                AgentFrameworkNames.ChildCheck, AfCodec.Write(new AfChildCheck { RunId = run }, AfJson.Default.AfChildCheck), runtime.Agents.Config.ToolRetry);
            if (AfCodec.Read(checkedState, AfJson.Default.AfChildCheckResult).State != "running" && !answer.IsCompleted)
            {
                return "The nested agent ended without answering.";
            }

            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(15).Ticks));
        }
    }

    private static async Task NotifyAsync(IWorkflowContext wf, AfParent parent, AfChildAnswer answer)
    {
        try
        {
            await wf.ExecuteActivityAsync(
                AgentFrameworkNames.ChildNotify,
                AfCodec.Write(new AfNotify { Parent = parent, Answer = answer }, AfJson.Default.AfNotify));
        }
        catch (ActivityFailureException)
        {
            // The parent will notice the child ended when it looks.
        }
    }

    /// <summary>A request the agent's own framework made (a hosted tool, an approval wrapper): the same exact-call approval a tool step uses, and the answer goes back into the next step.</summary>
    private async Task<AfApprovalResponse> DecideAsync(IAgentContext ctx, AgentDefinition definition, AfApprovalRequest request)
    {
        Func<string, string>? redact = runtime.Redactor;
        ApprovalDecision decision = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action = $"Call tool '{request.Tool}'",
            Details = redact is null ? request.ArgumentsJson : redact(request.ArgumentsJson),
            Tool = request.Tool,
            Requirement = definition.FrameworkApproval,
        });
        string? who = decision.Approver?.Subject;
        return new AfApprovalResponse
        {
            Request = request.Request,
            Approved = decision.IsApproved,
            Reason = decision.IsApproved ? decision.Comment : decision.Outcome == ApprovalOutcome.Expired ? "nobody approved in time" : $"a person rejected the call{(who is null ? "" : $" ({who})")}",
        };
    }

    /// <summary>The text of the assistant messages in a JSON array of chat messages.</summary>
    internal static string AssistantText(JsonElement messages)
    {
        var text = new StringBuilder();
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out JsonElement role) || role.GetString() != "assistant")
            {
                continue;
            }

            foreach (JsonElement content in message.GetProperty("contents").EnumerateArray())
            {
                if (content.GetProperty("$type").GetString() == "text" && content.TryGetProperty("text", out JsonElement t))
                {
                    text.Append(t.GetString());
                }
            }
        }

        return text.ToString();
    }
}
