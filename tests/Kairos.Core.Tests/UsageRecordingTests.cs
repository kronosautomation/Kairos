using System.Text;
using Kairos.Testing;

namespace Kairos.Core.Tests;

public sealed class UsageRecordingTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("66666666-0000-0000-0000-000000000001"), Guid.Parse("66666666-0000-0000-0000-0000000000a1"));

    private static ActivityCompleted Completed(UsageRecord? usage) =>
        new(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid(), 4, DateTimeOffset.UnixEpoch, 1, [1, 2, 3]) { Usage = usage };

    [Fact]
    public void Usage_round_trips_through_the_codec_and_changes_the_hash_only_when_present()
    {
        ActivityCompleted plain = Completed(null);
        ActivityCompleted withUsage = plain with { Usage = new UsageRecord(1200, 340, 800, "gpt-5.1-2026-10") };

        var decoded = (ActivityCompleted)HistoryEventCodec.Decode(HistoryEventCodec.Encode(withUsage), [], []);
        Assert.Equal(new UsageRecord(1200, 340, 800, "gpt-5.1-2026-10"), decoded.Usage);
        Assert.Null(((ActivityCompleted)HistoryEventCodec.Decode(HistoryEventCodec.Encode(plain), [], [])).Usage);

        // An event without usage encodes exactly as it did before usage existed: 4 (id) + 4 + 3 (result) after the header.
        Assert.Equal(HistoryEventCodec.Encode(plain).Length + 24 + 4 + Encoding.UTF8.GetByteCount("gpt-5.1-2026-10"), HistoryEventCodec.Encode(withUsage).Length);
        Assert.NotEqual(HashChain.ComputeHash(plain, HashChain.GenesisHash), HashChain.ComputeHash(withUsage, HashChain.GenesisHash));
        Assert.Equal(HashChain.ComputeHash(plain, HashChain.GenesisHash), HashChain.ComputeHash(plain with { Usage = null }, HashChain.GenesisHash));
    }

    [Fact]
    public void A_usage_record_that_cannot_be_true_is_refused_on_decode()
    {
        byte[] good = HistoryEventCodec.Encode(Completed(new UsageRecord(10, 5, 4, "m")));
        ActivityCompleted impossible = Completed(new UsageRecord(10, 5, 11, "m")); // more cached than input
        ActivityCompleted negative = Completed(new UsageRecord(-1, 5, 0, "m"));

        _ = HistoryEventCodec.Decode(good, [], []);
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(HistoryEventCodec.Encode(impossible), [], []));
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(HistoryEventCodec.Encode(negative), [], []));
    }

    [Fact]
    public async Task An_activity_that_reports_usage_has_it_recorded_once_in_its_completion_event_and_replay_does_not_count_it_again()
    {
        int executions = 0;
        var activities = new ActivityRegistry().Register("model", (context, input, _) =>
        {
            executions++;
            context.ReportUsage(new UsageRecord(100, 20, 60, "model-x"));
            return Task.FromResult(input);
        });
        var store = new InMemoryKairosStore();
        var clock = new VirtualTimeProvider();
        var client = new KairosClient(store, clock);
        var workflows = new WorkflowRegistry().Register("agent", "default", (ctx, input) => AgentWorkflow(ctx, input));
        StartRunResult start = await client.StartAsync(Scope, "u-1", "agent", Encoding.UTF8.GetBytes("x"));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false });
        for (int i = 0; i < 6; i++)
        {
            await Driver.DriveAsync(worker);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        Assert.Equal(RunState.Completed, await client.GetStateAsync(run));
        Assert.Equal(2, executions);
        RunUsage usage = await client.GetUsageAsync(run);
        Assert.Equal((2, 200L, 40L, 120L, 240L), (usage.Steps, usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.TotalTokens));
        ModelUsage model = Assert.Single(usage.ByModel);
        Assert.Equal(("model-x", 2), (model.Model, model.Steps));

        // Reading it again, or replaying the whole history, adds nothing: the figures are in the events, not in the replay.
        _ = WorkflowReplayer.Replay((ctx, input) => AgentWorkflow(ctx, input), await client.GetHistoryAsync(run));
        RunUsage again = await client.GetUsageAsync(run);
        Assert.Equal((usage.Steps, usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens), (again.Steps, again.InputTokens, again.OutputTokens, again.CachedInputTokens));
        HashChain.Verify(await client.GetHistoryAsync(run));

        static async Task<byte[]> AgentWorkflow(IWorkflowContext ctx, byte[] input)
        {
            await ctx.ExecuteActivityAsync("model", input);
            await ctx.DelayAsync(TimeSpan.FromSeconds(1));
            await ctx.ExecuteActivityAsync("model", input);
            return input;
        }
    }

    [Fact]
    public async Task Usage_is_readable_without_a_key_when_payloads_are_sealed()
    {
        var protector = new ReversingProtector();
        var store = new InMemoryKairosStore();
        var clock = new VirtualTimeProvider();
        var client = new KairosClient(store, clock, payloadProtector: protector);
        var workflows = new WorkflowRegistry().Register("agent", "default", async (ctx, input) => await ctx.ExecuteActivityAsync("model", input));
        var activities = new ActivityRegistry().Register("model", (context, input, _) =>
        {
            context.ReportUsage(new UsageRecord(7, 3, 0, "model-y"));
            return Task.FromResult(input);
        });
        StartRunResult start = await client.StartAsync(Scope, "u-2", "agent", Encoding.UTF8.GetBytes("secret"));
        await Driver.DriveAsync(new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false }, protector));

        var keyless = new KairosClient(store, clock); // no protector: ciphertext only
        RunUsage usage = await keyless.GetUsageAsync(new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId));

        Assert.Equal((1, 7L, 3L), (usage.Steps, usage.InputTokens, usage.OutputTokens));
    }

    [Fact]
    public void A_model_name_longer_than_the_limit_is_cut_and_a_run_without_usage_reports_none()
    {
        var context = new ActivityContext(new RunKey(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid()), 1, "model", 1, "key");

        context.ReportUsage(new UsageRecord(1, 1, 0, new string('m', 500)));

        Assert.Equal(UsageRecord.MaxModelLength, context.Usage!.Model.Length);
        Assert.Same(RunUsage.None, RunUsage.From([Completed(null)]));
    }

    /// <summary>Seals by reversing after a hash of the context; opens only under the same context.</summary>
    private sealed class ReversingProtector : IPayloadProtector
    {
        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<byte[]>([.. System.Security.Cryptography.SHA256.HashData(context.Span), .. plaintext.ToArray().Reverse()]);

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] all = protectedPayload.ToArray();
            return all.Length >= 32 && all.AsSpan(0, 32).SequenceEqual(System.Security.Cryptography.SHA256.HashData(context.Span))
                ? ValueTask.FromResult(all.Skip(32).Reverse().ToArray())
                : throw new PayloadProtectionException();
        }
    }
}
