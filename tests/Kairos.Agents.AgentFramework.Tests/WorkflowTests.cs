using Kairos.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>A durable Agent Framework workflow: its supersteps are recorded steps, its requests wait without holding compute, and it is resumed from its checkpoint on whichever worker takes the next step.</summary>
public sealed class WorkflowTests
{
    internal static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    internal static AfHarness Harness(Ledger ledger, Action<AgentFrameworkBuilder>? more = null, bool faults = false, Func<IKairosStore, IKairosStore>? wrap = null) =>
        new(af =>
        {
            af.AddWorkflow<string>("chain", _ => TestWorkflows.Chain(ledger));
            more?.Invoke(af);
        }, faultInjection: faults, wrapStore: wrap);

    internal static async Task<DurableWorkflowRun> StartAsync(AfHarness h, string workflow, string input, string instance = "w1") =>
        await AfHarness.Retry(async () => await h.Workflows.Get(workflow).StartAsync(input, instance).AsTask());

    internal static async Task<WorkflowView> ReadAsync(DurableWorkflowRun run) => await AfHarness.Retry(async () => await run.ReadAsync().AsTask());

    internal static async Task<WorkflowView> FinishAsync(AfHarness h, DurableWorkflowRun run)
    {
        await h.DriveAsync();
        return await ReadAsync(run);
    }

    internal static int Steps(IReadOnlyList<HistoryEvent> history) => history.OfType<ActivityScheduled>().Count(a => a.ActivityType == AgentFrameworkNames.WorkflowStep);

    [Fact]
    public async Task A_workflow_runs_to_its_end_a_superstep_at_a_time_and_yields_its_outputs()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        DurableWorkflowRun run = await StartAsync(h, "chain", "x");

        WorkflowView view = await FinishAsync(h, run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal("x>a>b>c", Assert.Single(view.Outputs).As<string>());
        Assert.Equal(4, view.Steps); // a, b, c, done: one superstep each
        Assert.Equal(4, Steps(await h.HistoryAsync(run.Run)));
        Assert.All(new[] { "a", "b", "c", "done" }, e => Assert.Equal(1, ledger.Runs(e)));
    }

    [Fact]
    public async Task A_setting_runs_several_supersteps_in_one_step()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, af => af.Configure(o => o.SuperstepsPerStep = 3));
        DurableWorkflowRun run = await StartAsync(h, "chain", "x");

        WorkflowView view = await FinishAsync(h, run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal("x>a>b>c", Assert.Single(view.Outputs).As<string>());
        Assert.Equal(2, Steps(await h.HistoryAsync(run.Run))); // three supersteps, then the last
    }

    [Fact]
    public async Task Executors_in_one_superstep_run_together()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, af => af.AddWorkflow<string>("fan", _ => TestWorkflows.FanOut(ledger)));
        DurableWorkflowRun run = await StartAsync(h, "fan", "x");

        WorkflowView view = await FinishAsync(h, run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal(["x>left", "x>right"], view.Outputs.Select(o => o.As<string>()!).Order(StringComparer.Ordinal));
        Assert.Equal(3, view.Steps); // split, then left and right together, then done (which runs for both messages)
    }

    [Fact]
    public async Task A_failing_executor_is_retried_as_a_superstep_and_what_it_said_is_not_recorded()
    {
        var ledger = new Ledger { Failures = 2 };
        AfHarness h = Harness(ledger, af => af.AddWorkflow<string>("flaky", _ => TestWorkflows.Flaky(ledger)));
        DurableWorkflowRun run = await StartAsync(h, "flaky", "x");

        WorkflowView view = await FinishAsync(h, run);

        Assert.True(view.Status == DurableWorkflowStatus.Completed, view.Error);
        Assert.Equal("x>a>b", Assert.Single(view.Outputs).As<string>());
        Assert.Equal(3, ledger.Runs("b")); // twice failed, once worked
        Assert.Equal(1, ledger.Runs("a")); // the superstep before it was not run again
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run.Run);
        Assert.DoesNotContain(history, e => e is ActivityFailed f && f.Message.Contains("secret detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workflow_that_keeps_failing_ends_the_run_with_the_executor_named()
    {
        var ledger = new Ledger { Failures = 100 };
        AfHarness h = Harness(ledger, af => af.AddWorkflow<string>("flaky", _ => TestWorkflows.Flaky(ledger)));
        DurableWorkflowRun run = await StartAsync(h, "flaky", "x");

        WorkflowView view = await FinishAsync(h, run);

        Assert.Equal(DurableWorkflowStatus.Failed, view.Status);
        Assert.Contains("'b'", view.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", view.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_with_a_name_that_exists_finds_the_run_instead_of_starting_another()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        DurableWorkflowRun first = await StartAsync(h, "chain", "x", "same");
        DurableWorkflowRun second = await StartAsync(h, "chain", "x", "same");
        await h.DriveAsync();

        Assert.Equal(first.Run, second.Run);
        Assert.Equal(1, ledger.Runs("a"));
        Assert.NotNull(await h.Workflows.Get("chain").FindAsync("same"));
        Assert.Null(await h.Workflows.Get("chain").FindAsync("nobody"));
    }
}
