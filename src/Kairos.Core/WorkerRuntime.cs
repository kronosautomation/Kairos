namespace Kairos;

public sealed class WorkerOptions
{
    public TimeSpan WorkflowTaskLease { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ActivityTaskLease { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Back-off before a task that failed replay (non-determinism, integrity) is offered again.</summary>
    public TimeSpan FailedTaskRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public bool VerifyHashChain { get; init; } = true;

    /// <summary>
    /// Renew the activity lease every third of its length while the activity runs, so activities may take far longer than
    /// the lease. Needs a <see cref="TimeProvider"/> that supports timers; simulations turn it off and call
    /// <see cref="ActivityContext.HeartbeatAsync"/> explicitly.
    /// </summary>
    public bool AutoHeartbeat { get; init; } = true;
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
    private readonly PayloadCrypto? _crypto;

    public WorkerRuntime(
        IKairosStore store, TenantScope scope, WorkflowRegistry workflows, ActivityRegistry activities,
        TimeProvider time, WorkerOptions? options = null, IPayloadProtector? payloadProtector = null)
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
        _crypto = payloadProtector is null ? null : new PayloadCrypto(payloadProtector);
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

        try
        {
            await ProcessClaimedWorkflowTaskAsync(task, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down with a claimed task: hand it back now instead of making the next worker wait out the lease.
            await ReleaseQuietlyAsync(() => _store.ReleaseWorkflowTaskAsync(task, _time.GetUtcNow(), _time.GetUtcNow(), countAttempt: false, CancellationToken.None)).ConfigureAwait(false);
            throw;
        }

        return true;
    }

    private async Task ProcessClaimedWorkflowTaskAsync(WorkflowTask task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_workflows.TryGet(task.WorkflowType, task.BuildId, out WorkflowFunc? workflow))
        {
            Fault(task, new InvalidOperationException($"Workflow '{task.WorkflowType}' build '{task.BuildId}' is not registered."));
            await _store.ReleaseWorkflowTaskAsync(task, now + _options.FailedTaskRetryDelay, now, countAttempt: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        ReplayResult result;
        try
        {
            if (_crypto is null)
            {
                result = WorkflowReplayer.Replay(workflow, task.History, _options.VerifyHashChain);
            }
            else
            {
                // Verify the chain over the ciphertext the server holds, then replay the opened (plaintext) copy.
                HashChain.Verify(task.History);
                IReadOnlyList<HistoryEvent> opened = await _crypto.OpenHistoryAsync(task.History, cancellationToken).ConfigureAwait(false);
                result = WorkflowReplayer.Replay(workflow, opened, verifyHashChain: false);
            }
        }
        catch (Exception ex) when (ex is NonDeterminismException or HistoryIntegrityException or PayloadProtectionException)
        {
            Fault(task, ex);
            await _store.ReleaseWorkflowTaskAsync(task, now + _options.FailedTaskRetryDelay, now, countAttempt: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset stamp = _time.GetUtcNow();
        var events = new List<HistoryEvent>(result.NewCommands.Count);
        for (int i = 0; i < result.NewCommands.Count; i++)
        {
            HistoryEvent plain = ToEvent(task.Run, result.NewCommands[i], stamp);
            events.Add(_crypto is null ? plain : await _crypto.SealEventAsync(plain, cancellationToken).ConfigureAwait(false));
        }

        await _store.CompleteWorkflowTaskAsync(task, events, stamp, cancellationToken).ConfigureAwait(false);
    }

#pragma warning disable CA1031 // Any activity exception is a recorded attempt failure.
    /// <returns>True when an activity attempt was claimed (whatever its outcome).</returns>
    public async ValueTask<bool> ProcessActivityTaskAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan lease = _options.ActivityTaskLease;
        ActivityTask? task = await _store.PollActivityTaskAsync(_scope, now, lease, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return false;
        }

        RunKey r = task.Run;
        HistoryEvent outcome;
        DateTimeOffset? retryAt = null;

        // Cancelled on shutdown or when a heartbeat learns the lease was lost; the activity sees this token.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool leaseLost = false;

        async ValueTask Beat()
        {
            CompletionOutcome o = await _store.HeartbeatActivityTaskAsync(task, _time.GetUtcNow(), lease, CancellationToken.None).ConfigureAwait(false);
            if (o == CompletionOutcome.StaleLease)
            {
                leaseLost = true;
                await attemptCts.CancelAsync().ConfigureAwait(false);
            }
        }

        Task? autoBeat = _options.AutoHeartbeat ? AutoHeartbeatAsync(Beat, lease / 3, attemptCts.Token) : null;
        try
        {
            if (!_activities.TryGet(task.ActivityType, out ActivityFunc? activity))
            {
                throw new ActivityNotRegisteredException(task.ActivityType);
            }

            var context = new ActivityContext(r, task.ActivityId, task.ActivityType, task.Attempt, task.IdempotencyKey) { Heartbeat = Beat };
            byte[] input = _crypto is null
                ? task.Input
                : await _crypto.OpenAsync(r.Scope, r.RunId, "act-in", task.ActivityId, task.Input, attemptCts.Token).ConfigureAwait(false);
            byte[] output = await activity(context, input, attemptCts.Token).ConfigureAwait(false);
            outcome = new ActivityCompleted(r.TenantId, r.NamespaceId, r.RunId, 0, _time.GetUtcNow(), task.ActivityId, output);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            return true; // another worker owns this task now: record nothing, stop quietly
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-activity: give the attempt back so a peer starts it at once; this is not a failed attempt.
            await ReleaseQuietlyAsync(() => _store.ReleaseActivityTaskAsync(task, _time.GetUtcNow(), _time.GetUtcNow(), CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // OutOfMemoryException is deliberately not recorded: it means the process is dying, the lease expires, and the
            // reaper counts the attempt (a poison task cannot loop forever, see StoreRules / ReapExpiredLeasesAsync).
            string errorType = ex.GetType().Name;
            if (errorType == StoreRules.LeaseExpiredError)
            {
                errorType += "Exception"; // that name is reserved for the record the store writes itself
            }

            bool retry = ex is not ActivityNotRegisteredException && task.RetryPolicy.ShouldRetry(task.Attempt, errorType);
            DateTimeOffset stamp = _time.GetUtcNow();
            outcome = new ActivityFailed(r.TenantId, r.NamespaceId, r.RunId, 0, stamp, task.ActivityId, task.Attempt, errorType, ex.Message, retry);
            retryAt = retry ? stamp + task.RetryPolicy.GetBackoff(task.Attempt) : null;
        }
        finally
        {
            await attemptCts.CancelAsync().ConfigureAwait(false);
            if (autoBeat is not null)
            {
                await autoBeat.ConfigureAwait(false);
            }
        }

        if (_crypto is not null)
        {
            outcome = await _crypto.SealEventAsync(outcome, cancellationToken).ConfigureAwait(false);
        }

        await _store.CompleteActivityTaskAsync(task, outcome, retryAt, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task AutoHeartbeatAsync(Func<ValueTask> beat, TimeSpan period, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, _time, ct).ConfigureAwait(false);
                await beat().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or NotSupportedException)
            {
                return; // cancelled, or the TimeProvider has no timers (virtual time): the activity heartbeats explicitly instead
            }
            catch (Exception)
            {
                // A store hiccup must not kill the attempt; the next beat retries and the lease still has two thirds in hand.
            }
        }
    }

    private static async Task ReleaseQuietlyAsync(Func<ValueTask> release)
    {
        try
        {
            await release().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: if the release fails the lease simply expires and the reaper takes over.
        }
    }
#pragma warning restore CA1031

    /// <summary>Maintenance tick: fires due timers and handles expired leases. Returns how many things it did.</summary>
    public async ValueTask<int> FireTimersAsync(CancellationToken cancellationToken)
    {
        int fired = await _store.FireDueTimersAsync(_scope, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        int reaped = await _store.ReapExpiredLeasesAsync(_scope, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return fired + reaped;
    }

    private void Fault(WorkflowTask task, Exception error)
    {
        KairosMetrics.WorkflowTaskFaults.Add(1, new KeyValuePair<string, object?>("error", error.GetType().Name));
        OnFault?.Invoke(new TaskFault(task.Run, error));
    }

    private static HistoryEvent ToEvent(RunKey r, WorkflowCommand command, DateTimeOffset ts) => command switch
    {
        ScheduleActivityCommand c => new ActivityScheduled(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.ActivityId, c.ActivityType, c.Input, c.RetryPolicy),
        StartTimerCommand c => new TimerStarted(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.TimerId, c.Duration),
        RecordPatchCommand c => new PatchMarker(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.PatchId),
        CompleteWorkflowCommand c => new WorkflowCompleted(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.Result),
        FailWorkflowCommand c => new WorkflowFailed(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.ErrorType, c.Message),
        ContinueAsNewCommand c => new WorkflowContinued(r.TenantId, r.NamespaceId, r.RunId, 0, ts, c.NewRunId, c.Input),
        _ => throw new InvalidOperationException($"Unknown command {command.GetType().Name}."),
    };

    private sealed class ActivityNotRegisteredException(string activityType)
        : Exception($"Activity type '{activityType}' is not registered on this worker.");
}
