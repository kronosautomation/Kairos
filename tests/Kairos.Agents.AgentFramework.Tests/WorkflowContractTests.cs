using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// What the durable workflows rely on in the pinned Microsoft.Agents.AI.Workflows. Nothing here involves Kairos: if a new package version changes one of these behaviours, these tests fail in CI instead of a run failing in production.
/// </summary>
public sealed class WorkflowContractTests
{
    private static Workflow Chain(Ledger ledger) => TestWorkflows.Chain(ledger);

    private static (CarrierStore Store, CheckpointManager Manager) Carrier() =>
        (new CarrierStore(), CheckpointManager.CreateJson(new CarrierStore(), new JsonSerializerOptions()));

    private static async Task<(List<WorkflowEvent> Events, CarrierStore Store)> RunUntilFirstSuperstepAsync<T>(Workflow workflow, T input)
        where T : notnull
    {
        var store = new CarrierStore();
        CheckpointManager manager = CheckpointManager.CreateJson(store, new JsonSerializerOptions());
        var events = new List<WorkflowEvent>();
        StreamingRun run = await InProcessExecution.Lockstep.WithCheckpointing(manager).RunStreamingAsync(workflow, input, CarrierStore.Session, CancellationToken.None);
        await using (run)
        {
            await foreach (WorkflowEvent e in run.WatchStreamAsync(false, CancellationToken.None))
            {
                events.Add(e);
                if (e is SuperStepCompletedEvent)
                {
                    break;
                }
            }
        }

        await Task.Delay(100); // anything that was going to run on its own would have by now
        return (events, store);
    }

    [Fact]
    public void The_package_is_the_version_the_checkpoint_walker_and_these_contracts_were_verified_against()
    {
        // A different version is not necessarily wrong; it is a reason to read this file's tests, run them, and then move the pin. A run in flight across such an upgrade needs an approval (OnStructureChange).
        Assert.StartsWith("1.24.", WorkflowDefinition.PackageVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_lockstep_streaming_run_does_not_start_the_next_superstep_until_it_is_asked_to_and_writes_one_checkpoint_for_the_one_it_ran()
    {
        var ledger = new Ledger();
        (List<WorkflowEvent> events, CarrierStore store) = await RunUntilFirstSuperstepAsync(Chain(ledger), "x");

        Assert.Equal(1, ledger.Runs("a"));
        Assert.Equal(0, ledger.Runs("b")); // the second superstep has not begun
        Assert.Equal(1, store.Written);
        SuperStepCompletedEvent completed = Assert.Single(events.OfType<SuperStepCompletedEvent>());
        Assert.True(completed.CompletionInfo!.HasPendingMessages); // b has a message waiting
        Assert.False(completed.CompletionInfo.HasPendingRequests);
        Assert.NotNull(completed.CompletionInfo.Checkpoint);
    }

    [Fact]
    public async Task A_failing_executor_does_not_throw_it_is_reported_as_events_and_no_checkpoint_is_written_for_its_superstep()
    {
        var ledger = new Ledger { Failures = 1 };
        var store = new CarrierStore();
        var events = new List<WorkflowEvent>();
        StreamingRun run = await InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(store, new JsonSerializerOptions())).RunStreamingAsync(TestWorkflows.Flaky(ledger), "x", CarrierStore.Session, CancellationToken.None);
        await using (run)
        {
            await foreach (WorkflowEvent e in run.WatchStreamAsync(false, CancellationToken.None))
            {
                events.Add(e);
            }
        }

        ExecutorFailedEvent failed = Assert.Single(events.OfType<ExecutorFailedEvent>());
        Assert.Equal("b", failed.ExecutorId);
        Assert.IsType<InvalidOperationException>(failed.Data);
        Assert.Single(events.OfType<SuperStepCompletedEvent>()); // a ran and was checkpointed; b's superstep did not complete
        Assert.Equal(1, store.Written);
    }

    [Fact]
    public async Task A_checkpoint_restores_into_a_new_workflow_object_and_a_request_survives_with_its_id()
    {
        var ledger = new Ledger();
        var store = new CarrierStore();
        ExternalRequest? asked = null;
        StreamingRun run = await InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(store, new JsonSerializerOptions())).RunStreamingAsync(TestWorkflows.Question(ledger), "x", CarrierStore.Session, CancellationToken.None);
        await using (run)
        {
            await foreach (WorkflowEvent e in run.WatchStreamAsync(false, CancellationToken.None))
            {
                if (e is RequestInfoEvent info)
                {
                    asked = info.Request;
                }
            }
        }

        Assert.NotNull(asked);
        JsonNode checkpoint = JsonNode.Parse(store.Last.GetRawText())!;
        Assert.Equal(asked.RequestId, checkpoint["runnerData"]!["outstandingRequests"]![0]!["requestId"]!.GetValue<string>());

        // a new process: a new workflow object, a new manager over a store that holds nothing but the checkpoint
        var restored = new CarrierStore();
        CheckpointInfo seed = restored.Seed(store.Last);
        Workflow again = TestWorkflows.Question(new Ledger());
        RequestPortInfo port = again.ReflectPorts()["ask"];
        var response = new ExternalResponse(port, asked.RequestId, new PortableValue("blue"));
        var outputs = new List<object>();
        StreamingRun resumed = await InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(restored, new JsonSerializerOptions())).ResumeStreamingAsync(again, seed, CancellationToken.None);
        await using (resumed)
        {
            await resumed.SendResponseAsync(response);
            await foreach (WorkflowEvent e in resumed.WatchStreamAsync(false, CancellationToken.None))
            {
                if (e is WorkflowOutputEvent o)
                {
                    outputs.Add(o.Data!);
                }
            }
        }

