using System.Text;
using System.Text.Json;

namespace Kairos.Agents.AgentFramework;

/// <summary>
/// The Kairos workflow that runs one durable Agent Framework workflow. It is ordinary Kairos workflow code and obeys the same rules: it only awaits context calls, so it replays from history and never runs Agent Framework
/// code. The executors run <b>inside activities</b>, a superstep (or a few) per activity; the requests the workflow raises become approvals or signals that wait without holding compute.
/// </summary>
internal sealed class WorkflowDriver(AgentFrameworkRuntime runtime)
{
    private sealed class Waiting(AfwRequest request, Task<AfwResponse> answer, bool approval)
    {
        public AfwRequest Request { get; } = request;

        public Task<AfwResponse> Answer { get; } = answer;

        public bool Approval { get; } = approval;
    }

    /// <summary>Where the run stands while the driver works on it.</summary>
    private sealed class State
    {
        public SessionChain Chain { get; } = new();

        public string? Structure { get; set; }

        public string? Version { get; set; }

        public int Steps { get; set; }

        public bool More { get; set; }

        public List<AfwOutput> Outputs { get; } = [];

        public List<Waiting> Waiting { get; } = [];

        public List<AfwResponse> Answered { get; } = [];

        public SortedSet<string> Conversations { get; } = new(StringComparer.Ordinal);
    }

    public async Task<byte[]> RunAsync(IAgentContext ctx, byte[] inputBytes)
    {
        AfwInput input = AfCodec.Read(inputBytes, AfwJson.Default.AfwInput);
        WorkflowDefinition definition = runtime.WorkflowDefinitionOf(input.Workflow); // a worker without the workflow does not register this workflow's type, so the engine leaves the run for one that has it
        ctx.OnBehalfOf = input.OnBehalfOf;

        // A saga: a cancel request stops the run at the next superstep boundary (a waiting request ends at once), and the conversations its executors started are asked to stop with it.
        return await ctx.RunSagaAsync(saga => LoopAsync(ctx, definition, input, saga));
    }

