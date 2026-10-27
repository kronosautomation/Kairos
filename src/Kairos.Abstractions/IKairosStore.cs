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

    /// <summary>Handed over to a fresh run of the same workflow id (see <see cref="IKairosStore.GetCurrentRunAsync"/>).</summary>
    ContinuedAsNew,
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

/// <summary>A workflow task parked after too many lease expiries. It is never offered again until an operator resumes or fails the run.</summary>
public sealed record StuckWorkflowTask(RunKey Run, string WorkflowType, string BuildId, int Attempts);

/// <summary>One privileged action (resume, fail run). Append-only.</summary>
public sealed record AuditEntry(Guid TenantId, Guid NamespaceId, Guid? RunId, string Action, string Actor, DateTimeOffset At, string Detail)
{
    /// <summary>
    /// The database login the store connected as, recorded by the database itself (PostgreSQL <c>session_user</c>), never by the caller. Together with
    /// <see cref="ClientAddress"/> this is who actually did it; <see cref="Actor"/> is the caller's own word for entries that are not exports.
    /// </summary>
    public string? SessionUser { get; init; }

    /// <summary>The network address the database saw the connection come from (<c>inet_client_addr()</c>), or null for a local connection.</summary>
    public string? ClientAddress { get; init; }

    /// <summary>The name the caller asked to be recorded under (for example <c>--actor</c>). <b>Claimed, not verified</b>; kept only so a reader can see what was claimed.</summary>
    public string? ClaimedActor { get; init; }

    /// <summary>The authenticated principal, when the export went through a client given one that an authenticator verified (<c>role:subject</c>); null otherwise.</summary>
    public string? VerifiedPrincipal { get; init; }
}

/// <summary>Everything needed to start a run.</summary>
/// <param name="Run">The new run's identity (tenant, namespace and a fresh run id).</param>
/// <param name="WorkflowId">Caller-chosen business id, unique per namespace; starting an existing id is deduplicated.</param>
/// <param name="WorkflowType">Registered workflow name.</param>
/// <param name="BuildId">The build the run is pinned to for its whole life.</param>
/// <param name="Input">The workflow input, opaque to the server (sealed on the client when encryption is on).</param>
/// <param name="Now">The start time recorded in history.</param>
/// <param name="TraceContext">W3C <c>traceparent</c> of the span that started the run, recorded in history (empty when there is none).</param>
/// <param name="StartedBy">The verified principal that started the run (<see cref="Principal.AuditName"/>), recorded in history; empty when there is none. At most <see cref="WorkflowStarted.MaxStartedByLength"/> characters.</param>
public sealed record StartRunRequest(RunKey Run, string WorkflowId, string WorkflowType, string BuildId, byte[] Input, DateTimeOffset Now, string TraceContext = "", string StartedBy = "");

/// <summary>The outcome of starting a run.</summary>
/// <param name="RunId">The new run, or the existing one when deduplicated.</param>
/// <param name="Started">True when a new run was created; false when the workflow id already had a current run.</param>
public sealed record StartRunResult(Guid RunId, bool Started);

/// <summary>A leased workflow task: the full history snapshot to replay and the fencing token to complete with.</summary>
public sealed record WorkflowTask(
    RunKey Run, string WorkflowType, string BuildId, long LeaseToken, int Attempt, IReadOnlyList<HistoryEvent> History);

/// <summary>A leased activity attempt. <see cref="IdempotencyKey"/> is stable across retries, crashes and lease losses.</summary>
public sealed record ActivityTask(
    RunKey Run, int ActivityId, string ActivityType, byte[] Input, RetryPolicy RetryPolicy, int Attempt, long LeaseToken, string TraceContext = "")
{
    public string IdempotencyKey => $"{Run.RunId:N}:{ActivityId}";
}

/// <summary>Which runs a listing returns by state.</summary>
public enum RunStateFilter
{
    /// <summary>Open and closed runs.</summary>
    Any,

    /// <summary>Runs that are still running.</summary>
    Open,

    /// <summary>Runs that completed, failed, were terminated or continued as new.</summary>
    Closed,
}

/// <summary>A position in a newest-first listing: the next page starts strictly after it.</summary>
public readonly record struct RunCursor(DateTimeOffset StartedAt, Guid RunId);

