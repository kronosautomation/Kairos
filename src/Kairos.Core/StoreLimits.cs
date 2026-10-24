namespace Kairos;

/// <summary>Resource limits a store enforces (bounded resources).</summary>
public sealed record StoreLimits
{
    /// <summary>
    /// Most events one run may hold. Completing a workflow task that would exceed it terminates the run with
    /// <c>HistoryLimitExceeded</c> instead; workflows are expected to continue as new long before this. Results, signals and
    /// timers that arrive while a task is in flight can overshoot by that many events, plus the boundary and termination records.
    /// </summary>
    public int MaxHistoryEvents { get; init; } = HistoryLimits.DefaultMaxEvents;
}
