namespace Kairos;

/// <summary>
/// Thrown by workflow code (<c>throw ctx.ContinueAsNew(input)</c>) to end this run and start a fresh one with a clean history.
/// Outstanding activities and timers of the old run are cancelled.
/// </summary>
public sealed class ContinueAsNewException : Exception
{
    public ContinueAsNewException(byte[] input)
        : base("The workflow continues as a new run.") => Input = input;

    public ContinueAsNewException()
        : this([])
    {
    }

    public ContinueAsNewException(string message)
        : base(message) => Input = [];

    public ContinueAsNewException(string message, Exception innerException)
        : base(message, innerException) => Input = [];

    public byte[] Input { get; }
}

/// <summary>History size policy. Histories are bounded: workflows continue as new well before the hard cap.</summary>
public static class HistoryLimits
{
    /// <summary>Hard cap per run. A run that would exceed it is terminated (fail closed), never allowed to grow without bound.</summary>
    public const int DefaultMaxEvents = 50_000;

    /// <summary><see cref="IWorkflowContext.ShouldContinueAsNew"/> turns true at this length (80% of the default cap).</summary>
    public const int ContinueAsNewSuggestedAt = 40_000;
}
