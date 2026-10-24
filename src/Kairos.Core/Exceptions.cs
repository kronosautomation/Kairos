namespace Kairos;

/// <summary>Replayed workflow code produced commands that do not match the recorded history.</summary>
public sealed class NonDeterminismException : Exception
{
    public NonDeterminismException()
    {
    }

    public NonDeterminismException(string message)
        : base(message)
    {
    }

    public NonDeterminismException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The hash chain of a history failed verification: tampering, reordering, truncation, or a gap.</summary>
public sealed class HistoryIntegrityException : Exception
{
    public HistoryIntegrityException()
    {
    }

    public HistoryIntegrityException(string message)
        : base(message)
    {
    }

    public HistoryIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HistoryIntegrityException(string message, long sequence)
        : base(message) => Sequence = sequence;

    /// <summary>Sequence number of the first event that failed verification (0 when not applicable).</summary>
    public long Sequence { get; }
}
