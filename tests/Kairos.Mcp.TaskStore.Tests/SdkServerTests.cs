using System.IO.Pipelines;
using Kairos.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kairos.Mcp.TaskStore.Tests;

/// <summary>The real SDK server, with the Kairos store plugged in, talking to the real SDK client over in-memory pipes.</summary>
public sealed class SdkServerTests
{
    [McpServerToolType]
    public sealed class Tools
    {
        [McpServerTool(Name = "slow_report"), System.ComponentModel.Description("Builds a report")]
        public static async Task<string> SlowReport(CancellationToken ct)
        {
            await Task.Delay(300, ct);
            return "report ready";
        }
    }

    [Fact]
    public async Task A_tool_call_made_as_a_task_is_tracked_in_a_kairos_run_and_survives_reading_it_from_another_store_instance()
    {
        var rig = new TaskRig();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<Tools>()
            .WithTasks(rig.Store);
        using IHost host = builder.Build();
        await host.StartAsync();

        await using McpClient client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        ResultOrCreatedTask<CallToolResult> outcome = await client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "slow_report" });

        Assert.True(outcome.IsTask);
        string taskId = outcome.TaskCreated!.TaskId;
        Assert.StartsWith("kmt1_", taskId, StringComparison.Ordinal);

        GetTaskResult state = await client.GetTaskAsync(taskId);
        for (int i = 0; i < 100 && state is not CompletedTaskResult; i++)
        {
            await Task.Delay(50);
            state = await client.GetTaskAsync(taskId);
        }

        Assert.IsType<CompletedTaskResult>(state);

        // A different store instance over the same database sees the same finished task.
        var other = new TaskRig(shared: rig.Kairos, time: rig.Time);
        Assert.Equal(McpTaskStatus.Completed, (await other.Store.GetTaskAsync(taskId, default))!.Status);
        await host.StopAsync();
    }
}
