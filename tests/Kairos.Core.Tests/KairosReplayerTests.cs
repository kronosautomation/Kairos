using System.Text;
using Kairos.Testing;

namespace Kairos.Core.Tests;

public sealed class KairosReplayerTests : IDisposable
{
    private const string Canary = "CANARY-8d42-production-data";
    private static readonly TenantScope Scope = new(Guid.Parse("77777777-0000-0000-0000-000000000001"), Guid.Parse("77777777-0000-0000-0000-0000000000a1"));
    private static readonly Principal Operator = new(Scope, Role.Operator, "dev@example.com", null);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-replayer-" + Guid.NewGuid().ToString("N"));

    public KairosReplayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static int s_workflowRuns;

    private static async Task<byte[]> Production(IWorkflowContext ctx, byte[] input)
    {
        Interlocked.Increment(ref s_workflowRuns);
        byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return receipt;
    }

    private async Task<string> ExportAsync(bool metadataOnly = false)
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("order", Production);
        engine.RegisterActivity("charge", input => Task.FromResult(Encoding.UTF8.GetBytes("receipt for " + Encoding.UTF8.GetString(input))));
        Guid id = engine.StartWorkflow("order", Encoding.UTF8.GetBytes(Canary), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(id);
        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, Operator, new RunKey(Scope.TenantId, Scope.NamespaceId, id), "reproduce ticket 4411", metadataOnly, engine.Time))!;
        string path = Path.Combine(_dir, $"{id:N}.khist");
        await File.WriteAllTextAsync(path, export.FileText);
        return path;
    }

    [Fact]
    public async Task The_workflow_runs_here_against_the_recorded_results_and_no_activity_runs()
    {
        string file = await ExportAsync();
        int before = Volatile.Read(ref s_workflowRuns);

        ReplayOutcome outcome = await KairosReplayer.ReplayAsync(file, Production);

        Assert.True(outcome.Succeeded);
        Assert.True(Volatile.Read(ref s_workflowRuns) > before); // the code ran in this process, where a debugger sees it
        Assert.True(outcome.Result!.IsClosed);
        Assert.Empty(outcome.Result.NewCommands);
        Assert.Contains("PASSED", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_divergence_is_reported_exactly_as_replay_check_reports_it()
    {
        string file = await ExportAsync();

        ReplayOutcome outcome = await KairosReplayer.ReplayAsync(file, async (ctx, input) =>
        {
            await ctx.DelayAsync(TimeSpan.FromSeconds(5));
            return await ctx.ExecuteActivityAsync("charge", input);
        });

        Assert.False(outcome.Succeeded);
        Assert.Contains("FAIL  nondeterministic  order", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("first divergence at event", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("recorded: ActivityScheduled: activity 1 'charge'", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("emitted:  StartTimer: timer 1 for 00:00:05", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, outcome.Text, StringComparison.Ordinal);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task AssertReplays_throws_with_the_report()
    {
        string file = await ExportAsync();

        await KairosReplayer.AssertReplaysAsync(file, Production);
        ReplayCheckFailedException error = await Assert.ThrowsAsync<ReplayCheckFailedException>(async () =>
            await KairosReplayer.AssertReplaysAsync(file, (_, _) => Task.FromResult<byte[]>([])));
        Assert.Contains("first divergence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_metadata_only_file_cannot_be_replayed_and_says_why()
    {
        string file = await ExportAsync(metadataOnly: true);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await KairosReplayer.ReplayAsync(file, Production));

        Assert.Contains("metadata-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_changed_after_export_is_reported_as_an_integrity_failure_and_nothing_is_replayed()
    {
        string file = await ExportAsync();
        List<HistoryEvent> history = [.. HistoryFile.Parse(await File.ReadAllTextAsync(file)).History];
        int at = history.FindIndex(e => e is ActivityCompleted);
        history[at] = ((ActivityCompleted)history[at]) with { Result = [0] };
        await File.WriteAllTextAsync(file, HistoryFile.Serialize(history, "edited"));
        int before = Volatile.Read(ref s_workflowRuns);

        ReplayOutcome outcome = await KairosReplayer.ReplayAsync(file, Production);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ReplayVerdict.IntegrityFailure, Assert.Single(outcome.Report.Findings).Verdict);
        Assert.Equal(before, Volatile.Read(ref s_workflowRuns));
    }

    [Fact]
    public async Task Sealed_exports_open_with_the_developers_own_protector_and_not_with_anyone_elses()
    {
        var mine = new KeyedProtector("developer-key");
        var store = new InMemoryKairosStore();
        var clock = new VirtualTimeProvider();
        var client = new KairosClient(store, clock, payloadProtector: mine);
        var workflows = new WorkflowRegistry().Register("order", "default", Production);
        var activities = new ActivityRegistry().Register("charge", (_, input, _) => Task.FromResult(input));
        StartRunResult start = await client.StartAsync(Scope, "o-sealed", "order", Encoding.UTF8.GetBytes(Canary));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false }, mine);
        for (int i = 0; i < 4; i++)
        {
            await Driver.DriveAsync(worker);
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        HistoryExport export = (await HistoryExporter.ExportAsync(store, Operator, run, "debug", false, clock))!;
        string path = Path.Combine(_dir, "sealed.khist");
        await File.WriteAllTextAsync(path, export.FileText);
        Assert.DoesNotContain(Canary, export.FileText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary))[..16], export.FileText, StringComparison.Ordinal);

        Assert.True((await KairosReplayer.ReplayAsync(path, Production, mine)).Succeeded);
        ReplayOutcome stranger = await KairosReplayer.ReplayAsync(path, Production, new KeyedProtector("someone-else"));
        Assert.Equal(ReplayVerdict.Unreadable, Assert.Single(stranger.Report.Findings).Verdict);
    }

    /// <summary>Opens only what it sealed (its own key), and only under the same context.</summary>
    private sealed class KeyedProtector(string key) : IPayloadProtector
    {
        private byte[] Mask(ReadOnlyMemory<byte> context) => System.Security.Cryptography.SHA256.HashData([.. Encoding.UTF8.GetBytes(key), .. context.ToArray()]);

        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] mask = Mask(context);
            return ValueTask.FromResult<byte[]>([.. mask, .. plaintext.ToArray().Select((b, i) => (byte)(b ^ mask[i % mask.Length]))]);
        }

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] mask = Mask(context);
            byte[] all = protectedPayload.ToArray();
            return all.Length >= mask.Length && all.AsSpan(0, mask.Length).SequenceEqual(mask)
                ? ValueTask.FromResult(all.Skip(mask.Length).Select((b, i) => (byte)(b ^ mask[i % mask.Length])).ToArray())
                : throw new PayloadProtectionException();
        }
    }
}
