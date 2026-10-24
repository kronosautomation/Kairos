namespace Kairos;

public readonly record struct InMemoryStoreStats(int Runs, int OpenRuns, int WorkflowTasks, int ActivityTasks, int Timers);

/// <summary>
/// Reference implementation of <see cref="IKairosStore"/> held in memory. Thread-safe (one lock). Semantics here are the
/// contract the PostgreSQL store must match; both run the same store contract tests.
/// </summary>
public sealed class InMemoryKairosStore : IKairosStore
{
    private readonly object _gate = new();
    private readonly Dictionary<TenantScope, ScopeData> _scopes = [];
    private long _order;

    public ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ScopeData scope = Scope(request.Run.Scope, create: true)!;
            if (scope.WorkflowIds.TryGetValue(request.WorkflowId, out Guid existing))
            {
                return ValueTask.FromResult(new StartRunResult(existing, false));
            }

            var run = new Run(request.Run, request.WorkflowType, request.BuildId);
            scope.Runs.Add(request.Run.RunId, run);
            scope.WorkflowIds.Add(request.WorkflowId, request.Run.RunId);
            Append(run, new WorkflowStarted(
                request.Run.TenantId, request.Run.NamespaceId, request.Run.RunId, 0, request.Now, request.WorkflowType, request.BuildId, request.Input));
            EnsureWorkflowTask(scope, run, request.Now);
            return ValueTask.FromResult(new StartRunResult(request.Run.RunId, true));
        }
    }

    public ValueTask<bool> SignalRunAsync(RunKey run, string signalName, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (FindRun(run) is not { State: RunState.Running } r)
            {
                return ValueTask.FromResult(false);
            }

            Append(r, new SignalReceived(run.TenantId, run.NamespaceId, run.RunId, 0, now, signalName, payload));
            EnsureWorkflowTask(Scope(run.Scope, create: false)!, r, now);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<WorkflowTask?> PollWorkflowTaskAsync(
        TenantScope scope, IReadOnlyCollection<string> buildIds, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ScopeData? data = Scope(scope, create: false);
            if (data is null)
            {
                return ValueTask.FromResult<WorkflowTask?>(null);
            }

            WorkflowTaskRow? best = null;
            foreach (WorkflowTaskRow row in data.WorkflowTasks.Values)
            {
                Run run = data.Runs[row.RunId];
                bool runnable = row.Leased ? row.LeaseExpires <= now : row.VisibleAt <= now;
                if (runnable && buildIds.Contains(run.BuildId) && (best is null || row.Order < best.Order))
                {
                    best = row;
                }
            }

            if (best is null)
            {
                return ValueTask.FromResult<WorkflowTask?>(null);
            }

            Run claimed = data.Runs[best.RunId];
            best.Leased = true;
            best.LeaseExpires = now + lease;
            claimed.WorkflowLeaseToken++;
            return ValueTask.FromResult<WorkflowTask?>(new WorkflowTask(
                claimed.Key, claimed.WorkflowType, claimed.BuildId, claimed.WorkflowLeaseToken, best.Attempt, claimed.History.ToArray()));
        }
    }

    public ValueTask<CompletionOutcome> CompleteWorkflowTaskAsync(
        WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        StoreRules.ValidateWorkflowTaskEvents(task.Run, newEvents);
        lock (_gate)
        {
            ScopeData? scope = Scope(task.Run.Scope, create: false);
            Run? run = FindRun(task.Run);
            if (scope is null || run is null || !scope.WorkflowTasks.TryGetValue(task.Run.RunId, out WorkflowTaskRow? row) ||
                !row.Leased || run.WorkflowLeaseToken != task.LeaseToken)
            {
                return ValueTask.FromResult(CompletionOutcome.StaleLease);
            }

            if (run.History.Count != task.History.Count)
            {
                row.Leased = false;
                row.VisibleAt = now;
                return ValueTask.FromResult(CompletionOutcome.SequenceConflict);
            }

            Append(run, new WorkflowTaskCompleted(task.Run.TenantId, task.Run.NamespaceId, task.Run.RunId, 0, now));
            for (int i = 0; i < newEvents.Count; i++)
            {
                HistoryEvent sealedEvent = Append(run, newEvents[i]);
                switch (sealedEvent)
                {
                    case ActivityScheduled s:
                        scope.ActivityTasks.Add((run.Key.RunId, s.ActivityId), new ActivityTaskRow(run.Key.RunId, s, now, ++_order));
                        break;
                    case TimerStarted t:
                        scope.Timers.Add((run.Key.RunId, t.TimerId), t.Timestamp + t.Duration);
                        break;
                    case WorkflowCompleted:
                        Close(scope, run, RunState.Completed);
                        break;
                    case WorkflowFailed:
                        Close(scope, run, RunState.Failed);
                        break;
                }
            }

            scope.WorkflowTasks.Remove(task.Run.RunId);
            return ValueTask.FromResult(CompletionOutcome.Applied);
        }
    }

    public ValueTask ReleaseWorkflowTaskAsync(WorkflowTask task, DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (Scope(task.Run.Scope, create: false) is { } scope && FindRun(task.Run) is { } run &&
                scope.WorkflowTasks.TryGetValue(task.Run.RunId, out WorkflowTaskRow? row) &&
                row.Leased && run.WorkflowLeaseToken == task.LeaseToken)
            {
                row.Leased = false;
                row.VisibleAt = retryAt;
                row.Attempt++;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ActivityTask?> PollActivityTaskAsync(TenantScope scope, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ScopeData? data = Scope(scope, create: false);
            ActivityTaskRow? best = null;
            if (data is not null)
            {
                foreach (ActivityTaskRow row in data.ActivityTasks.Values)
                {
                    bool runnable = row.Leased ? row.LeaseExpires <= now : row.VisibleAt <= now;
                    if (runnable && (best is null || row.Order < best.Order))
                    {
                        best = row;
                    }
                }
            }

            if (best is null)
            {
                return ValueTask.FromResult<ActivityTask?>(null);
            }

            best.Leased = true;
            best.LeaseExpires = now + lease;
            best.LeaseToken++;
            ActivityScheduled s = best.Scheduled;
            return ValueTask.FromResult<ActivityTask?>(new ActivityTask(
                new RunKey(s.TenantId, s.NamespaceId, s.RunId), s.ActivityId, s.ActivityType, s.Input, s.RetryPolicy, best.Attempt, best.LeaseToken));
        }
    }

    public ValueTask<CompletionOutcome> CompleteActivityTaskAsync(
        ActivityTask task, HistoryEvent outcome, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StoreRules.ValidateActivityOutcome(task, outcome, retryAt);
        lock (_gate)
        {
            ScopeData? scope = Scope(task.Run.Scope, create: false);
            if (scope is null || !scope.ActivityTasks.TryGetValue((task.Run.RunId, task.ActivityId), out ActivityTaskRow? row) ||
                !row.Leased || row.LeaseToken != task.LeaseToken)
            {
                return ValueTask.FromResult(CompletionOutcome.StaleLease);
            }

            Run? run = FindRun(task.Run);
            if (run is not { State: RunState.Running })
            {
                scope.ActivityTasks.Remove((task.Run.RunId, task.ActivityId));
                return ValueTask.FromResult(CompletionOutcome.Applied); // workflow already closed: result is moot
            }

            Append(run, outcome);
            if (retryAt is { } at)
            {
                row.Leased = false;
                row.VisibleAt = at;
                row.Attempt++;
            }
            else
            {
                scope.ActivityTasks.Remove((task.Run.RunId, task.ActivityId));
                EnsureWorkflowTask(scope, run, now);
            }

            return ValueTask.FromResult(CompletionOutcome.Applied);
        }
    }

    public ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ScopeData? data = Scope(scope, create: false);
            if (data is null)
            {
                return ValueTask.FromResult(0);
            }

            var due = new List<((Guid RunId, int TimerId) Key, DateTimeOffset At)>();
            foreach (KeyValuePair<(Guid RunId, int TimerId), DateTimeOffset> timer in data.Timers)
            {
                if (timer.Value <= now)
                {
                    due.Add((timer.Key, timer.Value));
                }
            }

            due.Sort(static (a, b) =>
            {
                int c = a.At.CompareTo(b.At);
                if (c != 0)
                {
                    return c;
                }

                c = a.Key.RunId.CompareTo(b.Key.RunId);
                return c != 0 ? c : a.Key.TimerId.CompareTo(b.Key.TimerId);
            });

            foreach (((Guid runId, int timerId), _) in due)
            {
                data.Timers.Remove((runId, timerId));
                Run run = data.Runs[runId];
                if (run.State != RunState.Running)
                {
                    continue;
                }

                Append(run, new TimerFired(scope.TenantId, scope.NamespaceId, runId, 0, now, timerId));
                EnsureWorkflowTask(data, run, now);
            }

            return ValueTask.FromResult(due.Count);
        }
    }

    public ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<HistoryEvent>>(FindRun(run)?.History.ToArray() ?? []);
        }
    }

    public ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FindRun(run)?.State);
        }
    }

    /// <summary>
    /// Restores a run from persisted history (a process restart): verifies the hash chain, then rebuilds the derived state
    /// (open activity tasks, timers, runnable workflow task) from the events alone.
    /// </summary>
    public RunKey Import(IReadOnlyList<HistoryEvent> history, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[0] is not WorkflowStarted started)
        {
            throw new ArgumentException("History must begin with WorkflowStarted.", nameof(history));
        }

        HashChain.Verify(history);
        var key = new RunKey(started.TenantId, started.NamespaceId, started.RunId);
        lock (_gate)
        {
            ScopeData scope = Scope(key.Scope, create: true)!;
            if (scope.Runs.ContainsKey(key.RunId))
            {
                throw new InvalidOperationException("Run already exists.");
            }

            var run = new Run(key, started.WorkflowType, started.BuildId);
            run.History.AddRange(history);
            scope.Runs.Add(key.RunId, run);
            scope.WorkflowIds.Add($"imported-{key.RunId:N}", key.RunId);

            var scheduled = new Dictionary<int, ActivityScheduled>();
            var failures = new Dictionary<int, int>();
            var startedTimers = new Dictionary<int, TimerStarted>();
            bool newExternal = false;
            foreach (HistoryEvent e in history)
            {
                switch (e)
                {
                    case ActivityScheduled s:
                        scheduled[s.ActivityId] = s;
                        break;
                    case ActivityCompleted c:
                        scheduled.Remove(c.ActivityId);
                        newExternal = true;
                        break;
                    case ActivityFailed f when f.WillRetry:
                        failures[f.ActivityId] = f.Attempt;
                        break;
                    case ActivityFailed f:
                        scheduled.Remove(f.ActivityId);
                        newExternal = true;
                        break;
                    case TimerStarted t:
                        startedTimers[t.TimerId] = t;
                        break;
                    case TimerFired t:
                        startedTimers.Remove(t.TimerId);
                        newExternal = true;
                        break;
                    case SignalReceived or WorkflowStarted:
                        newExternal = true;
                        break;
                    case WorkflowTaskCompleted:
                        newExternal = false;
                        break;
                    case WorkflowCompleted:
                        run.State = RunState.Completed;
                        break;
                    case WorkflowFailed:
                        run.State = RunState.Failed;
                        break;
                }
            }

            if (run.State == RunState.Running)
            {
                foreach (ActivityScheduled s in scheduled.Values)
                {
                    scope.ActivityTasks.Add((key.RunId, s.ActivityId), new ActivityTaskRow(key.RunId, s, now, ++_order)
                    {
                        Attempt = failures.GetValueOrDefault(s.ActivityId) + 1,
                    });
                }

                foreach (TimerStarted t in startedTimers.Values)
                {
                    scope.Timers.Add((key.RunId, t.TimerId), t.Timestamp + t.Duration);
                }

                if (newExternal)
                {
                    EnsureWorkflowTask(scope, run, now);
                }
            }

            return key;
        }
    }

    /// <summary>Resource counters for bounded-resource checks (I9): everything should drain to zero once runs close.</summary>
    public InMemoryStoreStats GetStats()
    {
        lock (_gate)
        {
            int runs = 0, wf = 0, act = 0, timers = 0, open = 0;
            foreach (ScopeData s in _scopes.Values)
            {
                runs += s.Runs.Count;
                wf += s.WorkflowTasks.Count;
                act += s.ActivityTasks.Count;
                timers += s.Timers.Count;
                foreach (Run r in s.Runs.Values)
                {
                    if (r.State == RunState.Running)
                    {
                        open++;
                    }
                }
            }

            return new InMemoryStoreStats(runs, open, wf, act, timers);
        }
    }

    /// <summary>
    /// The earliest moment after <paramref name="now"/> at which something becomes runnable (a timer is due, a retry becomes
    /// visible, a lease expires), or null. Lets simulators and test engines jump virtual time instead of waiting.
    /// </summary>
    public DateTimeOffset? NextWakeup(TenantScope scope, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Scope(scope, create: false) is not { } data)
            {
                return null;
            }

            DateTimeOffset? best = null;

            void Consider(DateTimeOffset at)
            {
                if (at > now && (best is null || at < best))
                {
                    best = at;
                }
            }

            foreach (DateTimeOffset due in data.Timers.Values)
            {
                Consider(due);
            }

            foreach (WorkflowTaskRow row in data.WorkflowTasks.Values)
            {
                Consider(row.Leased ? row.LeaseExpires : row.VisibleAt);
            }

            foreach (ActivityTaskRow row in data.ActivityTasks.Values)
            {
                Consider(row.Leased ? row.LeaseExpires : row.VisibleAt);
            }

            return best;
        }
    }

    private ScopeData? Scope(TenantScope scope, bool create)
    {
        if (_scopes.TryGetValue(scope, out ScopeData? data))
        {
            return data;
        }

        if (!create)
        {
            return null;
        }

        data = new ScopeData();
        _scopes.Add(scope, data);
        return data;
    }

    private Run? FindRun(RunKey key) =>
        _scopes.TryGetValue(key.Scope, out ScopeData? s) && s.Runs.TryGetValue(key.RunId, out Run? r) ? r : null;

    private static HistoryEvent Append(Run run, HistoryEvent template)
    {
        StoreRules.RequireSameRun(run.Key, template);
        ReadOnlySpan<byte> prev = run.History.Count == 0 ? HashChain.GenesisHash : run.History[^1].Hash;
        HistoryEvent sealedEvent = StoreRules.Seal(template, run.History.Count + 1, prev);
        run.History.Add(sealedEvent);
        return sealedEvent;
    }

    private void EnsureWorkflowTask(ScopeData scope, Run run, DateTimeOffset now)
    {
        if (!scope.WorkflowTasks.ContainsKey(run.Key.RunId))
        {
            scope.WorkflowTasks.Add(run.Key.RunId, new WorkflowTaskRow(run.Key.RunId, now, ++_order));
        }
    }

    private static void Close(ScopeData scope, Run run, RunState state)
    {
        run.State = state;
        Guid id = run.Key.RunId;
        foreach ((Guid RunId, int ActivityId) key in scope.ActivityTasks.Keys.Where(k => k.RunId == id).ToArray())
        {
            scope.ActivityTasks.Remove(key);
        }

        foreach ((Guid RunId, int TimerId) key in scope.Timers.Keys.Where(k => k.RunId == id).ToArray())
        {
            scope.Timers.Remove(key);
        }
    }

    private sealed class ScopeData
    {
        public Dictionary<Guid, Run> Runs { get; } = [];

        public Dictionary<string, Guid> WorkflowIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, WorkflowTaskRow> WorkflowTasks { get; } = [];

        public Dictionary<(Guid RunId, int ActivityId), ActivityTaskRow> ActivityTasks { get; } = [];

        public Dictionary<(Guid RunId, int TimerId), DateTimeOffset> Timers { get; } = [];
    }

    private sealed class Run(RunKey key, string workflowType, string buildId)
    {
        public RunKey Key { get; } = key;

        public string WorkflowType { get; } = workflowType;

        public string BuildId { get; } = buildId;

        public List<HistoryEvent> History { get; } = [];

        public RunState State { get; set; }

        /// <summary>Per-run, never reset: a stale holder can never match a later claim.</summary>
        public long WorkflowLeaseToken { get; set; }
    }

    private sealed class WorkflowTaskRow(Guid runId, DateTimeOffset visibleAt, long order)
    {
        public Guid RunId { get; } = runId;

        public DateTimeOffset VisibleAt { get; set; } = visibleAt;

        public long Order { get; } = order;

        public bool Leased { get; set; }

        public DateTimeOffset LeaseExpires { get; set; }

        public int Attempt { get; set; } = 1;
    }

    private sealed class ActivityTaskRow(Guid runId, ActivityScheduled scheduled, DateTimeOffset visibleAt, long order)
    {
        public Guid RunId { get; } = runId;

        public ActivityScheduled Scheduled { get; } = scheduled;

        public DateTimeOffset VisibleAt { get; set; } = visibleAt;

        public long Order { get; } = order;

        public bool Leased { get; set; }

        public DateTimeOffset LeaseExpires { get; set; }

        public long LeaseToken { get; set; }

        public int Attempt { get; set; } = 1;
    }
}
