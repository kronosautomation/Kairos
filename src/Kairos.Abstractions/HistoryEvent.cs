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
}

public sealed record ActivityScheduled(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int ActivityId, string ActivityType, byte[] Input, RetryPolicy RetryPolicy)
    : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.ActivityScheduled;
}

public sealed record ActivityCompleted(
    Guid TenantId, Guid NamespaceId, Guid RunId, long Sequence, DateTimeOffset Timestamp,
    int ActivityId, byte[] Result) : HistoryEvent(TenantId, NamespaceId, RunId, Sequence, Timestamp)
{
    public override HistoryEventType Type => HistoryEventType.ActivityCompleted;
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
