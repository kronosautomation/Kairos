namespace Kairos;

/// <summary>Retry behaviour for a single activity. Immutable; backoff is exponential and capped.</summary>
public sealed record RetryPolicy
{
    public static RetryPolicy Default { get; } = new();

    public static RetryPolicy NoRetry { get; } = new() { MaximumAttempts = 1 };

    /// <summary>Total attempts including the first. Must be at least 1.</summary>
    public int MaximumAttempts { get; init; } = 3;

    public TimeSpan InitialInterval { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffCoefficient { get; init; } = 2.0;

    public TimeSpan MaximumInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Exception type names (<see cref="Type.Name"/>) that are never retried.</summary>
    public IReadOnlyList<string> NonRetryableErrorTypes { get; init; } = [];

    public bool Equals(RetryPolicy? other) =>
        other is not null &&
        MaximumAttempts == other.MaximumAttempts &&
        InitialInterval == other.InitialInterval &&
        BackoffCoefficient.Equals(other.BackoffCoefficient) &&
        MaximumInterval == other.MaximumInterval &&
        NonRetryableErrorTypes.SequenceEqual(other.NonRetryableErrorTypes);

    public override int GetHashCode() =>
        HashCode.Combine(MaximumAttempts, InitialInterval, BackoffCoefficient, MaximumInterval, NonRetryableErrorTypes.Count);

    /// <summary>Delay to wait after the given failed attempt (1-based) before the next one.</summary>
    public TimeSpan GetBackoff(int failedAttempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        double ticks = InitialInterval.Ticks * Math.Pow(BackoffCoefficient, failedAttempt - 1);
        return ticks >= MaximumInterval.Ticks ? MaximumInterval : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>True when another attempt may follow the given failed attempt (1-based) of the given error type.</summary>
    public bool ShouldRetry(int failedAttempt, string errorType)
    {
        if (failedAttempt >= MaximumAttempts)
        {
            return false;
        }

        for (int i = 0; i < NonRetryableErrorTypes.Count; i++)
        {
            if (string.Equals(NonRetryableErrorTypes[i], errorType, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
