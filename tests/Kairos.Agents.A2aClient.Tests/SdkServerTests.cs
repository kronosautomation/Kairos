using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using A2A;
using A2A.AspNetCore;
using Kairos.A2A;
using Kairos.Agents;
using Kairos.Agents.A2aClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kairos.Agents.A2aClient.Tests;

/// <summary>The durable client against the real SDK server over HTTP: signed Agent Cards are fetched, verified as received, and then used.</summary>
public sealed class SdkServerTests
{
    private sealed class ReportAgent : IAgentHandler
    {
        public static int Executions;

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Executions);
            var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
            await updater.SubmitAsync(cancellationToken: cancellationToken);
            await updater.StartWorkAsync(cancellationToken: cancellationToken);
            await Task.Delay(300, cancellationToken);
            await updater.AddArtifactAsync([Part.FromText("report for " + context.UserText)], cancellationToken: cancellationToken);
            await updater.CompleteAsync(cancellationToken: cancellationToken);
        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<string> Why(A2aRig rig, RunKey run)
    {
        IReadOnlyList<HistoryEvent> h = await rig.HistoryAsync(run);
        return string.Join(" | ", h.OfType<ActivityFailed>().Select(f => f.ErrorType + ": " + f.Message)) + (h[^1] is WorkflowFailed w ? " || " + w.Message : "");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static AgentCard Card(int port) => new()
    {
        Name = "Report agent",
        Description = "Builds reports.",
        Version = "1.0",
        SupportedInterfaces = [new AgentInterface { Url = $"http://127.0.0.1:{port}/a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
        Capabilities = new AgentCapabilities { Streaming = false },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "report", Name = "Report", Description = "Builds a report.", Tags = ["reports"] }],
    };

    private static async Task<(WebApplication App, int Port)> ServeAsync(Func<int, AgentCard> card)
    {
        int port = FreePort();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        WebApplication app = builder.Build();
        var server = new A2AServer(new ReportAgent(), new InMemoryTaskStore(), new ChannelEventNotifier(), NullLogger<A2AServer>.Instance, new A2AServerOptions());
        app.MapA2A(server, "/a2a");
        app.MapWellKnownAgentCard(card(port));
        await app.StartAsync();
        return (app, port);
    }

    private static async Task<(A2aRig Rig, RunKey Run, RunState State)> CallAsync(int port, AgentCardVerifier? verifier)
    {
        var http = new HttpClient();
        var rig = new A2aRig(b => b.AddAgent("reports", new Uri($"http://127.0.0.1:{port}/"), http, verifier).ReadOnlyAgents("reports"));
        rig.Workflow("call", async (ctx, _) =>
        {
            A2aAgentResult r = await ctx.CallA2aAgentAsync("reports", "Ada", new A2aCallOptions { MinimumPollInterval = TimeSpan.FromMilliseconds(100) });
            return Encoding.UTF8.GetBytes(r.Text ?? "");
        });
        RunKey run = await rig.StartAsync("call");
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        RunState state;
        do
        {
            state = await rig.RunAsync(run, jumpClock: false);
            if (state == RunState.Running)
            {
                // The agent works in real time, so let real time pass and move the virtual clock only a little per round.
                await Task.Delay(100);
                rig.Time.Advance(TimeSpan.FromSeconds(3));
            }
        }
        while (state == RunState.Running && DateTime.UtcNow < deadline);

        return (rig, run, state);
    }

    [Fact]
    public async Task A_signed_card_is_verified_and_the_agent_is_called_through_a_polled_task()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new AgentCardSigner(key, "reports-2026");
        var verifier = new AgentCardVerifier(new Dictionary<string, byte[]> { ["reports-2026"] = signer.ExportPublicKey() });
        (WebApplication app, int port) = await ServeAsync(p => signer.Sign(Card(p)));
        await using (app)
        {
            int before = ReportAgent.Executions;
            (A2aRig rig, RunKey run, RunState state) = await CallAsync(port, verifier);

            Assert.True(state == RunState.Completed, await Why(rig, run));
            Assert.Equal("report for Ada", await rig.ResultAsync(run));
            Assert.Equal(1, ReportAgent.Executions - before);
        }
    }

    [Fact]
    public async Task An_unsigned_card_is_refused_when_signatures_are_required_and_nothing_is_sent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new AgentCardVerifier(new Dictionary<string, byte[]> { ["reports-2026"] = new AgentCardSigner(key, "reports-2026").ExportPublicKey() });
        (WebApplication app, int port) = await ServeAsync(Card);
        await using (app)
        {
            int before = ReportAgent.Executions;
            (A2aRig rig, RunKey run, RunState state) = await CallAsync(port, verifier);

            Assert.Equal(RunState.Failed, state);
            Assert.Equal(nameof(UntrustedAgentCardException), (await rig.HistoryAsync(run)).OfType<ActivityFailed>().First().ErrorType);
            Assert.Equal(before, ReportAgent.Executions);
        }
    }

    [Fact]
    public async Task A_card_signed_by_someone_else_is_refused()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new AgentCardVerifier(new Dictionary<string, byte[]> { ["reports-2026"] = new AgentCardSigner(publisher, "reports-2026").ExportPublicKey() });
        var forger = new AgentCardSigner(impostor, "reports-2026");
        (WebApplication app, int port) = await ServeAsync(p => forger.Sign(Card(p)));
        await using (app)
        {
            (_, _, RunState state) = await CallAsync(port, verifier);

            Assert.Equal(RunState.Failed, state);
        }
    }

    [Fact]
    public async Task Without_a_verifier_an_unsigned_card_is_accepted()
    {
        (WebApplication app, int port) = await ServeAsync(Card);
        await using (app)
        {
            (A2aRig rig, RunKey run, RunState state) = await CallAsync(port, verifier: null);

            Assert.True(state == RunState.Completed, await Why(rig, run));
            Assert.Equal("report for Ada", await rig.ResultAsync(run));
        }
    }
}
