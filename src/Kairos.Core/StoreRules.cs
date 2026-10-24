namespace Kairos;

/// <summary>
/// Store-independent rules every <see cref="IKairosStore"/> implementation applies, so the in-memory reference and
/// PostgreSQL cannot drift apart on what a worker may write.
/// </summary>
public static class StoreRules
{
    /// <summary>Error type recorded when a lease expires without a completion (the worker died, hung, or was partitioned).</summary>
    public const string LeaseExpiredError = "LeaseExpired";

    /// <summary>A workflow task whose lease expires this many times is parked as stuck instead of being offered forever.</summary>
    public const int MaxWorkflowTaskAttempts = 10;

    /// <summary>Delay before an expired or failed workflow task is offered again: 1 s doubling to 60 s.</summary>
    public static TimeSpan WorkflowTaskBackoff(int failedAttempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Max(0, failedAttempt - 1))));

    /// <summary>
    /// A workflow worker may only emit command-derived events for its own run. It can never forge results, signals or timer
    /// firings: those are written by the store on behalf of activity workers, clients and the timer service.
    /// </summary>
    public static void ValidateWorkflowTaskEvents(RunKey run, IReadOnlyList<HistoryEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        for (int i = 0; i < events.Count; i++)
        {
            HistoryEvent e = events[i];
            RequireSameRun(run, e);
            if (e.Type is not (HistoryEventType.ActivityScheduled or HistoryEventType.TimerStarted or HistoryEventType.PatchMarker
                or HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed))
            {
                throw new ArgumentException($"A workflow task may not append {e.Type} events.", nameof(events));
            }

            if (e.Type is HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed && i != events.Count - 1)
            {
                throw new ArgumentException("A closing event must be last.", nameof(events));
            }
        }
    }

    public static void ValidateActivityOutcome(ActivityTask task, HistoryEvent outcome, DateTimeOffset? retryAt)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(outcome);
        RequireSameRun(task.Run, outcome);
        switch (outcome)
        {
            case ActivityCompleted c when c.ActivityId == task.ActivityId && retryAt is null:
            case ActivityFailed f when f.ActivityId == task.ActivityId && (f.WillRetry == retryAt.HasValue):
                return;
            default:
                throw new ArgumentException("Outcome does not match the activity task.", nameof(outcome));
        }
    }

    public static void RequireSameRun(RunKey run, HistoryEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.TenantId != run.TenantId || e.NamespaceId != run.NamespaceId || e.RunId != run.RunId)
        {
            throw new ArgumentException("Event belongs to a different tenant, namespace or run.", nameof(e));
        }
    }

    /// <summary>Assigns sequence and hash-chain fields to a template event.</summary>
    public static HistoryEvent Seal(HistoryEvent template, long sequence, ReadOnlySpan<byte> prevHash) =>
        HashChain.Seal(template with { Sequence = sequence }, prevHash);

    public static bool IsClosing(HistoryEvent e) => e.Type is HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed or HistoryEventType.RunTerminated;
}
