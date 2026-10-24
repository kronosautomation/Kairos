namespace Kairos;

/// <summary>Isolation boundary: every store call is confined to one tenant and namespace.</summary>
public readonly record struct TenantScope(Guid TenantId, Guid NamespaceId);

public readonly record struct RunKey(Guid TenantId, Guid NamespaceId, Guid RunId)
{
    public TenantScope Scope => new(TenantId, NamespaceId);
}

public enum RunState
{
    Running,
    Completed,
    Failed,
}

public enum CompletionOutcome
{
    /// <summary>The completion was recorded.</summary>
    Applied,

    /// <summary>The lease/fencing token no longer matches; another worker owns the task. Nothing was written.</summary>
    StaleLease,

    /// <summary>History moved since the task was read (signal, timer, ...). The task was released for re-run.</summary>
    SequenceConflict,
}

/// <param name="WorkflowId">Caller-chosen business id, unique per namespace; starting an existing id is deduplicated.</param>
public sealed record StartRunRequest(RunKey Run, string WorkflowId, string WorkflowType, string BuildId, byte[] Input, DateTimeOffset Now);

/// <param name="RunId">The new run, or the existing one when deduplicated.</param>
public sealed record StartRunResult(Guid RunId, bool Started);

/// <summary>A leased workflow task: the full history snapshot to replay and the fencing token to complete with.</summary>
public sealed record WorkflowTask(
    RunKey Run, string WorkflowType, string BuildId, long LeaseToken, int Attempt, IReadOnlyList<HistoryEvent> History);

/// <summary>A leased activity attempt. <see cref="IdempotencyKey"/> is stable across retries, crashes and lease losses.</summary>
public sealed record ActivityTask(
    RunKey Run, int ActivityId, string ActivityType, byte[] Input, RetryPolicy RetryPolicy, int Attempt, long LeaseToken)
{
    public string IdempotencyKey => $"{Run.RunId:N}:{ActivityId}";
}

/// <summary>
/// Persistence contract (reference: PostgreSQL). Events passed in are templates: the store assigns
/// <see cref="HistoryEvent.Sequence"/>, <see cref="HistoryEvent.PrevHash"/> and <see cref="HistoryEvent.Hash"/>.
/// Every method is confined to the tenant/namespace it is given and fails closed on anything else.
/// </summary>
public interface IKairosStore
{
    /// <summary>Appends WorkflowStarted and makes the first workflow task runnable. Deduplicates by workflow id.</summary>
    ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken);

    /// <summary>Appends SignalReceived. Returns false when the run is closed or unknown.</summary>
    ValueTask<bool> SignalRunAsync(RunKey run, string signalName, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Claims one runnable workflow task for one of <paramref name="buildIds"/> (run pinning), or null.</summary>
    ValueTask<WorkflowTask?> PollWorkflowTaskAsync(
        TenantScope scope, IReadOnlyCollection<string> buildIds, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically appends <paramref name="newEvents"/> (empty when the workflow merely blocked), derives follow-up work
    /// (activity tasks from ActivityScheduled, timers from TimerStarted, closing on WorkflowCompleted/Failed) and
    /// retires the task. Rejected when the lease token is stale or history advanced past the task's snapshot.
    /// </summary>
    ValueTask<CompletionOutcome> CompleteWorkflowTaskAsync(
        WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the task back to be retried at <paramref name="retryAt"/>. A failure (e.g. non-determinism) counts as an attempt;
    /// a graceful shutdown (<paramref name="countAttempt"/> false) does not. Guarded by the lease token.
    /// </summary>
    ValueTask ReleaseWorkflowTaskAsync(WorkflowTask task, DateTimeOffset retryAt, DateTimeOffset now, bool countAttempt, CancellationToken cancellationToken);

    ValueTask<ActivityTask?> PollActivityTaskAsync(TenantScope scope, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Records an attempt's outcome: ActivityCompleted, a terminal ActivityFailed, or a retryable ActivityFailed
    /// (<paramref name="retryAt"/> non-null, task re-queued). Completed/terminal outcomes make the workflow runnable.
    /// </summary>
    ValueTask<CompletionOutcome> CompleteActivityTaskAsync(
        ActivityTask task, HistoryEvent outcome, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Extends the activity's lease by <paramref name="extendBy"/> from now (renewal). <see cref="CompletionOutcome.StaleLease"/> when
    /// the token no longer matches, which tells the holder it lost the task and should stop.
    /// </summary>
    ValueTask<CompletionOutcome> HeartbeatActivityTaskAsync(ActivityTask task, DateTimeOffset now, TimeSpan extendBy, CancellationToken cancellationToken);

    /// <summary>Gives the attempt back (graceful shutdown) to be claimed again at <paramref name="retryAt"/>. Not counted as an attempt. Guarded by the lease token.</summary>
    ValueTask ReleaseActivityTaskAsync(ActivityTask task, DateTimeOffset retryAt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Handles every lease that has expired: an activity attempt becomes a recorded <c>ActivityFailed("LeaseExpired")</c> and is
    /// re-offered after the retry policy's backoff, or fails terminally once <c>MaximumAttempts</c> is used up; an expired workflow
    /// task is re-offered after a backoff and, after too many expiries, parked as stuck (never dropped, never handed out again
    /// until an operator intervenes). Returns the number of leases handled.
    /// </summary>
    ValueTask<int> ReapExpiredLeasesAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Appends TimerFired for every due timer in scope and makes their workflows runnable. Returns the count.</summary>
    ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken);

    /// <summary>Null when the run does not exist in this scope.</summary>
    ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken);
}
