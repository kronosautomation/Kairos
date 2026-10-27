using Kairos.Agents;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// A nested agent is a run of its own, and it does not outlive the run that asked it: cancelling a conversation cancels the nested agents it is waiting for (and theirs), terminating one does the same through the
/// operator's call or, failing that, through the nested agent noticing. A cancel reaches each nested agent once, however often the worker dies on the way.
/// </summary>
public sealed class CancelTests
{
    private static readonly Principal Operator = new(AfHarness.Scope, Role.Operator, "ops", null);

    private sealed class World
    {
        public int Refunds;
    }

    /// <summary>manager asks researcher, which asks specialist, which wants a refund that a person has to approve. Nothing happens in the specialist until someone approves.</summary>
    private static AfHarness Chain(World world, bool faults = false, Action<AgentFrameworkOptions>? options = null)
    {
        AIFunction refund = AIFunctionFactory.Create((string orderId) => { Interlocked.Increment(ref world.Refunds); return "refunded"; }, "issue_refund", "Refunds.");
        ScriptedChatClient Asks(string tool, string json) => new(messages => messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, "done: " + messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().First().Result)
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c-" + tool, tool, AfHarness.Args(json))]));
        return new AfHarness(
            af =>
            {
                af.Configure(o => options?.Invoke(o));
                af.AddAgent(AfHarness.AgentWith(Asks("researcher", """{"query":"q"}"""), "manager", DurableAgentFunction.For("researcher", "Looks things up.")), r => r.ReadOnly("researcher"));
                af.AddAgent(AfHarness.AgentWith(Asks("specialist", """{"query":"q"}"""), "researcher", DurableAgentFunction.For("specialist", "Knows the details.")), r => r.ReadOnly("specialist"));
                af.AddAgent(AfHarness.AgentWith(Asks("issue_refund", """{"orderId":"A-1"}"""), "specialist", new ApprovalRequiredAIFunction(refund)));
            },
            faultInjection: faults);
    }

