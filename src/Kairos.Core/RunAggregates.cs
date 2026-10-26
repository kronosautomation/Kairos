namespace Kairos;

/// <summary>
/// The answers to aggregate questions, worked out from the runs' histories by <see cref="RunProjection"/>. <see cref="ScanUsageAsync"/> and <see cref="ScanPendingAsync"/> read every run: they are the
/// reference a projecting store is tested against, and the answer of stores that do not project.
/// </summary>
public static class RunAggregates
{
    private const int ListPage = 500;

    /// <summary>Folds per-run projections into the summary for the days <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static UsageSummary Combine(IEnumerable<(string WorkflowType, RunProjectionResult Projection)> runs, DateOnly from, DateOnly to, int behind = 0)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var buckets = new Dictionary<(string, DateOnly, string), long[]>();
        var counted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string type, RunProjectionResult projection) in runs)
        {
            bool any = false;
            foreach (RunUsageDay d in projection.Usage.Where(d => d.Day >= from && d.Day <= to))
            {
                any = true;
                if (!buckets.TryGetValue((type, d.Day, d.Model), out long[]? row))
                {
                    buckets[(type, d.Day, d.Model)] = row = new long[8];
                }

                row[0] += d.Steps;
                row[1] += d.FailedAttempts;
                row[2] += d.InputTokens;
                row[3] += d.OutputTokens;
                row[4] += d.CachedInputTokens;
                row[5] += d.SavedInputTokens;
                row[6] += d.SavedOutputTokens;
                row[7] += d.SavedCachedInputTokens;
            }

            if (any)
            {
                counted[type] = counted.GetValueOrDefault(type) + 1;
            }
        }

        return new UsageSummary(
            [.. buckets.OrderBy(b => b.Key.Item1, StringComparer.Ordinal).ThenBy(b => b.Key.Item2).ThenBy(b => b.Key.Item3, StringComparer.Ordinal).Select(b =>
                new UsageBucket(b.Key.Item1, b.Key.Item2, b.Key.Item3, b.Value[0], b.Value[1], b.Value[2], b.Value[3], b.Value[4], b.Value[5], b.Value[6], b.Value[7]))],
            [.. counted.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new WorkflowTypeRuns(c.Key, c.Value))],
            behind);
    }

    /// <summary>The approvals waiting, oldest first, after <paramref name="after"/>, at most <paramref name="limit"/>.</summary>
    public static PendingApprovalPage Page(IEnumerable<PendingApprovalItem> waiting, int limit, PendingApprovalCursor? after, int behind = 0)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        PendingApprovalItem[] all = [.. waiting.OrderBy(w => w.RequestedAt).ThenBy(w => w.ApprovalId, StringComparer.Ordinal)];
        IEnumerable<PendingApprovalItem> rest = after is { } c
            ? all.Where(w => w.RequestedAt > c.RequestedAt || (w.RequestedAt == c.RequestedAt && string.CompareOrdinal(w.ApprovalId, c.ApprovalId) > 0))
            : all;
        return new PendingApprovalPage([.. rest.Take(limit)], all.Length, behind);
    }

    /// <summary>Reads every run of the scope and answers <see cref="IKairosStore.GetUsageSummaryAsync"/> by scanning.</summary>
    public static async ValueTask<UsageSummary> ScanUsageAsync(IKairosStore store, TenantScope scope, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var projections = new List<(string, RunProjectionResult)>();
        await foreach ((RunSummary run, IReadOnlyList<HistoryEvent> history) in ReadAllAsync(store, scope, cancellationToken).ConfigureAwait(false))
        {
            projections.Add((run.WorkflowType, RunProjection.Of(history)));
        }

        return Combine(projections, from, to);
    }

    /// <summary>Reads every run of the scope and answers <see cref="IKairosStore.GetPendingApprovalsAsync"/> by scanning.</summary>
    public static async ValueTask<PendingApprovalPage> ScanPendingAsync(IKairosStore store, TenantScope scope, int limit, PendingApprovalCursor? after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var waiting = new List<PendingApprovalItem>();
        await foreach ((RunSummary run, IReadOnlyList<HistoryEvent> history) in ReadAllAsync(store, scope, cancellationToken).ConfigureAwait(false))
        {
            foreach (RunPendingApproval p in RunProjection.Of(history).Pending)
            {
                waiting.Add(new PendingApprovalItem(run.Run, run.WorkflowType, p.ApprovalId, p.RequestedAt));
            }
        }

        return Page(waiting, limit, after);
    }

    private static async IAsyncEnumerable<(RunSummary Run, IReadOnlyList<HistoryEvent> History)> ReadAllAsync(
        IKairosStore store, TenantScope scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RunCursor? after = null;
        while (true)
        {
            IReadOnlyList<RunSummary> page = await store.ListRunsAsync(scope, new RunFilter { After = after }, ListPage, cancellationToken).ConfigureAwait(false);
            foreach (RunSummary run in page)
            {
                yield return (run, await store.GetHistoryAsync(run.Run, cancellationToken).ConfigureAwait(false));
            }

            if (page.Count < ListPage)
            {
                yield break;
            }

            after = page[^1].Cursor;
        }
    }
}
