namespace Kairos;

/// <summary>How replaying one recorded history against a candidate build turned out.</summary>
public enum ReplayVerdict
{
    /// <summary>The candidate code reproduces the recorded run.</summary>
    Passed,

    /// <summary>The candidate code does something other than what was recorded. <see cref="ReplayFinding.Divergence"/> says where.</summary>
    Nondeterministic,

    /// <summary>The hash chain does not verify: the stored history was altered, truncated or reordered.</summary>
    IntegrityFailure,

    /// <summary>The payloads could not be opened (no payload protector configured for sealed data, or the key provider refused). Nothing was replayed.</summary>
    Unreadable,

    /// <summary>The candidate build does not contain this workflow type and build, so it will never run this history. Reported, not replayed.</summary>
    NotCovered,

    /// <summary>The replay failed for another reason (the candidate threw while being replayed). <see cref="ReplayFinding.Detail"/> names the exception type.</summary>
    Error,
}

/// <summary>The first point where candidate code departed from a recorded history. Names, ids and sizes only: never payload contents.</summary>
/// <param name="Sequence">Sequence number of the recorded event (0 at the end of history).</param>
/// <param name="Recorded">What history says happened.</param>
/// <param name="Emitted">What the candidate did instead.</param>
public sealed record ReplayDivergence(long Sequence, string Recorded, string Emitted);

/// <summary>The result for one history. <see cref="Detail"/> is a fixed phrase or an exception type name, never data from the history.</summary>
/// <param name="Source">Where the history came from: <c>run:{id}</c> or <c>file:{name}</c>.</param>
/// <param name="WorkflowType">The workflow type recorded in the history.</param>
/// <param name="BuildId">The build the run is pinned to.</param>
/// <param name="Verdict">The outcome.</param>
/// <param name="Events">Number of events in the history.</param>
/// <param name="Divergence">Set for <see cref="ReplayVerdict.Nondeterministic"/> and, with the sequence, for <see cref="ReplayVerdict.IntegrityFailure"/>.</param>
/// <param name="Detail">A short fixed explanation.</param>
public sealed record ReplayFinding(string Source, string WorkflowType, string BuildId, ReplayVerdict Verdict, int Events, ReplayDivergence? Divergence, string Detail)
{
    public bool IsFailure => Verdict is ReplayVerdict.Nondeterministic or ReplayVerdict.IntegrityFailure or ReplayVerdict.Unreadable or ReplayVerdict.Error;
}

/// <summary>What the store holds for one workflow type and build, and how much of it was replayed.</summary>
public sealed record ReplayCoverage(string WorkflowType, string BuildId, int Open, int Closed, int Checked, bool InCandidate);

/// <summary>Everything a replay check found.</summary>
public sealed class ReplayCheckReport
{
    internal ReplayCheckReport(IReadOnlyList<ReplayFinding> findings, IReadOnlyList<ReplayCoverage> coverage)
    {
        Findings = findings;
        Coverage = coverage;
    }

    /// <summary>A report made of findings only (no store coverage).</summary>
    public static ReplayCheckReport FromFindings(IEnumerable<ReplayFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return new ReplayCheckReport([.. findings], []);
    }

    /// <summary>One report for several checks (for example a store and a directory of history files).</summary>
    public static ReplayCheckReport Combine(IEnumerable<ReplayCheckReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ReplayCheckReport[] all = [.. reports];
        return new ReplayCheckReport([.. all.SelectMany(r => r.Findings)], [.. all.SelectMany(r => r.Coverage)]);
    }

    public IReadOnlyList<ReplayFinding> Findings { get; }

    /// <summary>One entry per workflow type and build found in the store (empty for a check of loose histories).</summary>
    public IReadOnlyList<ReplayCoverage> Coverage { get; }

    /// <summary>Histories actually replayed (everything except <see cref="ReplayVerdict.NotCovered"/>).</summary>
    public int Checked => Findings.Count(f => f.Verdict != ReplayVerdict.NotCovered);

    public IReadOnlyList<ReplayFinding> Failures => [.. Findings.Where(f => f.IsFailure)];

    /// <summary>Workflow type and build pairs the store has runs of that the candidate does not contain.</summary>
    public IReadOnlyList<ReplayCoverage> Uncovered => [.. Coverage.Where(c => !c.InCandidate)];

