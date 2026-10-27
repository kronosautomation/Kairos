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
