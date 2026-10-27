using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>The one-line way: <c>services.AddKairos(...).AddAgentFramework(af =&gt; af.AddAgent(agent))</c>, in a real host with real workers.</summary>
public sealed class HostingTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    private sealed class Lines : ILoggerProvider
    {
        public List<string> All { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(Lines owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.All)
                {
                    owner.All.Add($"{logLevel}: {formatter(state, exception)}");
                }
            }
        }
    }

    private static IHost Host(Action<AgentFrameworkBuilder> configure, Lines? logs = null)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddKairos(o =>
        {
            o.UseStore(new InMemoryKairosStore());
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(10);
        }).AddAgentFramework(configure);
        return builder.Build();
    }

    [Fact]
    public async Task One_line_makes_an_agent_durable_and_callers_get_a_real_AIAgent()
    {
        ScriptedChatClient model = AfHarness.LookupRefundAnswer();
        using IHost host = Host(af => af.AddAgent(AfHarness.SupportAgent(model), r => r.ReadOnly("lookup_order")));
        await host.StartAsync();
        try
        {
            AIAgent support = host.Services.GetRequiredKeyedService<AIAgent>("support");
            Assert.IsType<DurableAIAgent>(support);
            Assert.Same(support, host.Services.GetRequiredService<DurableAgents>().Get("support"));
            Assert.Same(support, host.Services.GetRequiredKeyedService<DurableAIAgent>("support"));

            AgentSession session = await support.CreateSessionAsync();
            Task<AgentResponse> call = support.RunAsync("refund order A-1042", session);

            // The refund waits for a person; the run holds no compute meanwhile. The approvals inbox is the history.
            var client = host.Services.GetRequiredService<KairosClient>();
            RunKey run = await WaitForRunAsync(client, host.Services.GetRequiredService<DurableAgents>(), (DurableAgentSession)session);
            PendingApproval pending = await WaitForApprovalAsync(client, run);
            Assert.Equal("issue_refund", pending.Tool);
            Assert.False(call.IsCompleted);

            var approvals = host.Services.GetRequiredService<AgentClient>();
            await approvals.ApproveAsync(run, pending.ApprovalId, new ApproverIdentity("user:alice", "Alice", ["finance"], Evidence: null));
            AgentResponse reply = await call.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("Refund issued.", reply.Text);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<RunKey> WaitForRunAsync(KairosClient client, DurableAgents agents, DurableAgentSession session)
    {
        string id = DurableAgents.WorkflowId("support", session.SessionId);
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            StartRunResult found = await client.StartAsync(KairosOptions.DefaultScope, id, AgentFrameworkNames.Conversation, AfCodec.Write(new AfDriverInput { Agent = "support" }, AfJson.Default.AfDriverInput));
            if (!found.Started)
            {
                return new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, found.RunId);
            }

            Assert.True(DateTime.UtcNow < deadline, "the conversation never started");
            await Task.Delay(10);
        }
    }

    private static async Task<PendingApproval> WaitForApprovalAsync(KairosClient client, RunKey run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            IReadOnlyList<PendingApproval> pending = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(run));
            if (pending.Count > 0)
            {
                return pending[0];
            }

            Assert.True(DateTime.UtcNow < deadline, "no approval was requested");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Tools_nobody_classified_are_logged_by_name_when_the_host_starts()
    {
        var logs = new Lines();
        AIFunction unclassified = AIFunctionFactory.Create((string q) => q, "send_receipt", "d");
        AIFunction classified = AIFunctionFactory.Create((string q) => q, "lookup", "d");
        using IHost host = Host(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "agent", unclassified, classified), r => r.ReadOnly("lookup")), logs);

        await host.StartAsync();
        await host.StopAsync();

        string warning = Assert.Single(logs.All, l => l.Contains("send_receipt", StringComparison.Ordinal));
        Assert.StartsWith("Warning:", warning, StringComparison.Ordinal);
        Assert.Contains("ReadOnly(\"send_receipt\")", warning, StringComparison.Ordinal);
        Assert.Contains("AllowWithoutApproval(\"send_receipt\")", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.All, l => l.Contains("tool 'lookup'", StringComparison.Ordinal));
    }

    [Fact]
    public void AddAgentFramework_needs_AddKairos_first_and_says_so()
    {
        var services = new ServiceCollection();

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => services.AddAgentFramework(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "a"))));

        Assert.Contains("AddKairos first", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_that_cannot_be_made_durable_stops_the_application_at_registration_not_at_the_first_message()
    {
        var services = new ServiceCollection();
        services.AddKairos(o => o.UseStore(new InMemoryKairosStore()));

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => services.AddAgentFramework(af => af.AddAgent(new OpaqueAgent("remote", (_, _) => "x"))));

        Assert.Contains("AllowTurnLevel()", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsDurable_finds_the_durable_twin_of_the_agent_you_registered()
    {
        ChatClientAgent mine = AfHarness.AgentWith(AfHarness.Echo("hello"), "greeter");
        using IHost host = Host(af => af.AddAgent(mine));
        await host.StartAsync();
        try
        {
            DurableAIAgent durable = mine.AsDurable(host.Services.GetRequiredService<DurableAgents>());
            AgentResponse reply = await durable.RunAsync("hi", await durable.CreateSessionAsync()).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("hello", reply.Text);
            Assert.Throws<UnknownAgentException>(() => AfHarness.AgentWith(AfHarness.Echo(), "other").AsDurable(host.Services.GetRequiredService<DurableAgents>()));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