        Assert.Equal(["answered: blue"], outputs);
    }

    [Fact]
    public async Task A_checkpoint_has_the_sections_the_redactor_and_the_requests_are_read_from_and_the_structure_is_the_same_in_every_one()
    {
        var ledger = new Ledger();
        var store = new CarrierStore();
        StreamingRun run = await InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(store, new JsonSerializerOptions())).RunStreamingAsync(TestWorkflows.Question(ledger), "x", CarrierStore.Session, CancellationToken.None);
        var checkpoints = new List<JsonNode>();
        await using (run)
        {
            await foreach (WorkflowEvent e in run.WatchStreamAsync(false, CancellationToken.None))
            {
                if (e is SuperStepCompletedEvent)
                {
                    checkpoints.Add(JsonNode.Parse(store.Last.GetRawText())!);
                }
            }
        }

        Assert.True(checkpoints.Count >= 2);
        string[] members = ["edgeStateData", "parent", "runnerData", "stateData", "stepNumber", "workflow"];
        foreach (JsonNode checkpoint in checkpoints)
        {
            Assert.All(checkpoint.AsObject().Select(p => p.Key), k => Assert.Contains(k, members)); // no member the walker does not know
            Assert.All(members.Where(m => m != "parent"), m => Assert.True(checkpoint.AsObject().ContainsKey(m), m)); // the first checkpoint of a run has no parent
            Assert.Equal(["instantiatedExecutors", "outstandingRequests", "queuedMessages"], checkpoint["runnerData"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        }

        Assert.Equal(checkpoints[0]["workflow"]!.ToJsonString(), checkpoints[^1]["workflow"]!.ToJsonString()); // the part a change leaves out
        Assert.NotEqual(checkpoints[0]["stepNumber"]!.ToJsonString(), checkpoints[^1]["stepNumber"]!.ToJsonString());

        // a message in flight and a request, in the shapes the redactor walks
        JsonNode queued = checkpoints[0]["runnerData"]!["queuedMessages"]!.AsObject().First().Value![0]!;
        Assert.Equal(["message", "messageType", "source"], queued.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["typeId", "value"], queued["message"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        JsonNode outstanding = checkpoints[^1]["runnerData"]!["outstandingRequests"]![0]!;
        Assert.Equal(["data", "portInfo", "requestId"], outstanding.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["typeId", "value"], outstanding["data"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_same_graph_built_twice_has_the_same_structure_and_a_changed_graph_does_not()
    {
        string first = WorkflowDefinition.StructureOf(TestWorkflows.Question(new Ledger()));
        Assert.Equal(first, WorkflowDefinition.StructureOf(TestWorkflows.Question(new Ledger())));
        Assert.NotEqual(first, WorkflowDefinition.StructureOf(TestWorkflows.QuestionWithAudit(new Ledger())));
        Assert.NotEqual(first, WorkflowDefinition.StructureOf(TestWorkflows.Approval(new Ledger()))); // a port of another type
        Assert.NotEqual(first, WorkflowDefinition.StructureOf(TestWorkflows.QuestionOtherPrepare(new Ledger()))); // the same ids, an executor of another type
        Assert.NotEqual(WorkflowDefinition.StructureOf(TestWorkflows.Chain(new Ledger())), WorkflowDefinition.StructureOf(TestWorkflows.Diamond(new Ledger()))); // the same executors, other edges
    }

    [Fact]
    public async Task Agent_framework_refuses_to_restore_a_checkpoint_into_a_graph_that_differs()
    {
        // This is why a changed graph ends the run with a reason instead of asking a person to approve it.
        var store = new CarrierStore();
        StreamingRun run = await InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(store, new JsonSerializerOptions())).RunStreamingAsync(TestWorkflows.Question(new Ledger()), "x", CarrierStore.Session, CancellationToken.None);
        await using (run)
        {
            await foreach (WorkflowEvent unused in run.WatchStreamAsync(false, CancellationToken.None))
            {
                _ = unused;
            }
        }

        var restored = new CarrierStore();
        CheckpointInfo seed = restored.Seed(store.Last);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(restored, new JsonSerializerOptions())).ResumeStreamingAsync(TestWorkflows.QuestionWithAudit(new Ledger()), seed, CancellationToken.None));
    }

    // ---- the walker ----------------------------------------------------------------------------------------------

    private static WorkflowTypePolicy Policy(Action<WorkflowRegistration>? configure = null)
    {
        var registration = new WorkflowRegistration("w", typeof(string), _ => throw new InvalidOperationException(), (_, _, _, _, _) => throw new InvalidOperationException());
        configure?.Invoke(registration);
        return new WorkflowTypePolicy(registration);
    }

    private static string Redact(string text) => text.Replace("SECRET", "[x]", StringComparison.Ordinal);

    [Fact]
    public void The_walker_redacts_the_text_in_strings_messages_and_lists_and_leaves_scalars_alone()
    {
        WorkflowTypePolicy policy = Policy();
        var checkpoint = JsonNode.Parse("""
            {
              "stepNumber": 3, "workflow": { "anything": "SECRET in the structure is not text" }, "parent": { "sessionId": "s", "checkpointId": "c" },
              "runnerData": {
                "instantiatedExecutors": ["a"],
                "queuedMessages": { "b": [
                  { "messageType": { "assemblyName": "System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e", "typeName": "System.String" }, "message": { "typeId": { "assemblyName": "x", "typeName": "System.String" }, "value": "a SECRET" }, "source": { "id": "a" } },
                  { "messageType": { "assemblyName": "x", "typeName": "System.Int32" }, "message": { "typeId": { "assemblyName": "x", "typeName": "System.Int32" }, "value": 42 }, "source": { "id": "a" } }
                ] },
                "outstandingRequests": []
              },
              "stateData": {
                "a||words": { "typeId": { "assemblyName": "x", "typeName": "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]" }, "value": ["one SECRET", "two"] },
                "a||chat": { "typeId": { "assemblyName": "x", "typeName": "Microsoft.Extensions.AI.ChatMessage" }, "value": { "role": "user", "contents": [ { "$type": "text", "text": "hello SECRET" } ] } }
              },
              "edgeStateData": {}
            }
            """)!;

        policy.RedactCheckpoint(checkpoint, Redact);

        string text = checkpoint.ToJsonString();
        Assert.Contains("a [x]", text, StringComparison.Ordinal);
        Assert.Contains("one [x]", text, StringComparison.Ordinal);
        Assert.Contains("hello [x]", text, StringComparison.Ordinal);
        Assert.Contains("SECRET in the structure", text, StringComparison.Ordinal); // the structure holds no text of anyone's
        Assert.Equal(42, checkpoint["runnerData"]!["queuedMessages"]!["b"]![1]!["message"]!["value"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{ "stepNumber": 1, "surprise": 1 }""", "surprise")]
    [InlineData("""{ "runnerData": { "mystery": [] } }""", "mystery")]
    [InlineData("""{ "stateData": { "k": { "typeId": { "typeName": "Customer.Secret" }, "value": { "n": "SECRET" } } } }""", "Customer.Secret")]
    [InlineData("""{ "stateData": { "k": "not a typed value" } }""", "typed value")]
    [InlineData("""{ "runnerData": { "queuedMessages": { "b": [ { "messageType": {}, "message": { "typeId": { "typeName": "System.String" }, "value": 5 }, "source": {} } ] } } }""", "not a JSON string")]
    public void The_walker_refuses_what_it_does_not_know_and_says_what(string json, string mentions)
    {
        WorkflowTypePolicy policy = Policy();
        SnapshotShapeException ex = Assert.Throws<SnapshotShapeException>(() => policy.RedactCheckpoint(JsonNode.Parse(json)!, Redact));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_walker_redacts_the_data_of_an_outstanding_request_and_the_requests_an_executor_wrapped()
    {
        WorkflowTypePolicy policy = Policy(r => r.RedactState<Ticket>(t => t with { Body = Redact(t.Body) }));
        var checkpoint = JsonNode.Parse("""
            {
              "runnerData": {
                "outstandingRequests": [ { "portInfo": { "portId": "ask" }, "requestId": "r1", "data": { "typeId": { "typeName": "Kairos.Agents.AgentFramework.Tests.Ticket" }, "value": { "id": "t", "body": "call SECRET now" } } } ]
              },
              "stateData": {
                "ask||WrappedRequestsStateKey": { "typeId": { "typeName": "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[Microsoft.Agents.AI.Workflows.ExternalRequest, Microsoft.Agents.AI.Workflows, Version=1.24.0.0, Culture=neutral, PublicKeyToken=f300afd708cefcd3]]" },
                  "value": { "r1": { "portInfo": { "portId": "ask" }, "requestId": "r1", "data": { "typeId": { "typeName": "System.String" }, "value": "also SECRET" } } } }
              }
            }
            """)!;

        policy.RedactCheckpoint(checkpoint, Redact);

        string text = checkpoint.ToJsonString();
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.Contains("call [x] now", text, StringComparison.Ordinal);
        Assert.Contains("also [x]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_type_is_left_alone_or_redacted_by_the_function_it_was_given()
    {
        WorkflowTypePolicy free = Policy(r => r.AllowStateType<Ticket>());
        WorkflowTypePolicy text = Policy(r => r.RedactState<Ticket>(t => t with { Body = Redact(t.Body) }));
        string json = """{ "stateData": { "k": { "typeId": { "typeName": "Kairos.Agents.AgentFramework.Tests.Ticket" }, "value": { "id": "t", "body": "SECRET" } } } }""";

        var left = JsonNode.Parse(json)!;
        free.RedactCheckpoint(left, Redact);
        Assert.Contains("SECRET", left.ToJsonString(), StringComparison.Ordinal);

        var redacted = JsonNode.Parse(json)!;
        text.RedactCheckpoint(redacted, Redact);
        Assert.DoesNotContain("SECRET", redacted.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("[x]", redacted.ToJsonString(), StringComparison.Ordinal);
    }
}
