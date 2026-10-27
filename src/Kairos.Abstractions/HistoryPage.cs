namespace Kairos;

/// <summary>A slice of one run's history, in the form the store holds it, and where the run's history ends.</summary>
/// <param name="Events">The events of the slice, in order of sequence number. Empty for a run that does not exist in the scope asked, and for a slice past the end.</param>
/// <param name="LastSequence">The sequence number of the run's last event when the slice was read (0 for a run that does not exist). More events than the slice holds exist when this is greater than the slice's last.</param>
public sealed record HistoryPage(IReadOnlyList<HistoryEvent> Events, long LastSequence)
{
    /// <summary>The most events one page may hold.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The page of <paramref name="history"/> (a whole history, sequence numbers 1..n) after <paramref name="afterSequence"/>: what a store without a cheaper way returns.</summary>
    public static HistoryPage After(IReadOnlyList<HistoryEvent> history, long afterSequence, int limit)
    {
        ArgumentNullException.ThrowIfNull(history);
        Check(afterSequence, limit);
        int skip = (int)Math.Min(afterSequence, history.Count);
        int take = Math.Min(limit, history.Count - skip);
        var events = new HistoryEvent[take];
        for (int i = 0; i < take; i++)
        {
            events[i] = history[skip + i];
        }

        return new HistoryPage(events, history.Count == 0 ? 0 : history[^1].Sequence);
    }

    /// <summary>The page that holds the last <paramref name="count"/> events of <paramref name="history"/>.</summary>
    public static HistoryPage Tail(IReadOnlyList<HistoryEvent> history, int count)
    {
        ArgumentNullException.ThrowIfNull(history);
        Check(0, count);
        return After(history, Math.Max(0, history.Count - count), count);
    }

    /// <summary>Rejects arguments no store accepts.</summary>
    public static void Check(long afterSequence, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
    }
}
