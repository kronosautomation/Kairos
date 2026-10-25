using System.Text.Json;
using Kairos.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kairos.Persistence.Tests;

/// <summary>MCP tasks on the real store: a task outlives the process that created it, and isolation between namespaces holds in the database.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class McpTasksOnPostgresTests(PostgresFixture fixture)
{
    private static IHost Host(string connectionString)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.UsePostgres(connectionString).AddWorkflow(McpTaskWorkflow.WorkflowType, McpTaskWorkflow.Run);
        });
        builder.Services.AddSingleton(sp => new KairosMcpTaskStore(sp.GetRequiredService<KairosClient>(), KairosOptions.DefaultScope));
        return builder.Build();
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A_task_outlives_the_host_that_created_it()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        string taskId;
        using (IHost first = Host(db.DataPlaneConnectionString))
        {
            await first.StartAsync();
            var store = first.Services.GetRequiredService<KairosMcpTaskStore>();
            taskId = (await store.CreateTaskAsync(default)).TaskId;
            await store.SetInputRequestsAsync(taskId, new Dictionary<string, InputRequest>
            {
                ["approve"] = InputRequest.ForElicitation(new ElicitRequestParams { Message = "Release the payment?" }),
            }, default);
            await first.StopAsync();
        }

        using IHost second = Host(db.DataPlaneConnectionString);
        await second.StartAsync();
        var restarted = second.Services.GetRequiredService<KairosMcpTaskStore>();
        McpTaskInfo waiting = (await restarted.GetTaskAsync(taskId, default))!;
        Assert.Equal(McpTaskStatus.InputRequired, waiting.Status);

        await restarted.ResolveInputRequestsAsync(taskId, new Dictionary<string, InputResponse>
        {
            ["approve"] = InputResponse.FromElicitResult(new ElicitResult { Action = "accept" }),
        }, default);
        await restarted.SetCompletedAsync(taskId, Json("""{"content":[{"type":"text","text":"released"}]}"""), default);

        McpTaskInfo done = (await restarted.GetTaskAsync(taskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, done.Status);
        Assert.Equal("released", done.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
        await second.StopAsync();
    }

    [Fact]
    public async Task A_task_id_from_another_namespace_finds_nothing()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = Host(db.DataPlaneConnectionString);
        await host.StartAsync();
        var mine = host.Services.GetRequiredService<KairosMcpTaskStore>();
        string taskId = (await mine.CreateTaskAsync(default)).TaskId;

        var other = new KairosMcpTaskStore(host.Services.GetRequiredService<KairosClient>(), new TenantScope(KairosOptions.DefaultScope.TenantId, Guid.NewGuid()));

        Assert.Null(await other.GetTaskAsync(taskId, default));
        Assert.False(await other.SetCancelledAsync(taskId, default));
        Assert.Equal(McpTaskStatus.Working, (await mine.GetTaskAsync(taskId, default))!.Status);
        await host.StopAsync();
    }
}
