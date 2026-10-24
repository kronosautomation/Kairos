namespace Kairos.Testing;

/// <summary>
/// Single-process convenience over the real pieces (in-memory store + worker runtime + virtual clock): register code, start runs,
/// drive them to quiescence, inspect histories. When nothing is runnable it jumps the clock to the next wakeup (timer due, retry
/// visible, lease expiry) instead of waiting.
/// </summary>
public sealed class TestEngine
{
    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();
    private readonly Dictionary<Guid, RunKey> _runs = [];
    private readonly Dictionary<TenantScope, WorkerRuntime> _workers = [];
    private readonly KairosClient _client;

    public TestEngine(DateTimeOffset? start = null)
    {
        Time = new VirtualTimeProvider(start);
        Store = new InMemoryKairosStore();
        _client = new KairosClient(Store, Time);
    }

    public VirtualTimeProvider Time { get; }

    public InMemoryKairosStore Store { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public void RegisterWorkflow(string workflowType, WorkflowFunc workflow) =>
        _workflows.Register(workflowType, KairosClient.DefaultBuildId, workflow);

    public void RegisterActivity(string activityType, Func<byte[], Task<byte[]>> activity) =>
        _activities.Register(activityType, (_, input, _) => activity(input));

    public Guid StartWorkflow(string workflowType, byte[] input, Guid tenantId, Guid namespaceId)
    {
        var scope = new TenantScope(tenantId, namespaceId);
        StartRunResult result = _client.StartAsync(scope, $"run-{_runs.Count}", workflowType, input).AsTask().GetAwaiter().GetResult();
        _runs[result.RunId] = new RunKey(tenantId, namespaceId, result.RunId);
        return result.RunId;
    }

    public void Signal(Guid runId, string signalName, byte[] payload)
    {
        if (!_client.SignalAsync(_runs[runId], signalName, payload).AsTask().GetAwaiter().GetResult())
        {
            throw new InvalidOperationException("Run is closed.");
        }
    }

    /// <summary>Adopts persisted history as if a new process had just started (only history survives).</summary>
    public Guid Load(IReadOnlyList<HistoryEvent> history)
    {
        RunKey key = Store.Import(history, Now);
        _runs[key.RunId] = key;
        return key.RunId;
    }

    public IReadOnlyList<HistoryEvent> GetHistory(Guid runId) =>
        _client.GetHistoryAsync(_runs[runId]).AsTask().GetAwaiter().GetResult();

    public RunState GetStatus(Guid runId) => _client.GetStateAsync(_runs[runId]).AsTask().GetAwaiter().GetResult()!.Value;

    /// <summary>Drives the run's scope until it closes or only an external signal can move it forward.</summary>
    public async Task<RunState> RunAsync(Guid runId)
    {
        TenantScope scope = _runs[runId].Scope;
        if (!_workers.TryGetValue(scope, out WorkerRuntime? worker))
        {
            worker = new WorkerRuntime(Store, scope, _workflows, _activities, Time);
            _workers.Add(scope, worker);
        }

        while (true)
        {
            await Driver.DriveAsync(worker);
            if (GetStatus(runId) != RunState.Running || Store.NextWakeup(scope, Now) is not { } wake)
            {
                return GetStatus(runId);
            }

            Time.AdvanceTo(wake);
        }
    }
}
