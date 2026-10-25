using System.Text;
using Kairos;
using Kairos.Testing;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>Shows the unit-test form of the deploy safety gate: record a run with the old code, then assert the new code still replays it.</summary>
public sealed class DeterminismCheckTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("44444444-0000-0000-0000-000000000001"), Guid.Parse("44444444-0000-0000-0000-0000000000a1"));

    private static async Task<byte[]> Shipped(IWorkflowContext ctx, byte[] input)
    {
        byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return receipt;
    }

    [Fact]
    public async Task Code_that_keeps_the_order_of_steps_replays_old_runs_and_code_that_moves_a_step_does_not()
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("ship", Shipped);
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        Guid run = engine.StartWorkflow("ship", Encoding.UTF8.GetBytes("order-1"), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(run);

        #region determinism-test
        // The runs are in the store (here a test store, in a pipeline the real one). The question: would the new code have done the same?
        var newBuild = new WorkflowRegistry().Register("ship", "default", Shipped);
        await DeterminismCheck.AssertStoreReplaysAsync(newBuild, engine.Store, Scope);
        #endregion

        var moved = new WorkflowRegistry().Register("ship", "default", async (ctx, input) =>
        {
            await ctx.DelayAsync(TimeSpan.FromSeconds(5)); // the timer now comes before the charge
            return await ctx.ExecuteActivityAsync("charge", input);
        });
        ReplayCheckFailedException failure = await Assert.ThrowsAsync<ReplayCheckFailedException>(
            async () => await DeterminismCheck.AssertStoreReplaysAsync(moved, engine.Store, Scope));
        Assert.Contains("first divergence", failure.Message, StringComparison.Ordinal);
    }
}
