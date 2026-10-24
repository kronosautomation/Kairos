namespace Kairos.Core.Tests;

/// <summary>
/// Cheap, deterministic allocation gates mirroring the BenchmarkDotNet suite (benchmarks/Kairos.Benchmarks).
/// Budgets are roughly 1.5x the measured value; a regression beyond them fails the build.
/// </summary>
public sealed class AllocationBudgetTests
{
    private const int Activities = 499; // 1 start + 499 x (scheduled, completed) + 1 completion = 1,000 events

    private static async Task<IReadOnlyList<HistoryEvent>> BuildHistoryAsync(WorkflowFunc workflow)
    {
        var engine = new InMemoryWorkflowEngine();
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
    public async Task Replay_of_1000_events_stays_within_allocation_budget()
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
        Assert.Equal(1000, history.Count);

        long allocated = Measure(() => WorkflowReplayer.Replay(workflow, history, verifyHashChain: false));

        Assert.True(allocated < 200_000, $"Replay allocated {allocated:N0} bytes (budget 200,000).");
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
