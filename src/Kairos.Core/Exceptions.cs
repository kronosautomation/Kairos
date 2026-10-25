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

    /// <summary>A divergence located in the history: where it is and what differs. All text is made of names, ids and sizes, never payload contents.</summary>
    /// <param name="message">The full description.</param>
    /// <param name="sequence">Sequence number of the recorded event the replay diverged at (0 when it is the end of history).</param>
    /// <param name="recorded">What history says happened, for example <c>ActivityScheduled: activity 3 'Charge'</c>.</param>
    /// <param name="emitted">What the code did instead, for example <c>StartTimer: timer 1 for 00:00:05</c>, or <c>nothing</c>.</param>
    public NonDeterminismException(string message, long sequence, string recorded, string emitted)
        : base(message)
    {
        Sequence = sequence;
        Recorded = recorded;
        Emitted = emitted;
    }

    /// <summary>Sequence number of the recorded event the replay diverged at; 0 when not known or at the end of history.</summary>
    public long Sequence { get; }

    /// <summary>What history recorded at the point of divergence (names, ids and sizes only). Empty when not known.</summary>
    public string Recorded { get; } = "";

    /// <summary>What the replayed code did instead (names, ids and sizes only). Empty when not known.</summary>
    public string Emitted { get; } = "";
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
