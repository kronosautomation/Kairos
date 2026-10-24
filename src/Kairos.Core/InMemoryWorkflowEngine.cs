namespace Kairos;

public enum WorkflowStatus
{
    /// <summary>Blocked on a signal or otherwise not finished.</summary>
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Single-process engine with a virtual clock: hosts workflow runs, executes activities with retries, fires timers, and
/// records every step in a hash-chained history. No I/O. Each task fully replays the run from history, which is what
/// makes <see cref="Load"/> (crash recovery) behave identically to normal progress. Not thread-safe.
/// </summary>
public sealed class InMemoryWorkflowEngine
{
    private readonly Dictionary<string, WorkflowFunc> _workflows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<byte[], Task<byte[]>>> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<HistoryEvent>> _runs = [];

    public InMemoryWorkflowEngine(DateTimeOffset? start = null) =>
        Now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Virtual time. Advances when timers fire and activities back off.</summary>
    public DateTimeOffset Now { get; private set; }

    public void RegisterWorkflow(string workflowType, WorkflowFunc workflow)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowType);
        ArgumentNullException.ThrowIfNull(workflow);
        _workflows[workflowType] = workflow;
    }

    public void RegisterActivity(string activityType, Func<byte[], Task<byte[]>> activity)
    {
        ArgumentException.ThrowIfNullOrEmpty(activityType);
        ArgumentNullException.ThrowIfNull(activity);
        _activities[activityType] = activity;
    }

    public Guid StartWorkflow(string workflowType, byte[] input, Guid tenantId, Guid namespaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowType);
        ArgumentNullException.ThrowIfNull(input);
        Guid runId = Guid.NewGuid();
        var history = new List<HistoryEvent>();
        _runs.Add(runId, history);
        Append(history, new WorkflowStarted(tenantId, namespaceId, runId, 1, Now, workflowType, input));
        return runId;
    }

    /// <summary>Delivers a signal. Call only between <see cref="RunAsync"/> calls, when the run is quiescent.</summary>
    public void Signal(Guid runId, string signalName, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalName);
        ArgumentNullException.ThrowIfNull(payload);
        List<HistoryEvent> history = GetRun(runId);
        if (IsClosed(history))
        {
            throw new InvalidOperationException("Run is closed.");
        }

        HistoryEvent head = history[0];
        Append(history, new SignalReceived(head.TenantId, head.NamespaceId, runId, history.Count + 1, Now, signalName, payload));
    }

    public IReadOnlyList<HistoryEvent> GetHistory(Guid runId) => GetRun(runId);

    public WorkflowStatus GetStatus(Guid runId) => GetRun(runId)[^1] switch
    {
        WorkflowCompleted => WorkflowStatus.Completed,
        WorkflowFailed => WorkflowStatus.Failed,
        _ => WorkflowStatus.Running,
    };

    /// <summary>Adopts a persisted history (simulating a restart): verifies the hash chain, then makes the run resumable.</summary>
    public Guid Load(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            throw new ArgumentException("History is empty.", nameof(history));
        }

        HashChain.Verify(history);
        Guid runId = history[0].RunId;
        _runs[runId] = [.. history];
        DateTimeOffset last = history[^1].Timestamp;
        if (last > Now)
        {
            Now = last;
        }

        return runId;
    }

    /// <summary>Drives the run until it closes or can make no further progress without a signal.</summary>
    public async Task<WorkflowStatus> RunAsync(Guid runId)
    {
        List<HistoryEvent> history = GetRun(runId);
        var started = (WorkflowStarted)history[0];
        if (!_workflows.TryGetValue(started.WorkflowType, out WorkflowFunc? workflow))
        {
            throw new InvalidOperationException($"Workflow type '{started.WorkflowType}' is not registered.");
        }

        while (!IsClosed(history))
        {
            ReplayResult result = WorkflowReplayer.Replay(workflow, history, verifyHashChain: false);

            if (result.NewCommands.Count > 0)
            {
                var activities = new List<ScheduleActivityCommand>();
                foreach (WorkflowCommand command in result.NewCommands)
                {
                    ApplyCommand(history, command, activities);
                }

                if (!IsClosed(history))
                {
                    foreach (ScheduleActivityCommand activity in activities)
                    {
                        await ExecuteActivityAsync(history, activity).ConfigureAwait(false);
                    }
                }

                continue;
            }

            if (!TryFireNextTimer(history))
            {
                break;
            }
        }

        return GetStatus(runId);
    }

    private static bool IsClosed(List<HistoryEvent> history) => history[^1] is WorkflowCompleted or WorkflowFailed;

    private List<HistoryEvent> GetRun(Guid runId) =>
        _runs.TryGetValue(runId, out List<HistoryEvent>? history)
            ? history
            : throw new KeyNotFoundException($"Unknown run {runId}.");

    private void ApplyCommand(List<HistoryEvent> history, WorkflowCommand command, List<ScheduleActivityCommand> activities)
    {
        HistoryEvent head = history[0];
        Guid tenant = head.TenantId, ns = head.NamespaceId, run = head.RunId;
        long seq = history.Count + 1;

        switch (command)
        {
            case ScheduleActivityCommand s:
                Append(history, new ActivityScheduled(tenant, ns, run, seq, Now, s.ActivityId, s.ActivityType, s.Input, s.RetryPolicy));
                activities.Add(s);
                break;
            case StartTimerCommand t:
                Append(history, new TimerStarted(tenant, ns, run, seq, Now, t.TimerId, t.Duration));
                break;
            case CompleteWorkflowCommand c:
                Append(history, new WorkflowCompleted(tenant, ns, run, seq, Now, c.Result));
                break;
            case FailWorkflowCommand f:
                Append(history, new WorkflowFailed(tenant, ns, run, seq, Now, f.ErrorType, f.Message));
                break;
            default:
                throw new InvalidOperationException($"Unknown command {command.GetType().Name}.");
        }
    }

