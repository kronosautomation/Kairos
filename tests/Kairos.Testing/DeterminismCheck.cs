namespace Kairos.Testing;

/// <summary>Thrown by <see cref="DeterminismCheck"/> when replaying recorded histories against your code fails. The message is the report: verdicts, ids, names and sequence numbers, never payload contents.</summary>
public sealed class ReplayCheckFailedException : Exception
{
    public ReplayCheckFailedException()
    {
    }

    public ReplayCheckFailedException(string message)
        : base(message)
    {
    }

    public ReplayCheckFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ReplayCheckFailedException(ReplayCheckReport report)
        : base(report.ToText())
    {
        Report = report;
    }

    /// <summary>The full report, when the failure came from a check.</summary>
    public ReplayCheckReport? Report { get; }
}

/// <summary>
/// The unit-test form of <c>kairos replay-check</c>: assert, in a test, that your workflow code still replays histories recorded by an earlier build. Keep a few
/// exported histories next to the tests (a corpus), or point it at a store holding real runs.
/// </summary>
public static class DeterminismCheck
{
    /// <summary>Replays every <c>.khist</c> history file under <paramref name="directory"/> against <paramref name="candidate"/>.</summary>
    /// <exception cref="ReplayCheckFailedException">A history diverged, could not be read, or none was found.</exception>
    public static async Task AssertCorpusReplaysAsync(WorkflowRegistry candidate, string directory, IPayloadProtector? protector = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var histories = new List<(string, IReadOnlyList<HistoryEvent>)>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.khist", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            histories.Add(($"file:{Path.GetRelativePath(directory, file)}", HistoryFile.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).History));
        }

        await AssertHistoriesReplayAsync(candidate, histories, protector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays the given histories against <paramref name="candidate"/>.</summary>
    /// <exception cref="ReplayCheckFailedException">A history diverged, could not be read, or none was given.</exception>
    public static async Task AssertHistoriesReplayAsync(
        WorkflowRegistry candidate, IEnumerable<(string Source, IReadOnlyList<HistoryEvent> History)> histories, IPayloadProtector? protector = null,
        CancellationToken cancellationToken = default)
    {
        ReplayCheckReport report = await new ReplayChecker(candidate, protector).CheckHistoriesAsync(histories, cancellationToken).ConfigureAwait(false);
        if (!report.Succeeded())
        {
            throw new ReplayCheckFailedException(report);
        }
    }

    /// <summary>Samples runs from <paramref name="store"/> (see <see cref="ReplayCheckOptions"/>) and replays them against <paramref name="candidate"/>.</summary>
    /// <exception cref="ReplayCheckFailedException">A history diverged, could not be read, nothing was replayed, or (with <paramref name="strictCoverage"/>) a workflow type or build in the store is missing from the candidate.</exception>
    public static async Task AssertStoreReplaysAsync(
        WorkflowRegistry candidate, IKairosStore store, TenantScope scope, ReplayCheckOptions? options = null, IPayloadProtector? protector = null,
        bool strictCoverage = false, CancellationToken cancellationToken = default)
    {
        ReplayCheckReport report = await new ReplayChecker(candidate, protector, options).CheckStoreAsync(store, scope, cancellationToken).ConfigureAwait(false);
        if (!report.Succeeded(strictCoverage))
        {
            throw new ReplayCheckFailedException(report);
        }
    }
}
