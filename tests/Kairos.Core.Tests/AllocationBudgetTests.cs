using Kairos.Testing;

namespace Kairos.Core.Tests;

/// <summary>
/// Cheap, deterministic allocation gates mirroring the BenchmarkDotNet suite (benchmarks/Kairos.Benchmarks).
/// Budgets are roughly 1.5x the measured value; a regression beyond them fails the build.
/// History: M1 measured ~133 KB per 1,000-event replay; the batch scheduler that deterministic replay needs (found by the
/// simulator) costs ~208 KB. Sticky caching removes full replays from the hot path; revisit then.
/// </summary>
public sealed class AllocationBudgetTests
{
    private const int Activities = 332; // started + 3 events per activity (result, boundary, next command) + 2 = 999 events

    private static async Task<IReadOnlyList<HistoryEvent>> BuildHistoryAsync(WorkflowFunc workflow)
    {
        var engine = new TestEngine();
        engine.RegisterActivity("step", input => Task.FromResult(input));
        engine.RegisterWorkflow("wf", workflow);
        Guid run = engine.StartWorkflow("wf", new byte[64], Guid.NewGuid(), Guid.NewGuid());
        await engine.RunAsync(run);
        return engine.GetHistory(run);
    }

    private static long Measure(Action action)
    {
        action(); // warm up: JIT, pools
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public async Task Replay_of_999_events_stays_within_allocation_budget()
    {
        WorkflowFunc workflow = async (ctx, input) =>
        {
            for (int i = 0; i < Activities; i++)
            {
                await ctx.ExecuteActivityAsync("step", input);
            }

            return input;
        };
        IReadOnlyList<HistoryEvent> history = await BuildHistoryAsync(workflow);
        Assert.Equal(999, history.Count);

        long allocated = Measure(() => WorkflowReplayer.Replay(workflow, history, verifyHashChain: false));

        Assert.True(allocated < 300_000, $"Replay allocated {allocated:N0} bytes (budget 300,000).");
    }

    [Fact]
    public async Task Hash_chain_verification_is_allocation_free()
    {
        IReadOnlyList<HistoryEvent> history = await BuildHistoryAsync(async (ctx, input) =>
        {
            for (int i = 0; i < Activities; i++)
            {
                await ctx.ExecuteActivityAsync("step", input);
            }

            return input;
        });

        long allocated = Measure(() => HashChain.Verify(history));

        Assert.Equal(0, allocated);
    }
}
