namespace Kairos;

/// <summary>A per-namespace resource limit (noisy-neighbour protection): one namespace cannot take the whole deployment.</summary>
public enum NamespaceLimit
{
    /// <summary>Runs that are running at once in the namespace.</summary>
    OpenRuns,

    /// <summary>Timers that are waiting to fire in the namespace.</summary>
    PendingTimers,
}

/// <summary>
/// Starting a run would take the namespace past its limit of runs that are open at once. Nothing was stored. Retrying helps only after some runs have finished; the limit is
/// <c>StoreLimits.MaxOpenRunsPerNamespace</c>.
/// </summary>
public sealed class NamespaceLimitExceededException : InvalidOperationException
{
    public NamespaceLimitExceededException()
        : this(NamespaceLimit.OpenRuns, 0)
    {
    }

    public NamespaceLimitExceededException(string message)
        : base(message)
    {
    }

    public NamespaceLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NamespaceLimitExceededException(NamespaceLimit limit, int max)
        : base($"The namespace has reached its limit of {max} {(limit == NamespaceLimit.OpenRuns ? "open runs" : "pending timers")}.")
    {
        Limit = limit;
        Max = max;
    }

    /// <summary>Which limit was reached.</summary>
    public NamespaceLimit Limit { get; }

    /// <summary>The limit that was configured.</summary>
    public int Max { get; }
}
