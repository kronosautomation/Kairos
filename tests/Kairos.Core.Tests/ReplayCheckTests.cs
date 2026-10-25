using System.Text;
using System.Text.Json;
using Kairos.Testing;

namespace Kairos.Core.Tests;

public sealed class ReplayCheckTests
{
    private const string Canary = "CANARY-9b1d-never-printed";
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-0000-0000-0000-000000000001"), Guid.Parse("11111111-0000-0000-0000-0000000000a1"));

    private static async Task<(TestEngine Engine, List<Guid> Runs)> RecordAsync(int closed, int open = 0)
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("order", Original);
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        var runs = new List<Guid>();
        for (int i = 0; i < closed + open; i++)
        {
            runs.Add(engine.StartWorkflow("order", Encoding.UTF8.GetBytes(i < closed ? $"{Canary}-{i}" : $"HOLD-{Canary}-{i}"), Scope.TenantId, Scope.NamespaceId));
            engine.Time.Advance(TimeSpan.FromMinutes(1));
        }

        if (runs.Count > 0)
        {
            await engine.RunAsync(runs[0]); // drives the whole scope; runs that hold wait for a signal that never comes
        }

        return (engine, runs);
    }

    private static async Task<byte[]> Original(IWorkflowContext ctx, byte[] input)
    {
        if (input.AsSpan().StartsWith("HOLD"u8))
        {
            await ctx.WaitForSignalAsync("release");
        }

        byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return receipt;
    }

    private static WorkflowRegistry Candidate(WorkflowFunc workflow, string build = "default") => new WorkflowRegistry().Register("order", build, workflow);

    [Fact]
    public async Task Unchanged_code_passes_and_open_and_closed_runs_are_both_replayed()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 3, open: 2);

        ReplayCheckReport report = await new ReplayChecker(Candidate(Original)).CheckStoreAsync(engine.Store, Scope, default);

        Assert.True(report.Succeeded());
        Assert.Equal(5, report.Checked);
        ReplayCoverage coverage = Assert.Single(report.Coverage);
        Assert.Equal(("order", "default", 2, 3, 5, true), (coverage.WorkflowType, coverage.BuildId, coverage.Open, coverage.Closed, coverage.Checked, coverage.InCandidate));
    }

    [Fact]
    public async Task A_changed_order_of_steps_is_reported_with_the_first_diverging_command_and_no_payload()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 2);

        ReplayCheckReport report = await new ReplayChecker(Candidate(async (ctx, input) =>
        {
            await ctx.DelayAsync(TimeSpan.FromSeconds(5)); // the timer now comes first
            return await ctx.ExecuteActivityAsync("charge", input);
        })).CheckStoreAsync(engine.Store, Scope, default);

        Assert.False(report.Succeeded());
        Assert.Equal(2, report.Failures.Count);
        ReplayFinding failure = report.Failures[0];
        Assert.Equal(ReplayVerdict.Nondeterministic, failure.Verdict);
        ReplayDivergence divergence = Assert.IsType<ReplayDivergence>(failure.Divergence);
        Assert.True(divergence.Sequence > 0);
        Assert.StartsWith("ActivityScheduled: activity 1 'charge' (", divergence.Recorded, StringComparison.Ordinal);
        Assert.Equal("StartTimer: timer 1 for 00:00:05", divergence.Emitted);
        Assert.DoesNotContain(Canary, report.ToText() + report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_input_to_the_same_activity_is_a_divergence_described_by_size_only()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 1);

        ReplayCheckReport report = await new ReplayChecker(Candidate(async (ctx, input) =>
        {
            byte[] receipt = await ctx.ExecuteActivityAsync("charge", input.Concat(Encoding.UTF8.GetBytes("-v2")).ToArray());
            await ctx.DelayAsync(TimeSpan.FromSeconds(5));
            return receipt;
        })).CheckStoreAsync(engine.Store, Scope, default);

        ReplayDivergence divergence = Assert.IsType<ReplayDivergence>(Assert.Single(report.Failures).Divergence);
        Assert.Contains("with a different input (", divergence.Emitted, StringComparison.Ordinal);
        Assert.Contains("bytes against", divergence.Emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, report.ToText() + report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extra_work_the_recorded_run_never_did_is_reported_too()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 1);

        ReplayCheckReport report = await new ReplayChecker(Candidate(async (ctx, input) =>
        {
            byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
            await ctx.DelayAsync(TimeSpan.FromSeconds(5));
            await ctx.ExecuteActivityAsync("audit", input); // added after the run finished
            return receipt;
        })).CheckStoreAsync(engine.Store, Scope, default);

        Assert.Equal(ReplayVerdict.Nondeterministic, Assert.Single(report.Failures).Verdict);
    }

    [Fact]
    public async Task A_workflow_type_or_build_the_candidate_lacks_is_listed_and_a_check_that_replays_nothing_does_not_pass()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 2);

        ReplayCheckReport report = await new ReplayChecker(Candidate(Original, build: "2026.11")).CheckStoreAsync(engine.Store, Scope, default);

        Assert.Equal(0, report.Checked);
        Assert.False(report.Succeeded());
        Assert.Contains("Nothing was replayed", report.ToText(), StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Coverage).InCandidate);
        Assert.Equal(ReplayVerdict.NotCovered, Assert.Single(report.Findings).Verdict);
    }

    [Fact]
    public async Task Strict_coverage_fails_when_in_flight_builds_are_missing_from_the_candidate()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 1);
        var client = new KairosClient(engine.Store, engine.Time);
        await client.StartAsync(Scope, "old-build-run", "order", [], "2026.09");

        ReplayCheckReport report = await new ReplayChecker(Candidate(Original)).CheckStoreAsync(engine.Store, Scope, default);

        Assert.True(report.Succeeded());
        Assert.False(report.Succeeded(strictCoverage: true));
        Assert.Equal("2026.09", Assert.Single(report.Uncovered).BuildId);
    }

    [Fact]
    public async Task The_sample_is_the_newest_runs_of_each_state_per_workflow_type_and_build()
    {
        (TestEngine engine, List<Guid> runs) = await RecordAsync(closed: 12, open: 4);

        ReplayCheckReport report = await new ReplayChecker(Candidate(Original), options: new ReplayCheckOptions { SamplePerGroup = 3 }).CheckStoreAsync(engine.Store, Scope, default);

        string[] sources = [.. report.Findings.Select(f => f.Source)];
        Assert.Equal(6, sources.Length);
        Assert.All(runs.Skip(13), id => Assert.Contains($"run:{id:D}", sources)); // the open runs started last: the 3 newest of 4
        Assert.All(runs.Skip(9).Take(3), id => Assert.Contains($"run:{id:D}", sources)); // the 3 newest closed
        Assert.DoesNotContain($"run:{runs[0]:D}", sources);
    }

    [Fact]
    public async Task A_tampered_history_is_an_integrity_failure_not_a_divergence()
    {
        (TestEngine engine, List<Guid> runs) = await RecordAsync(closed: 1);
        List<HistoryEvent> history = [.. engine.GetHistory(runs[0])];
        int at = history.FindIndex(e => e is ActivityScheduled);
        history[at] = ((ActivityScheduled)history[at]) with { Input = [1, 2, 3] };

        ReplayFinding finding = await new ReplayChecker(Candidate(Original)).CheckAsync("run:x", history, default);

        Assert.Equal(ReplayVerdict.IntegrityFailure, finding.Verdict);
        Assert.Equal(history[at].Sequence, finding.Divergence!.Sequence);
    }

    [Fact]
    public async Task Sealed_histories_replay_with_a_protector_and_are_refused_without_one()
    {
        var protector = new ReversingProtector();
        var store = new InMemoryKairosStore();
        var clock = new VirtualTimeProvider();
        var client = new KairosClient(store, clock, payloadProtector: protector);
        var workflows = new WorkflowRegistry().Register("order", "default", Original);
        var failures = 0;
        var activities = new ActivityRegistry().Register("charge", (_, input, _) => ++failures < 2 ? throw new InvalidOperationException("boom") : Task.FromResult(input));
        await client.StartAsync(Scope, "o-1", "order", Encoding.UTF8.GetBytes(Canary));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false }, protector);
        for (int i = 0; i < 6; i++)
        {
            await Driver.DriveAsync(worker);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        RunKey run = (await store.GetCurrentRunAsync(Scope, "o-1", default))!.Value;
        IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(run, default);
        Assert.Equal(RunState.Completed, await store.GetRunStateAsync(run, default));
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(((WorkflowStarted)history[0]).Input), StringComparison.Ordinal); // really sealed
        Assert.Contains(history, e => e is ActivityFailed { Message: { } m } && m.StartsWith("enc:", StringComparison.Ordinal)); // and so is the failure text

        ReplayFinding opened = await new ReplayChecker(Candidate(Original), protector).CheckAsync("run:sealed", history, default);
        Assert.Equal(ReplayVerdict.Passed, opened.Verdict);

        ReplayFinding refused = await new ReplayChecker(Candidate(Original)).CheckAsync("run:sealed", history, default);
        Assert.Equal(ReplayVerdict.Unreadable, refused.Verdict);
        Assert.DoesNotContain(Canary, refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_golden_corpus_replays_against_the_code_it_names()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Corpus");
        int checkedFiles = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.khist", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            HistoryFileContents contents = HistoryFile.Parse(await File.ReadAllTextAsync(file));
            var started = (WorkflowStarted)contents.History[0];
            var registry = new WorkflowRegistry().Register(started.WorkflowType, started.BuildId, GoldenCorpus.Resolve(contents.ReplayWith));

            ReplayFinding finding = await new ReplayChecker(registry).CheckAsync($"file:{Path.GetFileName(file)}", contents.History, default);

            Assert.True(finding.Verdict == ReplayVerdict.Passed, $"{file}: {finding.Verdict} {finding.Divergence}");
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 15);
    }

    [Fact]
    public async Task The_json_report_is_valid_and_says_whether_it_passed()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 1);
        ReplayCheckReport bad = await new ReplayChecker(Candidate(async (ctx, _) =>
        {
            await ctx.DelayAsync(TimeSpan.FromSeconds(1));
            return [];
        })).CheckStoreAsync(engine.Store, Scope, default);

        using JsonDocument doc = JsonDocument.Parse(bad.ToJson());
        Assert.False(doc.RootElement.GetProperty("succeeded").GetBoolean());
        JsonElement first = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("Nondeterministic", first.GetProperty("verdict").GetString());
        Assert.True(first.GetProperty("firstDivergence").GetProperty("sequence").GetInt64() > 0);
    }

    [Fact]
    public async Task The_test_helpers_throw_with_the_report_and_pass_quietly_otherwise()
    {
        (TestEngine engine, _) = await RecordAsync(closed: 2);

        await DeterminismCheck.AssertStoreReplaysAsync(Candidate(Original), engine.Store, Scope);
        ReplayCheckFailedException error = await Assert.ThrowsAsync<ReplayCheckFailedException>(async () =>
            await DeterminismCheck.AssertStoreReplaysAsync(Candidate(async (ctx, _) =>
            {
                await ctx.DelayAsync(TimeSpan.FromSeconds(1));
                return [];
            }), engine.Store, Scope));
        Assert.Contains("first divergence at event", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Report);
    }

    [Fact]
    public async Task A_store_that_cannot_list_runs_stops_the_check_instead_of_passing_it()
    {
        var checker = new ReplayChecker(Candidate(Original));

        await Assert.ThrowsAsync<NotSupportedException>(async () => await checker.CheckStoreAsync(new NoListingStore(), Scope, default));
    }

    private sealed class NoListingStore : ForwardingStore
    {
        public NoListingStore()
            : base(new InMemoryKairosStore())
        {
        }

        public override ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Reverses the bytes after prefixing a hash of the context: opens only under the same context, and the plaintext never appears in what is stored.</summary>
    private sealed class ReversingProtector : IPayloadProtector
    {
        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] bytes = [.. System.Security.Cryptography.SHA256.HashData(context.Span), .. plaintext.ToArray().Reverse()];
            return ValueTask.FromResult(bytes);
        }

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] expected = System.Security.Cryptography.SHA256.HashData(context.Span);
            byte[] all = protectedPayload.ToArray();
            if (all.Length < 32 || !all.AsSpan(0, 32).SequenceEqual(expected))
            {
                throw new PayloadProtectionException();
            }

            return ValueTask.FromResult(all.Skip(32).Reverse().ToArray());
        }
    }
}
