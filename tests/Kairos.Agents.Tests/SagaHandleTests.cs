using System.Text;

namespace Kairos.Agents.Tests;

public sealed class SagaHandleTests
{
    private static readonly byte[] Empty = [];

    private static AgentHarness Harness(List<string> log)
    {
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()));
        foreach (string name in new[] { "do", "undo-a", "undo-b" })
        {
            h.Activity(name, (_, input, _) =>
            {
                lock (log)
                {
                    log.Add(name);
                }

                return Task.FromResult(input);
            });
        }

        return h;
    }

    [Fact]
    public async Task A_discarded_undo_does_not_run_and_is_left_out_of_the_recorded_plan()
    {
        var log = new List<string>();
        AgentHarness h = Harness(log);
        h.Workflow("w", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                SagaCompensationHandle a = saga.Compensate("a", "undo-a", Empty);
                saga.Compensate("b", "undo-b", Empty);
                a.Discard();
                Assert.True(a.IsDiscarded);
                await ctx.Workflow.ExecuteActivityAsync("do", Empty);
                throw new InvalidOperationException("boom");
            });
            return Empty;
        });
        RunKey run = await h.StartAsync("w");

        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(["do", "undo-b"], log);
        Assert.Equal(["b"], Assert.Single(SagaHistory.Rollbacks(await h.HistoryAsync(run))).Planned);
    }

    [Fact]
    public async Task When_every_undo_is_discarded_there_is_no_rollback_at_all()
    {
        var log = new List<string>();
        AgentHarness h = Harness(log);
        h.Workflow("w", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                saga.Compensate("a", "undo-a", Empty).Discard();
                await Task.CompletedTask;
                throw new InvalidOperationException("boom");
            });
            return Empty;
        });
        RunKey run = await h.StartAsync("w");

        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Empty(log);
        Assert.Empty(SagaHistory.Rollbacks(await h.HistoryAsync(run)));
    }

    [Fact]
    public async Task A_discarded_undo_does_not_stop_a_saga_from_continuing_as_new()
    {
        AgentHarness h = Harness([]);
        h.Workflow("w", async (ctx, input) =>
        {
            if (input.Length > 0)
            {
                return input;
            }

            await ctx.RunSagaAsync(async saga =>
            {
                saga.Compensate("a", "undo-a", Empty).Discard();
                await Task.CompletedTask;
                throw ctx.Workflow.ContinueAsNew(new byte[] { 1 });
            });
            return Empty;
        });
        RunKey run = await h.StartAsync("w");
        await h.RunAsync(run);

        Assert.IsType<WorkflowContinued>((await h.HistoryAsync(run))[^1]);
    }

    [Fact]
    public async Task WhenCancelled_completes_when_a_cancel_arrives_and_not_before()
    {
        AgentHarness h = Harness([]);
        h.Workflow("w", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                Task wait = ctx.Workflow.DelayAsync(TimeSpan.FromHours(1));
                await Task.WhenAny(wait, saga.WhenCancelled);
                saga.ThrowIfCancelled();
            });
            return Empty;
        });
        RunKey run = await h.StartAsync("w");
        Assert.Equal(RunState.Running, await h.RunAsync(run, jumpClock: false));

        await h.Agent.RequestCancelAsync(run, "enough");

        Assert.Equal(RunState.Failed, await h.RunAsync(run, jumpClock: false));
        Assert.Equal(nameof(SagaCancelledException), (await h.FailureAsync(run)).ErrorType);
    }

    [Fact]
    public async Task CurrentSaga_is_the_innermost_running_saga_and_hands_back_to_the_outer_one()
    {
        AgentHarness h = Harness([]);
        var seen = new List<string>();
        h.Workflow("w", async (ctx, _) =>
        {
            seen.Add(ctx.CurrentSaga is null ? "none" : "some");
            await ctx.RunSagaAsync(async outer =>
            {
                seen.Add(ReferenceEquals(ctx.CurrentSaga, outer) ? "outer" : "?");
                await ctx.RunSagaAsync(async inner => seen.Add(ReferenceEquals(ctx.CurrentSaga, inner) ? "inner" : "?"));
                seen.Add(ReferenceEquals(ctx.CurrentSaga, outer) ? "outer-again" : "?");
            });
            seen.Add(ctx.CurrentSaga is null ? "none" : "some");
            return Encoding.UTF8.GetBytes("ok");
        });
        RunKey run = await h.StartAsync("w");

        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal(["none", "outer", "inner", "outer-again", "none"], seen.Take(5));
    }
}