    private async Task<byte[]> LoopAsync(IAgentContext ctx, WorkflowDefinition definition, AfwInput input, ISaga saga)
    {
        IWorkflowContext wf = ctx.Workflow;
        var state = new State { Structure = input.Structure, Version = input.Version, Steps = input.Steps, More = input.More };
        state.Outputs.AddRange(input.Outputs);
        foreach (string conversation in input.Conversations)
        {
            state.Conversations.Add(conversation);
        }

        if (input.Checkpoint is { } held)
        {
            state.Chain.StartFrom(held);
        }

        state.Answered.AddRange(input.Answered);
        foreach (AfwRequest request in input.Waiting)
        {
            state.Waiting.Add(WaitFor(ctx, definition, request));
        }

        try
        {
            while (true)
            {
                saga.ThrowIfCancelled();
                foreach (Waiting answered in state.Waiting.Where(w => w.Answer.IsCompleted).ToList())
                {
                    state.Answered.Add(answered.Answer.Result);
                    state.Waiting.Remove(answered);
                }

                bool first = state.Chain.IsEmpty && input.Input is not null;
                if (!first && !state.More && state.Answered.Count == 0)
                {
                    if (state.Waiting.Count == 0)
                    {
                        break; // nothing queued and nothing outstanding
                    }

                    await Task.WhenAny(state.Waiting.Select(w => (Task)w.Answer).Append(saga.WhenCancelled));
                    continue;
                }

                // A long workflow hands over to a fresh run before its history gets too long, carrying the checkpoint once. Open approvals cannot move (they belong to this run's history), so a hand-over
                // waits until they are decided; open signal requests are waited for again by the new run.
                if ((wf.ShouldContinueAsNew || (input.ContinueAfterEvents is { } limit && wf.HistoryLength >= limit)) && state.Waiting.All(w => !w.Approval))
                {
                    throw wf.ContinueAsNew(AfCodec.Write(HandOver(input, state), AfwJson.Default.AfwInput));
                }

                AfwStepResult result = await StepAsync(ctx, definition, input, state);
                state.Chain.Record(result.CheckpointHash, result.Checkpoint, result.Delta, result.BaseHash, result.StepId);
                state.Structure = result.Structure ?? state.Structure;
                state.Version = result.Version ?? state.Version;
                state.Steps++;
                state.More = result.More;
                state.Answered.Clear();
                state.Outputs.AddRange(result.Outputs);
                foreach (string conversation in result.Conversations)
                {
                    state.Conversations.Add(conversation);
                }

                foreach (AfwRequest request in result.Requests.Where(r => !state.Waiting.Exists(w => w.Request.RequestId == r.RequestId)))
                {
                    state.Waiting.Add(WaitFor(ctx, definition, request));
                }

                if (result.Done)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not ContinueAsNewException && state.Conversations.Count > 0)
        {
            // The conversations the executors started are the workflow's: they stop with it, whatever stopped it.
            await CancelConversationsAsync(wf, state, ex is SagaCancelledException ? "the workflow was cancelled" : "the workflow ended with an error");
            throw;
        }

        return AfCodec.Write(new AfwResult { Outputs = [.. state.Outputs], Steps = state.Steps }, AfwJson.Default.AfwResult);
    }

    private static AfwInput HandOver(AfwInput input, State state) =>
        input with
        {
            Input = null,
            Checkpoint = state.Chain.IsEmpty ? null : state.Chain.Materialize(),
            Structure = state.Structure,
            Version = state.Version,
            Steps = state.Steps,
            More = state.More,
            Outputs = [.. state.Outputs],
            Waiting = [.. state.Waiting.Select(w => w.Request)],
            Answered = [.. state.Answered],
            Conversations = [.. state.Conversations],
        };

    private static async Task CancelConversationsAsync(IWorkflowContext wf, State state, string reason)
    {
        try
        {
            await wf.ExecuteActivityAsync(
                AgentFrameworkNames.WorkflowCancel,
                AfCodec.Write(new AfwCancelConversations { WorkflowIds = [.. state.Conversations], Reason = reason }, AfwJson.Default.AfwCancelConversations),
                AgentFrameworkRuntime.WorkflowStepRetry);
        }
        catch (ActivityFailureException)
        {
            // They find out when they are next looked at; the workflow ends either way.
        }
    }

    private static async Task<AfwStepResult> StepAsync(IAgentContext ctx, WorkflowDefinition definition, AfwInput input, State state)
    {
        IWorkflowContext wf = ctx.Workflow;
        int overrides = 0;
        bool sendWhole = false;
        while (true)
        {
            SessionChain chain = state.Chain;
            var request = new AfwStepRequest
            {
                Workflow = definition.Name,
                Structure = state.Structure,
                Version = state.Version,
                Input = chain.IsEmpty ? input.Input : null,
                Checkpoint = sendWhole && !chain.IsEmpty ? chain.Materialize() : null,
                CheckpointHash = chain.Hash,
                CheckpointFrom = chain.From,
                StepsSinceFull = chain.StepsSinceFull,
                Responses = [.. state.Answered],
                Supersteps = input.SuperstepsPerStep,
                OnBehalfOf = input.OnBehalfOf,
                Number = state.Steps,
            };
            byte[] bytes = await wf.ExecuteActivityAsync(AgentFrameworkNames.WorkflowStep, AfCodec.Write(request, AfwJson.Default.AfwStepRequest), AgentFrameworkRuntime.WorkflowStepRetry);
            AfwStepResult result = AfCodec.Read(bytes, AfwJson.Default.AfwStepResult);

            switch (result.Outcome)
            {
                case AfwOutcomes.NeedSnapshot:
                    // The worker that took the step does not hold the checkpoint (another worker ran the last step, or it was dropped from the worker's cache): send it once and ask again. Nothing was run.
                    if (sendWhole || chain.IsEmpty)
                    {
                        throw new SnapshotIntegrityException("A worker did not take the checkpoint it was sent.");
                    }

                    sendWhole = true;
                    continue;

                case AfwOutcomes.StructureChanged:
                    if (++overrides > 3)
                    {
                        throw new DefinitionChangedException($"Workflow '{definition.Name}' changed again and again while the run waited.");
                    }

                    (state.Structure, state.Version) = await AllowStructureChangeAsync(ctx, definition, result);
                    continue;

                default:
                    return result;
            }
        }
    }

    /// <summary>
    /// The worker has another structure of the workflow (or another Agent Framework version, whose checkpoint format may differ) than the run started with. A person decides, as an approval: the verified approver is recorded
    /// in history, and an override mark makes the decision easy to find (<see cref="AgentFrameworkAudit"/>). Returns what to pin the run to from now on.
    /// </summary>
    private static async Task<(string Structure, string Version)> AllowStructureChangeAsync(IAgentContext ctx, WorkflowDefinition definition, AfwStepResult changed)
    {
        string fromStructure = changed.PinnedStructure ?? "";
        string toStructure = changed.Structure ?? "";
        string fromVersion = changed.PinnedVersion ?? "";
        string toVersion = changed.Version ?? "";
        ApprovalDecision decision = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action = $"Continue this run on a changed workflow '{definition.Name}'",
            Details = $"The run started on structure {Short(fromStructure)} (Agent Framework {fromVersion}); the worker now has {Short(toStructure)} ({toVersion}).",
            Requirement = definition.Registration.StructureChangeApproval,
        });
        if (!decision.IsApproved)
        {
            throw new DefinitionChangedException($"The structure of workflow '{definition.Name}' (or the Agent Framework version) changed since the run started and the change was not approved.");
        }