/// <summary>What <see cref="IKairosStore.ListRunsAsync"/> returns.</summary>
public sealed record RunFilter
{
    public string? WorkflowType { get; init; }

    public string? BuildId { get; init; }

    public RunStateFilter State { get; init; }

    /// <summary>Closed runs started before this time are left out. Open runs are always included, whenever they started: they are the ones still running on some build.</summary>
    public DateTimeOffset? StartedSince { get; init; }

    /// <summary>Continue a listing after this position (the <see cref="RunSummary"/> that ended the previous page).</summary>
    public RunCursor? After { get; init; }
}

/// <summary>One run in a listing. Metadata only: no payload.</summary>
public sealed record RunSummary(RunKey Run, string WorkflowId, string WorkflowType, string BuildId, RunState State, DateTimeOffset StartedAt)
{
    public RunCursor Cursor => new(StartedAt, Run.RunId);
}

/// <summary>How many runs of one workflow type and build there are, open and closed (closed ones filtered like <see cref="RunFilter.StartedSince"/>).</summary>
public sealed record RunGroup(string WorkflowType, string BuildId, int Open, int Closed);

/// <summary>Work that is ready to be claimed right now. Growth means workers are not keeping up.</summary>
public sealed record WorkBacklog(int WorkflowTasks, int ActivityTasks, int DueTimers)
{
    /// <summary>Ready workflow tasks by the build they are pinned to, when the store can say (empty otherwise). Their sum is <see cref="WorkflowTasks"/>.</summary>
    public IReadOnlyDictionary<string, int> WorkflowTasksByBuild { get; init; } = new Dictionary<string, int>();

    // Two backlogs are equal when their counts are; the breakdown by build is detail and not every store has it.
    public bool Equals(WorkBacklog? other) =>
        other is not null && WorkflowTasks == other.WorkflowTasks && ActivityTasks == other.ActivityTasks && DueTimers == other.DueTimers;

    public override int GetHashCode() => HashCode.Combine(WorkflowTasks, ActivityTasks, DueTimers);
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

