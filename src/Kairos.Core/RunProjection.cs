namespace Kairos;

/// <summary>One day and model of a run's model usage, as <see cref="RunProjection"/> reads it from the history.</summary>
/// <param name="Day">The UTC day of the recorded attempt.</param>
/// <param name="Model">The model that answered (as the provider reports it).</param>
/// <param name="Steps">Model steps that completed.</param>
/// <param name="FailedAttempts">Attempts that were paid for and then failed.</param>
/// <param name="InputTokens">Input tokens of both, cached ones included.</param>
/// <param name="OutputTokens">Output tokens of both.</param>
/// <param name="CachedInputTokens">The part of <paramref name="InputTokens"/> served from the provider's cache.</param>
/// <param name="SavedInputTokens">Of <paramref name="InputTokens"/>, those of completed steps that had completed before a later failure of the run, and so were not paid for again when it carried on.</param>
/// <param name="SavedOutputTokens">The same for output tokens.</param>
/// <param name="SavedCachedInputTokens">The same for cached input tokens.</param>
public sealed record RunUsageDay(
    DateOnly Day, string Model, long Steps, long FailedAttempts, long InputTokens, long OutputTokens, long CachedInputTokens,
    long SavedInputTokens, long SavedOutputTokens, long SavedCachedInputTokens);

/// <summary>An approval a run asked for and that nobody has resolved.</summary>
/// <param name="ApprovalId">The id the agent runtime gave the approval (also in the name of the signal that decides it).</param>
/// <param name="RequestedAt">When the request was recorded.</param>
public sealed record RunPendingApproval(string ApprovalId, DateTimeOffset RequestedAt);

/// <summary>What the server can say about one run without opening anything.</summary>
/// <param name="LastSequence">The last event that was read.</param>
/// <param name="Usage">Model usage by day and model.</param>
/// <param name="Pending">Approvals the run is still waiting for (none once the run has closed).</param>
public sealed record RunProjectionResult(long LastSequence, IReadOnlyList<RunUsageDay> Usage, IReadOnlyList<RunPendingApproval> Pending);

/// <summary>
/// Reads the facts the server answers aggregate questions from out of one run's history: model usage (readable counts the worker records with the step) and the approvals the run is waiting for
/// (readable markers the agent runtime leaves, see <see cref="ApprovalRequestedMarker"/>). It reads no payload, so it works on sealed histories and on metadata-only ones.
/// A store that projects keeps the result per run and replaces it as a whole, so the aggregate always equals what scanning the histories gives.
/// </summary>
public static class RunProjection
{
    /// <summary>
    /// The prefix of the patch marker an agent leaves when it asks for an approval; the rest is the approval id. Patch markers are readable and replay-neutral, which makes them the place for
    /// a fact the server must know and cannot read from a sealed payload.
    /// </summary>
    public const string ApprovalRequestedMarker = "kairos.approval.requested:";

    /// <summary>The prefix of the marker left when the approval ended: decided (accepted), timed out, or its run was told to stop.</summary>
    public const string ApprovalResolvedMarker = "kairos.approval.resolved:";

    /// <summary>Projects <paramref name="history"/> (a verified history of one run, sealed or opened, full or metadata only).</summary>
    public static RunProjectionResult Of(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        long lastFailure = 0;
        foreach (HistoryEvent e in history)
        {
            if (e is ActivityFailed)
            {
                lastFailure = e.Sequence;
            }
        }

        var usage = new Dictionary<(DateOnly Day, string Model), long[]>();
        var pending = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (HistoryEvent e in history)
        {
            switch (e)
            {
                case ActivityCompleted { Usage: { } u } done:
                    {
                        long[] row = Row(usage, done.Timestamp, u.Model);
                        row[0]++;
                        row[2] += u.InputTokens;
                        row[3] += u.OutputTokens;
                        row[4] += u.CachedInputTokens;
                        if (done.Sequence < lastFailure)
                        {
                            row[5] += u.InputTokens;
                            row[6] += u.OutputTokens;
                            row[7] += u.CachedInputTokens;
                        }

                        break;
                    }

                case ActivityFailed { Usage: { } u } failed:
                    {
                        long[] row = Row(usage, failed.Timestamp, u.Model);
                        row[1]++;
                        row[2] += u.InputTokens;
                        row[3] += u.OutputTokens;
                        row[4] += u.CachedInputTokens;
                        break;
                    }

                case PatchMarker m when m.PatchId.StartsWith(ApprovalRequestedMarker, StringComparison.Ordinal):
                    pending[m.PatchId[ApprovalRequestedMarker.Length..]] = m.Timestamp;
                    break;
                case PatchMarker m when m.PatchId.StartsWith(ApprovalResolvedMarker, StringComparison.Ordinal):
                    pending.Remove(m.PatchId[ApprovalResolvedMarker.Length..]);
                    break;
            }
        }

        if (history.Count > 0 && history[^1] is WorkflowCompleted or WorkflowFailed or WorkflowContinued or RunTerminated)
        {
            pending.Clear(); // a closed run waits for nobody
        }

        return new RunProjectionResult(
            history.Count == 0 ? 0 : history[^1].Sequence,
            [.. usage.OrderBy(p => p.Key.Day).ThenBy(p => p.Key.Model, StringComparer.Ordinal).Select(p =>
                new RunUsageDay(p.Key.Day, p.Key.Model, p.Value[0], p.Value[1], p.Value[2], p.Value[3], p.Value[4], p.Value[5], p.Value[6], p.Value[7]))],
            [.. pending.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new RunPendingApproval(p.Key, p.Value))]);
    }

    // [steps, failed, input, output, cached, savedInput, savedOutput, savedCached]
    private static long[] Row(Dictionary<(DateOnly, string), long[]> rows, DateTimeOffset at, string model)
    {
        (DateOnly, string) key = (DateOnly.FromDateTime(at.UtcDateTime), model);
        if (!rows.TryGetValue(key, out long[]? row))
        {
            rows[key] = row = new long[8];
        }

        return row;
    }
}
