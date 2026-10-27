using Kairos.Agents;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// A durable workflow killed at every store operation, before and after the store applied it: the outcome is the fault-free one, a recorded superstep is never run again, every request is answered once, and an executor runs
/// at most once more than it would have (the superstep that was running).
/// </summary>
public sealed class WorkflowCrashTests
{
    private static readonly ApproverIdentity Alice = WorkflowTests.Alice;

    private static string Outcome(WorkflowView view) =>
        $"{view.Status}: {string.Join(" | ", view.Outputs.Select(o => o.As<string>()!).Order(StringComparer.Ordinal))}{(view.Status is DurableWorkflowStatus.Failed ? " [" + view.Error + "]" : "")}";

    [Fact]
    public async Task A_fan_out_with_a_decision_in_the_middle_survives_a_crash_at_any_store_operation()
    {
        var ledgers = new List<Ledger>();
        AfHarness Build()
        {
            var ledger = new Ledger();
            ledgers.Add(ledger);
            return new AfHarness(
                af => af.AddWorkflow<string>("fan", _ => TestWorkflows.FanOutWithDecision(ledger), r => r.RequestsAsApprovals("approve", new ApprovalRequirement { Roles = ["finance"] })), faultInjection: true);
        }

        await CrashMatrixTests.MatrixAsync(
            Build,
            async h =>
            {
                DurableWorkflowRun run = await AfHarness.Retry(async () => await h.Workflows.Get("fan").StartAsync("x", "w1").AsTask());
                await h.DriveAsync();
                WorkflowView waiting = await WorkflowTests.ReadAsync(run);
                if (waiting.Status != DurableWorkflowStatus.Waiting)
                {
                    return "not waiting: " + Outcome(waiting);
                }

                string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run.Run)).Single().ApprovalId;
                await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run.Run, id, Alice).AsTask());
                await h.DriveAsync();
                return Outcome(await WorkflowTests.ReadAsync(run));
            },
            (h, where) =>
            {
                Ledger ledger = ledgers[^1];
                foreach (string executor in new[] { "split", "prepare", "c1", "c2", "end", "conclude" })
                {
                    Assert.True(ledger.Runs(executor) is >= 1 and <= 2, $"{where}: executor '{executor}' ran {ledger.Runs(executor)} times.");
                }

                return Task.CompletedTask;
            },
            minimumOperations: 40);
    }

    [Fact]
    public async Task A_request_answered_by_signal_survives_a_crash_at_any_store_operation_and_is_answered_once()
    {
        var ledgers = new List<Ledger>();
        AfHarness Build()
        {
            var ledger = new Ledger();
            ledgers.Add(ledger);
            return new AfHarness(af => af.AddWorkflow<string>("question", _ => TestWorkflows.Question(ledger)), faultInjection: true);
        }

        await CrashMatrixTests.MatrixAsync(
            Build,
            async h =>
            {
                DurableWorkflowRun run = await AfHarness.Retry(async () => await h.Workflows.Get("question").StartAsync("x", "w1").AsTask());
                await h.DriveAsync();
                WorkflowView waiting = await WorkflowTests.ReadAsync(run);
                if (waiting.Pending.Count != 1)
                {
                    return "not waiting: " + Outcome(waiting);
                }

                await AfHarness.Retry(async () => await run.RespondAsync(waiting.Pending[0].RequestId, "blue").AsTask());
                await h.DriveAsync();
                return Outcome(await WorkflowTests.ReadAsync(run));
            },
            (h, where) =>
            {
                Assert.True(ledgers[^1].Runs("conclude") is 1 or 2, $"{where}: the concluding executor ran {ledgers[^1].Runs("conclude")} times.");
                return Task.CompletedTask;
            },
            minimumOperations: 25);
    }

    [Fact]
    public async Task A_durable_agent_in_an_executor_is_called_once_in_the_world_whatever_dies_and_whenever()
    {
        var worlds = new List<(Ledger Ledger, ScriptedChatClient Model)>();
        AfHarness Build()
        {
            var ledger = new Ledger();
            ScriptedChatClient model = AfHarness.Echo("an old pond");
            worlds.Add((ledger, model));
            var h = new AfHarness(
                af =>
                {
                    af.Configure(o =>
                    {
                        o.SuperstepsPerStep = 2;
                        o.WorkflowAgentWait = TimeSpan.FromSeconds(20); // a step that waits for ever is a failure of this test, found in seconds
                    });
                    af.AddAgent(AfHarness.AgentWith(model, "writer"));
                    af.AddWorkflow<string>("agentic", ctx => TestWorkflows.AgentThenQuestion(ledger, ctx.Agents));
                },
                faultInjection: true);
            h.RunConversationsFromTheWaitingExecutor();
            return h;
        }

        await CrashMatrixTests.MatrixAsync(
            Build,
            async h =>
            {
                DurableWorkflowRun run = await AfHarness.Retry(async () => await h.Workflows.Get("agentic").StartAsync("write a haiku", "w1").AsTask());
                await h.DriveAsync();
                WorkflowView waiting = await WorkflowTests.ReadAsync(run);
                if (waiting.Pending.Count != 1)
                {
                    return "not waiting: " + Outcome(waiting);
                }

                await AfHarness.Retry(async () => await run.RespondAsync(waiting.Pending[0].RequestId, "nice").AsTask());
                await h.DriveAsync();
                return Outcome(await WorkflowTests.ReadAsync(run));
            },
            (h, where) =>
            {
                (Ledger ledger, ScriptedChatClient model) = worlds[^1];
                Assert.True(model.CallCount is >= 1 and <= 2, $"{where}: the model was called {model.CallCount} times (a model call whose result died with the worker may be made once more, never more).");
                Assert.True(ledger.Runs("write") is >= 1 and <= 3, $"{where}: the executor that asks the agent ran {ledger.Runs("write")} times.");
                return Task.CompletedTask;
            },
            minimumOperations: 30);
    }
}
