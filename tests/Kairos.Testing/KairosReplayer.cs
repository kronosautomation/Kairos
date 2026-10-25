namespace Kairos.Testing;

/// <summary>The result of replaying one history against local workflow code.</summary>
/// <param name="Report">The same report <c>kairos replay-check</c> prints: verdict, and for a divergence the first diverging command.</param>
/// <param name="Result">What the code would do next, beyond the recorded history (nothing for a closed run). Null when the replay did not run to the end.</param>
public sealed record ReplayOutcome(ReplayCheckReport Report, ReplayResult? Result)
{
    /// <summary>True when the code reproduced the recorded run.</summary>
    public bool Succeeded => Report.Succeeded();

    /// <summary>The report as text, the way <c>replay-check</c> prints it.</summary>
    public string Text => Report.ToText();
}

/// <summary>
/// Replays a run exported with <c>kairos history export</c> against the workflow code on your machine, under your debugger. The workflow runs in your process, so a
/// breakpoint in it is hit in the order the run originally took it. Activities are not executed: their recorded results are fed back. Payloads are opened with the
/// protector you pass, which should use your own identity with your key provider; the file holds ciphertext only, so nobody else's key is involved.
/// </summary>
/// <remarks>
/// The file is verified first (the whole hash chain), so a history that was altered after export is reported, not replayed. A divergence is reported exactly as
/// <c>replay-check</c> reports it: the first recorded event the code no longer matches, and what it did instead, by names, ids and sizes.
/// </remarks>
public static class KairosReplayer
{
    /// <summary>Replays the history file at <paramref name="path"/> with <paramref name="workflow"/> as the code for the run's workflow type and build.</summary>
    /// <exception cref="InvalidOperationException">The file is a metadata-only export: there is nothing to replay with.</exception>
    /// <exception cref="FormatException">The file is not a history file.</exception>
    public static async Task<ReplayOutcome> ReplayAsync(string path, WorkflowFunc workflow, IPayloadProtector? protector = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        IReadOnlyList<HistoryEvent> history = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(history, workflow, protector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays the history file at <paramref name="path"/> against the workflow in <paramref name="candidate"/> registered for its type and build.</summary>
    public static async Task<ReplayOutcome> ReplayAsync(string path, WorkflowRegistry candidate, IPayloadProtector? protector = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        IReadOnlyList<HistoryEvent> history = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return await RunAsync(Path.GetFileName(path), history, candidate, protector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays a history already in memory with <paramref name="workflow"/>.</summary>
    public static async Task<ReplayOutcome> ReplayAsync(
        IReadOnlyList<HistoryEvent> history, WorkflowFunc workflow, IPayloadProtector? protector = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(workflow);
        if (history.Count == 0 || history[0] is not WorkflowStarted started)
        {
            throw new ArgumentException("The history must begin with WorkflowStarted.", nameof(history));
        }

        var registry = new WorkflowRegistry().Register(started.WorkflowType, started.BuildId, workflow);
        return await RunAsync($"run:{started.RunId:D}", history, registry, protector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="ReplayAsync(string, WorkflowFunc, IPayloadProtector?, CancellationToken)"/> but throws <see cref="ReplayCheckFailedException"/> when the code diverges, for use in a test.</summary>
    public static async Task AssertReplaysAsync(string path, WorkflowFunc workflow, IPayloadProtector? protector = null, CancellationToken cancellationToken = default)
    {
        ReplayOutcome outcome = await ReplayAsync(path, workflow, protector, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            throw new ReplayCheckFailedException(outcome.Report);
        }
    }

    private static async Task<IReadOnlyList<HistoryEvent>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        HistoryFileContents contents = HistoryFile.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return contents.MetadataOnly
            ? throw new InvalidOperationException("This file is a metadata-only export: its payloads were removed, so there is nothing to replay with. Export again without --metadata-only.")
            : contents.History;
    }

    private static async Task<ReplayOutcome> RunAsync(
        string source, IReadOnlyList<HistoryEvent> history, WorkflowRegistry registry, IPayloadProtector? protector, CancellationToken cancellationToken)
    {
        var checker = new ReplayChecker(registry, protector);
        (ReplayFinding finding, ReplayResult? result) = await checker.ReplayAsync(source, history, cancellationToken).ConfigureAwait(false);
        return new ReplayOutcome(ReplayCheckReport.FromFindings([finding]), result);
    }
}
