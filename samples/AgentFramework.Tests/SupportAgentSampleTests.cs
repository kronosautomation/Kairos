using Kairos.Agents;
using Kairos.Samples.AgentFramework;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Samples.AgentFramework.Tests;

/// <summary>Every region the Agent Framework page quotes is run here: in a real host, against the in-memory store, with a scripted model.</summary>
public sealed class SupportAgentSampleTests
{
    private sealed class Orders : IOrders
    {
        public List<string> Lookups { get; } = [];

        public List<string> RefundKeys { get; } = [];

        public Task<string> FindAsync(string orderId, CancellationToken cancellationToken)
        {
            Lookups.Add(orderId);
            return Task.FromResult($"order {orderId}: shipped");
        }

        public Task<string> RefundAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken)
        {
            RefundKeys.Add(idempotencyKey);
            return Task.FromResult($"refunded {orderId}");
        }
    }

    private static IHost Host(IChatClient model, IOrders orders, TokenBudget budget)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSupportAgent(
            o =>
            {
                o.UseStore(new InMemoryKairosStore());
                o.PollFallbackInterval = TimeSpan.FromMilliseconds(10);
            },
            model, orders, budget);
        return builder.Build();
    }

    private static async Task<RunKey> WaitForRunAsync(IServiceProvider services)
    {
        IKairosStore store = services.GetRequiredService<IKairosStore>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            RunSummary[] runs = [.. await store.ListRunsAsync(KairosOptions.DefaultScope, new RunFilter(), 10, CancellationToken.None)];
            if (runs.Length > 0)
            {
                return runs[0].Run;
            }

            Assert.True(DateTime.UtcNow < deadline, "the conversation never started");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_lookup_runs_the_refund_waits_for_a_person_and_the_answer_comes_after()
    {
        var orders = new Orders();
        var budget = new TokenBudget(100_000);
        using IHost host = Host(new DemoModel(), orders, budget);
        await host.StartAsync();
        try
        {
            Task<string> answer = SupportAgentSample.AskAsync(host.Services, "Please refund order A-1042.", CancellationToken.None);
            RunKey run = await WaitForRunAsync(host.Services);

            // The refund is held for approval; the read-only lookup was not.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            IReadOnlyList<PendingApproval> waiting;
            var client = host.Services.GetRequiredService<KairosClient>();
            while ((waiting = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(run))).Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "no approval was requested");
                await Task.Delay(10);
            }

            Assert.Equal("issue_refund", waiting[0].Tool);
            Assert.Equal(["A-1042"], orders.Lookups);
            Assert.Empty(orders.RefundKeys);
            Assert.False(answer.IsCompleted);

            Assert.True(await SupportAgentSample.ApproveFirstAsync(host.Services, run, new ApproverIdentity("user:alice", "Alice", ["finance"]), CancellationToken.None));

            Assert.Equal("Your order A-1042 has been refunded.", await answer.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Single(orders.RefundKeys);
            Assert.StartsWith(run.RunId.ToString("N"), orders.RefundKeys[0], StringComparison.Ordinal); // the key the refund service de-duplicates on is the step's
            Assert.InRange(budget.Used, 1, 3 * 150); // three model calls, settled at what they cost, not at the worst case
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_budget_that_is_used_up_pauses_the_run_for_a_person_instead_of_calling_the_model()
    {
        var model = new CountingModel();
        using IHost host = Host(model, new Orders(), new TokenBudget(tokensPerDay: 100)); // less than one call may cost (400)
        await host.StartAsync();
        try
        {
            Task<string> answer = SupportAgentSample.AskAsync(host.Services, "Please refund order A-1042.", CancellationToken.None);
            RunKey run = await WaitForRunAsync(host.Services);
            var client = host.Services.GetRequiredService<KairosClient>();
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            IReadOnlyList<PendingApproval> waiting;
            while ((waiting = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(run))).Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "no approval was requested");
                await Task.Delay(10);
            }

            Assert.Contains("daily-tokens", waiting[0].Action, StringComparison.Ordinal);
            Assert.Equal(["finance"], waiting[0].Requirement.Roles);
            Assert.Equal(0, model.Calls);
            Assert.False(answer.IsCompleted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class CountingModel : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
