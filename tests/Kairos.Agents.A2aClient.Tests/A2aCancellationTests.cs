using Kairos.Agents;
using Kairos.Agents.A2aClient;
using Kairos.Testing.Simulation;

namespace Kairos.Agents.A2aClient.Tests;

/// <summary>Cancelling a workflow that is waiting on a remote agent cancels the remote task as part of the rollback.</summary>
public sealed class A2aCancellationTests
{
    private static A2aRig InSaga(FakeAgent agent, Func<Task>? afterCall = null, bool faults = false)
    {
        A2aRig rig = A2aRig.For(agent, faults: faults);
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.CallA2aAgentAsync("billing", "Pay invoice 42");
                if (afterCall is not null)
                {
                    await afterCall();
                }
            });
            return [];
        });
        return rig;
    }

    [Fact]
    public async Task Cancelling_the_workflow_while_the_agent_works_cancels_the_remote_task_as_part_of_the_rollback()
    {
        var agent = new FakeAgent { WorkingPolls = int.MaxValue };
        A2aRig rig = InSaga(agent);
        RunKey run = await rig.StartAsync("call");
        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false));
        Assert.Empty(agent.Cancelled);

        await new AgentClient(rig.Client, rig.Time).RequestCancelAsync(run, "the customer withdrew");
        Assert.Equal(RunState.Failed, await rig.RunAsync(run, jumpClock: false));

        Assert.Equal(["remote-1"], agent.Cancelled); // at once, not after the hour-long timeout
        Assert.Equal(nameof(SagaCancelledException), (await rig.FailureAsync(run)).ErrorType);
        IReadOnlyList<HistoryEvent> history = await rig.HistoryAsync(run);
        SagaRollback rollback = Assert.Single(SagaHistory.Rollbacks(history));
        Assert.Equal(["cancel the A2A task remote-1 on billing"], rollback.Finished);
        Assert.Null(SagaHistory.CheckCompensationsComplete(history));
    }

    [Fact]
    public async Task A_cancel_ends_the_wait_between_polls_immediately()
    {
        var agent = new FakeAgent { WorkingPolls = int.MaxValue };
        A2aRig rig = InSaga(agent);
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run, jumpClock: false); // now waiting on a poll timer
        DateTimeOffset before = rig.Time.GetUtcNow();

        await new AgentClient(rig.Client, rig.Time).RequestCancelAsync(run, "stop");
        await rig.RunAsync(run, jumpClock: false);

        Assert.Equal(before, rig.Time.GetUtcNow()); // no clock jump was needed: the cancel did not wait for the timer
        Assert.Equal(RunState.Failed, (await rig.Client.GetStateAsync(run))!.Value);
    }

    [Fact]
    public async Task A_failure_while_the_agent_waits_for_an_answer_cancels_it_too()
    {
        var agent = new FakeAgent { WorkingPolls = 1, Final = A2aState.InputRequired };
        A2aRig rig = A2aRig.For(agent);
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                try
                {
                    await ctx.CallA2aAgentAsync("billing", "Pay invoice 42");
                }
                catch (A2aInputRequiredException)
                {
                    // the agent stopped to ask a question, so its task is alive; the saga then fails for another reason
                }

                throw new InvalidOperationException("our side gave up");
            });
            return [];
        });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        Assert.Equal(["remote-1"], agent.Cancelled);
    }

    [Fact]
    public async Task A_task_that_finished_by_itself_is_not_cancelled_when_a_later_step_fails()
    {
        var agent = new FakeAgent { WorkingPolls = 1, Answer = new A2aReply(A2aState.Completed, "paid", null, "remote-1", "ctx-1", null) };
        A2aRig rig = InSaga(agent, () => throw new InvalidOperationException("a later step failed"));
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));

        Assert.Empty(agent.Cancelled);
        Assert.Empty(SagaHistory.Rollbacks(await rig.HistoryAsync(run)));
    }

    [Theory]
    [InlineData(A2aState.Failed)]
    [InlineData(A2aState.Rejected)]
    [InlineData(A2aState.Canceled)]
    public async Task A_task_the_agent_ended_is_not_cancelled_when_the_saga_rolls_back(A2aState final)
    {
        var agent = new FakeAgent { WorkingPolls = 1, Final = final };
        A2aRig rig = InSaga(agent);
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));

        Assert.Empty(agent.Cancelled);
        Assert.Empty(SagaHistory.Rollbacks(await rig.HistoryAsync(run)));
    }

    private sealed class FlakyCancel(int failures) : IA2aAgentEndpoint
    {
        private readonly FakeAgent _inner = new() { WorkingPolls = int.MaxValue };
        private int _attempts;

        public int CancelAttempts => Volatile.Read(ref _attempts);

        public List<string> Cancelled => _inner.Cancelled;

        public ValueTask<A2aReply> SendAsync(string messageId, string? text, string? dataJson, string? taskId, string? contextId, CancellationToken cancellationToken) =>
            _inner.SendAsync(messageId, text, dataJson, taskId, contextId, cancellationToken);

        public ValueTask<A2aReply> GetTaskAsync(string taskId, CancellationToken cancellationToken) => _inner.GetTaskAsync(taskId, cancellationToken);

        public ValueTask CancelTaskAsync(string taskId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) <= failures)
            {
                throw new IOException("agent unreachable");
            }

            return _inner.CancelTaskAsync(taskId, cancellationToken);
        }
    }

    [Fact]
    public async Task A_cancel_that_does_not_reach_the_agent_is_retried_by_the_rollback_until_it_does()
    {
        var agent = new FlakyCancel(failures: 3);
        A2aRig rig = new(b => b.AddAgent("billing", agent).ReadOnlyAgents("billing"));
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga => await ctx.CallA2aAgentAsync("billing", "Pay invoice 42"));
            return [];
        });
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run, jumpClock: false);

        await new AgentClient(rig.Client, rig.Time).RequestCancelAsync(run, "stop");
        Assert.Equal(RunState.Failed, await rig.RunAsync(run));

        Assert.Equal(["remote-1"], agent.Cancelled);
        Assert.Equal(4, agent.CancelAttempts);
        Assert.Null(SagaHistory.CheckCompensationsComplete(await rig.HistoryAsync(run)));
    }

    [Fact]
    public async Task After_an_inner_saga_ends_the_outer_one_still_cancels_the_call()
    {
        var agent = new FakeAgent { WorkingPolls = int.MaxValue };
        A2aRig rig = A2aRig.For(agent);
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async outer =>
            {
                await ctx.RunSagaAsync(async inner => await ctx.Workflow.DelayAsync(TimeSpan.FromSeconds(1)));
                await ctx.CallA2aAgentAsync("billing", "Pay invoice 42");
            });
            return [];
        });
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run, jumpClock: false);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.RunAsync(run, jumpClock: false); // past the inner saga, now polling

        await new AgentClient(rig.Client, rig.Time).RequestCancelAsync(run, "stop");
        Assert.Equal(RunState.Failed, await rig.RunAsync(run, jumpClock: false));

        Assert.Equal(["remote-1"], agent.Cancelled);
    }

    [Fact]
    public async Task A_crash_at_any_store_operation_during_the_cancellation_still_cancels_the_remote_task_and_completes_the_rollback()
    {
        static async Task<(A2aRig Rig, FakeAgent Agent, RunKey Run)> Drive(long? crashAt, FaultKind kind)
        {
            var agent = new FakeAgent { WorkingPolls = int.MaxValue };
            A2aRig rig = InSaga(agent, faults: true);
            rig.Faults!.FaultsEnabled = false;
            if (crashAt is { } at)
            {
                rig.Faults.ForcedFault = index => index == at ? kind : FaultKind.None;
            }

            RunKey run = await rig.StartAsync("call");
            await rig.RunAsync(run, jumpClock: false);
            await A2aRig.Retry(async () => await new AgentClient(rig.Client, rig.Time).RequestCancelAsync(run, "stop").AsTask());
            Assert.Equal(RunState.Failed, await rig.RunAsync(run));
            return (rig, agent, run);
        }

        (A2aRig baseline, _, _) = await Drive(null, FaultKind.None);
        long operations = baseline.Faults!.OperationCount;
        Assert.True(operations > 10);

        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                (A2aRig rig, FakeAgent agent, RunKey run) = await Drive(k, kind);

                string where = $"crash {kind} at operation {k}";
                Assert.True(agent.Cancelled.Count >= 1 && agent.Cancelled.All(t => t == "remote-1"), $"{where}: cancelled {string.Join(',', agent.Cancelled)}");
                IReadOnlyList<HistoryEvent> history = await rig.HistoryAsync(run);
                Assert.True(SagaHistory.CheckCompensationsComplete(history) is null, $"{where}: rollback incomplete");
                Assert.Equal(nameof(SagaCancelledException), ((WorkflowFailed)history[^1]).ErrorType);
            }
        }
    }
}
