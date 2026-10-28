using Kairos.Agents;
using Kairos.Samples.AgentFramework;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Samples.AgentFramework.Tests;

/// <summary>The regions of the Agent Framework page about <c>AllowWithoutApproval</c> and a rule of the operators' policy: the owner's mark does not outrank the rule.</summary>
public sealed class CreditAgentSampleTests
{
    private sealed class Credits : ICredits
    {
        public List<(string Customer, decimal Amount)> Issued { get; } = [];

        public Task<string> IssueAsync(string customerId, decimal amount, CancellationToken cancellationToken)
        {
            Issued.Add((customerId, amount));
            return Task.FromResult($"credited {amount} to {customerId}");
        }
    }

    private static IHost Host(decimal amount, ICredits credits)
    {
        var model = new ScriptedChatClient(messages =>
            messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatMessage(ChatRole.Assistant, "Done.")
                : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "issue_credit", new Dictionary<string, object?> { ["customerId"] = "C-1", ["amount"] = amount })]));
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddCreditAgent(
            o =>
            {
                o.UseStore(new InMemoryKairosStore()).AllowUnencryptedPayloads();
                o.PollFallbackInterval = TimeSpan.FromMilliseconds(10);
            },
            model, credits);
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
    public async Task A_small_credit_runs_without_asking_because_the_owner_allowed_the_tool()
    {
        var credits = new Credits();
        using IHost host = Host(20m, credits);
        await host.StartAsync();
        try
        {
            var credit = host.Services.GetRequiredKeyedService<Microsoft.Agents.AI.AIAgent>("credits");
            var session = await credit.CreateSessionAsync();

            Microsoft.Agents.AI.AgentResponse answer = await credit.RunAsync("credit C-1 with 20", session).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("Done.", answer.Text);
            Assert.Equal([("C-1", 20m)], credits.Issued);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_credit_over_the_limit_waits_for_finance_whatever_the_owner_allowed()
    {
        var credits = new Credits();
        using IHost host = Host(500m, credits);
        await host.StartAsync();
        try
        {
            var credit = host.Services.GetRequiredKeyedService<Microsoft.Agents.AI.AIAgent>("credits");
            var session = await credit.CreateSessionAsync();
            Task<Microsoft.Agents.AI.AgentResponse> answer = credit.RunAsync("credit C-1 with 500", session);
            RunKey run = await WaitForRunAsync(host.Services);
            var client = host.Services.GetRequiredService<KairosClient>();
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            IReadOnlyList<PendingApproval> waiting;
            while ((waiting = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(run))).Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the rule's approval was never requested");
                await Task.Delay(10);
            }

            Assert.Equal("issue_credit", waiting[0].Tool);
            Assert.Equal(["finance"], waiting[0].Requirement.Roles); // the rule's requirement, not the owner's mark
            Assert.Empty(credits.Issued);
            Assert.False(answer.IsCompleted);

            await Task.Delay(300);
            Assert.Empty(credits.Issued); // still waiting: nothing was credited
            Assert.False(answer.IsCompleted);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
