using Kairos.Mcp;
using Kairos.Testing;

namespace Kairos.Mcp.TaskStore.Tests;

/// <summary>The Kairos pieces in one process: in-memory store, a worker that runs the task workflow, a virtual clock.</summary>
internal sealed class TaskRig
{
    public static readonly TenantScope ScopeA = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    public static readonly TenantScope ScopeB = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b2"));

    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();

    public TaskRig(McpTaskStoreOptions? options = null, TenantScope? scope = null, InMemoryKairosStore? shared = null, VirtualTimeProvider? time = null, IPayloadProtector? protector = null)
    {
        Time = time ?? new VirtualTimeProvider();
        Kairos = shared ?? new InMemoryKairosStore();
        Scope = scope ?? ScopeA;
        Protector = protector;
        Client = new KairosClient(Kairos, Time, payloadProtector: protector);
        _workflows.Register(McpTaskWorkflow.WorkflowType, KairosClient.DefaultBuildId, McpTaskWorkflow.Run);
        Worker = new WorkerRuntime(Kairos, Scope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false }, protector);
        Store = new KairosMcpTaskStore(Client, Scope, options);
    }

    public VirtualTimeProvider Time { get; }

    public InMemoryKairosStore Kairos { get; }

    public TenantScope Scope { get; }

    public IPayloadProtector? Protector { get; }

    public KairosClient Client { get; }

    public WorkerRuntime Worker { get; }

    public KairosMcpTaskStore Store { get; }

    public ActivityRegistry Activities => _activities;

    public void Workflow(string type, WorkflowFunc body) => _workflows.Register(type, KairosClient.DefaultBuildId, body);

    public void Activity(string type, ActivityFunc body) => _activities.Register(type, body);

    public Task SettleAsync() => Driver.DriveAsync(Worker);

    /// <summary>Drives until the run closes or only outside input can move it; the clock jumps to the next wakeup.</summary>
    public async Task<RunState> RunAsync(RunKey run)
    {
        while (true)
        {
            await Driver.DriveAsync(Worker);
            RunState state = (await Client.GetStateAsync(run))!.Value;
            if (state != RunState.Running || Kairos.NextWakeup(Scope, Time.GetUtcNow()) is not { } wake)
            {
                return state;
            }

            Time.AdvanceTo(wake);
        }
    }

    public async Task<RunKey> StartAsync(string type, string id, byte[]? input = null)
    {
        StartRunResult s = await Client.StartAsync(Scope, id, type, input ?? []);
        return new RunKey(Scope.TenantId, Scope.NamespaceId, s.RunId);
    }
}
