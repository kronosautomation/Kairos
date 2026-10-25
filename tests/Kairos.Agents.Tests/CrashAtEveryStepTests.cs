using System.Text;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

/// <summary>
/// Layer 4 for the agent layer. For each scenario: count the store operations of a fault-free run, then kill the worker at every single
/// operation boundary (before the store applied the call, and after it applied but before the caller heard), restart, and require the same
/// outcome, a verified history, exactly-once effects (I3) and, for rollbacks, complete compensation (I11).
/// </summary>
public sealed class CrashAtEveryStepTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly byte[] Empty = [];
    private static readonly string[] EffectTypes = ["book:flight", "book:hotel", "cancel:flight", "cancel:hotel", "refund"];

    private sealed record Scenario(string Name, Action<AgentHarness> Setup, Func<AgentHarness, Task<RunKey>> Drive, Func<AgentHarness, RunKey, Task>? Extra = null);

    private static AgentHarness Build(Scenario scenario, Func<AgentsBuilder, AgentsBuilder>? configure = null)
    {
        var h = new AgentHarness(b =>
        {
            b.AddModel("m", AgentHarness.Echo()).ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement(Roles: ["finance"])))
                .AddTool(AgentHarness.WriteTool("refund", _ => "refunded"));
            configure?.Invoke(b);
        }, faultInjection: true);
        h.Faults!.FaultsEnabled = false;
        foreach (string type in EffectTypes.Where(t => t.Contains(':', StringComparison.Ordinal)))
        {
            h.Activity(type, (ctx, _, _) =>
            {
                h.External.Apply(ctx.IdempotencyKey);
                return Task.FromResult(new byte[] { 1 });
            });
        }

        h.Activity("charge", (_, _, _) => throw new InvalidOperationException("card declined"));
        scenario.Setup(h);
        return h;
    }

    private static readonly Scenario TripRollback = new(
        "trip-rollback",
        h => h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty);
                saga.Compensate("flight", "cancel:flight", Empty);
                await ctx.Workflow.ExecuteActivityAsync("book:hotel", Empty);
                saga.Compensate("hotel", "cancel:hotel", Empty);
                await ctx.Workflow.ExecuteActivityAsync("charge", Empty, RetryPolicy.NoRetry);
            });
            return Empty;
        }),
        async h =>
        {
            RunKey run = await h.StartAsync("trip");
            Assert.Equal(RunState.Failed, await h.RunAsync(run));
            return run;
        });

    private static readonly Scenario RejectedApproval = new(
        "rejected-approval-rollback",
        h => h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty);
                saga.Compensate("flight", "cancel:flight", Empty);
                (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "charge", Requirement = new ApprovalRequirement(Roles: ["finance"]) })).ThrowIfNotApproved();
            });
            return Empty;
        }),
        async h =>
        {
            RunKey run = await h.StartAsync("trip");
            Assert.Equal(RunState.Running, await h.RunAsync(run));
            string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
            await AgentHarness.Retry(async () => await h.Agent.RejectAsync(run, id, Alice).AsTask());
            Assert.Equal(RunState.Failed, await h.RunAsync(run));
            return run;
        });

    private static readonly Scenario CancelledSaga = new(
        "cancelled-while-waiting",
        h => h.Workflow("trip", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("book:flight", Empty);
                saga.Compensate("flight", "cancel:flight", Empty);
                await ctx.Workflow.ExecuteActivityAsync("book:hotel", Empty);
                saga.Compensate("hotel", "cancel:hotel", Empty);
                await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "charge" });
            });
            return Empty;
        }),
        async h =>
        {
            RunKey run = await h.StartAsync("trip");
            Assert.Equal(RunState.Running, await h.RunAsync(run));
            await AgentHarness.Retry(async () => await h.Agent.RequestCancelAsync(run, "stop").AsTask());
            Assert.Equal(RunState.Failed, await h.RunAsync(run));
            return run;
        });

    private static readonly Scenario ApprovedWrite = new(
        "approved-write-tool",
        h => h.Workflow("call", async (ctx, _) =>
        {
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", null));
            return Encoding.UTF8.GetBytes(r.Outcome.ToString());
        }),
        async h =>
        {
            RunKey run = await h.StartAsync("call");
            Assert.Equal(RunState.Running, await h.RunAsync(run));
            string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
            await AgentHarness.Retry(async () => await h.Agent.ApproveAsync(run, id, Alice).AsTask());
            Assert.Equal(RunState.Completed, await h.RunAsync(run));
            return run;
        });

    public static TheoryData<string> Names() => ["trip-rollback", "rejected-approval-rollback", "cancelled-while-waiting", "approved-write-tool"];

    private static Scenario Pick(string name) => name switch
    {
        "trip-rollback" => TripRollback,
        "rejected-approval-rollback" => RejectedApproval,
        "cancelled-while-waiting" => CancelledSaga,
        _ => ApprovedWrite,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Surviving_a_crash_at_any_store_operation_changes_nothing(string name)
    {
        Scenario scenario = Pick(name);

        AgentHarness baseline = Build(scenario);
        RunKey baseRun = await scenario.Drive(baseline);
        string expected = await SignatureAsync(baseline, baseRun);
        long operations = baseline.Faults!.OperationCount;
        Assert.True(operations > 10, "The reference run did too little to be a meaningful crash matrix.");

        int crashes = 0;
        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                AgentHarness h = Build(scenario);
                long at = k;
                FaultKind crashKind = kind;
                h.Faults!.ForcedFault = index => index == at ? crashKind : FaultKind.None;

                RunKey run = await scenario.Drive(h);
                crashes += h.Faults.OperationCount > at ? 1 : 0;

                string where = $"{name}: crash {kind} at store operation {k}";
                string actual = await SignatureAsync(h, run);
                Assert.True(expected == actual, $"{where}: outcome differs from the fault-free run. expected {expected} actual {actual}");
                await AssertSoundAsync(h, run, where);
            }
        }

        Assert.True(crashes > 0, "No crash was actually injected.");
    }

    private static async Task<string> SignatureAsync(AgentHarness h, RunKey run)
    {
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        string steps = string.Join(',', history.OfType<ActivityScheduled>().Select(a => a.ActivityType));
        string end = history[^1] switch
        {
            WorkflowFailed f => "failed:" + f.ErrorType,
            WorkflowCompleted c => "completed:" + Encoding.UTF8.GetString(c.Result),
            _ => history[^1].Type.ToString(),
        };
        return $"{steps}|{end}";
    }

    private static async Task AssertSoundAsync(AgentHarness h, RunKey run, string where)
    {
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        HashChain.Verify(history); // I2 and I6

        // I11: every compensation that began completed, in order.
        string? violation = SagaHistory.CheckCompensationsComplete(history);
        Assert.True(violation is null, $"{where}: I11 violated: {violation}");

        // I3: the outside world saw exactly the keys of the effectful steps that were scheduled, each effect once.
        var scheduled = history.OfType<ActivityScheduled>().Where(a => EffectTypes.Contains(a.ActivityType)).ToList();
        var expectedKeys = scheduled.Select(a => $"{run.RunId:N}:{a.ActivityId}").ToHashSet(StringComparer.Ordinal);
        foreach (string key in h.External.Effects)
        {
            Assert.True(expectedKeys.Contains(key), $"{where}: effect for key {key} that no history scheduled.");
        }

        foreach (ActivityCompleted done in history.OfType<ActivityCompleted>())
        {
            ActivityScheduled? step = scheduled.Find(s => s.ActivityId == done.ActivityId);
            if (step is { ActivityType: not "refund" })
            {
                Assert.True(h.External.Effects.Contains($"{run.RunId:N}:{done.ActivityId}"), $"{where}: {step.ActivityType} completed without its effect.");
            }
        }
    }

    // ---- model calls under crashes ---------------------------------------------------------------------------

    [Fact]
    public async Task Model_calls_under_crashes_never_exceed_unique_steps_plus_the_crashes_and_always_reuse_the_steps_key()
    {
        ScriptedChatClient probe = AgentHarness.CallsToolThenAnswers("lookup");
        var scenario = new Scenario(
            "agent-loop",
            h => h.Workflow("agent", async (ctx, _) => AgentHarness.Bytes(await AgentHarness.AgentLoopAsync(ctx, "m", "go", "lookup"))),
            async h =>
            {
                RunKey run = await h.StartAsync("agent");
                Assert.Equal(RunState.Completed, await h.RunAsync(run));
                return run;
            });

        long operations;
        {
            ScriptedChatClient model = AgentHarness.CallsToolThenAnswers("lookup");
            AgentHarness baseline = Build(scenario, b => b.AddModel("m", model).AddTool(AgentHarness.ReadTool("lookup", _ => "42")));
            await scenario.Drive(baseline);
            operations = baseline.Faults!.OperationCount;
            Assert.Equal(2, model.CallCount);
        }

        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                ScriptedChatClient model = AgentHarness.CallsToolThenAnswers("lookup");
                AgentHarness h = Build(scenario, b => b.AddModel("m", model).AddTool(AgentHarness.ReadTool("lookup", _ => "42")));
                long at = k;
                FaultKind crashKind = kind;
                h.Faults!.ForcedFault = index => index == at ? crashKind : FaultKind.None;

                RunKey run = await scenario.Drive(h);

                string where = $"crash {kind} at store operation {k}";
                IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
                Assert.Equal("all done", AgentHarness.Text(((WorkflowCompleted)history[^1]).Result));
                // One crash can cost at most one repeated call (the answer was lost before it was recorded).
                Assert.True(model.CallCount is >= 2 and <= 3, $"{where}: {model.CallCount} model calls for 2 steps.");
                var keys = history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentActivityNames.Chat)
                    .Select(a => $"{run.RunId:N}:{a.ActivityId}").ToHashSet(StringComparer.Ordinal);
                Assert.All(model.Keys, key => Assert.True(keys.Contains(key!), $"{where}: model called with key {key}"));
            }
        }

        Assert.NotNull(probe);
    }
}
