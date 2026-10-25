using System.IO.Pipelines;
using System.Text;
using Kairos.Agents.McpClient;
using Kairos.Mcp;
using Kairos.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kairos.Agents.McpClient.Tests;

/// <summary>The durable client steps against the real SDK server and client, joined by in-memory pipes. The server keeps its tasks in Kairos.</summary>
public sealed class SdkEndpointTests
{
    [McpServerToolType]
    public sealed class Tools
    {
        private static int _executions;

        public static int Executions => Volatile.Read(ref _executions);

        [McpServerTool(Name = "slow_report"), System.ComponentModel.Description("Builds a report")]
        public static async Task<string> SlowReport(string customer, CancellationToken ct)
        {
            Interlocked.Increment(ref _executions);
            await Task.Delay(400, ct);
            return $"report for {customer}";
        }
    }

    private static async Task<(IHost Host, ModelContextProtocol.Client.McpClient Client)> ConnectAsync()
    {
        var serverStore = new KairosMcpTaskStore(
            new KairosClient(new InMemoryKairosStore(), TimeProvider.System), new TenantScope(Guid.NewGuid(), Guid.NewGuid()));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<Tools>()
            .WithTasks(serverStore);
        IHost host = builder.Build();
        await host.StartAsync();
        ModelContextProtocol.Client.McpClient client = await ModelContextProtocol.Client.McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        return (host, client);
    }

    private static async Task<(McpRig Rig, RunKey Run)> RunAgentAsync(ModelContextProtocol.Client.McpClient client, McpCallOptions options)
    {
        var rig = new McpRig(new FakeServer(), b => b.AddServer("reports", client).ReadOnlyTools("reports", "slow_report"));
        rig.Workflow("call", async (ctx, _) =>
        {
            McpToolResult r = await ctx.CallMcpToolAsync("reports", "slow_report", new Dictionary<string, object?> { ["customer"] = "Ada" }, options);
            return Encoding.UTF8.GetBytes(r.Text);
        });
        RunKey run = await rig.StartAsync("call");
        // Real time passes on the server, so wait for it between drives instead of jumping the virtual clock past it.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        RunState state;
        do
        {
            state = await rig.RunAsync(run, jumpClock: true);
            if (state == RunState.Running)
            {
                await Task.Delay(100);
            }
        }
        while (state == RunState.Running && DateTime.UtcNow < deadline);

        return (rig, run);
    }

    [Fact]
    public async Task A_long_remote_call_runs_as_a_remote_task_and_the_result_is_recorded_once()
    {
        (IHost host, ModelContextProtocol.Client.McpClient client) = await ConnectAsync();
        await using (client)
        {
            int before = Tools.Executions;
            (McpRig rig, RunKey run) = await RunAgentAsync(client, new McpCallOptions { UseTasks = true, MinimumPollInterval = TimeSpan.FromMilliseconds(100) });

            Assert.Equal(RunState.Completed, (await rig.Client.GetStateAsync(run))!.Value);
            Assert.Equal("report for Ada", await rig.ResultAsync(run));
            Assert.Equal(1, Tools.Executions - before); // the server ran the tool once
            Assert.NotEmpty((await rig.HistoryAsync(run)).OfType<TimerStarted>()); // and the wait was a durable timer
        }

        await host.StopAsync();
        host.Dispose();
    }

    [Fact]
    public async Task Without_tasks_the_same_call_is_answered_directly()
    {
        (IHost host, ModelContextProtocol.Client.McpClient client) = await ConnectAsync();
        await using (client)
        {
            (McpRig rig, RunKey run) = await RunAgentAsync(client, new McpCallOptions { UseTasks = false });

            Assert.Equal("report for Ada", await rig.ResultAsync(run));
            Assert.Empty((await rig.HistoryAsync(run)).OfType<TimerStarted>());
        }

        await host.StopAsync();
        host.Dispose();
    }
}