    private static async Task<RunSummary> RunOfAsync(AfHarness h, string agent) =>
        Assert.Single(await AfHarness.Retry(async () => await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default).AsTask()), r => r.WorkflowId.StartsWith($"af/{agent}/", StringComparison.Ordinal));

    private static int Cancels(IReadOnlyList<HistoryEvent> history) => history.OfType<SignalReceived>().Count(s => s.SignalName == ReservedSignals.Cancel);

    private static string FailureOf(IReadOnlyList<HistoryEvent> history) => history[^1] is WorkflowFailed f ? f.ErrorType : history[^1].GetType().Name;

    [Fact]
    public async Task Cancelling_a_conversation_cancels_the_nested_agents_it_is_waiting_for_and_theirs_and_nothing_is_called()
    {
        var world = new World();
        AfHarness h = Chain(world);
        DurableAIAgent manager = h.Agents.Get("manager");
        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("manager", "s", "go");
        Assert.Null(reply); // the specialist waits for a person; the others wait for it, holding nothing

        Assert.True(await manager.CancelAsync(manager.Session("s"), "customer left"));
        await h.DriveAsync(jumpClock: false); // at once: nothing waited for a timer to notice

        foreach (string agent in new[] { "manager", "researcher", "specialist" })
        {
            IReadOnlyList<HistoryEvent> history = await h.HistoryAsync((await RunOfAsync(h, agent)).Run);
            Assert.Equal(nameof(SagaCancelledException), FailureOf(history));
            Assert.Equal(1, Cancels(history));
        }

        Assert.Equal(0, world.Refunds);
        ConversationEndedException ended = await Assert.ThrowsAsync<ConversationEndedException>(async () => await manager.TryReadAsync(turn));
        Assert.Contains("was cancelled", ended.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed (", ended.Message, StringComparison.Ordinal); // a cancel is not reported as a failure
    }

    [Fact]
    public async Task A_cancel_leaves_a_readable_rollback_in_the_history_of_each_run_that_asked_a_nested_agent()
    {
        AfHarness h = Chain(new World());
        await h.AskAsync("manager", "s", "go");
        DurableAIAgent manager = h.Agents.Get("manager");

        await manager.CancelAsync(manager.Session("s"));
        await h.DriveAsync();

        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync((await RunOfAsync(h, "manager")).Run);
        Assert.Single(history.OfType<ActivityScheduled>(), a => a.ActivityType == AgentFrameworkNames.ChildCancel);
        Assert.Contains(history.OfType<ActivityScheduled>(), a => a.ActivityType == AgentActivityNames.SagaMark);
    }

    [Fact]
    public async Task Asking_twice_is_one_request()
    {
        AfHarness h = Chain(new World());
        await h.AskAsync("manager", "s", "go");
        DurableAIAgent manager = h.Agents.Get("manager");

        await manager.CancelAsync(manager.Session("s"));
        await manager.CancelAsync(manager.Session("s"));
        await h.DriveAsync();

        Assert.Equal(1, Cancels(await h.HistoryAsync((await RunOfAsync(h, "manager")).Run)));
    }

    [Fact]
    public async Task A_conversation_waiting_for_its_next_message_is_stopped_at_once_and_refuses_the_next_one()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo("hello"), "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        await h.AskAsync("chat", "s", "hi");

        Assert.True(await chat.CancelAsync(chat.Session("s")));
        await h.DriveAsync();

        RunSummary run = Assert.Single(await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 10, default));
        Assert.Equal(RunState.Failed, run.State);
        await Assert.ThrowsAsync<ConversationEndedException>(async () => await chat.SendAsync([new ChatMessage(ChatRole.User, "again")], chat.Session("s")));
    }

    [Fact]
    public async Task Cancelling_a_conversation_that_has_not_started_or_has_ended_says_so()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo("hello"), "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");

        Assert.False(await chat.CancelAsync(chat.Session("never-started")));
    }

    [Fact]
    public async Task A_step_that_is_running_when_the_cancel_arrives_finishes_and_is_recorded_before_the_run_stops()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.CallsToolThenAnswers("lookup", """{"q":"x"}"""), "chat", AIFunctionFactory.Create((string q) => "found", "lookup", "d")), r => r.ReadOnly("lookup")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableTurn turn = await chat.SendAsync([new ChatMessage(ChatRole.User, "go")], chat.Session("s"));
        await chat.CancelAsync(chat.Session("s"));

        await h.DriveAsync();

        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(await h.RunOfAsync("chat", "s"));
        Assert.Equal(nameof(SagaCancelledException), FailureOf(history));
        Assert.All(history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentFrameworkNames.Step), a => Assert.Contains(history.OfType<ActivityCompleted>(), c => c.ActivityId == a.ActivityId)); // nothing was abandoned
        Assert.NotNull(turn);
    }

    [Fact]
    public async Task Terminating_the_conversation_from_here_cancels_the_nested_agents_it_asked_first()
    {
        var world = new World();
        AfHarness h = Chain(world);
        DurableAIAgent manager = h.Agents.Get("manager");
        await h.AskAsync("manager", "s", "go");

        Assert.True(await manager.TerminateAsync(manager.Session("s"), Operator, "operator stopped it"));
        await h.DriveAsync(jumpClock: false); // no waiting for anyone to notice: they were told

        Assert.IsType<RunTerminated>((await h.HistoryAsync((await RunOfAsync(h, "manager")).Run))[^1]);
        foreach (string agent in new[] { "researcher", "specialist" })
        {
            IReadOnlyList<HistoryEvent> history = await h.HistoryAsync((await RunOfAsync(h, agent)).Run);
            Assert.Equal((nameof(SagaCancelledException), 1), (FailureOf(history), Cancels(history)));
        }

        Assert.Equal(0, world.Refunds);
    }

    [Fact]
    public async Task A_nested_agent_whose_parent_was_terminated_by_someone_else_notices_and_stops()
    {
        var world = new World();
        AfHarness h = Chain(world, options: o => o.ParentWatchInterval = TimeSpan.FromSeconds(20));
        await h.AskAsync("manager", "s", "go");
        RunSummary manager = await RunOfAsync(h, "manager");

        Assert.True(await h.Client.TerminateRunAsync(Operator, manager.Run, "terminated in the dashboard")); // the library is not asked; nobody tells the nested agents
        h.Time.Advance(TimeSpan.FromMinutes(20));
        await h.DriveAsync(); // time passes: the nested agents look

        foreach (string agent in new[] { "researcher", "specialist" })
        {
            IReadOnlyList<HistoryEvent> history = await h.HistoryAsync((await RunOfAsync(h, agent)).Run);
            Assert.Equal((nameof(SagaCancelledException), 1), (FailureOf(history), Cancels(history)));
        }

        Assert.Equal(0, world.Refunds);
    }

    [Fact]
    public async Task A_nested_agent_whose_parent_is_still_open_is_left_alone_however_long_it_looks()
    {
        var world = new World();
        AfHarness h = Chain(world, options: o => o.ParentWatchInterval = TimeSpan.FromSeconds(5));
        await h.AskAsync("manager", "s", "go");

        for (int i = 0; i < 4; i++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(20));
            await h.DriveAsync();
        }

        RunSummary specialist = await RunOfAsync(h, "specialist");
        Assert.Equal(RunState.Running, specialist.State);
        Assert.Equal(0, Cancels(await h.HistoryAsync(specialist.Run)));
    }

    [Fact]
    public async Task Without_a_watch_interval_nested_agents_are_cancelled_only_by_the_calls_that_name_them()
    {
        AfHarness h = Chain(new World(), options: o => o.ParentWatchInterval = null);
        await h.AskAsync("manager", "s", "go");
        RunSummary manager = await RunOfAsync(h, "manager");
        Assert.True(await h.Client.TerminateRunAsync(Operator, manager.Run, "gone"));

        h.Time.Advance(TimeSpan.FromHours(1));
        await h.DriveAsync();

        Assert.Equal(RunState.Running, (await RunOfAsync(h, "researcher")).State);
    }

    [Fact]
    public async Task A_cancel_reaches_each_nested_agent_once_however_often_the_worker_dies_on_the_way()
    {
        // Nested agents and a person's approval pending, then a cancel: every store operation from the first message to the end of the rollback is a place to die, before and after the store applied it.
        var worlds = new Dictionary<AfHarness, World>();
        AfHarness Build()
        {
            var world = new World();
            AfHarness h = Chain(world, faults: true);
            worlds[h] = world;
            return h;
        }

        async Task<string> Drive(AfHarness h)
        {
            DurableAIAgent manager = h.Agents.Get("manager");
            await h.AskAsync("manager", "s", "go");
            await AfHarness.Retry(async () => await manager.CancelAsync(manager.Session("s")));
            await h.DriveAsync();
            var states = new List<string>();
            foreach (string agent in new[] { "manager", "researcher", "specialist" })
            {
                states.Add(FailureOf(await h.HistoryAsync((await RunOfAsync(h, agent)).Run)));
            }

            return string.Join(",", states);
        }

        await CrashMatrixTests.MatrixAsync(
            Build,
            Drive,
            async (h, where) =>
            {
                Assert.True(worlds[h].Refunds == 0, $"{where}: the refund was made");
                var planned = new Dictionary<string, string>
                {
                    ["manager"] = "cancel nested agent researcher",
                    ["researcher"] = "cancel nested agent specialist",
                };
                foreach (string agent in new[] { "manager", "researcher", "specialist" })
                {
                    IReadOnlyList<HistoryEvent> history = await h.HistoryAsync((await RunOfAsync(h, agent)).Run);
                    Assert.True(Cancels(history) == 1, $"{where}: {agent} was asked to cancel {Cancels(history)} times");
                    Assert.True(SagaHistory.CheckCompensationsComplete(history) is null, $"{where}: {agent}: {SagaHistory.CheckCompensationsComplete(history)}");

                    // The rollback of a run that asked a nested agent undid exactly that, once; the one that asked nobody had nothing to undo.
                    IReadOnlyList<SagaRollback> rollbacks = SagaHistory.Rollbacks(history);
                    if (planned.TryGetValue(agent, out string? undo))
                    {
                        SagaRollback rollback = Assert.Single(rollbacks);
                        Assert.True(rollback.Ended && rollback.Planned.SequenceEqual([undo]) && rollback.Failed.Count == 0, $"{where}: {agent}'s rollback was {string.Join("|", rollback.Planned)} ended={rollback.Ended}");
                    }
                    else
                    {
                        Assert.True(rollbacks.Count == 0, $"{where}: {agent} rolled back");
                    }
                }
            },
            minimumOperations: 40);
    }

    [Fact]
    public async Task A_nested_agent_that_has_already_answered_is_not_asked_to_stop_when_the_run_that_asked_it_is_cancelled_later()
    {
        int refunds = 0;
        AIFunction refund = AIFunctionFactory.Create((string orderId) => { Interlocked.Increment(ref refunds); return "refunded"; }, "issue_refund", "Refunds.");
        var manager = new ScriptedChatClient(messages =>
        {
            int results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            return results switch
            {
                0 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "researcher", AfHarness.Args("""{"query":"q"}"""))]),
                1 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "issue_refund", AfHarness.Args("""{"orderId":"A-1"}"""))]),
                _ => new ChatMessage(ChatRole.Assistant, "done"),
            };
        });
        var h = new AfHarness(af =>
        {
            af.AddAgent(AfHarness.AgentWith(manager, "manager", DurableAgentFunction.For("researcher"), new ApprovalRequiredAIFunction(refund)), r => r.ReadOnly("researcher"));
            af.AddAgent(AfHarness.AgentWith(AfHarness.Echo("found"), "researcher"));
        });
        await h.AskAsync("manager", "s", "go"); // the researcher answered; now the refund waits for a person
        DurableAIAgent durable = h.Agents.Get("manager");

        await durable.CancelAsync(durable.Session("s"));
        await h.DriveAsync();

        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(await h.RunOfAsync("manager", "s"));
        Assert.Equal(nameof(SagaCancelledException), FailureOf(history));
        Assert.Empty(SagaHistory.Rollbacks(history)); // it had nothing to undo
        Assert.DoesNotContain(history.OfType<ActivityScheduled>(), a => a.ActivityType == AgentFrameworkNames.ChildCancel);
        Assert.Equal(0, refunds);
    }

    [Fact]
    public async Task A_cancel_that_arrives_in_the_middle_of_a_turn_stops_it_before_the_next_model_call()
    {
        DurableAIAgent? agent = null;
        AfHarness? harness = null;
        AIFunction lookup = AIFunctionFactory.Create(
            (string q) =>
            {
                agent!.CancelAsync(agent.Session("s")).AsTask().GetAwaiter().GetResult(); // the tool step is where the cancel arrives
                return "found";
            },
            "lookup", "d");
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("lookup", """{"q":"x"}""");
        harness = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat", lookup), r => r.ReadOnly("lookup")));
        agent = harness.Agents.Get("chat");
        await agent.SendAsync([new ChatMessage(ChatRole.User, "go")], agent.Session("s"));

        await harness.DriveAsync();

        Assert.Equal(1, model.CallCount); // the tool ran and was recorded; the second model call was not made
        Assert.Equal(nameof(SagaCancelledException), FailureOf(await harness.HistoryAsync(await harness.RunOfAsync("chat", "s"))));
    }

    private static ActivityContext Context(AfHarness h, Guid? tenant = null) =>
        new(new RunKey(tenant ?? AfHarness.Scope.TenantId, AfHarness.Scope.NamespaceId, Guid.NewGuid()), 1, AgentFrameworkNames.ParentWatch, 1, "k");

    [Fact]
    public async Task A_nested_agent_watches_a_run_of_its_own_tenant_and_namespace_only()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat")));
        var elsewhere = new AfParent { TenantId = Guid.NewGuid(), NamespaceId = AfHarness.Scope.NamespaceId, RunId = Guid.NewGuid(), Signal = "af.child.x" };
        var otherNamespace = new AfParent { TenantId = AfHarness.Scope.TenantId, NamespaceId = Guid.NewGuid(), RunId = Guid.NewGuid(), Signal = "af.child.x" };

        foreach (AfParent parent in new[] { elsewhere, otherNamespace })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await h.Runtime.Activities[AgentFrameworkNames.ParentWatch](Context(h), AfCodec.Write(new AfParentWatch { Parent = parent }, AfJson.Default.AfParentWatch), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Only_the_conversations_of_durable_agents_are_cancelled_this_way()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat")));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await h.Runtime.Activities[AgentFrameworkNames.ChildCancel](Context(h), AfCodec.Write(new AfChildCancel { WorkflowId = "orders/42", Reason = "x" }, AfJson.Default.AfChildCancel), CancellationToken.None));
    }

    [Fact]
    public async Task Asking_a_conversation_that_does_not_exist_to_stop_is_not_an_error()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat")));

        byte[] reply = await h.Runtime.Activities[AgentFrameworkNames.ChildCancel](Context(h), AfCodec.Write(new AfChildCancel { WorkflowId = "af/chat/nobody", Reason = "x" }, AfJson.Default.AfChildCancel), CancellationToken.None);

        Assert.False(AfCodec.Read(reply, AfJson.Default.AfChildCancelResult).Sent);
    }

    [Fact]
    public async Task Asking_a_nested_agent_to_stop_twice_by_the_same_step_is_one_request_in_its_history()
    {
        // The step that asks is run again after a crash; the request it sends must be the same request.
        AfHarness h = Chain(new World());
        await h.AskAsync("manager", "s", "go");
        RunSummary researcher = await RunOfAsync(h, "researcher");
        byte[] ask = AfCodec.Write(new AfChildCancel { WorkflowId = researcher.WorkflowId, Reason = "x" }, AfJson.Default.AfChildCancel);

        for (int i = 0; i < 3; i++)
        {
            await h.Runtime.Activities[AgentFrameworkNames.ChildCancel](Context(h), ask, CancellationToken.None);
        }

        Assert.Equal(1, Cancels(await h.HistoryAsync(researcher.Run)));
    }
}
