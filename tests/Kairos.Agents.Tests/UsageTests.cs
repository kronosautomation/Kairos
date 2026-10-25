using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

public sealed class UsageTests
{
    [Fact]
    public async Task Each_model_step_records_its_usage_once_and_the_run_reports_the_sum()
    {
        var model = new ScriptedChatClient(messages => messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, "done")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", AgentHarness.WireArgs("{}"))]));
        var h = new AgentHarness(b => b.AddModel("m", model).AddTool(AgentHarness.ReadTool("lookup", _ => "found")));
        h.Workflow("agent", async (ctx, _) => AgentHarness.Bytes(await AgentHarness.AgentLoopAsync(ctx, "m", "hello", "lookup")));

        RunKey run = await h.StartAsync("agent");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        UsageRecord[] recorded = [.. history.OfType<ActivityCompleted>().Where(c => c.Usage is not null).Select(c => c.Usage!)];
        Assert.Equal(2, recorded.Length); // two model steps; the tool step reports none
        Assert.All(recorded, u => Assert.Equal((10L, 5L, 0L, "m"), (u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.Model)));

        RunUsage usage = await h.Client.GetUsageAsync(run);
        Assert.Equal((2, 20L, 10L, 30L), (usage.Steps, usage.InputTokens, usage.OutputTokens, usage.TotalTokens));
        Assert.Equal(model.CallCount, usage.Steps);
    }

    [Fact]
    public async Task Cached_tokens_and_the_answering_model_are_recorded_and_a_crash_does_not_double_count()
    {
        var cached = new CachedUsageClient();
        var h = new AgentHarness(b => b.AddModel("alias", cached), faultInjection: true, seed: 5);
        h.Workflow("agent", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("alias", [new ChatMessage(ChatRole.User, "hi")])).Text));

        RunKey run = await h.StartAsync("agent");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        RunUsage usage = await h.Client.GetUsageAsync(run);
        Assert.Equal(1, usage.Steps); // however many attempts the faults caused, one step completed and is counted once
        ModelUsage model = Assert.Single(usage.ByModel);
        Assert.Equal(("provider-model-v3", 400L, 25L, 300L), (model.Model, model.InputTokens, model.OutputTokens, model.CachedInputTokens));
    }

    private sealed class CachedUsageClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                ModelId = "provider-model-v3",
                Usage = new UsageDetails { InputTokenCount = 400, OutputTokenCount = 25, TotalTokenCount = 425, CachedInputTokenCount = 300 },
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