    /// <summary>
    /// True when nothing failed, something was replayed (a check that saw nothing proves nothing), and, with <paramref name="strictCoverage"/>, every workflow type and build in
    /// the store is also in the candidate.
    /// </summary>
    public bool Succeeded(bool strictCoverage = false) =>
        Failures.Count == 0 && Checked > 0 && (!strictCoverage || Uncovered.Count == 0 && Findings.All(f => f.Verdict != ReplayVerdict.NotCovered));
}

/// <summary>How much to replay.</summary>
public sealed class ReplayCheckOptions
{
    /// <summary>
    /// Histories to replay per workflow type, build and state (open or closed): the newest ones. Open runs are the ones the candidate would take over, so they are
    /// sampled separately from closed ones. <see cref="int.MaxValue"/> replays everything.
    /// </summary>
    public int SamplePerGroup { get; init; } = 25;

    /// <summary>Closed runs started before this are not sampled. Open runs always are.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Histories replayed at the same time.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>How many runs one store listing returns.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>Stop reading a history longer than this many events and report it as an error (a guard for huge runs).</summary>
    public int MaxEvents { get; init; } = 100_000;
}

/// <summary>
/// Replays recorded histories against a candidate build and reports where the candidate would diverge. This is the deploy safety gate: run it where the data lives (a pipeline job
/// with the right to open payloads), so production payloads never leave. Output is verdicts, ids, names and sequence numbers: no payload content is ever copied into a result.
/// </summary>
/// <param name="candidate">The workflow code of the build under test, registered per type and build.</param>
/// <param name="protector">Opens sealed payloads. Null when the application does not use client-side encryption.</param>
/// <param name="options">How much to replay.</param>
public sealed class ReplayChecker(WorkflowRegistry candidate, IPayloadProtector? protector = null, ReplayCheckOptions? options = null)
{
    private readonly ReplayCheckOptions _options = options ?? new ReplayCheckOptions();
    private readonly PayloadCrypto? _crypto = protector is null ? null : new PayloadCrypto(protector);

