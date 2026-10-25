using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

public sealed class ModelStepTests
{
    [Fact]
    public async Task A_model_reply_is_recorded_and_replay_never_calls_the_model_again()
    {
        ScriptedChatClient model = AgentHarness.Echo("hello there");
        var h = new AgentHarness(b => b.AddModel("m", model));
        h.Workflow("chat", async (ctx, input) =>
        {
            AgentReply reply = await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, AgentHarness.Text(input))]);
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5)); // forces further workflow tasks, each a full replay
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5));
            return AgentHarness.Bytes(reply.Text);
        });

        RunKey run = await h.StartAsync("chat", "hi");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal("hello there", await h.ResultTextAsync(run));
        Assert.Equal(1, model.CallCount); // three workflow tasks replayed the step; only the first one called the model
    }

    [Fact]
    public async Task Model_calls_equal_unique_steps_across_many_replays()
    {
        ScriptedChatClient model = AgentHarness.CallsToolThenAnswers("lookup");
        var h = new AgentHarness(b => b.AddModel("m", model).AddTool(AgentHarness.ReadTool("lookup", _ => "42")));
        h.Workflow("agent", async (ctx, input) => AgentHarness.Bytes(await AgentHarness.AgentLoopAsync(ctx, "m", "what is it", "lookup")));

        RunKey run = await h.StartAsync("agent");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal("all done", await h.ResultTextAsync(run));
        Assert.Equal(2, model.CallCount); // two chat steps, however many tasks replayed them
    }

    [Fact]
    public async Task Token_usage_is_recorded_and_can_be_read_back_from_history()
    {
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()));
        h.Workflow("chat", async (ctx, _) =>
        {
            await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "a")]);
            await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "b")]);
            return [];
        });

        RunKey run = await h.StartAsync("chat");
        await h.RunAsync(run);

        AgentUsage usage = AgentHistory.TotalUsage(await h.HistoryAsync(run));
        Assert.Equal(new AgentUsage(20, 10, 30), usage);
    }

    [Fact]
    public async Task The_idempotency_key_of_a_model_step_is_the_same_on_every_attempt()
    {
        int attempt = 0;
        var model = new ScriptedChatClient(_ =>
        {
            if (Interlocked.Increment(ref attempt) < 3)
            {
                throw new ModelRateLimitedException("slow down");
            }

            return new ChatMessage(ChatRole.Assistant, "ok");
        });
        var h = new AgentHarness(b => b.AddModel("m", model));
        h.Workflow("chat", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "x")])).Text));

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(3, model.CallCount);
        Assert.Single(model.Keys.Distinct());
        Assert.StartsWith(run.RunId.ToString("N"), model.Keys[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_model_alias_fails_at_once_without_retrying()
    {
        var h = new AgentHarness(_ => { });
        h.Workflow("chat", async (ctx, _) =>
        {
            await ctx.ChatAsync("nope", [new ChatMessage(ChatRole.User, "x")]);
            return [];
        });

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        ActivityFailed failure = Assert.Single(history.OfType<ActivityFailed>());
        Assert.Equal(nameof(UnknownModelException), failure.ErrorType);
        Assert.False(failure.WillRetry);
    }

    [Fact]
    public async Task Redaction_applies_before_anything_is_recorded()
    {
        var model = new ScriptedChatClient(messages =>
            new ChatMessage(ChatRole.Assistant, "your card is 4111-1111-1111-1111"));
        var h = new AgentHarness(b => b.AddModel("m", model).UseRedactor(t => t.Replace("4111-1111-1111-1111", "[card]", StringComparison.Ordinal)));
        h.Workflow("chat", async (ctx, input) =>
        {
            AgentReply reply = await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "my card is 4111-1111-1111-1111")]);
            return AgentHarness.Bytes(reply.Text);
        });

        RunKey run = await h.StartAsync("chat");
        await h.RunAsync(run);

        Assert.Equal("your card is [card]", await h.ResultTextAsync(run));
        foreach (HistoryEvent e in await h.HistoryAsync(run))
        {
            byte[] payload = e switch { ActivityScheduled s => s.Input, ActivityCompleted c => c.Result, _ => [] };
            Assert.DoesNotContain("4111", AgentHarness.Text(payload), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Streaming_shows_text_to_an_observer_but_records_only_the_final_reply()
    {
        var seen = new List<string>();
        var observer = new CollectingObserver(seen);
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo("a whole answer")).UseObserver(observer));
        h.Workflow("chat", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "x")])).Text));

        RunKey run = await h.StartAsync("chat");
        await h.RunAsync(run);

        Assert.Equal("a whole answer", await h.ResultTextAsync(run));
        Assert.Contains("a whole answer", string.Concat(seen), StringComparison.Ordinal);
        Assert.Single((await h.HistoryAsync(run)).OfType<ActivityCompleted>());
    }

    [Fact]
    public async Task A_broken_observer_does_not_fail_the_step()
    {
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo("fine")).UseObserver(new ThrowingObserver()));
        h.Workflow("chat", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "x")])).Text));

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("fine", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task Tool_declarations_reach_the_model_and_unknown_tool_names_are_refused_up_front()
    {
        IList<AITool>? offered = null;
        var model = new ScriptedChatClient(_ => new ChatMessage(ChatRole.Assistant, "x"));
        var spy = new OptionsSpy(model, o => offered = o?.Tools);
        var h = new AgentHarness(b => b.AddModel("m", spy).AddTool(AgentHarness.ReadTool("lookup", _ => "1")));
        h.Workflow("chat", async (ctx, _) =>
        {
            await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "x")], ctx.Tools("lookup"));
            return [];
        });
        RunKey run = await h.StartAsync("chat");
        await h.RunAsync(run);
        Assert.Equal("lookup", Assert.Single(offered!).Name);

        var ctxFactory = h.Runtime;
        Assert.Throws<UnknownToolException>(() => ctxFactory.CreateContext(new Kairos.WorkflowExecution((_, _) => Task.FromResult(Array.Empty<byte>()), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())).Tools("missing"));
    }

    [Fact]
    public async Task Content_a_step_cannot_record_is_refused_clearly()
    {
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()));
        h.Workflow("chat", async (ctx, _) =>
        {
            await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2 }, "image/png")])]);
            return [];
        });

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Contains(nameof(NotSupportedException), (await h.FailureAsync(run)).ErrorType, StringComparison.Ordinal);
    }

    private sealed class CollectingObserver(List<string> seen) : IAgentObserver
    {
        public ValueTask OnTextAsync(RunKey run, int stepId, string delta, CancellationToken cancellationToken)
        {
            seen.Add(delta);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IAgentObserver
    {
        public ValueTask OnTextAsync(RunKey run, int stepId, string delta, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the UI went away");
    }

    private sealed class OptionsSpy(IChatClient inner, Action<ChatOptions?> spy) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            spy(options);
            return inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
        }
    }
}
