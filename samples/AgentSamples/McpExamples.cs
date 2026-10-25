using System.Text;
using Kairos.Agents;
using Kairos.Agents.McpClient;
using Kairos.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;

namespace Kairos.Samples.Agents;

public static class McpTaskExamples
{
    #region task-store
    // The store keeps every task of this MCP server as a Kairos run, so tasks survive a restart of the server.
    public static KairosMcpTaskStore CreateTaskStore(IKairosStore database, TenantScope scope) => new(
        new KairosClient(database, TimeProvider.System),
        scope,
        new McpTaskStoreOptions
        {
            // The SDK runs a task's work inside the server process. If that process dies, the work is gone while the task still says "working".
            // A task that has been silent this long is recorded as failed, so clients are not left polling forever.
            AbandonedAfter = TimeSpan.FromMinutes(15),
        });

    public static IMcpServerBuilder AddKairosTasks(this IMcpServerBuilder server, KairosMcpTaskStore store) => server.WithTasks(store);
    #endregion

    #region task-workflow
    // Workers close the run behind each task. Register the task workflow wherever your Kairos workers run.
    public static void RegisterTaskWorkflow(WorkflowRegistry workflows) =>
        workflows.Register(McpTaskWorkflow.WorkflowType, KairosClient.DefaultBuildId, McpTaskWorkflow.Run);
    #endregion

    #region task-attach
    // A tool that is itself a Kairos workflow: report its run as the task. Status, result and cancellation come from the run.
    public static async Task<string> StartReportToolAsync(KairosClient client, KairosMcpTaskStore store, TenantScope scope, string customerId)
    {
        StartRunResult started = await client.StartAsync(scope, $"report-{customerId}", "customer-report", Encoding.UTF8.GetBytes(customerId));
        McpTaskInfo task = await store.AttachRunAsync(new RunKey(scope.TenantId, scope.NamespaceId, started.RunId), CancellationToken.None);
        return task.TaskId;
    }
    #endregion
}

public static class McpClientExamples
{
    #region mcp-client-setup
    // Name the servers an agent may call. Only the tools listed as read-only run without a person's approval.
    public static McpClientRuntime ConfigureMcp(ModelContextProtocol.Client.McpClient crm) => new McpClientsBuilder()
        .AddServer("crm", crm)
        .ReadOnlyTools("crm", "lookup_customer", "list_orders")
        .Build();
    #endregion

    #region mcp-client-call
    public static async Task<byte[]> ResearchAsync(IAgentContext ctx, byte[] input)
    {
        // Recorded once. If the process dies and the run replays, the server is not called again.
        McpToolResult customer = await ctx.CallMcpToolAsync(
            "crm", "lookup_customer", new Dictionary<string, object?> { ["email"] = Encoding.UTF8.GetString(input) });

        if (customer.IsError)
        {
            return Encoding.UTF8.GetBytes("No such customer.");
        }

        // A tool that is not declared read-only waits here for an approval, then runs with the approval attached.
        await ctx.CallMcpToolAsync("crm", "add_note", new Dictionary<string, object?> { ["text"] = "Contacted by the support agent." });
        return Encoding.UTF8.GetBytes(customer.Text);
    }
    #endregion

    #region mcp-client-long
    public static async Task<string> BuildReportAsync(IAgentContext ctx, string customerId)
    {
        // A server that runs the call as a task is polled with durable timers: the task id is in the history, so a restart resumes
        // polling instead of starting the report again. Give up (and cancel it remotely) after two hours.
        McpToolResult report = await ctx.CallMcpToolAsync(
            "reports", "build_report", new Dictionary<string, object?> { ["customer"] = customerId },
            new McpCallOptions { Timeout = TimeSpan.FromHours(2), MaximumPollInterval = TimeSpan.FromSeconds(30) });
        return report.Text;
    }
    #endregion
}
