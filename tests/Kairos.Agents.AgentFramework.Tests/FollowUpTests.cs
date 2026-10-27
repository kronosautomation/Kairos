using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>Cases the mutation run showed to be unguarded: another client's unredacted message, calls the provider already made, a worker without the agent, a notification to a run of another tenant.</summary>
public sealed class FollowUpTests
{
    [Fact]
    public async Task A_message_sent_without_the_handle_is_redacted_by_the_workflow_before_a_step_records_it()
    {
        const string Secret = "SECRET-123-4567";
        ScriptedChatClient model = new(_ => new ChatMessage(ChatRole.Assistant, "ok"));
        var h = new AfHarness(af => af.ConfigureAgents(b => b.UseRedactor(t => t.Replace(Secret, "[redacted]", StringComparison.Ordinal))).AddAgent(AfHarness.AgentWith(model, "chat")));
        await h.AskAsync("chat", "s", "hello");
        RunKey run = await h.RunOfAsync("chat", "s");

        // Another client, one that holds no redactor, signals the conversation directly.
        var raw = new AfTurn
        {
            TurnId = "raw-1",
            Messages = System.Text.Json.JsonSerializer.SerializeToElement(new List<ChatMessage> { new(ChatRole.User, "my number is " + Secret) }, AIJsonUtilities.DefaultOptions),
        };
        Assert.True(await h.Client.SignalAsync(run, AgentFrameworkNames.MessageSignal, AfCodec.Write(raw, AfJson.Default.AfTurn), "raw-1", default));
        await h.DriveAsync();

        string steps = string.Join('\n', (await h.HistoryAsync(run)).OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentFrameworkNames.Step).Select(a => System.Text.Encoding.UTF8.GetString(a.Input)));
        Assert.Contains("[redacted]", steps, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, steps, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_the_provider_already_made_is_not_a_request_for_a_tool_step()
    {
        ScriptedChatClient informational = new(_ => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", AfHarness.Args("""{"q":"x"}""")) { InformationalOnly = true }, new TextContent("done")]));
        AIFunction lookup = AIFunctionFactory.Create((string q) => "x", "lookup", "d");
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(informational, "a", lookup), r => r.ReadOnly("lookup")));

        (AgentResponse? reply, _) = await h.AskAsync("a", "s", "go");

        Assert.Equal("done", AfHarness.Text(reply));
        Assert.Equal(1, informational.CallCount);
        Assert.DoesNotContain((await h.HistoryAsync(await h.RunOfAsync("a", "s"))).OfType<ActivityScheduled>(), a => a.ActivityType == AgentActivityNames.Tool);
    }

    [Fact]
    public async Task A_turn_level_answer_that_contains_a_call_is_the_answer_not_a_request_for_a_tool_step()
    {
        var opaque = new CallingOpaqueAgent();
        var h = new AfHarness(af => af.AddAgent(opaque, r => r.AllowTurnLevel()));

        (AgentResponse? reply, _) = await h.AskAsync("opaque", "s", "go");

        Assert.Equal("answered", AfHarness.Text(reply));
        Assert.DoesNotContain((await h.HistoryAsync(await h.RunOfAsync("opaque", "s"))).OfType<ActivityScheduled>(), a => a.ActivityType == AgentActivityNames.Tool);
    }

    private sealed class CallingOpaqueAgent : AIAgent
    {
        public override string? Name => "opaque";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new Session());

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(System.Text.Json.JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            new(new Session());

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "some_tool", AfHarness.Args("{}")), new TextContent("answered")])));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (AgentResponseUpdate update in (await RunCoreAsync(messages, session, options, cancellationToken)).ToAgentResponseUpdates())
            {
                yield return update;
            }
        }

        private sealed class Session : AgentSession;
    }

    [Fact]
    public async Task A_worker_without_the_agent_leaves_the_run_for_one_that_has_it()
    {
        ScriptedChatClient model = AfHarness.Echo("hello");
        var withAgent = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));
        DurableAIAgent chat = withAgent.Agents.Get("chat");
        DurableTurn turn = await chat.SendAsync([new ChatMessage(ChatRole.User, "hi")], chat.Session("s"));

        var without = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "other")), redeployOf: withAgent);
        var faults = new List<Exception>();
        without.Worker.OnFault = f => faults.Add(f.Error);
        await without.Worker.ProcessWorkflowTaskAsync(default);
        RunKey run = await withAgent.RunOfAsync("chat", "s");

        Assert.Single(faults); // the workflow type of an agent it does not have is not registered: the task is released, not failed
        Assert.Equal(RunState.Running, await withAgent.Client.GetStateAsync(run));

        withAgent.Time.Advance(TimeSpan.FromSeconds(5));
        await withAgent.DriveAsync();
        Assert.Equal("hello", AfHarness.Text(await withAgent.ReadAsync("chat", turn)));
    }

    [Fact]
    public async Task A_nested_agent_answers_only_a_run_of_its_own_tenant_and_namespace()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat")));
        var elsewhere = new AfParent { TenantId = Guid.NewGuid(), NamespaceId = Guid.NewGuid(), RunId = Guid.NewGuid(), Signal = "af.child.x" };
        byte[] input = AfCodec.Write(new AfNotify { Parent = elsewhere, Answer = new AfChildAnswer { Text = "hi" } }, AfJson.Default.AfNotify);
        var context = new ActivityContext(new RunKey(AfHarness.Scope.TenantId, AfHarness.Scope.NamespaceId, Guid.NewGuid()), 1, AgentFrameworkNames.ChildNotify, 1, "k");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Runtime.Activities[AgentFrameworkNames.ChildNotify](context, input, default));
    }

    [Fact]
    public async Task Closing_a_conversation_that_never_started_starts_nothing()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableAgentSession session = chat.Session("never");

        await chat.CloseAsync(session);

        Assert.Null(await chat.FindRunAsync(session));
        Assert.Empty(await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 10, default));
    }
}