    /// <summary>
    /// Samples the runs of one tenant scope: for every workflow type and build, the newest <see cref="ReplayCheckOptions.SamplePerGroup"/> open runs and the same number of
    /// closed ones, and replays each. Workflow types and builds the candidate does not contain are listed, not replayed.
    /// </summary>
    /// <exception cref="NotSupportedException">The store cannot list runs.</exception>
    public async Task<ReplayCheckReport> CheckStoreAsync(IKairosStore store, TenantScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<RunGroup> groups = await store.GetRunGroupsAsync(scope, _options.Since, cancellationToken).ConfigureAwait(false);

        var findings = new List<ReplayFinding>();
        var coverage = new List<ReplayCoverage>();
        foreach (RunGroup group in groups)
        {
            bool covered = candidate.TryGet(group.WorkflowType, group.BuildId, out _);
            if (!covered)
            {
                coverage.Add(new ReplayCoverage(group.WorkflowType, group.BuildId, group.Open, group.Closed, 0, InCandidate: false));
                findings.Add(new ReplayFinding(
                    $"group:{group.WorkflowType}/{group.BuildId}", group.WorkflowType, group.BuildId, ReplayVerdict.NotCovered, 0, null,
                    $"{group.Open} open and {group.Closed} closed run(s) of a workflow type and build the candidate does not contain"));
                continue;
            }

            var runs = new List<RunSummary>();
            foreach (RunStateFilter state in new[] { RunStateFilter.Open, RunStateFilter.Closed })
            {
                runs.AddRange(await SampleAsync(store, scope, group, state, cancellationToken).ConfigureAwait(false));
            }

            var results = new ReplayFinding[runs.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, runs.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = cancellationToken },
                async (i, ct) =>
                {
                    IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(runs[i].Run, ct).ConfigureAwait(false);
                    results[i] = await CheckAsync($"run:{runs[i].Run.RunId:D}", history, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            findings.AddRange(results);
            coverage.Add(new ReplayCoverage(group.WorkflowType, group.BuildId, group.Open, group.Closed, results.Length, InCandidate: true));
        }

        return new ReplayCheckReport(findings, coverage);
    }

    /// <summary>Replays histories that were read from files or exported, for example a corpus kept next to the code.</summary>
    public async Task<ReplayCheckReport> CheckHistoriesAsync(IEnumerable<(string Source, IReadOnlyList<HistoryEvent> History)> histories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(histories);
        var findings = new List<ReplayFinding>();
        foreach ((string source, IReadOnlyList<HistoryEvent> history) in histories)
        {
            findings.Add(await CheckAsync(source, history, cancellationToken).ConfigureAwait(false));
        }

        return new ReplayCheckReport(findings, []);
    }

    /// <summary>Replays one history against the candidate.</summary>
    public async ValueTask<ReplayFinding> CheckAsync(string source, IReadOnlyList<HistoryEvent> history, CancellationToken cancellationToken) =>
        (await ReplayAsync(source, history, cancellationToken).ConfigureAwait(false)).Finding;

    /// <summary>
    /// Replays one history against the candidate and also returns what the code would do next (the commands beyond the recorded history), which is empty for a closed run.
    /// The workflow code runs once, in this process, so a debugger attached to it stops at its breakpoints in the order the run originally took them.
    /// </summary>
    public async ValueTask<(ReplayFinding Finding, ReplayResult? Result)> ReplayAsync(string source, IReadOnlyList<HistoryEvent> history, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[0] is not WorkflowStarted started)
        {
            return (new ReplayFinding(source, "", "", ReplayVerdict.Error, history.Count, null, "the history does not begin with WorkflowStarted"), null);
        }

        string type = started.WorkflowType;
        string build = started.BuildId;
        ReplayFinding Finding(ReplayVerdict verdict, ReplayDivergence? divergence, string detail) => new(source, type, build, verdict, history.Count, divergence, detail);

        if (!candidate.TryGet(type, build, out WorkflowFunc? workflow))
        {
            return (Finding(ReplayVerdict.NotCovered, null, "the candidate does not contain this workflow type and build"), null);
        }

        if (history.Count > _options.MaxEvents)
        {
            return (Finding(ReplayVerdict.Error, null, $"the history has {history.Count} events, more than the limit of {_options.MaxEvents}"), null);
        }

        try
        {
            HashChain.Verify(history);
        }
        catch (HistoryIntegrityException ex)
        {
            return (Finding(ReplayVerdict.IntegrityFailure, new ReplayDivergence(ex.Sequence, "the stored hash chain", "a different hash"), "the hash chain does not verify"), null);
        }

        IReadOnlyList<HistoryEvent> readable = history;
        if (_crypto is null)
        {
            if (history.Any(IsSealedText))
            {
                return (Finding(ReplayVerdict.Unreadable, null, "the history is sealed (client-side encryption) but no payload protector is configured"), null);
            }
        }
        else
        {
            try
            {
                readable = await _crypto.OpenHistoryAsync(history, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Finding(ReplayVerdict.Unreadable, null, $"the payloads could not be opened ({ex.GetType().Name})"), null);
            }
        }

        try
        {
            ReplayResult result = WorkflowReplayer.Replay(workflow, readable, verifyHashChain: false);
            return (Finding(ReplayVerdict.Passed, null, ""), result);
        }
        catch (NonDeterminismException ex)
        {
            // The structured fields are names, ids and sizes. Older messages carry no structure; they are fixed text built by the engine.
            var divergence = new ReplayDivergence(ex.Sequence, ex.Recorded.Length > 0 ? ex.Recorded : ex.Message, ex.Emitted.Length > 0 ? ex.Emitted : "see recorded");
            return (Finding(ReplayVerdict.Nondeterministic, divergence, "the candidate diverges from the recorded history"), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Finding(ReplayVerdict.Error, null, $"the replay failed with {ex.GetType().Name}"), null);
        }
    }

    private async Task<List<RunSummary>> SampleAsync(IKairosStore store, TenantScope scope, RunGroup group, RunStateFilter state, CancellationToken cancellationToken)
    {
        var taken = new List<RunSummary>();
        long want = _options.SamplePerGroup;
        RunCursor? cursor = null;
        while (taken.Count < want)
        {
            int page = (int)Math.Min(_options.PageSize, want - taken.Count);
            IReadOnlyList<RunSummary> runs = await store.ListRunsAsync(
                scope, new RunFilter { WorkflowType = group.WorkflowType, BuildId = group.BuildId, State = state, StartedSince = _options.Since, After = cursor }, page, cancellationToken)
                .ConfigureAwait(false);
            if (runs.Count == 0)
            {
                break;
            }

            taken.AddRange(runs);
            cursor = runs[^1].Cursor;
        }

        return taken;
    }

    private static bool IsSealedText(HistoryEvent e) => e switch
    {
        WorkflowFailed f => f.Message.StartsWith("enc:", StringComparison.Ordinal),
        ActivityFailed f => f.Message.StartsWith("enc:", StringComparison.Ordinal),
        _ => false,
    };
}
