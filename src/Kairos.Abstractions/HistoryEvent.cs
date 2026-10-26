namespace Kairos;

public enum HistoryEventType : byte
{
    WorkflowStarted = 1,
    ActivityScheduled = 2,
    ActivityCompleted = 3,
    ActivityFailed = 4,
    TimerStarted = 5,
    TimerFired = 6,
    SignalReceived = 7,
    WorkflowCompleted = 8,
    WorkflowFailed = 9,
    PatchMarker = 10,
    WorkflowTaskCompleted = 11,
    RunTerminated = 12,
    WorkflowContinued = 13,
}

/// <summary>
/// One immutable entry of a run's append-only history. <see cref="Hash"/> is SHA-256 over
/// <see cref="PrevHash"/> and the canonical encoding of the event (computed in Kairos.Core).
/// </summary>
public abstract record HistoryEvent(Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp)
{
    public abstract HistoryEventType Type { get; }

    /// <summary>Hash of the preceding event; 32 zero bytes for the first event.</summary>
    public byte[] PrevHash { get; init; } = [];

    /// <summary>Hash of this event chained to <see cref="PrevHash"/>.</summary>
    public byte[] Hash { get; init; } = [];
}

public sealed record WorkflowStarted(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    string WorkflowType, string BuildId, byte[] Input) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.WorkflowStarted;

    /// <summary>The run this one continues (continue-as-new); <see cref="Guid.Empty"/> for a fresh start.</summary>
    public Guid PreviousRunId { get; init; }

    /// <summary>
    /// W3C <c>traceparent</c> of the span that started the run, or empty. Kept in history so every task of the run, on any worker and after any
    /// restart, continues the same trace. It is readable metadata (random ids only), never derived from a payload.
    /// </summary>
    public string TraceContext { get; init; } = "";

    /// <summary>
    /// Who started the run: the authenticated principal the host verified, as <c>role:subject</c> (see <see cref="Principal.AuditName"/>), or empty when the start was not made by an
    /// authenticated caller (an embedded client, a bus message) or the run was written before this was recorded. It is readable metadata, like workflow names: an identifier
    /// the identity provider issued, never a payload. Only the host that verified the caller sets it, so a caller cannot choose it. A run that continues as new keeps it.
    /// </summary>
    public string StartedBy { get; init; } = "";

    /// <summary>Longest <see cref="StartedBy"/> that is recorded (a role name, a colon and a subject of at most 255 characters).</summary>
    public const int MaxStartedByLength = 280;
}

public sealed record ActivityScheduled(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int ActivityId, string ActivityType, byte[] Input, RetryPolicy RetryPolicy)
    : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.ActivityScheduled;

    /// <summary>W3C <c>traceparent</c> of the workflow task that scheduled the activity, or empty. Every attempt, retry and restart is a child of it.</summary>
    public string TraceContext { get; init; } = "";
}

public sealed record ActivityCompleted(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int ActivityId, byte[] Result) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.ActivityCompleted;

    /// <summary>
    /// What a model step consumed, recorded with the step that consumed it (null for every other activity, and for histories written before usage was recorded).
    /// Counts are readable metadata, like sizes: they are not sealed. Because the figure lives in the event, replaying a run never counts it again.
    /// </summary>
    public UsageRecord? Usage { get; init; }
}

/// <summary>Tokens consumed by one model step, as the model client reported them. Zero when the client did not say.</summary>
/// <param name="InputTokens">Tokens in the prompt, including any that were served from a cache.</param>
/// <param name="OutputTokens">Tokens in the reply.</param>
/// <param name="CachedInputTokens">The part of <paramref name="InputTokens"/> served from the provider's prompt cache.</param>
/// <param name="Model">The model that answered (its identifier as the provider reports it), the key a price table is looked up by. At most <see cref="MaxModelLength"/> characters.</param>
public sealed record UsageRecord(long InputTokens, long OutputTokens, long CachedInputTokens, string Model)
{
    /// <summary>Longest model identifier that is recorded; longer ones are cut.</summary>
    public const int MaxModelLength = 128;

    /// <summary>The sum of input and output tokens.</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Batch boundary, appended by the store each time a workflow task completes. All external events (start, results, timers,
/// signals) since the previous boundary were processed together by one run of workflow code; the command events that follow
/// are what that run produced. Replay reproduces the same batches, which is what makes it deterministic when several
/// events arrive between two tasks.
/// </summary>
public sealed record WorkflowTaskCompleted(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp)
    : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.WorkflowTaskCompleted;
}

/// <summary>
/// An operator closed the run from outside the workflow (<c>FailRunAsync</c>), for example a run parked after repeated lease
/// expiry. The run ends as failed; workflow code is not replayed to produce this event.
/// </summary>
public sealed record RunTerminated(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    string Reason) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.RunTerminated;
}

/// <summary>
/// The run closed itself and handed over to a fresh run of the same workflow id with a new, empty history
/// (<paramref name="NewRunId"/>), carrying <paramref name="Input"/> forward. This is how long-lived workflows stay bounded.
/// </summary>
public sealed record WorkflowContinued(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    Guid NewRunId, byte[] Input) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.WorkflowContinued;
}

/// <summary>Records that a patch (<see cref="IWorkflowContext.IsPatched"/>) was taken by this run, so replays take the same branch.</summary>
public sealed record PatchMarker(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    string PatchId) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.PatchMarker;
}

/// <summary>One failed attempt. When <see cref="WillRetry"/> is false the failure is terminal and reaches the workflow.</summary>
public sealed record ActivityFailed(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int ActivityId, int Attempt, string ErrorType, string Message, bool WillRetry)
    : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.ActivityFailed;

    /// <summary>
    /// What the failed attempt consumed before it failed, when the activity reported it (null otherwise, and always null for the lease-expiry record the store writes itself).
    /// A call that was paid for and then failed is still spent; recording it here keeps retried attempts from being invisible.
    /// </summary>
    public UsageRecord? Usage { get; init; }
}

public sealed record TimerStarted(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int TimerId, TimeSpan Duration) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.TimerStarted;
}

public sealed record TimerFired(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int TimerId) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.TimerFired;
}

public sealed record SignalReceived(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    string SignalName, byte[] Payload) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.SignalReceived;

    /// <summary>
    /// Client-generated id of this delivery. It authenticates the payload (equal-named signals are not interchangeable) and lets the
    /// store ignore a redelivered signal. Empty only in histories written before ids existed.
    /// </summary>
    public string SignalId { get; init; } = "";

    /// <summary>W3C <c>traceparent</c> of the span that sent the signal, or empty.</summary>
    public string TraceContext { get; init; } = "";
}

public sealed record WorkflowCompleted(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    byte[] Result) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.WorkflowCompleted;
}

public sealed record WorkflowFailed(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    string ErrorType, string Message) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.WorkflowFailed;
}
