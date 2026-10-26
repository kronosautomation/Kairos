namespace Kairos;

/// <summary>
/// Store-independent rules every <see cref="IKairosStore"/> implementation applies, so the in-memory reference and
/// PostgreSQL cannot drift apart on what a worker may write.
/// </summary>
public static class StoreRules
{
    /// <summary>The audit action written for every history export.</summary>
    public const string ExportAuditAction = "ExportHistory";

    /// <summary>Longest reason that is recorded. The reason is free text in an audit log: say why, do not paste data.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>An export always names who and why. Both are required, and the reason has a bound.</summary>
    public static void ValidateExport(string claimedActor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedActor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaxReasonLength)
        {
            throw new ArgumentException($"The reason is longer than {MaxReasonLength} characters.", nameof(reason));
        }
    }

    /// <summary>
    /// The starter recorded in <see cref="WorkflowStarted.StartedBy"/>: empty, or a printable identifier of at most <see cref="WorkflowStarted.MaxStartedByLength"/> characters.
    /// Anything else is a bug in the host that built it, so it throws rather than recording something that could not be read back.
    /// </summary>
    public static string CheckStartedBy(string startedBy)
    {
        ArgumentNullException.ThrowIfNull(startedBy);
        if (startedBy.Length > WorkflowStarted.MaxStartedByLength || startedBy.Any(char.IsControl))
        {
            throw new ArgumentException("The starter must be a printable identifier of at most " + WorkflowStarted.MaxStartedByLength + " characters.", nameof(startedBy));
        }

        return startedBy;
    }

    /// <summary>The audit action written when a run is asked to stop.</summary>
    public const string CancelAuditAction = "CancelRun";

    /// <summary>The audit action written when a stuck workflow task is resumed.</summary>
    public const string ResumeAuditAction = "ResumeWorkflowTask";

    /// <summary>The audit action written when a run is closed from outside.</summary>
    public const string FailAuditAction = "FailRun";

    /// <summary>
    /// A verified principal may only be recorded against a run in its own scope. The role is not checked here (that is <see cref="AccessPolicy"/>, at the caller);
    /// a mismatch is a bug in the caller, so it throws rather than answering "not found".
    /// </summary>
    public static void RequirePrincipalInScope(RunKey run, Principal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Scope != run.Scope)
        {
            throw new ArgumentException("The principal is not in the run's tenant and namespace.", nameof(actor));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Subject);
    }

    /// <summary>An optional reason with the same bound as every other recorded reason.</summary>
    public static void ValidateOptionalReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Length > MaxReasonLength)
        {
            throw new ArgumentException($"The reason is longer than {MaxReasonLength} characters.", nameof(reason));
        }
    }

    /// <summary>
    /// The audit detail of an export: the reason (marked when metadata-only) and the hash of the last event exported, which ties a file to this entry.
    /// </summary>
    public static string ExportDetail(string reason, bool metadataOnly, IReadOnlyList<HistoryEvent> exported) =>
        (metadataOnly ? "metadata only: " : "") + reason + " (head " + Convert.ToHexStringLower(exported[^1].Hash) + ")";

    /// <summary>The reason recorded when a run would outgrow the history cap without continuing as new.</summary>
    public static string HistoryLimitReason(int max) =>
        $"HistoryLimitExceeded: the run reached the {max}-event history cap without continuing as new.";

    /// <summary>Error type recorded when a lease expires without a completion (the worker died, hung, or was partitioned).</summary>
    public const string LeaseExpiredError = "LeaseExpired";

    /// <summary>The fixed text of the lease-expiry record. It is the only failure text the store writes, and the only one accepted unsealed.</summary>
    public const string LeaseExpiredMessage = "The attempt lease expired before it completed.";

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
                or HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed or HistoryEventType.WorkflowContinued))
            {
                throw new ArgumentException($"A workflow task may not append {e.Type} events.", nameof(events));
            }

            if (e.Type is HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed or HistoryEventType.WorkflowContinued && i != events.Count - 1)
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

    public static bool IsClosing(HistoryEvent e) => e.Type is HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed or HistoryEventType.RunTerminated
            or HistoryEventType.WorkflowContinued;
}