#pragma warning disable CA1031 // Any exception from an activity is a recorded failure, not an engine fault.
    private async Task ExecuteActivityAsync(List<HistoryEvent> history, ScheduleActivityCommand command)
    {
        HistoryEvent head = history[0];
        Guid tenant = head.TenantId, ns = head.NamespaceId, run = head.RunId;

        for (int attempt = 1; ; attempt++)
        {
            string errorType;
            string message;
            try
            {
                if (!_activities.TryGetValue(command.ActivityType, out Func<byte[], Task<byte[]>>? activity))
                {
                    throw new InvalidOperationException($"Activity type '{command.ActivityType}' is not registered.");
                }

                byte[] result = await activity(command.Input).ConfigureAwait(false);
                Append(history, new ActivityCompleted(tenant, ns, run, history.Count + 1, Now, command.ActivityId, result));
                return;
            }
            catch (Exception ex)
            {
                errorType = ex.GetType().Name;
                message = ex.Message;
            }

            bool retry = command.RetryPolicy.ShouldRetry(attempt, errorType);
            Append(history, new ActivityFailed(tenant, ns, run, history.Count + 1, Now, command.ActivityId, attempt, errorType, message, retry));
            if (!retry)
            {
                return;
            }

            Now += command.RetryPolicy.GetBackoff(attempt);
        }
    }
#pragma warning restore CA1031

    private bool TryFireNextTimer(List<HistoryEvent> history)
    {
        var open = new Dictionary<int, DateTimeOffset>();
        for (int i = 0; i < history.Count; i++)
        {
            switch (history[i])
            {
                case TimerStarted t:
                    open[t.TimerId] = t.Timestamp + t.Duration;
                    break;
                case TimerFired f:
                    open.Remove(f.TimerId);
                    break;
            }
        }

        if (open.Count == 0)
        {
            return false;
        }

        int nextId = 0;
        DateTimeOffset nextDue = default;
        foreach ((int id, DateTimeOffset due) in open)
        {
            if (nextId == 0 || due < nextDue || (due == nextDue && id < nextId))
            {
                nextId = id;
                nextDue = due;
            }
        }

        if (nextDue > Now)
        {
            Now = nextDue;
        }

        HistoryEvent head = history[0];
        Append(history, new TimerFired(head.TenantId, head.NamespaceId, head.RunId, history.Count + 1, Now, nextId));
        return true;
    }

    private static void Append(List<HistoryEvent> history, HistoryEvent e)
    {
        ReadOnlySpan<byte> prev = history.Count == 0 ? HashChain.GenesisHash : history[^1].Hash;
        history.Add(HashChain.Seal(e, prev));
    }
}
