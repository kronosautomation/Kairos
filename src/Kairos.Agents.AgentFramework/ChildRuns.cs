using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

public sealed partial class AgentFrameworkRuntime
{
    /// <summary>
    /// The tool step of a call to a nested durable agent: starts the nested agent's conversation as a run of its own and returns a marker the driver turns into a wait. Starting is idempotent (the run's id is derived
    /// from the step's idempotency key), so a retried step finds the run it already started.
    /// </summary>
    internal async ValueTask<string> StartChildAsync(ToolInvocation invocation, string child, CancellationToken cancellationToken)
    {
        string query = invocation.Arguments.ValueKind == JsonValueKind.Object && invocation.Arguments.TryGetProperty("query", out JsonElement q) ? q.GetString() ?? "" : "";
        string key = invocation.IdempotencyKey.Replace(':', '-');
        string signal = $"af.child.{key}";
        var parent = new AfParent { TenantId = invocation.Run.TenantId, NamespaceId = invocation.Run.NamespaceId, RunId = invocation.Run.RunId, Signal = signal };
        var input = new AfDriverInput
        {
            Agent = child,
            Pending = [new AfTurn { TurnId = key, Messages = JsonSerializer.SerializeToElement(new List<ChatMessage> { new(ChatRole.User, query) }, AIJsonUtilities.DefaultOptions) }],
            OnBehalfOf = invocation.OnBehalfOf,
            Parent = parent,
            ParentWatchSeconds = Options.ParentWatchInterval?.TotalSeconds,
            MaxStepsPerTurn = Options.MaxStepsPerTurn,
            ContinueAfterEvents = Options.ContinueAfterEvents,
        };
        StartRunResult started = await Client.StartAsync(
            new TenantScope(invocation.Run.TenantId, invocation.Run.NamespaceId), $"af/{child}/{key}", AgentFrameworkNames.ConversationType(child),
            AfCodec.Write(input, AfJson.Default.AfDriverInput), cancellationToken: cancellationToken).ConfigureAwait(false);
        return $"kairos.af.child/{signal}/{started.RunId:N}";
    }

    private async Task<byte[]> ChildCheckAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        AfChildCheck check = AfCodec.Read(input, AfJson.Default.AfChildCheck);
        RunState? state = await Client.GetStateAsync(new RunKey(context.Run.TenantId, context.Run.NamespaceId, check.RunId), cancellationToken).ConfigureAwait(false);
        string name = state switch
        {
            RunState.Running or RunState.ContinuedAsNew => "running", // a conversation that continued as new carries on under another run id and still answers
            RunState.Completed => "completed",
            RunState.Failed => "failed",
            _ => "other",
        };
        return AfCodec.Write(new AfChildCheckResult { State = name }, AfJson.Default.AfChildCheckResult);
    }

    /// <summary>
    /// Asks a nested agent to stop because the run that called it was cancelled. The request goes to the conversation's current run (a nested agent that handed over is in another run than the one that was started) with the
    /// reserved cancel signal and its fixed signal id, so a step that is run again, or a second request, is still one request: the nested agent's history holds one cancel and its rollback happens once.
    /// </summary>
    private async Task<byte[]> ChildCancelAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        AfChildCancel cancel = AfCodec.Read(input, AfJson.Default.AfChildCancel);
        if (!cancel.WorkflowId.StartsWith("af/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the conversations of durable agents are cancelled this way.");
        }

        // The scope is the cancelled run's own: a nested agent is always a run of its parent's tenant and namespace.
        var scope = new TenantScope(context.Run.TenantId, context.Run.NamespaceId);
        RunKey? child = await Client.GetCurrentRunAsync(scope, cancel.WorkflowId, cancellationToken).ConfigureAwait(false);
        bool sent = child is { } run && await Client.SignalAsync(run, ReservedSignals.Cancel, Encoding.UTF8.GetBytes(cancel.Reason), ReservedSignals.CancelSignalId, cancellationToken).ConfigureAwait(false);
        return AfCodec.Write(new AfChildCancelResult { Sent = sent }, AfJson.Default.AfChildCancelResult);
    }

    /// <summary>
    /// The nested agent's run asks whether the run that called it is still open. A run that is closed (it was terminated, which gives its workflow no chance to cancel its children, or it ended some other way) does not want the
    /// answer: this run then asks itself to stop, with the same cancel request an operator would send, and its rollback is the usual one.
    /// </summary>
    private async Task<byte[]> ParentWatchAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        AfParentWatch watch = AfCodec.Read(input, AfJson.Default.AfParentWatch);
        if (watch.Parent.TenantId != context.Run.TenantId || watch.Parent.NamespaceId != context.Run.NamespaceId)
        {
            throw new InvalidOperationException("A nested agent watches a run of its own tenant and namespace only.");
        }

        RunState? state = await Client.GetStateAsync(new RunKey(watch.Parent.TenantId, watch.Parent.NamespaceId, watch.Parent.RunId), cancellationToken).ConfigureAwait(false);
        bool open = state is RunState.Running or RunState.ContinuedAsNew; // a parent that handed over still wants the answer
        if (!open)
        {
            await Client.SignalAsync(context.Run, ReservedSignals.Cancel, Encoding.UTF8.GetBytes("the run that asked this nested agent has ended"), ReservedSignals.CancelSignalId, cancellationToken).ConfigureAwait(false);
        }

        return AfCodec.Write(new AfParentWatchResult { Open = open }, AfJson.Default.AfParentWatchResult);
    }

    private async Task<byte[]> ChildNotifyAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        AfNotify notify = AfCodec.Read(input, AfJson.Default.AfNotify);
        if (notify.Parent.TenantId != context.Run.TenantId || notify.Parent.NamespaceId != context.Run.NamespaceId)
        {
            throw new InvalidOperationException("A nested agent answers a run of its own tenant and namespace only."); // defence in depth: the store would refuse it too
        }

        var run = new RunKey(notify.Parent.TenantId, notify.Parent.NamespaceId, notify.Parent.RunId);

        // One answer per nested run: the signal id is fixed, so a retried step signals once.
        await Client.SignalAsync(run, notify.Parent.Signal, AfCodec.Write(notify.Answer, AfJson.Default.AfChildAnswer), "answer", cancellationToken).ConfigureAwait(false);
        return [];
    }
}
