using System.Text.Json;
using Kairos.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI.Workflows;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>What registration refuses, who may do what to a workflow run, what a redactor promises, how a workflow that changed is dealt with, and how a long run hands over.</summary>
public sealed class WorkflowSafetyTests
{
    private static readonly ApproverIdentity Alice = WorkflowTests.Alice;
    private static readonly ApproverIdentity Bob = new("user:bob", "Bob", ["support"]);
    private static readonly Principal Operator = new(AfHarness.Scope, Role.Operator, "ops", null);
    private static readonly Principal Reader = new(AfHarness.Scope, Role.Reader, "intern", null);

    private static InvalidOperationException Refused(Action<AgentFrameworkBuilder> configure) =>
        Assert.Throws<InvalidOperationException>(() => new AfHarness(configure));

    // ---- registration ------------------------------------------------------------------------------------------

    [Fact]
    public void A_workflow_whose_executor_ids_change_from_one_build_to_the_next_is_refused_at_registration()
    {
        var ledger = new Ledger();
        InvalidOperationException ex = Refused(af => af.AddWorkflow<string>("unstable", _ => TestWorkflows.Unstable(ledger)));
        Assert.Contains("explicit, stable ids", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'unstable'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approval_port_must_exist_and_must_be_answered_with_a_bool()
    {
        var ledger = new Ledger();
        InvalidOperationException missing = Refused(af => af.AddWorkflow<string>("w", _ => TestWorkflows.Approval(ledger), r => r.RequestsAsApprovals("nope")));
        Assert.Contains("\"nope\"", missing.Message, StringComparison.Ordinal);
        Assert.Contains("approve", missing.Message, StringComparison.Ordinal); // it lists the ports there are

        InvalidOperationException wrongType = Refused(af => af.AddWorkflow<string>("w", _ => TestWorkflows.Question(ledger), r => r.RequestsAsApprovals("ask")));
        Assert.Contains("'ask'", wrongType.Message, StringComparison.Ordinal);
        Assert.Contains("String", wrongType.Message, StringComparison.Ordinal);
        Assert.Contains("bool", wrongType.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_must_be_stable_and_unique()
    {
        var ledger = new Ledger();
        Assert.Contains("stable name", Refused(af => af.AddWorkflow<string>("no spaces!", _ => TestWorkflows.Chain(ledger))).Message, StringComparison.Ordinal);
        Assert.Contains("Two workflows", Refused(af => af.AddWorkflow<string>("w", _ => TestWorkflows.Chain(ledger)).AddWorkflow<string>("w", _ => TestWorkflows.Chain(ledger))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_a_workflow_that_is_not_registered_or_with_an_input_it_does_not_take_is_refused()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger);
        Assert.Throws<UnknownWorkflowException>(() => h.Workflows.Get("nobody"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await h.Workflows.Get("chain").StartAsync(42));
    }

    // ---- redaction ---------------------------------------------------------------------------------------------

    [Fact]
    public void With_a_redactor_a_type_the_workflow_passes_around_must_be_declared_and_the_error_names_the_type_and_the_option()
    {
        var ledger = new Ledger();
        InvalidOperationException ex = Refused(af => af.ConfigureAgents(b => b.UseRedactor(RedactionTests.Redact)).AddWorkflow<string>("approval", _ => TestWorkflows.Approval(ledger)));
        Assert.Contains("Ticket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AllowStateType<Ticket>()", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RedactState<Ticket>", ex.Message, StringComparison.Ordinal);

        // Declared free of text, or given a way into its text: accepted.
        _ = new AfHarness(af => af.ConfigureAgents(b => b.UseRedactor(RedactionTests.Redact)).AddWorkflow<string>("approval", _ => TestWorkflows.Approval(ledger), r => r.AllowStateType<Ticket>()));
        _ = new AfHarness(af => af.ConfigureAgents(b => b.UseRedactor(RedactionTests.Redact)).AddWorkflow<string>("approval", _ => TestWorkflows.Approval(ledger), r => r.RedactState<Ticket>(t => t with { Body = RedactionTests.Redact(t.Body) })));
    }

    [Fact]
    public async Task With_a_redactor_nothing_recorded_holds_the_text_not_the_input_the_messages_the_state_the_requests_or_the_outputs()
    {
        var ledger = new Ledger();
        AfHarness h = new(af => af
            .ConfigureAgents(b => b.UseRedactor(RedactionTests.Redact))
            .AddWorkflow<string>("stateful", _ => TestWorkflows.Stateful(ledger), r => r.RedactState<Ticket>(t => t with { Body = RedactionTests.Redact(t.Body) }))
            .AddWorkflow<string>("question", _ => TestWorkflows.Question(ledger), r => r.RedactState<Ticket>(t => t with { Body = RedactionTests.Redact(t.Body) })));

        DurableWorkflowRun stateful = await WorkflowTests.StartAsync(h, "stateful", $"my number is {RedactionTests_Secret}", "s1");
        WorkflowView done = await WorkflowTests.FinishAsync(h, stateful);
        Assert.True(done.Status == DurableWorkflowStatus.Completed, done.Error);
        Assert.Equal("my number is [redacted]", Assert.Single(done.Outputs).As<string>());
        Assert.DoesNotContain(RedactionTests_Secret, RedactionTests.Everything(await h.HistoryAsync(stateful.Run)), StringComparison.Ordinal);

        DurableWorkflowRun question = await WorkflowTests.StartAsync(h, "question", $"call me on {RedactionTests_Secret}", "q1");
        WorkflowView waiting = await WorkflowTests.FinishAsync(h, question);
        Assert.Equal("call me on [redacted]", waiting.Pending[0].As<Ticket>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Body);
        Assert.DoesNotContain(RedactionTests_Secret, RedactionTests.Everything(await h.HistoryAsync(question.Run)), StringComparison.Ordinal);
    }

    private const string RedactionTests_Secret = "SECRET-123-4567";

    [Fact]
    public async Task A_value_of_a_type_the_redactor_does_not_know_is_refused_when_it_is_recorded_and_never_skipped()
    {
        var ledger = new Ledger();

        // The state's type cannot be seen from the protocol, so registration accepts the workflow; the first checkpoint that holds it is refused, naming the type and the option.
        AfHarness h = new(af => af.ConfigureAgents(b => b.UseRedactor(RedactionTests.Redact)).AddWorkflow<string>("stateful", _ => TestWorkflows.Stateful(ledger)));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "stateful", "x");

        WorkflowView view = await WorkflowTests.FinishAsync(h, run);

        Assert.Equal(DurableWorkflowStatus.Failed, view.Status);
        Assert.Contains("SnapshotShapeException", view.Error, StringComparison.Ordinal);
        Assert.Contains("Ticket", view.Error, StringComparison.Ordinal);
        Assert.Contains("RedactState<T>", view.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(RedactionTests_Secret, RedactionTests.Everything(await h.HistoryAsync(run.Run)), StringComparison.Ordinal);
        Assert.Equal(1, ledger.Runs("remember")); // a failure that is the author's to fix is not retried
    }

    // ---- who may do what ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_operations_on_a_run_are_allowed_to_the_roles_that_may_do_them_and_an_approval_needs_the_right_verified_approver()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, WorkflowRequestTests.Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "approval", "x");
        await WorkflowTests.FinishAsync(h, run);

        // terminate: operators and above; a reader may only read
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await run.TerminateAsync(Reader, "no right"));
        Assert.Equal(DurableWorkflowStatus.Waiting, (await WorkflowTests.ReadAsync(run)).Status);

        // the approval needs the finance role the registration asked for: a verified approver without it is refused, and the workflow keeps waiting
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run.Run)).Single().ApprovalId;
        await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run.Run, id, Bob).AsTask());
        await h.DriveAsync();
        Assert.Equal(DurableWorkflowStatus.Waiting, (await WorkflowTests.ReadAsync(run)).Status);
        Assert.Equal(1, AgentAudit.Read(await h.HistoryAsync(run.Run)).OfType<ApprovalAuditRecord>().Sum(a => a.Refused));

        await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run.Run, id, Alice).AsTask());
        Assert.Equal(DurableWorkflowStatus.Completed, (await WorkflowTests.FinishAsync(h, run)).Status);

