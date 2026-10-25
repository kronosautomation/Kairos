using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Cli;

/// <summary>Exit codes of <c>kairos replay-check</c>.</summary>
internal static class ExitCode
{
    /// <summary>Every sampled history replayed.</summary>
    public const int Passed = 0;

    /// <summary>At least one history diverged, failed its integrity check, could not be opened, or errored; or nothing was replayed; or (strict) coverage is incomplete.</summary>
    public const int Failed = 1;

    /// <summary>The command was used wrongly or could not run (bad option, no connection, build could not be loaded, store cannot list).</summary>
    public const int Usage = 2;
}

internal static class ReplayCheckCommand
{
    public static async Task<int> RunAsync(
        ReplayCheckArguments args, IKairosStore? store, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        Candidate candidate;
        try
        {
            candidate = CandidateLoader.Load(args.Build, args.Setup);
        }
        catch (ArgumentException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCode.Usage;
        }

        if (candidate.Protector is null)
        {
            await stderr.WriteLineAsync(
                "warning: the setup configures no payload protector. If the application uses client-side encryption, add UsePayloadProtector to the setup, or every sealed history is reported as unreadable.")
                .ConfigureAwait(false);
        }

        string? connection = args.Connection ?? Environment.GetEnvironmentVariable(args.ConnectionVariable);
        if (store is null && string.IsNullOrEmpty(connection) && args.Corpus is null)
        {
            await stderr.WriteLineAsync(
                $"Give the histories to replay: a database (set {args.ConnectionVariable}, or --connection) and/or --corpus <directory of .khist files>.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        var options = new ReplayCheckOptions { SamplePerGroup = args.Sample, Since = args.Since };
        var checker = new ReplayChecker(candidate.Workflows, candidate.Protector, options);
        var reports = new List<ReplayCheckReport>();
        NpgsqlDataSource? owned = null;
        try
        {
            if (store is null && !string.IsNullOrEmpty(connection))
            {
                owned = NpgsqlDataSource.Create(connection);
                store = new PostgresKairosStore(owned);
            }

            if (store is not null)
            {
                try
                {
                    reports.Add(await checker.CheckStoreAsync(store, args.Scope, cancellationToken).ConfigureAwait(false));
                }
                catch (NotSupportedException)
                {
                    await stderr.WriteLineAsync("This store cannot list runs, so there is nothing to sample.").ConfigureAwait(false);
                    return ExitCode.Usage;
                }
                catch (NpgsqlException ex)
                {
                    await stderr.WriteLineAsync($"The database could not be read ({ex.GetType().Name}). Check the connection and the rights of the role on the Kairos tables.").ConfigureAwait(false);
                    return ExitCode.Usage;
                }
            }

            if (args.Corpus is not null)
            {
                reports.Add(await CheckCorpusAsync(checker, args.Corpus, stderr, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }

        ReplayCheckReport report = ReplayCheckReport.Combine(reports);
        await stdout.WriteAsync(args.Json ? report.ToJson(args.StrictCoverage) : report.ToText(args.StrictCoverage)).ConfigureAwait(false);
        return report.Succeeded(args.StrictCoverage) ? ExitCode.Passed : ExitCode.Failed;
    }

    private static async Task<ReplayCheckReport> CheckCorpusAsync(ReplayChecker checker, string directory, TextWriter stderr, CancellationToken cancellationToken)
    {
        var histories = new List<(string, IReadOnlyList<HistoryEvent>)>();
        var unreadable = new List<ReplayFinding>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.khist", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = $"file:{Path.GetRelativePath(directory, file)}";
            try
            {
                histories.Add((name, HistoryFile.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).History));
            }
            catch (FormatException)
            {
                unreadable.Add(new ReplayFinding(name, "", "", ReplayVerdict.Error, 0, null, "the file is not a valid history file"));
            }
        }

        if (histories.Count == 0 && unreadable.Count == 0)
        {
            await stderr.WriteLineAsync($"No .khist files under '{directory}'.").ConfigureAwait(false);
        }

        ReplayCheckReport replayed = await checker.CheckHistoriesAsync(histories, cancellationToken).ConfigureAwait(false);
        return ReplayCheckReport.Combine([replayed, ReplayCheckReport.FromFindings(unreadable)]);
    }
}
