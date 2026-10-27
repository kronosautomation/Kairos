using System.Text.Json;
using Kairos.Agents;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>The step activity on its own, called the way a worker calls it: what it refuses, what it says instead of running, and what it keeps out of the world.</summary>
public sealed class WorkflowStepTests
{
    private static async Task<AfwStepResult> CallAsync(AfHarness h, AfwStepRequest request, string key = "direct")
    {
        var context = new ActivityContext(new RunKey(AfHarness.Scope.TenantId, AfHarness.Scope.NamespaceId, Guid.NewGuid()), 1, AgentFrameworkNames.WorkflowStep, 1, key + "-" + Guid.NewGuid().ToString("N"));
        byte[] reply = await h.Runtime.Activities[AgentFrameworkNames.WorkflowStep](context, AfCodec.Write(request, AfwJson.Default.AfwStepRequest), CancellationToken.None);
        return AfCodec.Read(reply, AfwJson.Default.AfwStepResult);
    }

    private static AfwStepRequest First(string workflow, string input = "x") => new() { Workflow = workflow, Input = JsonSerializer.SerializeToElement(input) };

    private static AfHarness Harness(Ledger ledger, Action<AgentFrameworkBuilder>? more = null) =>
        new(af =>
        {
            af.AddWorkflow<string>("chain", _ => TestWorkflows.Chain(ledger));
            af.AddWorkflow<string>("question", _ => TestWorkflows.Question(ledger));
            more?.Invoke(af);
        });

    /// <summary>The second step of a chain: the checkpoint the first step recorded, named as the workflow would name it.</summary>
    private static AfwStepRequest Second(AfwStepResult first, string workflow = "chain") => new()
    {
        Workflow = workflow,
        Structure = first.Structure,
        Version = first.Version,
        Checkpoint = first.Checkpoint,
        CheckpointHash = first.CheckpointHash,
        CheckpointFrom = first.StepId,
    };

