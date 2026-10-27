using System.Diagnostics.Metrics;
using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>Durable agents called from executors: answered through their conversations, once even when the superstep runs again, never waited for longer than the limit, and stopped with the workflow.</summary>
public sealed class WorkflowAgentTests
{
    private static readonly Principal Operator = new(AfHarness.Scope, Role.Operator, "ops", null);
    private static readonly Principal Viewer = new(AfHarness.Scope, Role.Reader, "intern", null);

    private static AfHarness Harness(Ledger ledger, ScriptedChatClient writer, Action<AgentFrameworkOptions>? options = null, bool faults = false, Action<AgentFrameworkBuilder>? more = null, params AITool[] writerTools)
    {
        var h = new AfHarness(af =>
        {
            af.Configure(o => options?.Invoke(o));
            af.AddAgent(AfHarness.AgentWith(writer, "writer", writerTools));
            af.AddWorkflow<string>("agentic", ctx => TestWorkflows.WithAgent(ledger, ctx.Agents));
            af.AddWorkflow<string>("agentic-flaky", ctx => TestWorkflows.AgentThenFlaky(ledger, ctx.Agents));
            af.AddWorkflow<string>("agentic-question", ctx => TestWorkflows.AgentThenQuestion(ledger, ctx.Agents));
            more?.Invoke(af);
        }, faultInjection: faults);
        h.RunConversationsFromTheWaitingExecutor(); // one thread, the same order every time: the executor that waits for a conversation runs the conversation's work itself
        return h;
    }

    private static async Task<DurableWorkflowRun> StartAsync(AfHarness h, string workflow, string input = "write a haiku") => await WorkflowTests.StartAsync(h, workflow, input);

