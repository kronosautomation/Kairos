namespace Kairos;

/// <summary>
/// Drains an <see cref="IEventSource"/> into Kairos: a start event starts a workflow, a signal event signals a run. Delivery from a
/// bus is at-least-once, so every apply is idempotent: a start is deduplicated by its workflow id and a signal by its event id.
/// A message is acknowledged only after Kairos has accepted it; any failure abandons it for redelivery.
/// </summary>
public sealed class InboundEventProcessor(IEventSource source, KairosClient client, string buildId = KairosClient.DefaultBuildId)
{
    /// <summary>Handles at most <paramref name="maxMessages"/> available messages and returns how many were acknowledged.</summary>
    /// <param name="maxMessages">Upper bound on messages taken in this call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<int> ProcessAsync(int maxMessages, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        int acknowledged = 0;
        for (int i = 0; i < maxMessages; i++)
        {
            IInboundMessage? message = await source.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            if (await TryApplyAsync(message.Payload, cancellationToken).ConfigureAwait(false))
            {
                await message.AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
                acknowledged++;
            }
            else
            {
                await message.AbandonAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return acknowledged;
    }

    private async ValueTask<bool> TryApplyAsync(InboundEvent inbound, CancellationToken cancellationToken)
    {
        try
        {
            switch (inbound)
            {
                case StartWorkflowEvent start:
                    await client.StartAsync(start.Scope, start.WorkflowId, start.WorkflowType, start.Input, buildId, cancellationToken).ConfigureAwait(false);
                    return true;
                case SignalRunEvent signal:
                    // False means the run is closed or unknown; redelivery cannot change that, so the message is done.
                    await client.SignalAsync(new RunKey(signal.Scope.TenantId, signal.Scope.NamespaceId, signal.RunId), signal.SignalName, signal.Payload, signal.EventId, cancellationToken).ConfigureAwait(false);
                    return true;
                default:
                    return true; // an event type this version does not know: acknowledging avoids a poison message
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
