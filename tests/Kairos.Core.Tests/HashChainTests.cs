using System.Text;

namespace Kairos.Core.Tests;

public sealed class HashChainTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Namespace = Guid.NewGuid();

    private static async Task<List<HistoryEvent>> SampleHistoryAsync()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("a", i => Task.FromResult(i));
        engine.RegisterWorkflow("wf", async (ctx, input) =>
        {
            byte[] r = await ctx.ExecuteActivityAsync("a", input);
            await ctx.DelayAsync(TimeSpan.FromSeconds(5));
            return r;
        });
        Guid run = engine.StartWorkflow("wf", Encoding.UTF8.GetBytes("payload"), Tenant, Namespace);
        await engine.RunAsync(run);
        return [.. engine.GetHistory(run)];
    }

    [Fact]
    public async Task Valid_chain_verifies_and_is_anchored_at_genesis()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();

        HashChain.Verify(history);
        Assert.True(history[0].PrevHash.AsSpan().SequenceEqual(HashChain.GenesisHash));
        for (int i = 1; i < history.Count; i++)
        {
            Assert.Equal(history[i - 1].Hash, history[i].PrevHash);
        }
    }

    [Fact]
    public async Task Modified_payload_is_detected()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        history[1] = ((ActivityScheduled)history[1]) with { Input = Encoding.UTF8.GetBytes("tampered") };

        var ex = Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
        Assert.Equal(2, ex.Sequence);
    }

    [Fact]
    public async Task Modified_event_with_recomputed_hash_breaks_the_next_link()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        HistoryEvent forged = history[1] with { Timestamp = history[1].Timestamp.AddDays(1) };
        history[1] = HashChain.Seal(forged, history[1].PrevHash);

        var ex = Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
        Assert.Equal(3, ex.Sequence);
    }

    [Fact]
    public async Task Reordered_events_are_detected()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        (history[1], history[2]) = (history[2], history[1]);

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
    }

    [Fact]
    public async Task Removed_event_is_detected()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        history.RemoveAt(2);

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
    }

    [Fact]
    public async Task Dropping_the_first_event_is_detected()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        history.RemoveAt(0);

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
    }

    [Fact]
    public async Task Event_from_another_run_is_detected()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        HistoryEvent foreign = history[2] with { RunId = Guid.NewGuid() };
        history[2] = HashChain.Seal(foreign, history[2].PrevHash);

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
    }

    [Fact]
    public async Task Replay_and_load_reject_tampered_history()
    {
        List<HistoryEvent> history = await SampleHistoryAsync();
        history[0] = ((WorkflowStarted)history[0]) with { Input = [1, 2, 3] };

        WorkflowFunc wf = (_, input) => Task.FromResult(input);
        Assert.Throws<HistoryIntegrityException>(() => WorkflowReplayer.Replay(wf, history));
        Assert.Throws<HistoryIntegrityException>(() => new InMemoryWorkflowEngine().Load(history));
    }
}
