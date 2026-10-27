using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// Waiting for an answer costs what is new, not what the conversation holds: a look reads the events the run has written since the message was sent, and a caller who has to wait asks the engine to say when the run is written
/// to instead of looking again after a pause.
/// </summary>
public sealed class WaitingTests
{
    private static AfHarness Support(out Func<CountingStore> counting, Action<AgentFrameworkOptions>? options = null)
    {
        CountingStore? store = null;
        var h = new AfHarness(
            af =>
            {
                af.Configure(o =>
                {
                    o.MaxSnapshotBytes = 16 * 1024 * 1024;
                    options?.Invoke(o);
                });
                af.AddAgent(MeasurementTests.SupportAgent(MeasurementTests.SupportModel()), r => r.ReadOnly("lookup_order", "lookup_customer"));
            },
            wrapStore: inner => store = new CountingStore(inner));
        counting = () => store!;
        return h;
    }

    [Fact]
    public async Task A_look_reads_only_what_the_run_has_written_since_the_message_was_sent()
    {
        AfHarness h = Support(out Func<CountingStore> counting);
        for (int i = 1; i <= 12; i++)
        {
            await h.AskAsync("support", "s", $"question {i}");
        }

        DurableAIAgent support = h.Agents.Get("support");
        RunKey run = await h.RunOfAsync("support", "s");
        int before = (await h.HistoryAsync(run)).Count;

        DurableTurn turn = await support.SendAsync([new ChatMessage(ChatRole.User, "and one more")], support.Session("s")); // no id of the caller's: nothing earlier can be its answer
        counting().Reset();
        Assert.Null(await support.TryReadAsync(turn));
        Assert.Equal(1, counting().Calls);
        Assert.Equal(1, counting().Events); // the message itself; none of the 12 conversations' worth of history before it

        await h.DriveAsync();
        int after = (await h.HistoryAsync(run)).Count;
        counting().Reset();
        AgentResponse? reply = await support.TryReadAsync(turn);

        Assert.NotNull(reply);
        Assert.Equal(after - before - 1, counting().Events); // the events written since the previous look
        Assert.True(counting().Events < 25, "a look at a conversation of twelve messages should not read twelve messages");
    }

    [Fact]
    public async Task Looking_again_after_the_answer_gives_the_same_answer_and_reads_nothing_more()
    {
        AfHarness h = Support(out Func<CountingStore> counting);
        DurableAIAgent support = h.Agents.Get("support");
        DurableTurn turn = await support.SendAsync([new ChatMessage(ChatRole.User, "hello")], support.Session("s"));
        await h.DriveAsync();
        AgentResponse? first = await support.TryReadAsync(turn);
        counting().Reset();

        AgentResponse? again = await support.TryReadAsync(turn);

        Assert.NotNull(first);
        Assert.Equal(first.Text, again?.Text);
        Assert.Equal(first.Usage?.TotalTokenCount, again?.Usage?.TotalTokenCount);
        Assert.Equal(0, counting().Events);
    }

    [Fact]
    public async Task A_message_sent_again_with_its_id_after_it_was_answered_finds_the_answer()
    {
        AfHarness h = Support(out _);
        DurableAIAgent support = h.Agents.Get("support");
        DurableAgentSession session = support.Session("s");
        DurableTurn first = await support.SendAsync([new ChatMessage(ChatRole.User, "hello")], session, "message-1");
        await h.DriveAsync();
        string? answer = (await support.TryReadAsync(first))?.Text;
        for (int i = 2; i <= 4; i++)
        {
            await h.AskAsync("support", "s", $"more {i}");
        }

        DurableTurn repeat = await support.SendAsync([new ChatMessage(ChatRole.User, "hello")], session, "message-1"); // a caller that did not hear the first reply
        await h.DriveAsync();

        Assert.NotNull(answer);
        Assert.Equal(answer, (await support.TryReadAsync(repeat))?.Text); // answered once; the repeat reads the answer from the run's start
    }

    [Fact]
    public async Task A_caller_waiting_is_woken_by_the_write_that_has_the_answer_and_not_by_the_end_of_its_wait()
    {
        AfHarness h = Support(out _, o => o.ReplyWait = TimeSpan.FromMinutes(5));
        DurableAIAgent support = h.Agents.Get("support");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Task<AgentResponse> asking = support.RunAsync("hello", support.Session("s"));
        await Task.Delay(150);
        Assert.False(asking.IsCompleted);
        await h.DriveAsync();
        AgentResponse reply = await asking.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(string.IsNullOrEmpty(reply.Text));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the answer should end the wait ({clock.Elapsed.TotalSeconds:N1} s of a five minute wait)");
    }

    [Fact]
    public async Task A_conversation_handed_over_to_a_fresh_run_while_the_caller_waits_is_followed_to_the_new_run()
    {
        AfHarness h = Support(out _, o => o.ContinueAfterEvents = 12);
        DurableAIAgent support = h.Agents.Get("support");
        DurableAgentSession session = support.Session("s");
        using var stop = new CancellationTokenSource();
        Task pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await h.DriveAsync();
                await Task.Delay(5);
            }
        });

        try
        {
            for (int i = 1; i <= 6; i++)
            {
                AgentResponse reply = await support.RunAsync($"question {i}", session).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(string.IsNullOrEmpty(reply.Text));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }

        Assert.True((await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)).Count >= 2, "the conversation should have been handed over");
    }

    [Fact]
    public async Task A_run_that_fails_while_the_caller_waits_ends_the_wait_with_the_failure()
    {
        var model = new ScriptedChatClient(_ => throw new InvalidOperationException("the model is down"));
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        using var stop = new CancellationTokenSource();
        Task pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await h.DriveAsync();
                await Task.Delay(5);
            }
        });

        try
        {
            await Assert.ThrowsAsync<ConversationEndedException>(async () => await chat.RunAsync("hi", chat.Session("s")).WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }
    }
}