    private static async Task<RunSummary[]> ConversationsAsync(AfHarness h) =>
        [.. (await AfHarness.Retry(async () => await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default).AsTask())).Where(r => r.WorkflowId.StartsWith("af/writer/", StringComparison.Ordinal))];

    [Fact]
    public async Task A_durable_agent_called_from_an_executor_answers_through_its_conversation_and_the_workflow_goes_on()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, AfHarness.Echo("an old pond"));
        DurableWorkflowRun run = await StartAsync(h, "agentic");

        await h.DriveAsync();
        WorkflowView view = await WorkflowTests.ReadAsync(run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal("an old pond", Assert.Single(view.Outputs).As<string>());
        RunSummary conversation = Assert.Single(await ConversationsAsync(h));
        Assert.StartsWith("af/writer/wf.", conversation.WorkflowId, StringComparison.Ordinal); // named by the step, and recognisable as the workflow's
    }

    [Fact]
    public async Task A_superstep_that_runs_again_finds_the_agent_call_it_already_made_so_the_model_is_asked_once()
    {
        var ledger = new Ledger { Failures = 1 };
        ScriptedChatClient writer = AfHarness.Echo("an old pond");

        // Both executors in one step: the failure of the second runs the first again, and its call to the agent must find the turn the conversation already answered.
        AfHarness h = Harness(ledger, writer, o => o.SuperstepsPerStep = 3);
        DurableWorkflowRun run = await StartAsync(h, "agentic-flaky");

        await h.DriveAsync();
        WorkflowView view = await WorkflowTests.ReadAsync(run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal("an old pond>b", Assert.Single(view.Outputs).As<string>());
        Assert.Equal(2, ledger.Runs("ask")); // the executor ran twice ...
        Assert.Equal(1, writer.CallCount); // ... the agent was asked once
        Assert.Single(await ConversationsAsync(h)); // and it is one conversation
    }

    [Fact]
    public async Task An_executor_that_waits_longer_than_the_limit_for_a_conversation_fails_its_step_and_the_error_names_the_conversation_and_the_request_port()
    {
        var ledger = new Ledger();
        var world = new[] { 0 };
        AIFunction refund = AIFunctionFactory.Create((string orderId) => { world[0]++; return "refunded"; }, "issue_refund", "Refunds.");
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("issue_refund", """{"orderId":"A-1"}""", "refunded");
        var waits = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "kairos.af.workflow.conversation_waits")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (waits)
            {
                waits.Add(value);
            }
        });
        listener.Start();
        AfHarness h = Harness(ledger, model, o => o.WorkflowAgentWait = TimeSpan.FromMilliseconds(400), writerTools: new ApprovalRequiredAIFunction(refund));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "agentic", "x");

        // The "writer" agent here wants a refund approved by a person: the executor would hold its worker until someone did.
        await h.DriveAsync();
        WorkflowView view = await WorkflowTests.ReadAsync(run);

        Assert.Equal(DurableWorkflowStatus.Failed, view.Status);
        Assert.True(view.Error!.Contains("WorkflowAgentWaitException", StringComparison.Ordinal), view.Error);
        Assert.True(view.Error!.Contains("af/writer/wf.", StringComparison.Ordinal), view.Error);
        Assert.True(view.Error!.Contains("RequestPort", StringComparison.Ordinal), view.Error);
        Assert.Equal(1, ledger.Runs("ask")); // not retried: the error is the author's to fix
        Assert.Equal(0, world[0]);
        lock (waits)
        {
            Assert.Equal(1, waits.Count(w => w == 1)); // one activity waited ...
            Assert.Equal(1, waits.Count(w => w == -1)); // ... and no longer does
        }
    }

    [Fact]
    public async Task Cancelling_a_workflow_cancels_the_conversations_its_executors_started()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, AfHarness.Echo("an old pond"));
        DurableWorkflowRun run = await StartAsync(h, "agentic-question");
        await h.DriveAsync();
        WorkflowView waiting = await WorkflowTests.ReadAsync(run);
        Assert.Equal(DurableWorkflowStatus.Waiting, waiting.Status); // the agent answered; a person has to answer the workflow

        Assert.True(await AfHarness.Retry(async () => await run.CancelAsync("changed our mind").AsTask()));
        await h.DriveAsync();

        WorkflowView view = await WorkflowTests.ReadAsync(run);
        Assert.Equal(DurableWorkflowStatus.Cancelled, view.Status);
        RunSummary conversation = Assert.Single(await ConversationsAsync(h));
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(conversation.Run);
        Assert.Equal(nameof(SagaCancelledException), Assert.IsType<WorkflowFailed>(history[^1]).ErrorType);
        Assert.Equal(1, history.OfType<SignalReceived>().Count(s => s.SignalName == ReservedSignals.Cancel));
    }

    [Fact]
    public async Task Terminating_a_workflow_asks_its_conversations_to_stop_first_and_needs_a_principal_who_may_terminate_runs()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, AfHarness.Echo("an old pond"));
        DurableWorkflowRun run = await StartAsync(h, "agentic-question");
        await h.DriveAsync();
        await WorkflowTests.ReadAsync(run);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await run.TerminateAsync(Viewer, "no right"));
        Assert.Equal(DurableWorkflowStatus.Waiting, (await WorkflowTests.ReadAsync(run)).Status); // nothing happened

        Assert.True(await AfHarness.Retry(async () => await run.TerminateAsync(Operator, "stuck").AsTask()));
        await h.DriveAsync();

        WorkflowView view = await WorkflowTests.ReadAsync(run);
        Assert.Equal(DurableWorkflowStatus.Terminated, view.Status);
        Assert.Equal("stuck", view.Error);
        RunSummary conversation = Assert.Single(await ConversationsAsync(h));
        Assert.Equal(nameof(SagaCancelledException), Assert.IsType<WorkflowFailed>((await h.HistoryAsync(conversation.Run))[^1]).ErrorType);
        RunTerminated terminated = Assert.Single((await h.HistoryAsync(run.Run)).OfType<RunTerminated>());
        Assert.Equal("stuck", terminated.Reason);
    }
}