        await ctx.Workflow.ExecuteActivityAsync(
            AgentFrameworkNames.WorkflowOverride,
            AfCodec.Write(
                new AfwOverrideMark
                {
                    Workflow = definition.Name,
                    FromStructure = fromStructure,
                    ToStructure = toStructure,
                    FromVersion = fromVersion,
                    ToVersion = toVersion,
                    ApprovalId = decision.ApprovalId,
                    Approver = decision.Approver?.Subject ?? "",
                    Roles = [.. decision.Approver?.Roles ?? []],
                },
                AfwJson.Default.AfwOverrideMark));
        return (toStructure, toVersion);

        static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];
    }

    /// <summary>Starts waiting for the answer to a request: an approval when the registration says the port is one, otherwise a signal. Neither holds compute.</summary>
    private Waiting WaitFor(IAgentContext ctx, WorkflowDefinition definition, AfwRequest request) =>
        definition.Registration.Approvals.TryGetValue(request.PortId, out ApprovalPort? port)
            ? new Waiting(request, ApprovalAsync(ctx, definition, request, port), approval: true)
            : new Waiting(request, SignalAsync(ctx.Workflow, request), approval: false);

    private async Task<AfwResponse> ApprovalAsync(IAgentContext ctx, WorkflowDefinition definition, AfwRequest request, ApprovalPort port)
    {
        ApprovalDescription description = port.Describe?.Invoke(new WorkflowRequest(request.RequestId, request.PortId, request.Data))
            ?? new ApprovalDescription($"Answer the request '{request.PortId}' of workflow '{definition.Name}'", request.Data.GetRawText());
        string? details = description.Details is null ? null : runtime.Redactor is { } redact ? redact(description.Details) : description.Details;
        ApprovalDecision decision = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action = description.Action,
            Details = details,
            Requirement = port.Requirement,
        });
        return new AfwResponse { RequestId = request.RequestId, PortId = request.PortId, Data = JsonSerializer.SerializeToElement(decision.IsApproved) };
    }

    private static async Task<AfwResponse> SignalAsync(IWorkflowContext wf, AfwRequest request)
    {
        byte[] payload = await wf.WaitForSignalAsync(AgentFrameworkNames.ResponseSignal(request.RequestId));
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
        return new AfwResponse { RequestId = request.RequestId, PortId = request.PortId, Data = document.RootElement.Clone() };
    }
}
