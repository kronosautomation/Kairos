using System.Text;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

public sealed class SagaTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    /// <summary>Activities that apply an effect keyed by idempotency key, and one that can be made to fail.</summary>
    private static AgentHarness Harness(Action<AgentsBuilder>? more = null, int chargeFailsTerminally = 0)
    {
        LogHolder.Value = [];
        AgentHarness h = null!;
        h = new AgentHarness(b =>
        {
            b.AddModel("m", AgentHarness.Echo());
            more?.Invoke(b);
        });
        foreach (string name in new[] { "book:flight", "book:hotel", "cancel:flight", "cancel:hotel" })
        {
            h.Activity(name, (ctx, _, _) =>
            {
                h.External.Apply(ctx.IdempotencyKey);
                lock (Log)
                {
                    Log.Add(name);
                }

                return Task.FromResult(new[] { (byte)1 });
            });
        }

        h.Activity("charge", (ctx, _, _) =>
        {
            lock (Log)
            {
                Log.Add("charge");
            }

            if (chargeFailsTerminally > 0)
            {
                throw new InvalidOperationException("card declined");
            }

            return Task.FromResult(new[] { (byte)1 });
        });
        return h;
    }

    // Per test: a fresh log, so tests can run in parallel with their own harness.
    private static readonly AsyncLocal<List<string>> LogHolder = new();

    private static List<string> Log => LogHolder.Value!;

    private static readonly byte[] Empty = [];

    private static RetryPolicy NoRetry => RetryPolicy.NoRetry;

    /// <summary>Book flight, book hotel, charge. If charging fails everything booked is undone.</summary>
    private static Func<IAgentContext, byte[], Task<byte[]>> Trip() => async (ctx, _) =>
    {
        await ctx.RunSagaAsync(async saga =>
        {
            await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
            saga.Compensate("flight", "cancel:flight", Empty);
            await ctx.Workflow.ExecuteActivityAsync("book:hotel", Empty, NoRetry);
            saga.Compensate("hotel", "cancel:hotel", Empty);
            await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry);
            return 0;
        });
        return Encoding.UTF8.GetBytes("booked");
    };

    [Fact]
    public async Task A_saga_that_succeeds_runs_no_compensation()
    {
        AgentHarness h = Harness();
        h.Workflow("trip", Trip());

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal("booked", await h.ResultTextAsync(run));
        Assert.Equal(["book:flight", "book:hotel", "charge"], Log);
        Assert.Empty(SagaHistory.Rollbacks(await h.HistoryAsync(run)));
    }

    [Fact]
    public async Task A_failing_step_undoes_the_earlier_steps_in_reverse_order_and_rethrows_the_original_failure()
    {
        AgentHarness h = Harness(chargeFailsTerminally: 1);
        h.Workflow("trip", Trip());

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(["book:flight", "book:hotel", "charge", "cancel:hotel", "cancel:flight"], Log);
        Assert.Equal(nameof(ActivityFailureException), (await h.FailureAsync(run)).ErrorType);
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        SagaRollback rollback = Assert.Single(SagaHistory.Rollbacks(history));
        Assert.Equal(["hotel", "flight"], rollback.Planned);
        Assert.Equal(["hotel", "flight"], rollback.Finished);
        Assert.True(rollback.Ended);
        Assert.Null(SagaHistory.CheckCompensationsComplete(history));
        Assert.Equal(4, h.External.Effects.Count); // two bookings, two cancellations, each once
    }

    [Fact]
    public async Task A_compensation_is_retried_until_it_works()
    {
        AgentHarness h = Harness(chargeFailsTerminally: 1);
        int cancels = 0;
        h.Activity("cancel:hotel", (ctx, _, _) =>
        {
            h.External.Apply(ctx.IdempotencyKey);
            if (++cancels < 7)
            {
                throw new InvalidOperationException("hotel system is down");
            }

            return Task.FromResult(new[] { (byte)1 });
        });
        h.Workflow("trip", Trip());

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(7, cancels);
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        Assert.Null(SagaHistory.CheckCompensationsComplete(history));
        Assert.Equal(nameof(ActivityFailureException), (await h.FailureAsync(run)).ErrorType); // rolled back completely: the original failure
        Assert.Contains("cancel:flight", Log);
    }

    [Fact]
    public async Task A_compensation_that_can_never_finish_is_reported_and_the_others_still_run()
    {
        AgentHarness h = Harness(chargeFailsTerminally: 1);
        h.Activity("cancel:hotel", (_, _, _) => throw new InvalidOperationException("hotel closed for good"));
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                saga.Compensate("flight", "cancel:flight", Empty);
                await ctx.Workflow.ExecuteActivityAsync("book:hotel", Empty, NoRetry);
                saga.Compensate("hotel", policy => ctx.Workflow.ExecuteActivityAsync(
                    "cancel:hotel", Empty, policy with { MaximumAttempts = 3, InitialInterval = TimeSpan.FromSeconds(1) }));
                await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry);
            });
            return [];
        });

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(nameof(SagaCompensationException), (await h.FailureAsync(run)).ErrorType);
        Assert.Contains("hotel", (await h.FailureAsync(run)).Message, StringComparison.Ordinal);
        Assert.Contains("cancel:flight", Log); // the failure of one undo does not stop the rest
        SagaRollback rollback = Assert.Single(SagaHistory.Rollbacks(await h.HistoryAsync(run)));
        Assert.Equal(["hotel"], rollback.Failed);
        Assert.Equal(["hotel", "flight"], rollback.Finished);
    }

    [Fact]
    public async Task A_rejected_approval_rolls_the_work_back()
    {
        AgentHarness h = Harness();
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                saga.Compensate("flight", "cancel:flight", Empty);
                (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Charge the card" })).ThrowIfNotApproved();
                await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry);
            });
            return [];
        });

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.RejectAsync(run, id, Alice, "no");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(["book:flight", "cancel:flight"], Log);
        Assert.Equal(nameof(ApprovalRejectedException), (await h.FailureAsync(run)).ErrorType);
    }

    [Fact]
    public async Task A_cancel_request_ends_a_wait_for_approval_and_rolls_back()
    {
        AgentHarness h = Harness();
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                saga.Compensate("flight", "cancel:flight", Empty);
                await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Charge the card" });
                await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry);
            });
            return [];
        });

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Running, await h.RunAsync(run));

        await h.Agent.RequestCancelAsync(run, "customer changed their mind");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(["book:flight", "cancel:flight"], Log);
        WorkflowFailed failed = await h.FailureAsync(run);
        Assert.Equal(nameof(SagaCancelledException), failed.ErrorType);
        Assert.Contains("customer changed their mind", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approval_that_arrives_after_the_cancel_changes_nothing()
    {
        AgentHarness h = Harness();
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                saga.Compensate("flight", "cancel:flight", Empty);
                (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Charge the card" })).ThrowIfNotApproved();
                await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry);
            });
            return Empty;
        });
        RunKey run = await h.StartAsync("trip");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.RequestCancelAsync(run, "stop");
        await h.Agent.ApproveAsync(run, id, Alice); // delivered in the same breath as the cancel, before the run reacted
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.DoesNotContain("charge", Log);
        Assert.Equal(["book:flight", "cancel:flight"], Log);
        Assert.False(await h.Agent.ApproveAsync(run, id, Alice)); // and once the run is closed nothing more is accepted
    }

    [Fact]
    public async Task A_cancel_is_noticed_at_the_next_agent_step_and_nothing_in_flight_is_abandoned()
    {
        AgentHarness h = Harness();
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                saga.Compensate("flight", "cancel:flight", Empty);
                await ctx.Workflow.WaitForSignalAsync("go");
                await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "next")]); // must not be called: the cancel arrived first
            });
            return [];
        });

        RunKey run = await h.StartAsync("trip");
        await h.RunAsync(run);
        await h.Agent.RequestCancelAsync(run, "stop");
        await h.Client.SignalAsync(run, "go", [], signalId: "go");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.DoesNotContain((await h.HistoryAsync(run)).OfType<ActivityScheduled>(), a => a.ActivityType == AgentActivityNames.Chat);
        Assert.Equal(["book:flight", "cancel:flight"], Log);
    }

    [Fact]
    public async Task Nested_sagas_each_undo_their_own_work()
    {
        AgentHarness h = Harness(chargeFailsTerminally: 1);
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async outer =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                outer.Compensate("flight", "cancel:flight", Empty);
                await ctx.RunSagaAsync(async inner =>
                {
                    await ctx.Workflow.ExecuteActivityAsync("book:hotel", Empty, NoRetry);
                    inner.Compensate("hotel", "cancel:hotel", Empty);
                    await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry);
                });
            });
            return [];
        });

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(["book:flight", "book:hotel", "charge", "cancel:hotel", "cancel:flight"], Log);
        Assert.Equal(2, SagaHistory.Rollbacks(await h.HistoryAsync(run)).Count);
        Assert.Null(SagaHistory.CheckCompensationsComplete(await h.HistoryAsync(run)));
    }

    [Fact]
    public async Task Continuing_as_new_with_compensations_registered_is_refused_and_rolls_back()
    {
        AgentHarness h = Harness();
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty, NoRetry);
                saga.Compensate("flight", "cancel:flight", Empty);
                throw ctx.Workflow.ContinueAsNew(new byte[] { 1 });
            });
            return [];
        });

        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Equal(nameof(InvalidOperationException), (await h.FailureAsync(run)).ErrorType);
        Assert.Equal(["book:flight", "cancel:flight"], Log);
    }

    [Fact]
    public async Task A_saga_with_nothing_registered_fails_like_any_workflow()
    {
        AgentHarness h = Harness(chargeFailsTerminally: 1);
        h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async _ => await ctx.Workflow.ExecuteActivityAsync("charge", Empty, NoRetry));
            return [];
        });
        RunKey run = await h.StartAsync("trip");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Empty(SagaHistory.Rollbacks(await h.HistoryAsync(run)));
    }

    [Fact]
    public async Task A_workflow_without_marker_support_still_rolls_back()
    {
        // A worker that does not have the marker activity registered must still undo the work: markers are best effort.
        var activities = new ActivityRegistry();
        var workflows = new WorkflowRegistry();
        var store = new InMemoryKairosStore();
        var time = new Kairos.Testing.VirtualTimeProvider();
        var undone = new List<string>();
        activities.Register("do", (_, _, _) => Task.FromResult(new[] { (byte)1 }));
        activities.Register("undo", (_, _, _) =>
        {
            undone.Add("undo");
            return Task.FromResult(new[] { (byte)1 });
        });
        workflows.Register("w", KairosClient.DefaultBuildId, async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.ExecuteActivityAsync("do", Empty, RetryPolicy.NoRetry);
                saga.Compensate("a", "undo", Empty);
                throw new InvalidOperationException("boom");
            });
            return [];
        });
        var scope = new TenantScope(Guid.NewGuid(), Guid.NewGuid());
        var client = new KairosClient(store, time);
        StartRunResult start = await client.StartAsync(scope, "w1", "w", []);
        var worker = new WorkerRuntime(store, scope, workflows, activities, time, new WorkerOptions { AutoHeartbeat = false, FailedTaskRetryDelay = TimeSpan.FromSeconds(1) });

        for (int i = 0; i < 40 && await client.GetStateAsync(new RunKey(scope.TenantId, scope.NamespaceId, start.RunId)) == RunState.Running; i++)
        {
            await Kairos.Testing.Driver.DriveAsync(worker);
            if (store.NextWakeup(scope, time.GetUtcNow()) is { } wake)
            {
                time.AdvanceTo(wake);
            }
        }

        Assert.Equal(["undo"], undone);
    }
}