    /// <summary>
    /// Appends SignalReceived. Returns false when the run is closed or unknown. A signal id already delivered to this run is ignored (true),
    /// so redelivery from a bus or a retrying client cannot signal twice.
    /// </summary>
    ValueTask<bool> SignalRunAsync(RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="SignalRunAsync"/> that also records the sender's W3C <c>traceparent</c> in the
    /// <see cref="SignalReceived"/> event. The default drops it, so a store written before tracing keeps working.
    /// </summary>
    ValueTask<bool> SignalRunTracedAsync(
        RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, string traceContext, CancellationToken cancellationToken) =>
        SignalRunAsync(run, signalName, signalId, payload, now, cancellationToken);

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

    /// <summary>
    /// Runs in this scope, newest first (by start time, then run id), at most <paramref name="limit"/>. Read-only, metadata only. Page with <see cref="RunFilter.After"/>.
    /// Stores that cannot list throw <see cref="NotSupportedException"/> rather than answer with an empty list, so that a check built on it cannot pass by seeing nothing.
    /// </summary>
    ValueTask<IReadOnlyList<RunSummary>> ListRunsAsync(TenantScope scope, RunFilter filter, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot list runs.");

    /// <summary>
    /// Model usage by workflow type (an "agent"), UTC day and model for the days <paramref name="firstDay"/> to <paramref name="lastDay"/>, both included, summed over the runs of the scope that recorded
    /// usage on those days. Only readable counts are summed, so it needs no key and works whatever the application seals. A store that keeps the aggregate first reads the runs that have new events
    /// (within a budget; <see cref="UsageSummary.Behind"/> says how many are left), so the answer is as new as the last event unless it says it is behind.
    /// Stores that cannot answer throw <see cref="NotSupportedException"/>.
    /// </summary>
    ValueTask<UsageSummary> GetUsageSummaryAsync(TenantScope scope, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot summarise usage.");

    /// <summary>
    /// The approvals runs of the scope are waiting for, oldest first, at most <paramref name="limit"/>, after <paramref name="after"/>. Metadata only: what an approval is about is sealed, and a viewer
    /// that may open it fetches the run. Runs that asked before the agent runtime recorded the readable marker for it (<c>RunProjection.ApprovalRequestedMarker</c>) are not listed.
    /// Stores that cannot answer throw <see cref="NotSupportedException"/>.
    /// </summary>
    ValueTask<PendingApprovalPage> GetPendingApprovalsAsync(TenantScope scope, int limit, PendingApprovalCursor? after, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot list pending approvals.");

    /// <summary>
    /// Reads the runs that have events the aggregates have not seen (at most <paramref name="maxRuns"/>) and brings the aggregates up to date with them, outside the hot path.
    /// Returns how many runs were read. Stores that answer by scanning have nothing to do and return zero.
    /// </summary>
    ValueTask<int> ProjectAggregatesAsync(TenantScope scope, int maxRuns, CancellationToken cancellationToken) => ValueTask.FromResult(0);

    /// <summary>The workflow types and builds with runs in this scope and how many are open or closed. Closed runs are counted from <paramref name="closedSince"/> (null: all).</summary>
    ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot list runs.");

    /// <summary>
    /// Reads a run's whole history for export and, in the same transaction, appends an audit entry (<c>ExportHistory</c>) with the <paramref name="reason"/> and who did it.
    /// <b>Who is never taken from the caller.</b> The entry's <see cref="AuditEntry.Actor"/> is <paramref name="verifiedPrincipal"/> when the caller was authenticated, and otherwise
    /// the store's own view of the connection (<c>db:</c> plus the database login); <see cref="AuditEntry.SessionUser"/> and <see cref="AuditEntry.ClientAddress"/> are written by the
    /// database. <paramref name="claimedActor"/> is stored apart, as <see cref="AuditEntry.ClaimedActor"/>. Null (and nothing audited) when the run does not exist in this scope. The caller is responsible for authorizing the actor
    /// (<c>Operation.ExportHistory</c>); the store records who and why and returns the history exactly as stored (ciphertext when encryption is on).
    /// </summary>
    ValueTask<IReadOnlyList<HistoryEvent>?> ExportHistoryAsync(
        RunKey run, string claimedActor, string? verifiedPrincipal, string reason, bool metadataOnly, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot export histories.");

    /// <summary>Counts of ready work in this scope, for queue-depth metrics. Null when the store cannot say (the default).</summary>
    ValueTask<WorkBacklog?> GetBacklogAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) => ValueTask.FromResult<WorkBacklog?>(null);

    /// <summary>Parked workflow tasks in this scope (see <see cref="StuckWorkflowTask"/>). The hosting layer logs and meters these.</summary>
    ValueTask<IReadOnlyList<StuckWorkflowTask>> GetStuckWorkflowTasksAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Unparks a stuck workflow task: attempts reset, runnable now. False when the run has no parked task. Audited.
    /// <paramref name="actor"/> is the caller's own word: the entry's <see cref="AuditEntry.Actor"/> is the store's view of the connection (<c>db:</c> plus the database login)
    /// and the supplied name is kept apart as <see cref="AuditEntry.ClaimedActor"/>. Use <see cref="ResumeWorkflowTaskVerifiedAsync"/> when the caller was authenticated.
    /// </summary>
    ValueTask<bool> ResumeWorkflowTaskAsync(RunKey run, string actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ResumeWorkflowTaskAsync"/> for an <b>authenticated</b> caller: the audit entry names <paramref name="actor"/>
    /// (<see cref="Principal.AuditName"/>, also <see cref="AuditEntry.VerifiedPrincipal"/>). The store does not authorize (that is <c>AccessPolicy</c>, at the caller) but refuses a principal
    /// whose scope is not the run's.
    /// </summary>
    /// <exception cref="ArgumentException">The principal's scope is not the run's scope.</exception>
    ValueTask<bool> ResumeWorkflowTaskVerifiedAsync(RunKey run, Principal actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot record a verified principal.");

    /// <summary>
    /// Closes an open run as failed (<see cref="RunTerminated"/>) and removes every task and timer it owns. False when the run is
    /// unknown or already closed. Audited.
    /// </summary>
    ValueTask<bool> FailRunAsync(RunKey run, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="FailRunAsync"/> for an <b>authenticated</b> caller: the audit entry names <paramref name="actor"/>
    /// and records it as <see cref="AuditEntry.VerifiedPrincipal"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The principal's scope is not the run's scope.</exception>
    ValueTask<bool> FailRunVerifiedAsync(RunKey run, string reason, Principal actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot record a verified principal.");

    /// <summary>
    /// Asks an open run to stop, as an authenticated caller: appends the reserved <c>kairos.cancel</c> signal (fixed signal id, so asking twice is one request, with no payload:
    /// a server that holds no key cannot author a sealed one) and the audit entry <c>CancelRun</c> in one transaction. The workflow decides how to stop (agent sagas roll back).
    /// True when the run was open (also when it had already been asked); false, and nothing audited, when the run is unknown, in another scope, or closed.
    /// </summary>
    /// <param name="run">The run to cancel.</param>
    /// <param name="reason">Why; optional, at most 500 characters (<c>StoreRules.MaxReasonLength</c>). Recorded in the audit entry only.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="traceContext">W3C <c>traceparent</c> of the request, recorded in the signal event (empty for none).</param>
    /// <param name="now">The time recorded.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException">The principal's scope is not the run's scope, or the reason is too long.</exception>
    ValueTask<bool> CancelRunAsync(RunKey run, string reason, Principal actor, string traceContext, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot cancel runs.");

    ValueTask<IReadOnlyList<AuditEntry>> GetAuditLogAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Appends TimerFired for every due timer in scope and makes their workflows runnable. Returns the count.</summary>
    ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken);

    /// <summary>
    /// The events of a run after sequence number <paramref name="afterSequence"/> (0 for from the start), at most <paramref name="limit"/> (1 to <see cref="HistoryPage.MaxLimit"/>), with where the history ends. The cost is that of
    /// the slice, not of the history. A run that does not exist in <paramref name="run"/>'s scope reads as empty. The default reads the whole history; stores that can do better override it.
    /// </summary>
    async ValueTask<HistoryPage> GetHistoryAfterAsync(RunKey run, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        HistoryPage.Check(afterSequence, limit);
        return HistoryPage.After(await GetHistoryAsync(run, cancellationToken).ConfigureAwait(false), afterSequence, limit);
    }

    /// <summary>The last <paramref name="count"/> events of a run (1 to <see cref="HistoryPage.MaxLimit"/>), with where the history ends. The cost is that of the slice, not of the history.</summary>
    async ValueTask<HistoryPage> GetHistoryTailAsync(RunKey run, int count, CancellationToken cancellationToken)
    {
        HistoryPage.Check(0, count);
        return HistoryPage.Tail(await GetHistoryAsync(run, cancellationToken).ConfigureAwait(false), count);
    }

    /// <summary>
    /// Completes with true as soon as the run has an event after sequence number <paramref name="afterSequence"/>, or with false when <paramref name="wait"/> has passed without one (a run that does not exist never has).
    /// This is the wait for run updates, separate from the workers' wait for work: waiting for it never takes a wake-up a worker is waiting for. The default looks every 100 milliseconds; stores that are told
    /// of appends override it.
    /// </summary>
    async ValueTask<bool> WaitForHistoryAsync(RunKey run, long afterSequence, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        DateTime deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if ((await GetHistoryTailAsync(run, 1, cancellationToken).ConfigureAwait(false)).LastSequence > afterSequence)
            {
                return true;
            }

            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The latest run in the continue-as-new chain of <paramref name="workflowId"/>, or null.</summary>
    ValueTask<RunKey?> GetCurrentRunAsync(TenantScope scope, string workflowId, CancellationToken cancellationToken);

    /// <summary>Null when the run does not exist in this scope.</summary>
    ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken);

    /// <summary>
    /// The workflow id and workflow type of a run, or null when there is no such run in <paramref name="run"/>'s scope (another tenant's run is absent too). Both are fixed when the run starts, so deciding by them and then acting on the run is
    /// not a race. A store that cannot say throws <see cref="NotSupportedException"/>.
    /// </summary>
    ValueTask<RunIdentity?> GetRunIdentityAsync(RunKey run, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store cannot say what kind of run a run is.");}
