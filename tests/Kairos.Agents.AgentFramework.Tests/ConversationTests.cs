using System.Text.Json;
using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>One run per conversation, a signal per message: the session threads through the steps, a message sent twice is answered once, and a long conversation carries on in a fresh run.</summary>
public sealed class ConversationTests
{
    private static ScriptedChatClient Counting(List<int>? sizes = null) => new(messages =>
    {
        sizes?.Add(messages.Count);
        int users = messages.Count(m => m.Role == ChatRole.User);
        return new ChatMessage(ChatRole.Assistant, $"answer {users}");
    });

    [Fact]
    public async Task The_session_is_threaded_through_the_messages_of_a_conversation()
    {
        var sizes = new List<int>();
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(sizes), "chat")));

        (AgentResponse? first, _) = await h.AskAsync("chat", "s1", "hello");
        (AgentResponse? second, _) = await h.AskAsync("chat", "s1", "and then?");

        Assert.Equal("answer 1", AfHarness.Text(first));
        Assert.Equal("answer 2", AfHarness.Text(second));
        Assert.Equal([1, 3], sizes); // the message; then both messages and the first answer (the instructions travel in the options)
    }

    [Fact]
    public async Task Separate_sessions_are_separate_conversations()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(), "chat")));

        (AgentResponse? a, _) = await h.AskAsync("chat", "alice", "hi");
        (AgentResponse? b, _) = await h.AskAsync("chat", "bob", "hi");
        (AgentResponse? a2, _) = await h.AskAsync("chat", "alice", "again");

        Assert.Equal(["answer 1", "answer 1", "answer 2"], [AfHarness.Text(a), AfHarness.Text(b), AfHarness.Text(a2)]);
        Assert.NotEqual(await h.RunOfAsync("chat", "alice"), await h.RunOfAsync("chat", "bob"));
    }

    [Fact]
    public async Task A_message_sent_twice_under_one_id_is_answered_once()
    {
        ScriptedChatClient model = Counting();
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableAgentSession session = chat.Session("s");

        DurableTurn first = await chat.SendAsync([new ChatMessage(ChatRole.User, "hello")], session, "m-1");
        await h.DriveAsync();
        DurableTurn again = await chat.SendAsync([new ChatMessage(ChatRole.User, "hello")], session, "m-1"); // the caller did not see the first send arrive
        await h.DriveAsync();

        Assert.Equal(first.TurnId, again.TurnId);
        Assert.Equal(1, model.CallCount);
        Assert.Equal("answer 1", AfHarness.Text(await chat.TryReadAsync(again)));
    }

    [Fact]
    public async Task A_conversation_that_grows_long_continues_in_a_fresh_run_with_its_session()
    {
        var sizes = new List<int>();
        var h = new AfHarness(af => af.Configure(o => o.ContinueAfterEvents = 8).AddAgent(AfHarness.AgentWith(Counting(sizes), "chat")));

        var runs = new List<RunKey>();
        var turns = new List<DurableTurn>();
        for (int i = 1; i <= 6; i++)
        {
            (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("chat", "long", $"message {i}");
            Assert.Equal($"answer {i}", AfHarness.Text(reply));
            turns.Add(turn);
            runs.Add(await h.RunOfAsync("chat", "long"));
        }

        Assert.True(runs.Distinct().Count() >= 3, "the conversation should have moved to fresh runs");
        Assert.Equal([1, 3, 5, 7, 9, 11], sizes); // the session came along every time
        RunKey first = runs[0];
        Assert.Equal(RunState.ContinuedAsNew, await h.Client.GetStateAsync(first));

        // An answer recorded in an earlier run is still found.
        Assert.Equal("answer 1", AfHarness.Text(await h.ReadAsync("chat", turns[0])));
    }

    [Fact]
    public async Task Messages_that_arrive_while_the_run_hands_over_are_carried_to_the_next_run()
    {
        ScriptedChatClient model = Counting();
        var h = new AfHarness(af => af.Configure(o => o.ContinueAfterEvents = 8).AddAgent(AfHarness.AgentWith(model, "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableAgentSession session = chat.Session("burst");

        var turns = new List<DurableTurn>();
        for (int i = 1; i <= 5; i++)
        {
            turns.Add(await chat.SendAsync([new ChatMessage(ChatRole.User, $"m{i}")], session, $"burst-{i}")); // five messages before the worker has looked at any
        }

        await h.DriveAsync();

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"answer {i + 1}", AfHarness.Text(await chat.TryReadAsync(turns[i])));
        }

        Assert.Equal(5, model.CallCount);
    }

    [Fact]
    public async Task A_conversation_that_is_closed_takes_no_more_messages()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(), "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableAgentSession session = chat.Session("s");
        await h.AskAsync("chat", "s", "hello");
        RunKey run = await h.RunOfAsync("chat", "s");

        await chat.CloseAsync(session);
        await h.DriveAsync();

        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(run));
        await Assert.ThrowsAsync<ConversationEndedException>(async () => await chat.SendAsync([new ChatMessage(ChatRole.User, "more")], session));
    }

    [Fact]
    public async Task A_conversation_nobody_writes_to_ends_after_the_idle_timeout_without_holding_compute()
    {
        var h = new AfHarness(af => af.Configure(o => o.ConversationIdleTimeout = TimeSpan.FromHours(1)).AddAgent(AfHarness.AgentWith(Counting(), "chat")));
        await h.AskAsync("chat", "s", "hello", jumpClock: false);
        RunKey run = await h.RunOfAsync("chat", "s");
        Assert.Equal(RunState.Running, await h.Client.GetStateAsync(run));

        h.Time.Advance(TimeSpan.FromMinutes(59));
        await h.DriveAsync(jumpClock: false);
        Assert.Equal(RunState.Running, await h.Client.GetStateAsync(run));

        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.DriveAsync(jumpClock: false);
        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(run));
    }

    [Fact]
    public async Task A_message_that_makes_the_agent_loop_forever_ends_the_run_with_a_clear_error()
    {
        var model = new ScriptedChatClient(_ => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), "lookup", AfHarness.Args("""{"q":"x"}"""))]));
        AIFunction lookup = AIFunctionFactory.Create((string q) => "x", "lookup", "d");
        var h = new AfHarness(af => af.Configure(o => o.MaxStepsPerTurn = 4).AddAgent(AfHarness.AgentWith(model, "loop", lookup), r => r.ReadOnly("lookup")));

        DurableTurn turn = await h.SendAsync("loop", "s", "go");

        AgentStepLimitException e = await Assert.ThrowsAsync<AgentStepLimitException>(async () => await h.Agents.Get("loop").TryReadAsync(turn));
        Assert.Contains("4 model calls", e.Message, StringComparison.Ordinal);
        Assert.Equal(4, model.CallCount);
    }

    [Fact]
    public async Task A_session_that_grows_past_the_limit_fails_the_step_with_a_clear_error()
    {
        ScriptedChatClient model = Counting();
        var h = new AfHarness(af => af.Configure(o => o.MaxSnapshotBytes = 1500).AddAgent(AfHarness.AgentWith(model, "chat")));

        (AgentResponse? first, _) = await h.AskAsync("chat", "s", "short");
        Assert.NotNull(first);
        DurableTurn big = await h.SendAsync("chat", "s", new string('x', 1400));

        ConversationEndedException e = await Assert.ThrowsAsync<ConversationEndedException>(async () => await h.Agents.Get("chat").TryReadAsync(big));
        Assert.Contains("SnapshotTooLargeException", e.Message, StringComparison.Ordinal);
        Assert.Contains("MaxSnapshotBytes", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, model.CallCount); // a failure that is the caller's to fix is not retried: the model was not asked a third time
    }

    [Fact]
    public async Task A_session_already_past_the_limit_is_refused_before_the_model_is_asked()
    {
        ScriptedChatClient model = Counting();
        var h1 = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));
        await h1.AskAsync("chat", "s", new string('x', 1400));
        Assert.Equal(1, model.CallCount);

        var h2 = new AfHarness(af => af.Configure(o => o.MaxSnapshotBytes = 1000).AddAgent(AfHarness.AgentWith(model, "chat")), redeployOf: h1); // the limit was lowered
        DurableTurn next = await h2.SendAsync("chat", "s", "more");

        Assert.Equal(1, model.CallCount);
        await Assert.ThrowsAsync<ConversationEndedException>(async () => await h2.Agents.Get("chat").TryReadAsync(next));
    }

    [Fact]
    public async Task A_session_survives_serialization_like_any_other_agent_session()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(), "chat")));
        AIAgent chat = h.Agents.Get("chat");
        AgentSession session = await chat.CreateSessionAsync();
        ((DurableAgentSession)session).OnBehalfOf = "customer:42";

        JsonElement saved = await chat.SerializeSessionAsync(session);
        var restored = (DurableAgentSession)await chat.DeserializeSessionAsync(saved);

        Assert.Equal(((DurableAgentSession)session).SessionId, restored.SessionId);
        Assert.Equal("customer:42", restored.OnBehalfOf);
        await Assert.ThrowsAsync<JsonException>(async () => await new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(), "other"))).Agents.Get("other").DeserializeSessionAsync(saved));
    }

    [Fact]
    public async Task The_durable_agent_is_an_ordinary_agent_that_answers_RunAsync()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(), "chat")));
        AIAgent chat = h.Agents.Get("chat");
        AgentSession session = await chat.CreateSessionAsync();

        using var done = new CancellationTokenSource();
        Task<AgentResponse> call = chat.RunAsync("hello there", session);
        Task pump = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                await h.DriveAsync();
                h.Time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(1);
            }
        });

        AgentResponse reply = await call.WaitAsync(TimeSpan.FromSeconds(30));
        AgentResponse second = await chat.RunAsync("again", session).WaitAsync(TimeSpan.FromSeconds(30));
        await done.CancelAsync();
        await pump;

        Assert.Equal(["answer 1", "answer 2"], [reply.Text, second.Text]);
        Assert.Equal("chat", reply.AgentId);
    }

    [Fact]
    public async Task A_conversation_goes_on_after_the_application_is_redeployed()
    {
        var sizes = new List<int>();
        var h1 = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(sizes), "chat")));
        await h1.AskAsync("chat", "s", "before");

        var h2 = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(Counting(sizes), "chat")), redeployOf: h1);
        (AgentResponse? reply, _) = await h2.AskAsync("chat", "s", "after");

        Assert.Equal("answer 2", AfHarness.Text(reply));
        Assert.Equal([1, 3], sizes);
    }
}
