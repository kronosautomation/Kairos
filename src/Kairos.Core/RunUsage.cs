namespace Kairos;

/// <summary>A count of attempts and the tokens they consumed.</summary>
public sealed record UsageTotals(int Attempts, long InputTokens, long OutputTokens, long CachedInputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;

    public static UsageTotals None { get; } = new(0, 0, 0, 0);

    public static UsageTotals operator +(UsageTotals a, UsageTotals b) =>
        new(a.Attempts + b.Attempts, a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CachedInputTokens + b.CachedInputTokens);
}

/// <summary>Usage of one model, summed over a run: the steps that completed, and separately the attempts that failed after consuming tokens.</summary>
public sealed record ModelUsage(string Model, int Steps, long InputTokens, long OutputTokens, long CachedInputTokens, UsageTotals? SpentOnFailedAttempts = null)
{
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>Attempts of this model that failed after reporting usage. They were paid for even though they produced no result.</summary>
    public UsageTotals Failed => SpentOnFailedAttempts ?? UsageTotals.None;

    /// <summary>Completed steps and failed attempts together: what was consumed.</summary>
    public UsageTotals Total => new UsageTotals(Steps, InputTokens, OutputTokens, CachedInputTokens) + Failed;
}

/// <summary>
/// What the model steps of a run consumed, from the usage recorded in their completion and failure events. Tokens only: turning them into money is a matter of a price
/// table, which is applied to these figures later and so can change without touching history.
/// </summary>
/// <param name="Steps">Model steps that completed.</param>
/// <param name="InputTokens">Input tokens of the completed steps.</param>
/// <param name="OutputTokens">Output tokens of the completed steps.</param>
/// <param name="CachedInputTokens">Cached input tokens of the completed steps.</param>
/// <param name="ByModel">The same, per model, with the failed attempts of each.</param>
/// <param name="SpentOnFailedAttempts">What attempts that failed (and were retried or ended the step) consumed before failing. Kept apart so a report can show waste; add it to the completed figures for what was actually consumed (<see cref="Total"/>).</param>
public sealed record RunUsage(
    int Steps, long InputTokens, long OutputTokens, long CachedInputTokens, IReadOnlyList<ModelUsage> ByModel, UsageTotals? SpentOnFailedAttempts = null)
{
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>Attempts that failed after reporting usage, summed. Zero when none did.</summary>
    public UsageTotals Failed => SpentOnFailedAttempts ?? UsageTotals.None;

    /// <summary>Everything consumed: completed steps plus failed attempts.</summary>
    public UsageTotals Total => new UsageTotals(Steps, InputTokens, OutputTokens, CachedInputTokens) + Failed;

    /// <summary>No usage recorded.</summary>
    public static RunUsage None { get; } = new(0, 0, 0, 0, []);

    /// <summary>Sums the usage recorded in <paramref name="history"/>. Events without usage (other activities, attempts that reported none, older histories) contribute nothing.</summary>
    public static RunUsage From(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var byModel = new SortedDictionary<string, (UsageTotals Done, UsageTotals Failed)>(StringComparer.Ordinal);
        UsageTotals done = UsageTotals.None, failed = UsageTotals.None;
        foreach (HistoryEvent e in history)
        {
            (UsageRecord? usage, bool isFailure) = e switch
            {
                ActivityCompleted { Usage: { } u } => (u, false),
                ActivityFailed { Usage: { } u } => (u, true),
                _ => (null, false),
            };
            if (usage is null)
            {
                continue;
            }

            var one = new UsageTotals(1, usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens);
            (UsageTotals modelDone, UsageTotals modelFailed) = byModel.GetValueOrDefault(usage.Model, (UsageTotals.None, UsageTotals.None));
            if (isFailure)
            {
                failed += one;
                byModel[usage.Model] = (modelDone, modelFailed + one);
            }
            else
            {
                done += one;
                byModel[usage.Model] = (modelDone + one, modelFailed);
            }
        }

        return done.Attempts == 0 && failed.Attempts == 0
            ? None
            : new RunUsage(
                done.Attempts, done.InputTokens, done.OutputTokens, done.CachedInputTokens,
                [.. byModel.Select(m => new ModelUsage(m.Key, m.Value.Done.Attempts, m.Value.Done.InputTokens, m.Value.Done.OutputTokens, m.Value.Done.CachedInputTokens, m.Value.Failed))],
                failed);
    }
}
