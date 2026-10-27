using Kairos.Testing;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>Counts what a client reads from history: the calls, the events and the payload bytes they return.</summary>
internal sealed class CountingStore(IKairosStore inner) : ForwardingStore(inner)
{
    public long Calls;
    public long Events;
    public long Bytes;

    public void Reset() => (Calls, Events, Bytes) = (0, 0, 0);

    public override async ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoryEvent> history = await base.GetHistoryAsync(run, cancellationToken);
        Count(history);
        return history;
    }

    public override async ValueTask<HistoryPage> GetHistoryAfterAsync(RunKey run, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        HistoryPage page = await base.GetHistoryAfterAsync(run, afterSequence, limit, cancellationToken);
        Count(page.Events);
        return page;
    }

    public override async ValueTask<HistoryPage> GetHistoryTailAsync(RunKey run, int count, CancellationToken cancellationToken)
    {
        HistoryPage page = await base.GetHistoryTailAsync(run, count, cancellationToken);
        Count(page.Events);
        return page;
    }

    internal void Count(IReadOnlyList<HistoryEvent> history)
    {
        Interlocked.Increment(ref Calls);
        Interlocked.Add(ref Events, history.Count);
        long bytes = 0;
        foreach (HistoryEvent e in history)
        {
            bytes += e switch
            {
                ActivityScheduled s => s.Input.Length,
                ActivityCompleted c => c.Result.Length,
                SignalReceived s => s.Payload.Length,
                WorkflowStarted w => w.Input.Length,
                WorkflowContinued c => c.Input.Length,
                WorkflowCompleted c => c.Result.Length,
                _ => 0,
            };
        }

        Interlocked.Add(ref Bytes, bytes);
    }
}
