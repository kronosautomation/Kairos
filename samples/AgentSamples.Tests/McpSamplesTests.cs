using System.Text;
using System.Text.Json;
using Kairos.Agents;
using Kairos.Agents.McpClient;
using Kairos.Mcp;
using Kairos.Testing;
using ModelContextProtocol.Extensions.Tasks;

namespace Kairos.Samples.Agents.Tests;

public sealed class McpSamplesTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private sealed class Endpoint : IMcpToolEndpoint
    {
        public List<string> Called { get; } = [];

        public int WorkingPolls { get; init; }

        private int _polls;

        public ValueTask<McpCallOutcome> CallAsync(string tool, string argumentsJson, string idempotencyKey, bool useTasks, CancellationToken cancellationToken)
        {
            Called.Add(tool);
            return ValueTask.FromResult(tool == "build_report" && useTasks
                ? new McpCallOutcome(null, "remote-7", 1000)
                : new McpCallOutcome(new McpToolResult(false, tool == "lookup_customer" ? "Ada Lovelace" : "ok"), null, 0));
        }

        public ValueTask<McpTaskPoll> PollAsync(string taskId, CancellationToken cancellationToken) => ValueTask.FromResult(
            ++_polls <= WorkingPolls
                ? new McpTaskPoll(McpRemoteStatus.Working, null, null, 0)
                : new McpTaskPoll(McpRemoteStatus.Completed, new McpToolResult(false, "report for 42"), null, 0));

        public ValueTask CancelAsync(string taskId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class Rig
    {
        private readonly WorkflowRegistry _workflows = new();
        private readonly ActivityRegistry _activities = new();

        public Rig(IMcpToolEndpoint? endpoint = null, string server = "crm", string[]? readOnly = null)
        {
            Time = new VirtualTimeProvider();
            Store = new InMemoryKairosStore();
            Client = new KairosClient(Store, Time);
            Agents = new AgentsBuilder().UseTimeProvider(Time).Build();
            Agents.RegisterInto(_activities);
            if (endpoint is not null)
            {
                new McpClientsBuilder().AddServer(server, endpoint).ReadOnlyTools(server, readOnly ?? []).Build().RegisterInto(_activities);
            }

            McpTaskExamples.RegisterTaskWorkflow(_workflows);
            Worker = new WorkerRuntime(Store, Scope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false });
        }

        public VirtualTimeProvider Time { get; }

        public InMemoryKairosStore Store { get; }

        public KairosClient Client { get; }

        public AgentsRuntime Agents { get; }

        public WorkerRuntime Worker { get; }

        public void Workflow(string type, Func<IAgentContext, byte[], Task<byte[]>> body) => _workflows.Register(type, KairosClient.DefaultBuildId, Agents.Workflow(body));

        public void Plain(string type, WorkflowFunc body) => _workflows.Register(type, KairosClient.DefaultBuildId, body);

        public async Task<RunKey> StartAsync(string type, string input)
        {
            StartRunResult s = await Client.StartAsync(Scope, "wf-" + type, type, Encoding.UTF8.GetBytes(input));
            return new RunKey(Scope.TenantId, Scope.NamespaceId, s.RunId);
        }

        public async Task<RunState> RunAsync(RunKey run, bool jumpClock = true)
        {
            while (true)
            {
                await Driver.DriveAsync(Worker);
                RunState state = (await Client.GetStateAsync(run))!.Value;
                if (state != RunState.Running || !jumpClock || Store.NextWakeup(Scope, Time.GetUtcNow()) is not { } wake)
                {
                    return state;
                }

                Time.AdvanceTo(wake);
            }
        }
    }

    [Fact]
    public async Task The_task_store_keeps_a_servers_tasks_as_runs()
    {
        var rig = new Rig();
        KairosMcpTaskStore store = McpTaskExamples.CreateTaskStore(rig.Store, Scope);

        McpTaskInfo task = await store.CreateTaskAsync(default);
        using JsonDocument result = JsonDocument.Parse("""{"content":[{"type":"text","text":"done"}]}""");
        await store.SetCompletedAsync(task.TaskId, result.RootElement, default);

        Assert.Equal(McpTaskStatus.Completed, (await store.GetTaskAsync(task.TaskId, default))!.Status);
    }

    [Fact]
    public async Task A_tool_that_is_a_workflow_is_reported_as_its_run()
    {
        var rig = new Rig();
        rig.Plain("customer-report", (ctx, input) => Task.FromResult(Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":"report"}]}""")));
        KairosMcpTaskStore store = McpTaskExamples.CreateTaskStore(rig.Store, Scope);

        string taskId = await McpTaskExamples.StartReportToolAsync(rig.Client, store, Scope, "42");
        await Driver.DriveAsync(rig.Worker);

        McpTaskInfo task = (await store.GetTaskAsync(taskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, task.Status);
        Assert.Equal("report", task.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_research_agent_reads_freely_and_waits_for_approval_before_writing()
    {
        var crm = new Endpoint();
        var rig = new Rig(crm, readOnly: ["lookup_customer", "list_orders"]);
        rig.Workflow("research", McpClientExamples.ResearchAsync);
        RunKey run = await rig.StartAsync("research", "ada@example.com");

        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(["lookup_customer"], crm.Called);

        PendingApproval pending = AgentHistory.PendingApprovals(await rig.Client.GetOpenedHistoryAsync(run)).Single();
        Assert.Equal("crm.add_note", pending.Tool);
        await new AgentClient(rig.Client, rig.Time).ApproveAsync(run, pending.ApprovalId, new ApproverIdentity("user:dana", "Dana"));

        Assert.Equal(RunState.Completed, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(["lookup_customer", "add_note"], crm.Called);
    }

    [Fact]
    public async Task A_long_report_is_polled_across_durable_timers()
    {
        var reports = new Endpoint { WorkingPolls = 3 };
        var rig = new Rig(reports, server: "reports", readOnly: ["build_report"]);
        rig.Workflow("report", async (ctx, input) => Encoding.UTF8.GetBytes(await McpClientExamples.BuildReportAsync(ctx, "42")));
        RunKey run = await rig.StartAsync("report", "");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));
        Assert.Equal(["build_report"], reports.Called);
    }
}
