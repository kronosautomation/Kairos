namespace Kairos;

/// <summary>Usage of one model, summed over a run.</summary>
public sealed record ModelUsage(string Model, int Steps, long InputTokens, long OutputTokens, long CachedInputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// What the model steps of a run consumed, from the usage recorded in their completion events. Tokens only: turning them into money is a matter of a price table, which
/// is applied to these figures later and so can change without touching history.
/// </summary>
public sealed record RunUsage(int Steps, long InputTokens, long OutputTokens, long CachedInputTokens, IReadOnlyList<ModelUsage> ByModel)
{
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>No usage recorded.</summary>
    public static RunUsage None { get; } = new(0, 0, 0, 0, []);

    /// <summary>Sums the usage recorded in <paramref name="history"/>. Events without usage (other activities, older histories) contribute nothing.</summary>
    public static RunUsage From(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var byModel = new SortedDictionary<string, (int Steps, long Input, long Output, long Cached)>(StringComparer.Ordinal);
        int steps = 0;
        long input = 0, output = 0, cached = 0;
        foreach (HistoryEvent e in history)
        {
            if (e is not ActivityCompleted { Usage: { } u })
            {
                continue;
            }

            steps++;
            input += u.InputTokens;
            output += u.OutputTokens;
            cached += u.CachedInputTokens;
            (int s, long i, long o, long c) = byModel.GetValueOrDefault(u.Model);
            byModel[u.Model] = (s + 1, i + u.InputTokens, o + u.OutputTokens, c + u.CachedInputTokens);
        }

        return steps == 0
            ? None
            : new RunUsage(steps, input, output, cached, [.. byModel.Select(m => new ModelUsage(m.Key, m.Value.Steps, m.Value.Input, m.Value.Output, m.Value.Cached))]);
    }
}
