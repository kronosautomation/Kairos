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

    /// <summary>
    /// Most runs that may be running at once in one namespace (default 100,000). Starting one more throws <see cref="NamespaceLimitExceededException"/> and stores nothing. The PostgreSQL store checks against a
    /// count it refreshes every <see cref="NamespaceCountCacheTtl"/> (and whenever the cached count says the limit is near), so concurrent starters on several nodes can overshoot by what they start in that
    /// window; the in-memory store counts exactly.
    /// </summary>
    public int MaxOpenRunsPerNamespace { get; init; } = DefaultMaxOpenRunsPerNamespace;

    /// <summary>
    /// Most timers that may be waiting to fire in one namespace (default 100,000). A workflow task that would start timers beyond it ends the run (<c>TimerLimitExceeded</c>), the same way the history cap does,
    /// so that one namespace's timers cannot swamp the deployment's timer sweep. Checked like <see cref="MaxOpenRunsPerNamespace"/>.
    /// </summary>
    public int MaxPendingTimersPerNamespace { get; init; } = DefaultMaxPendingTimersPerNamespace;

    /// <summary>How long the PostgreSQL store trusts a namespace's counted runs and timers before it counts again. Default five seconds.</summary>
    public TimeSpan NamespaceCountCacheTtl { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The default for <see cref="MaxOpenRunsPerNamespace"/>.</summary>
    public const int DefaultMaxOpenRunsPerNamespace = 100_000;

    /// <summary>The default for <see cref="MaxPendingTimersPerNamespace"/>.</summary>
    public const int DefaultMaxPendingTimersPerNamespace = 100_000;
}
