using Kairos.Agents;
using Kairos.Testing.Simulation;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>A request raised by an executor waits for a person without holding compute: as an approval when its port is registered as one, otherwise as a signal.</summary>
public sealed class WorkflowRequestTests
{
    private static readonly ApproverIdentity Alice = WorkflowTests.Alice;

    internal static Action<AgentFrameworkBuilder> Registered(Ledger ledger) => af =>
    {
        af.AddWorkflow<string>("approval", _ => TestWorkflows.Approval(ledger), r => r.RequestsAsApprovals("approve", new ApprovalRequirement { Roles = ["finance"] }));
        af.AddWorkflow<string>("question", _ => TestWorkflows.Question(ledger));
    };

    private static async Task<string> PendingApprovalAsync(AfHarness h, DurableWorkflowRun run) =>
        AgentHistory.PendingApprovals(await h.HistoryAsync(run.Run)).Single().ApprovalId;

    [Fact]
    public async Task A_request_through_an_approval_port_waits_for_a_person_and_the_workflow_goes_on_with_the_decision()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "approval", "refund A-1");

        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);

        Assert.Equal(DurableWorkflowStatus.Waiting, waiting.Status);
        WorkflowRequest request = Assert.Single(waiting.Pending);
        Assert.True(request.IsApproval);
        Assert.Equal("approve", request.PortId);
        Assert.Equal("refund A-1", request.As<Ticket>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Body);

        // The approval says what is being decided: the port, and the request.
        PendingApproval asked = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run.Run)));
        Assert.Contains("approve", asked.Action, StringComparison.Ordinal);
        Assert.Contains("refund A-1", asked.Details, StringComparison.Ordinal);

        await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run.Run, await PendingApprovalAsync(h, run), Alice).AsTask());
        WorkflowView done = await WorkflowTests.FinishAsync(h, run);

        Assert.True(done.Status == DurableWorkflowStatus.Completed, done.Error);
        Assert.Equal("approved", Assert.Single(done.Outputs).As<string>());
        Assert.Empty(done.Pending);
        Assert.Equal(1, ledger.Runs("prepare")); // the superstep before the request was not run again
        Assert.Equal(1, ledger.Runs("conclude"));
    }

    [Fact]
    public async Task A_rejection_is_an_answer_too()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "approval", "x");
        await WorkflowTests.FinishAsync(h, run);

        await AfHarness.Retry(async () => await h.Approvals.RejectAsync(run.Run, await PendingApprovalAsync(h, run), Alice, "no").AsTask());
        WorkflowView done = await WorkflowTests.FinishAsync(h, run);

        Assert.Equal("rejected", Assert.Single(done.Outputs).As<string>());
    }

    [Fact]
    public async Task The_approval_is_decided_by_a_verified_approver_who_is_recorded_and_a_signal_cannot_stand_in_for_it()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "approval", "x");
        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await run.RespondAsync(waiting.Pending[0].RequestId, true));
        Assert.Contains("approval", refused.Message, StringComparison.Ordinal);

        await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run.Run, await PendingApprovalAsync(h, run), Alice).AsTask());
        await WorkflowTests.FinishAsync(h, run);

        ApprovalAuditRecord record = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run.Run)).OfType<ApprovalAuditRecord>());
        Assert.Equal("user:alice", record.Approver);
    }

    [Fact]
    public async Task A_request_through_any_other_port_is_answered_by_a_signal_and_answering_twice_is_one_answer()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "question", "what colour?");

        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);
        WorkflowRequest request = Assert.Single(waiting.Pending);
        Assert.False(request.IsApproval);
        Assert.Equal("ask", request.PortId);
        Assert.Equal(2, waiting.Steps); // the superstep that prepared the ticket and the one that raised the request

        Assert.True(await AfHarness.Retry(async () => await run.RespondAsync(request.RequestId, "blue").AsTask()));
        Assert.True(await AfHarness.Retry(async () => await run.RespondAsync(request.RequestId, "blue").AsTask())); // taken again, not answered again
        WorkflowView done = await WorkflowTests.FinishAsync(h, run);

        Assert.True(done.Status == DurableWorkflowStatus.Completed, done.Error);
        Assert.Equal("answered: blue", Assert.Single(done.Outputs).As<string>());
        Assert.Single((await h.HistoryAsync(run.Run)).OfType<SignalReceived>(), s => s.SignalName.StartsWith("af.workflow.response/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_that_stays_open_while_the_rest_of_the_workflow_runs_is_asked_once()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, af => af.AddWorkflow<string>("fan", _ => TestWorkflows.FanOutWithDecision(ledger), r => r.RequestsAsApprovals("approve", new ApprovalRequirement { Roles = ["finance"] })));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "fan", "x");

        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);

        Assert.Equal(DurableWorkflowStatus.Waiting, waiting.Status);
        Assert.Equal("x>c1>c2", Assert.Single(waiting.Outputs).As<string>()); // the other branch ran to its end in steps that all listed the open request
        Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run.Run))); // and the person was asked once
    }

    [Fact]
    public async Task A_waiting_workflow_holds_nothing_and_asking_where_it_is_costs_what_is_new()
    {
        var ledger = new Ledger();
        var counting = new List<CountingStore>();
        AfHarness h = WorkflowTests.Harness(ledger, Registered(ledger), wrap: store =>
        {
            var c = new CountingStore(store);
            counting.Add(c);
            return c;
        });
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "question", "x");
        await h.DriveAsync();

        WorkflowView first = await WorkflowTests.ReadAsync(run);
        long before = counting[0].Events;
        WorkflowView again = await WorkflowTests.ReadAsync(run);

        Assert.Equal(DurableWorkflowStatus.Waiting, first.Status);
        Assert.Equal(first.Pending[0].RequestId, again.Pending[0].RequestId);
        Assert.Equal(0, counting[0].Events - before); // nothing was written, so nothing was read
        Assert.Null(h.MemoryStore.NextWakeup(AfHarness.Scope, h.Time.GetUtcNow())); // no timer, no task: nothing is waiting for a worker
    }

    [Fact]
    public async Task Another_worker_takes_the_next_step_from_the_recorded_checkpoint_and_the_one_before_the_request_is_not_run_again()
    {
        var ledger = new Ledger();
        AfHarness first = WorkflowTests.Harness(ledger, Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(first, "approval", "x");
        await WorkflowTests.FinishAsync(first, run);

        // A redeploy: new workers with nothing in their cache. The step that follows the person's answer asks for the checkpoint and gets it.
        var ledger2 = new Ledger();
        AfHarness second = new(Registered(ledger2).Combine(af => af.AddWorkflow<string>("chain", _ => TestWorkflows.Chain(ledger2))), redeployOf: first);
        await AfHarness.Retry(async () => await second.Approvals.ApproveAsync(run.Run, await PendingApprovalAsync(second, run), Alice).AsTask());
        WorkflowView done = await WorkflowTests.FinishAsync(second, run);

        Assert.True(done.Status == DurableWorkflowStatus.Completed, done.Error);
        Assert.Equal("approved", Assert.Single(done.Outputs).As<string>());
        Assert.Equal(0, ledger2.Runs("prepare"));
        Assert.Equal(1, ledger2.Runs("conclude"));
        IReadOnlyList<HistoryEvent> history = await second.HistoryAsync(run.Run);
        Assert.Contains(history.OfType<ActivityCompleted>(), c => System.Text.Encoding.UTF8.GetString(c.Result).Contains("need-snapshot", StringComparison.Ordinal));
    }
}

internal static class BuilderExtensions
{
    public static Action<AgentFrameworkBuilder> Combine(this Action<AgentFrameworkBuilder> first, Action<AgentFrameworkBuilder> second) => b =>
    {
        first(b);
        second(b);
    };
}