    [Fact]
    public async Task The_first_step_runs_one_superstep_and_records_the_checkpoint_whole_with_what_the_second_needs_to_ask_for_it()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);

        AfwStepResult first = await CallAsync(h, First("chain"));

        Assert.Equal(AfwOutcomes.Done, first.Outcome);
        Assert.Equal(1, first.Supersteps);
        Assert.True(first.More);
        Assert.False(first.Done);
        Assert.NotNull(first.Checkpoint);
        Assert.NotNull(first.CheckpointHash);
        Assert.Equal(1, ledger.Runs("a"));
        Assert.Equal(0, ledger.Runs("b"));
        Assert.Equal(64, first.Structure!.Length);
    }

    [Fact]
    public async Task A_checkpoint_that_is_not_the_one_its_hash_names_is_refused_and_nothing_runs()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        AfwStepResult first = await CallAsync(h, First("chain"));
        int ran = ledger.Total;

        await Assert.ThrowsAsync<SnapshotIntegrityException>(async () => await CallAsync(h, Second(first) with { CheckpointHash = new string('b', 64) }));

        Assert.Equal(ran, ledger.Total);
    }

    [Fact]
    public async Task A_worker_that_does_not_hold_the_checkpoint_says_so_and_runs_nothing()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        AfwStepResult first = await CallAsync(h, First("chain"));
        int ran = ledger.Total;

        AfwStepResult answer = await CallAsync(h, Second(first) with { Checkpoint = null, CheckpointHash = new string('c', 64) });

        Assert.Equal(AfwOutcomes.NeedSnapshot, answer.Outcome);
        Assert.Equal(ran, ledger.Total);
    }

    [Fact]
    public async Task A_run_pinned_to_another_structure_or_version_is_told_so_and_nothing_runs()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        AfwStepResult first = await CallAsync(h, First("chain"));
        int ran = ledger.Total;

        AfwStepResult byStructure = await CallAsync(h, Second(first) with { Structure = new string('0', 64) });
        AfwStepResult byVersion = await CallAsync(h, Second(first) with { Version = "9.9.9.9" });

        Assert.Equal(AfwOutcomes.StructureChanged, byStructure.Outcome);
        Assert.Equal(new string('0', 64), byStructure.PinnedStructure);
        Assert.Equal(first.Structure, byStructure.Structure);
        Assert.Equal(AfwOutcomes.StructureChanged, byVersion.Outcome);
        Assert.Equal("9.9.9.9", byVersion.PinnedVersion);
        Assert.Equal(ran, ledger.Total);
    }

    [Fact]
    public async Task A_checkpoint_over_the_limit_is_refused_going_in_and_coming_out_with_the_option_named()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        AfwStepResult first = await CallAsync(h, First("chain"));

        h.Runtime.Options.MaxCheckpointBytes = 200;
        SnapshotTooLargeException going = await Assert.ThrowsAsync<SnapshotTooLargeException>(async () => await CallAsync(h, Second(first)));
        SnapshotTooLargeException coming = await Assert.ThrowsAsync<SnapshotTooLargeException>(async () => await CallAsync(h, First("chain")));

        Assert.Contains("is larger than the 200 bytes", going.Message, StringComparison.Ordinal);
        Assert.Contains("MaxCheckpointBytes", going.Message, StringComparison.Ordinal);
        Assert.Contains("grew past the 200 bytes", coming.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_with_neither_an_input_nor_a_checkpoint_a_workflow_nobody_registered_and_a_response_to_no_port_are_refused()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        AfwStepResult asking = await CallAsync(h, Second(await CallAsync(h, First("question")), "question")); // the step in which the request is raised

        await Assert.ThrowsAsync<FormatException>(async () => await CallAsync(h, new AfwStepRequest { Workflow = "chain" }));
        await Assert.ThrowsAsync<UnknownWorkflowException>(async () => await CallAsync(h, First("nobody")));
        await Assert.ThrowsAsync<FormatException>(async () =>
            await CallAsync(h, Second(asking, "question") with { Responses = [new AfwResponse { RequestId = asking.Requests[0].RequestId, PortId = "elsewhere", Data = JsonSerializer.SerializeToElement("x") }] }));
    }

    [Fact]
    public async Task A_response_is_delivered_to_the_request_it_names_and_a_request_nobody_answered_is_still_listed()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger);
        AfwStepResult first = await CallAsync(h, First("question"));
        AfwStepResult second = await CallAsync(h, Second(first, "question")); // the request is raised in the second superstep
        AfwRequest request = Assert.Single(second.Requests);
        Assert.Equal("ask", request.PortId);
        Assert.False(second.Done); // a request is outstanding

        AfwStepResult third = await CallAsync(h, Second(second, "question") with { Responses = [new AfwResponse { RequestId = request.RequestId, PortId = "ask", Data = JsonSerializer.SerializeToElement("blue") }] });
        Assert.Empty(third.Requests); // the answer was taken: the request is no longer listed
        Assert.True(third.More); // and the superstep that took it has handed the answer on
        Assert.Equal(0, ledger.Runs("conclude"));

        AfwStepResult fourth = await CallAsync(h, Second(third, "question"));

        Assert.Equal("answered: blue", Assert.Single(fourth.Outputs).Data.GetString());
        Assert.True(fourth.Done);
        Assert.Equal(1, ledger.Runs("conclude"));
    }

    [Fact]
    public async Task A_step_records_the_checkpoint_as_a_change_until_it_is_time_for_a_whole_one()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, af => af.Configure(o => o.FullSnapshotEvery = 3));
        AfwStepResult first = await CallAsync(h, First("chain"));

        AfwStepResult second = await CallAsync(h, Second(first)); // 0 steps since the whole one: 1 of 3
        AfwStepResult third = await CallAsync(h, Second(second) with { StepsSinceFull = 1 }); // 2 of 3
        AfwStepResult fourth = await CallAsync(h, Second(third) with { StepsSinceFull = 2 }); // 3 of 3: whole again

        Assert.NotNull(first.Checkpoint); // the first is always whole
        Assert.Null(second.Checkpoint);
        Assert.NotNull(second.Delta);
        Assert.Equal(first.CheckpointHash, second.BaseHash);
        Assert.Null(third.Checkpoint);
        Assert.Equal(second.CheckpointHash, third.BaseHash);
        Assert.NotNull(fourth.Checkpoint);
        Assert.Null(fourth.Delta);
        Assert.True(second.Delta!.Value.GetRawText().Length < first.CheckpointBytes, "a change is recorded only when it is smaller than the checkpoint");
    }

    [Fact]
    public async Task A_workflow_that_has_finished_says_so()
    {
        var ledger = new Ledger();
        AfHarness h = Harness(ledger, null);
        AfwStepResult result = await CallAsync(h, First("chain") with { Supersteps = 10 });

        Assert.True(result.Done);
        Assert.False(result.More);
        Assert.Equal(4, result.Supersteps);
        Assert.Equal("x>a>b>c", Assert.Single(result.Outputs).Data.GetString());
    }

    [Fact]
    public async Task With_a_redactor_an_output_is_redacted_before_it_is_recorded_and_one_of_a_type_it_does_not_know_is_refused()
    {
        var ledger = new Ledger();
        AfHarness redacted = new(af => af
            .ConfigureAgents(b => b.UseRedactor(RedactionTests.Redact))
            .AddWorkflow<string>("chain", _ => TestWorkflows.Chain(ledger)));

        AfwStepResult result = await CallAsync(redacted, First("chain", "my number is SECRET-123-4567") with { Supersteps = 10 });

        Assert.Equal("my number is [redacted]>a>b>c", Assert.Single(result.Outputs).Data.GetString());
        Assert.DoesNotContain("SECRET-123-4567", System.Text.Json.JsonSerializer.Serialize(result.Checkpoint), StringComparison.Ordinal);
    }
}
