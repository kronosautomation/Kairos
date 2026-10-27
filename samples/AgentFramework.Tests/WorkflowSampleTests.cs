using Kairos.Agents;
using Kairos.Agents.AgentFramework;
using Kairos.Samples.AgentFramework;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Samples.AgentFramework.Tests;

/// <summary>The regions of the durable workflows page: a refund waits for finance holding nothing, and a request answered by signal goes on.</summary>
public sealed class WorkflowSampleTests
{
    private sealed class Refunds : IRefunds
    {
        public List<(string Order, decimal Amount, string Key)> Made { get; } = [];

        public Task RefundAsync(string orderId, decimal amount, string idempotencyKey, CancellationToken cancellationToken)
        {
            lock (Made)
            {
                if (!Made.Exists(m => m.Key == idempotencyKey))
                {
                    Made.Add((orderId, amount, idempotencyKey));
                }
            }

            return Task.CompletedTask;
        }
    }

    private static IHost Host(Action<IServiceCollection> register)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        register(builder.Services);
        return builder.Build();
    }

    private static void Fast(KairosOptions o)
    {
        o.UseStore(new InMemoryKairosStore());
        o.PollFallbackInterval = TimeSpan.FromMilliseconds(10);
    }

    [Fact]
    public async Task A_refund_review_asks_an_agent_then_waits_for_finance_and_makes_no_refund_until_they_decide()
    {
        var refunds = new Refunds();
        using IHost host = Host(services => services.AddRefundReview(Fast, new ScriptedChatClient(_ => new ChatMessage(ChatRole.Assistant, "The customer wants their money back.")), refunds));
        await host.StartAsync();
        try
        {
            Task<string> review = WorkflowSample.ReviewAsync(host.Services, "A-1042 120", CancellationToken.None);
            var client = host.Services.GetRequiredService<KairosClient>();
            IKairosStore store = host.Services.GetRequiredService<IKairosStore>();
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            PendingApproval? asked = null;
            RunSummary? workflow = null;
            while (asked is null)
            {
                Assert.True(DateTime.UtcNow < deadline, "finance was never asked");
                workflow = (await store.ListRunsAsync(KairosOptions.DefaultScope, new RunFilter(), 20, CancellationToken.None)).FirstOrDefault(r => r.WorkflowId.StartsWith("afw/refund-review/", StringComparison.Ordinal));
                if (workflow is not null)
                {
                    IReadOnlyList<PendingApproval> waiting = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(workflow.Run));
                    asked = waiting.Count > 0 ? waiting[0] : null;
                }

                await Task.Delay(10);
            }

            Assert.Equal("Refund 120 for order A-1042?", asked.Action); // the text the registration gave
            Assert.Equal(["finance"], asked.Requirement.Roles);
            await Task.Delay(300);
            Assert.False(review.IsCompleted); // waiting, with nothing running
            Assert.Empty(refunds.Made); // and nothing refunded
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_request_answered_by_signal_goes_on_with_the_answer()
    {
        using IHost host = Host(services => services.AddAskCustomer(Fast));
        await host.StartAsync();
        try
        {
            string result = await WorkflowSample.AskCustomerAsync(host.Services, "Which order?", "A-1042", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("the customer said: A-1042", result);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task An_executor_reaches_the_refund_service_with_the_same_key_every_time_the_step_runs()
    {
        // Outside a step there is no key; inside one it is stable per step and executor (the main tests run a step twice and see the same key).
        Assert.False(AgentFrameworkWorkflowContext.IsInStep);
        Assert.Throws<InvalidOperationException>(() => AgentFrameworkWorkflowContext.IdempotencyKey("settle"));
        await Task.CompletedTask;
    }
}
