namespace Kairos.Core.Tests;

/// <summary>
/// Layer 6. Every history under Corpus/ must verify, keep its exact byte encoding, and replay with today's code as a closed no-op.
/// A failure here is a compatibility break for in-flight runs: fix the engine, or gate the change with the patch API.
/// Never re-record to make it pass. Record once per release: KAIROS_CORPUS_RECORD=&lt;dir&gt; dotnet test --filter Record_the_corpus.
/// </summary>
public sealed class GoldenCorpusTests
{
    private static string CorpusRoot => Path.Combine(AppContext.BaseDirectory, "Corpus");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        if (Directory.Exists(CorpusRoot))
        {
            foreach (string file in Directory.EnumerateFiles(CorpusRoot, "*.khist", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(CorpusRoot, file));
            }
        }

        return data;
    }

    [Fact]
    public async Task Record_the_corpus()
    {
        string? target = Environment.GetEnvironmentVariable("KAIROS_CORPUS_RECORD");
        if (string.IsNullOrEmpty(target))
        {
            return; // recording is a release step, not part of normal runs
        }

        Directory.CreateDirectory(target);
        foreach (CorpusEntry entry in await GoldenCorpus.RecordAllAsync())
        {
            await File.WriteAllTextAsync(Path.Combine(target, entry.File), GoldenCorpus.Serialize(entry));
        }
    }

    [Fact]
    public void The_corpus_exists_and_covers_every_event_type()
    {
        string[] files = [.. Directory.EnumerateFiles(CorpusRoot, "*.khist", SearchOption.AllDirectories)];
        Assert.True(files.Length >= 15, $"Expected the recorded corpus, found {files.Length} files under {CorpusRoot}.");

        HashSet<HistoryEventType> seen = [];
        foreach (string file in files)
        {
            foreach (HistoryEvent e in GoldenCorpus.Parse(file, File.ReadAllText(file)).History)
            {
                seen.Add(e.Type);
            }
        }

        // A new event type must ship with a recorded history that uses it, or old builds' compatibility with it is untested.
        HistoryEventType[] missing = [.. Enum.GetValues<HistoryEventType>().Where(t => !seen.Contains(t))];
        Assert.True(missing.Length == 0, $"No corpus history contains: {string.Join(", ", missing)}. Record one.");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Recorded_history_still_verifies_keeps_its_encoding_and_replays_without_divergence(string relativePath)
    {
        string path = Path.Combine(CorpusRoot, relativePath);
        CorpusEntry entry = GoldenCorpus.Parse(path, File.ReadAllText(path));

        // Integrity: the hash algorithm and canonical encoding are unchanged (any drift breaks every stored chain).
        HashChain.Verify(entry.History);
        string[] eventLines = [.. File.ReadLines(path).Where(l => l.StartsWith("event: ", StringComparison.Ordinal))];
        Assert.Equal(eventLines.Length, entry.History.Count);
        for (int i = 0; i < eventLines.Length; i++)
        {
            byte[] stored = Convert.FromBase64String(eventLines[i]["event: ".Length..].Split(' ')[0]);
            Assert.Equal(stored, HistoryEventCodec.Encode(entry.History[i]));
        }

        // Determinism: today's code replays it, the run is closed, and nothing is left to do.
        ReplayResult replay = WorkflowReplayer.Replay(GoldenCorpus.Resolve(entry.ReplayWith), entry.History);
        Assert.True(replay.IsClosed, $"{relativePath}: replay did not reach the recorded end.");
        Assert.Empty(replay.NewCommands);

        string expected = File.ReadLines(path).Single(l => l.StartsWith("outcome: ", StringComparison.Ordinal))["outcome: ".Length..];
        Assert.Equal(expected, GoldenCorpus.Outcome(entry.History[^1]));
    }

    [Fact]
    public void A_gated_change_replays_old_histories_and_an_ungated_one_is_caught()
    {
        string path = Path.Combine(CorpusRoot, RecordedBy, "sim-patch-point.khist");
        CorpusEntry old = GoldenCorpus.Parse(path, File.ReadAllText(path));

        // The patched build (extra activity behind IsPatched) replays the unpatched history: I10.
        Assert.True(WorkflowReplayer.Replay(GoldenCorpus.Resolve("sim:patch-point:patched"), old.History).IsClosed);

        // The same extra activity without the gate is exactly what the corpus exists to catch.
        WorkflowFunc ungated = async (ctx, input) =>
        {
            await ctx.ExecuteActivityAsync("act", new byte[] { 0, 0, 0, 0 });
            await ctx.ExecuteActivityAsync("act", new byte[] { 0, 0, 0, 0 });
            return input;
        };
        Assert.Throws<NonDeterminismException>(() => WorkflowReplayer.Replay(ungated, old.History));
    }

    private static string RecordedBy => GoldenCorpus.RecordedBy;
}
