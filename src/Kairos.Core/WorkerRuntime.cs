namespace Kairos;

public sealed class WorkerOptions
{
    public TimeSpan WorkflowTaskLease { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ActivityTaskLease { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Back-off before a task that failed replay (non-determinism, integrity) is offered again.</summary>
    public TimeSpan FailedTaskRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public bool VerifyHashChain { get; init; } = true;
}

/// <summary>A workflow task that could not be processed (e.g. non-determinism). The run is not advanced and not lost.</summary>
public sealed record TaskFault(RunKey Run, Exception Error);

/// <summary>
/// The worker side of the engine, one step at a time. Each method claims at most one task, does the work, and reports back;
/// hosts call them in a loop, simulators call them in a seeded order. All time comes from the injected
/// <see cref="TimeProvider"/>; all persistence goes through <see cref="IKairosStore"/>. Stateless between calls
/// (full replay per task; sticky caching is a later optimisation).
/// </summary>
public sealed class WorkerRuntime
{
    private readonly IKairosStore _store;
    private readonly TenantScope _scope;
    private readonly WorkflowRegistry _workflows;
    private readonly ActivityRegistry _activities;
    private readonly TimeProvider _time;
    private readonly WorkerOptions _options;

    public WorkerRuntime(
        IKairosStore store, TenantScope scope, WorkflowRegistry workflows, ActivityRegistry activities,
        TimeProvider time, WorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _scope = scope;
        _workflows = workflows;
        _activities = activities;
        _time = time;
        _options = options ?? new WorkerOptions();
    }

    /// <summary>Called for every task that failed replay. Hook for logging/metrics; must not throw.</summary>
    public Action<TaskFault>? OnFault { get; set; }

    /// <returns>True when a task was claimed (whatever its outcome).</returns>
    public async ValueTask<bool> ProcessWorkflowTaskAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        WorkflowTask? task = await _store.PollWorkflowTaskAsync(_scope, _workflows.BuildIds, now, _options.WorkflowTaskLease, cancellationToken)
            .ConfigureAwait(false);
        if (task is null)
        {
            return false;
        }

        if (!_workflows.TryGet(task.WorkflowType, task.BuildId, out WorkflowFunc? workflow))
        {
            Fault(task, new InvalidOperationException($"Workflow '{task.WorkflowType}' build '{task.BuildId}' is not registered."));
            await _store.ReleaseWorkflowTaskAsync(task, now + _options.FailedTaskRetryDelay, cancellationToken).ConfigureAwait(false);
            return true;
        }

        ReplayResult result;
        try
        {
            result = WorkflowReplayer.Replay(workflow, task.History, _options.VerifyHashChain);
        }
        catch (Exception ex) when (ex is NonDeterminismException or HistoryIntegrityException)
        {
            Fault(task, ex);
            await _store.ReleaseWorkflowTaskAsync(task, now + _options.FailedTaskRetryDelay, cancellationToken).ConfigureAwait(false);
            return true;
        }

        DateTimeOffset stamp = _time.GetUtcNow();
        var events = new List<HistoryEvent>(result.NewCommands.Count);
        for (int i = 0; i < result.NewCommands.Count; i++)
        {
            events.Add(ToEvent(task.Run, result.NewCommands[i], stamp));
        }

        await _store.CompleteWorkflowTaskAsync(task, events, stamp, cancellationToken).ConfigureAwait(false);
        return true;
    }

#pragma warning disable CA1031 // Any activity exception is a recorded attempt failure.
    /// <returns>True when an activity attempt was claimed (whatever its outcome).</returns>
    public async ValueTask<bool> ProcessActivityTaskAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        ActivityTask? task = await _store.PollActivityTaskAsync(_scope, now, _options.ActivityTaskLease, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return false;
        }

        RunKey r = task.Run;
        HistoryEvent outcome;
        DateTimeOffset? retryAt = null;
        try
        {
            if (!_activities.TryGet(task.ActivityType, out ActivityFunc? activity))
            {
                throw new ActivityNotRegisteredException(task.ActivityType);
            }

            var context = new ActivityContext(r, task.ActivityId, task.ActivityType, task.Attempt, task.IdempotencyKey);
            byte[] output = await activity(context, task.Input, cancellationToken).ConfigureAwait(false);
            outcome = new ActivityCompleted(r.TenantId, r.NamespaceId, r.RunId, 0, _time.GetUtcNow(), task.ActivityId, output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutting down: record nothing, the lease expires and the attempt is re-run
        }
        catch (Exception ex)
        {
            string errorType = ex.GetType().Name;
            bool retry = ex is not ActivityNotRegisteredException && task.RetryPolicy.ShouldRetry(task.Attempt, errorType);
            DateTimeOffset stamp = _time.GetUtcNow();
            outcome = new ActivityFailed(r.TenantId, r.NamespaceId, r.RunId, 0, stamp, task.ActivityId, task.Attempt, errorType, ex.Message, retry);
            retryAt = retry ? stamp + task.RetryPolicy.GetBackoff(task.Attempt) : null;
        }

        await _store.CompleteActivityTaskAsync(task, outcome, retryAt, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return true;
    }
#pragma warning restore CA1031

    public ValueTask<int> FireTimersAsync(CancellationToken cancellationToken) =>
        _store.FireDueTimersAsync(_scope, _time.GetUtcNow(), cancellationToken);

    private void Fault(WorkflowTask task, Exception error) => OnFault?.Invoke(new TaskFault(task.Run, error));

    private static HistoryEvent ToEvent(RunKey r, WorkflowCommand command, DateTimeOffset ts) => command switch
    {
        ScheduleActivityCommand c => new ActivityScheduled(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.ActivityId, c.ActivityType, c.Input, c.RetryPolicy),
        StartTimerCommand c => new TimerStarted(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.TimerId, c.Duration),
        RecordPatchCommand c => new PatchMarker(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.PatchId),
        CompleteWorkflowCommand c => new WorkflowCompleted(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.Result),
        FailWorkflowCommand c => new WorkflowFailed(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.ErrorType, c.Message),
        _ => throw new InvalidOperationException($"Unknown command {command.GetType().Name}."),
    };

    private sealed class ActivityNotRegisteredException(string activityType)
        : Exception($"Activity type '{activityType}' is not registered on this worker.");
}