        // an operator may terminate a run that is open
        DurableWorkflowRun another = await WorkflowTests.StartAsync(h, "approval", "y", "w2");
        await WorkflowTests.FinishAsync(h, another);
        Assert.True(await AfHarness.Retry(async () => await another.TerminateAsync(Operator, "stuck").AsTask()));
        Assert.Equal(DurableWorkflowStatus.Terminated, (await WorkflowTests.ReadAsync(another)).Status);
    }

    [Fact]
    public async Task Another_tenants_workflow_cannot_be_found_read_answered_or_cancelled()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, WorkflowRequestTests.Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "question", "x");
        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);

        var otherScope = new TenantScope(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
        var other = new DurableWorkflows(h.Runtime, h.Client, otherScope, TimeProvider.System);

        Assert.Null(await other.Get("question").FindAsync("w1"));

        // Even holding the run's id, the other tenant's handle reads nothing, answers nothing, cancels nothing: the run is not there in its scope.
        DurableWorkflowRun foreign = new(other, h.Runtime.WorkflowDefinitionOf("question"), "w1", new RunKey(otherScope.TenantId, otherScope.NamespaceId, run.Run.RunId));
        WorkflowView seen = await foreign.ReadAsync();
        Assert.Equal(DurableWorkflowStatus.Running, seen.Status); // nothing of the run is visible
        Assert.Empty(seen.Pending);
        Assert.False(await foreign.RespondAsync(waiting.Pending[0].RequestId, "stolen"));
        Assert.False(await foreign.CancelAsync("not yours"));

        await h.DriveAsync();
        Assert.Equal(DurableWorkflowStatus.Waiting, (await WorkflowTests.ReadAsync(run)).Status); // untouched
    }

    // ---- a workflow that changed -------------------------------------------------------------------------------

    /// <summary>A deploy of a build with the same workflow and a newer Agent Framework package (whose checkpoint format may differ).</summary>
    private static AfHarness NewerPackage(AfHarness first, Ledger ledger, string version = "1.25.0.0")
    {
        var second = new AfHarness(WorkflowRequestTests.Registered(ledger), redeployOf: first);
        second.Runtime.WorkflowPackageVersion = version;
        return second;
    }

    [Fact]
    public async Task A_run_in_flight_on_a_newer_agent_framework_waits_for_a_person_who_is_recorded_before_it_goes_on()
    {
        var ledger = new Ledger();
        AfHarness first = WorkflowTests.Harness(ledger, WorkflowRequestTests.Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(first, "question", "x");
        WorkflowView waiting = await WorkflowTests.FinishAsync(first, run);

        var ledger2 = new Ledger();
        AfHarness second = NewerPackage(first, ledger2);
        Assert.True(await AfHarness.Retry(async () => await run.RespondAsync(waiting.Pending[0].RequestId, "blue").AsTask()));
        await second.DriveAsync();

        PendingApproval change = Assert.Single(AgentHistory.PendingApprovals(await second.HistoryAsync(run.Run)));
        Assert.Contains("changed workflow 'question'", change.Action, StringComparison.Ordinal);
        Assert.Contains("1.25.0.0", change.Details, StringComparison.Ordinal);
        Assert.Equal(DurableWorkflowStatus.Running, (await WorkflowTests.ReadAsync(run)).Status); // nothing ran on the new package
        Assert.Equal(0, ledger2.Total);

        await AfHarness.Retry(async () => await second.Approvals.ApproveAsync(run.Run, change.ApprovalId, Alice).AsTask());
        WorkflowView done = await WorkflowTests.FinishAsync(second, run);

        Assert.True(done.Status == DurableWorkflowStatus.Completed, done.Error);
        Assert.Equal("answered: blue", Assert.Single(done.Outputs).As<string>());
        WorkflowOverrideAuditRecord mark = Assert.Single(AgentFrameworkAudit.Read(await second.HistoryAsync(run.Run)).OfType<WorkflowOverrideAuditRecord>());
        Assert.Equal("user:alice", mark.Approver);
        Assert.Equal(mark.FromStructure, mark.ToStructure);
        Assert.NotEqual(mark.FromVersion, mark.ToVersion);
        Assert.Equal("1.25.0.0", mark.ToVersion);
    }

    [Fact]
    public async Task A_change_nobody_allows_ends_the_run_with_an_error_and_nothing_runs_on_the_changed_workflow()
    {
        var ledger = new Ledger();
        AfHarness first = WorkflowTests.Harness(ledger, WorkflowRequestTests.Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(first, "question", "x");
        WorkflowView waiting = await WorkflowTests.FinishAsync(first, run);
        var ledger2 = new Ledger();
        AfHarness second = NewerPackage(first, ledger2);
        await run.RespondAsync(waiting.Pending[0].RequestId, "blue");
        await second.DriveAsync();

        string id = AgentHistory.PendingApprovals(await second.HistoryAsync(run.Run)).Single().ApprovalId;
        await AfHarness.Retry(async () => await second.Approvals.RejectAsync(run.Run, id, Alice, "not now").AsTask());
        WorkflowView view = await WorkflowTests.FinishAsync(second, run);

        Assert.Equal(DurableWorkflowStatus.Failed, view.Status);
        Assert.Contains("DefinitionChangedException", view.Error, StringComparison.Ordinal);
        Assert.Equal(0, ledger2.Total);
    }

    [Fact]
    public async Task A_changed_graph_that_agent_framework_cannot_restore_ends_the_run_with_the_reason_instead_of_asking_a_person_for_something_impossible()
    {
        var ledger = new Ledger();
        AfHarness first = WorkflowTests.Harness(ledger, WorkflowRequestTests.Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(first, "question", "x");
        WorkflowView waiting = await WorkflowTests.FinishAsync(first, run);

        var ledger2 = new Ledger();
        var second = new AfHarness(af => af.AddWorkflow<string>("question", _ => TestWorkflows.QuestionWithAudit(ledger2)), redeployOf: first); // one more executor after the port
        await run.RespondAsync(waiting.Pending[0].RequestId, "blue");
        WorkflowView view = await WorkflowTests.FinishAsync(second, run);

        Assert.Equal(DurableWorkflowStatus.Failed, view.Status);
        Assert.Contains("WorkflowIncompatibleException", view.Error, StringComparison.Ordinal);
        Assert.Contains("same executors, edges and ports", view.Error, StringComparison.Ordinal);
        Assert.Empty(AgentHistory.PendingApprovals(await second.HistoryAsync(run.Run))); // nobody was asked to approve what cannot be done
        Assert.Equal(0, ledger2.Total);
    }

    // ---- stopping ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancel_ends_a_workflow_that_waits_for_a_request_at_once()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, WorkflowRequestTests.Registered(ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "question", "x");
        await WorkflowTests.FinishAsync(h, run);

        Assert.True(await AfHarness.Retry(async () => await run.CancelAsync("changed our mind").AsTask()));
        Assert.True(await AfHarness.Retry(async () => await run.CancelAsync("changed our mind").AsTask())); // asking twice is one request
        await h.DriveAsync(jumpClock: false);

        WorkflowView view = await WorkflowTests.ReadAsync(run);
        Assert.Equal(DurableWorkflowStatus.Cancelled, view.Status);
        Assert.Contains("changed our mind", view.Error, StringComparison.Ordinal);
        Assert.Equal(1, (await h.HistoryAsync(run.Run)).OfType<SignalReceived>().Count(s => s.SignalName == ReservedSignals.Cancel));
    }

    [Fact]
    public async Task A_cancel_that_arrives_in_the_middle_of_a_superstep_stops_the_workflow_at_the_boundary_after_it()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger);
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "chain", "x");
        ledger.OnRun = executor =>
        {
            if (executor == "b")
            {
                ledger.OnRun = null;
                run.CancelAsync("stop").AsTask().GetAwaiter().GetResult();
            }
        };

        WorkflowView view = await WorkflowTests.FinishAsync(h, run);

        Assert.Equal(DurableWorkflowStatus.Cancelled, view.Status);
        Assert.Equal(1, ledger.Runs("b")); // the superstep in flight finished and was recorded
        Assert.Equal(0, ledger.Runs("c")); // the next one did not start
    }

    // ---- hand-over ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_long_workflow_hands_over_to_a_fresh_run_carrying_its_checkpoint_and_finishes_with_every_output()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, af => af.Configure(o => o.ContinueAfterEvents = 8));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "chain", "x");

        WorkflowView view = await WorkflowTests.FinishAsync(h, run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal("x>a>b>c", Assert.Single(view.Outputs).As<string>());
        Assert.Equal(4, view.Steps);
        Assert.NotEqual(default, run.Run);
        IReadOnlyList<RunSummary> runs = [.. (await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)).Where(r => r.WorkflowId == "afw/chain/w1")];
        Assert.True(runs.Count >= 2, $"expected a hand-over, saw {runs.Count} run(s)");
        Assert.All(new[] { "a", "b", "c", "done" }, e => Assert.Equal(1, ledger.Runs(e))); // nothing ran twice because of it
    }

    [Fact]
    public async Task A_request_that_waits_across_a_hand_over_is_answered_in_the_fresh_run()
    {
        var ledger = new Ledger();
        AfHarness h = WorkflowTests.Harness(ledger, af => af.Configure(o => o.ContinueAfterEvents = 14).AddWorkflow<string>("parallel", _ => TestWorkflows.Parallel(ledger)));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "parallel", "x");

        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);

        Assert.Equal(DurableWorkflowStatus.Waiting, waiting.Status); // the chain finished; the request is still open ...
        Assert.Equal("x>c1>c2>c3>c4>c5", Assert.Single(waiting.Outputs).As<string>());
        IReadOnlyList<RunSummary> runs = [.. (await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)).Where(r => r.WorkflowId == "afw/parallel/w1")];
        Assert.True(runs.Count >= 2, $"expected a hand-over while the request waited, saw {runs.Count} run(s)"); // ... and the run handed over while it waited

        Assert.True(await AfHarness.Retry(async () => await run.RespondAsync(waiting.Pending[0].RequestId, "yes").AsTask()));
        WorkflowView done = await WorkflowTests.FinishAsync(h, run);

        Assert.True(done.Status == DurableWorkflowStatus.Completed, done.Error);
        Assert.Equal(["answered: yes", "x>c1>c2>c3>c4>c5"], done.Outputs.Select(o => o.As<string>()!).Order(StringComparer.Ordinal));
        Assert.Equal(1, ledger.Runs("to-ticket"));
    }
}
