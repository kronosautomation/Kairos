namespace Kairos;

public readonly record struct StoreStats(int Runs, int OpenRuns, int WorkflowTasks, int ActivityTasks, int Timers, int StuckWorkflowTasks = 0);

/// <summary>
/// Reference implementation of <see cref="IKairosStore"/> held in memory. Thread-safe (one lock). Semantics here are the
/// contract the PostgreSQL store must match; both run the same store contract tests.
/// </summary>
public sealed class InMemoryKairosStore(StoreLimits? limits = null) : IKairosStore
{
    private readonly StoreLimits _limits = limits ?? new StoreLimits();

    private readonly object _gate = new();
    private readonly UpdateGate _updates = new();
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

            var run = new Run(request.Run, request.WorkflowType, request.BuildId) { WorkflowId = request.WorkflowId };
            scope.Runs.Add(request.Run.RunId, run);
            scope.WorkflowIds.Add(request.WorkflowId, request.Run.RunId);
            Append(run, new WorkflowStarted(
                request.Run.TenantId, request.Run.NamespaceId, request.Run.RunId, 0, request.Now, request.WorkflowType, request.BuildId, request.Input)
            {
                TraceContext = request.TraceContext,
                StartedBy = StoreRules.CheckStartedBy(request.StartedBy),
            });
            EnsureWorkflowTask(scope, run, request.Now);
            return ValueTask.FromResult(new StartRunResult(request.Run.RunId, true));
        }
    }

    public ValueTask<bool> SignalRunAsync(RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        SignalRunTracedAsync(run, signalName, signalId, payload, now, "", cancellationToken);

    public ValueTask<bool> SignalRunTracedAsync(
        RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, string traceContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(traceContext);
        ArgumentException.ThrowIfNullOrEmpty(signalId);
        lock (_gate)
        {
            if (FindRun(run) is not { State: RunState.Running } r)
            {
                return ValueTask.FromResult(false);
            }

            if (!r.SignalIds.Add(signalId))
            {
                return ValueTask.FromResult(true); // already delivered
            }

            Append(r, new SignalReceived(run.TenantId, run.NamespaceId, run.RunId, 0, now, signalName, payload) { SignalId = signalId, TraceContext = traceContext });
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
                bool runnable = !row.Leased && !row.Stuck && row.VisibleAt <= now;
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

            // Bounded history (I9): a task that would push the run past the cap, and does not itself close or continue, ends the run.
            bool closes = newEvents.Count > 0 && StoreRules.IsClosing(newEvents[^1]);
            if (!closes && run.History.Count + newEvents.Count + 1 > _limits.MaxHistoryEvents) // one slot stays free for the RunTerminated record
            {
                Append(run, new RunTerminated(task.Run.TenantId, task.Run.NamespaceId, task.Run.RunId, 0, now, StoreRules.HistoryLimitReason(_limits.MaxHistoryEvents)));
                Close(scope, run, RunState.Failed);
                scope.WorkflowTasks.Remove(task.Run.RunId);
                return ValueTask.FromResult(CompletionOutcome.Applied);
            }

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
                    case WorkflowContinued c:
                        Close(scope, run, RunState.ContinuedAsNew);
                        StartContinuation(scope, run, c, now);
                        break;
                }
            }

            scope.WorkflowTasks.Remove(task.Run.RunId);
            return ValueTask.FromResult(CompletionOutcome.Applied);
        }
    }

    /// <summary>The new run of a continue-as-new chain: same workflow id, type and build, empty history, input carried over.</summary>
    private void StartContinuation(ScopeData scope, Run previous, WorkflowContinued c, DateTimeOffset now)
    {
        var key = new RunKey(previous.Key.TenantId, previous.Key.NamespaceId, c.NewRunId);
        var next = new Run(key, previous.WorkflowType, previous.BuildId) { WorkflowId = previous.WorkflowId };
        scope.Runs.Add(c.NewRunId, next);
        scope.WorkflowIds[previous.WorkflowId] = c.NewRunId;
        Append(next, new WorkflowStarted(key.TenantId, key.NamespaceId, c.NewRunId, 0, now, previous.WorkflowType, previous.BuildId, c.Input)
        {
            PreviousRunId = previous.Key.RunId,
            TraceContext = previous.History.Count > 0 && previous.History[0] is WorkflowStarted first ? first.TraceContext : "", // one trace across the whole chain
            StartedBy = previous.History.Count > 0 && previous.History[0] is WorkflowStarted origin ? origin.StartedBy : "", // and one starter
        });
        EnsureWorkflowTask(scope, next, now);
    }

    public ValueTask ReleaseWorkflowTaskAsync(WorkflowTask task, DateTimeOffset retryAt, DateTimeOffset now, bool countAttempt, CancellationToken cancellationToken)
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
                if (countAttempt)
                {
                    row.Attempt++;
                }
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
                    bool runnable = !row.Leased && row.VisibleAt <= now;
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
                new RunKey(s.TenantId, s.NamespaceId, s.RunId), s.ActivityId, s.ActivityType, s.Input, s.RetryPolicy, best.Attempt, best.LeaseToken, s.TraceContext));
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

    public ValueTask<CompletionOutcome> HeartbeatActivityTaskAsync(ActivityTask task, DateTimeOffset now, TimeSpan extendBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (Scope(task.Run.Scope, create: false) is { } scope &&
                scope.ActivityTasks.TryGetValue((task.Run.RunId, task.ActivityId), out ActivityTaskRow? row) &&
                row.Leased && row.LeaseToken == task.LeaseToken)
            {
                DateTimeOffset renewed = now + extendBy;
                if (renewed > row.LeaseExpires)
                {
                    row.LeaseExpires = renewed;
                }

                return ValueTask.FromResult(CompletionOutcome.Applied);
            }

            return ValueTask.FromResult(CompletionOutcome.StaleLease);
        }
    }

    public ValueTask ReleaseActivityTaskAsync(ActivityTask task, DateTimeOffset retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (Scope(task.Run.Scope, create: false) is { } scope &&
                scope.ActivityTasks.TryGetValue((task.Run.RunId, task.ActivityId), out ActivityTaskRow? row) &&
                row.Leased && row.LeaseToken == task.LeaseToken)
            {
                row.Leased = false;
                row.VisibleAt = retryAt; // not an attempt: the activity did not fail, the worker is going away
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReapExpiredLeasesAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Scope(scope, create: false) is not { } data)
            {
                return ValueTask.FromResult(0);
            }

            int handled = 0;
            var expiredActivities = new List<ActivityTaskRow>();
            foreach (ActivityTaskRow row in data.ActivityTasks.Values)
            {
                if (row.Leased && row.LeaseExpires <= now)
                {
                    expiredActivities.Add(row);
                }
            }

            expiredActivities.Sort(static (a, b) => a.Order.CompareTo(b.Order));
            foreach (ActivityTaskRow row in expiredActivities)
            {
                handled++;
                Run run = data.Runs[row.RunId];
                ActivityScheduled s = row.Scheduled;
                if (run.State != RunState.Running)
                {
                    data.ActivityTasks.Remove((row.RunId, s.ActivityId));
                    continue;
                }

                bool retry = s.RetryPolicy.ShouldRetry(row.Attempt, StoreRules.LeaseExpiredError);
                Append(run, new ActivityFailed(
                    run.Key.TenantId, run.Key.NamespaceId, run.Key.RunId, 0, now, s.ActivityId, row.Attempt,
                    StoreRules.LeaseExpiredError, StoreRules.LeaseExpiredMessage, retry));
                if (retry)
                {
                    row.Leased = false;
                    row.VisibleAt = now + s.RetryPolicy.GetBackoff(row.Attempt);
                    row.Attempt++;
                }
                else
                {
                    data.ActivityTasks.Remove((row.RunId, s.ActivityId));
                    EnsureWorkflowTask(data, run, now);
                }
            }

            foreach (WorkflowTaskRow row in data.WorkflowTasks.Values)
            {
                if (row.Leased && row.LeaseExpires <= now)
                {
                    handled++;
                    int failed = row.Attempt;
                    row.Leased = false;
                    row.Attempt++;
                    if (row.Attempt > StoreRules.MaxWorkflowTaskAttempts)
                    {
                        row.Stuck = true; // parked and visible in stats, never dropped
                    }
                    else
                    {
                        row.VisibleAt = now + StoreRules.WorkflowTaskBackoff(failed);
                    }
                }
            }

            return ValueTask.FromResult(handled);
        }
    }

    public ValueTask<IReadOnlyList<RunSummary>> ListRunsAsync(TenantScope scope, RunFilter filter, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            if (Scope(scope, create: false) is not { } data)
            {
                return ValueTask.FromResult<IReadOnlyList<RunSummary>>([]);
            }

            IEnumerable<RunSummary> all = data.Runs.Values
                .Where(r => r.History.Count > 0)
                .Select(r => new RunSummary(r.Key, r.WorkflowId, r.WorkflowType, r.BuildId, r.State, r.History[0].Timestamp))
                .Where(s => (filter.WorkflowType is null || s.WorkflowType == filter.WorkflowType) && (filter.BuildId is null || s.BuildId == filter.BuildId))
                .Where(s => filter.State switch
                {
                    RunStateFilter.Open => s.State == RunState.Running,
                    RunStateFilter.Closed => s.State != RunState.Running,
                    _ => true,
                })
                .Where(s => s.State == RunState.Running || filter.StartedSince is not { } since || s.StartedAt >= since)
                .OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Run.RunId);
            if (filter.After is { } after)
            {
                all = all.Where(s => s.StartedAt < after.StartedAt || (s.StartedAt == after.StartedAt && s.Run.RunId.CompareTo(after.RunId) < 0));
            }

            return ValueTask.FromResult<IReadOnlyList<RunSummary>>([.. all.Take(limit)]);
        }
    }

    public ValueTask<UsageSummary> GetUsageSummaryAsync(TenantScope scope, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        RunAggregates.ScanUsageAsync(this, scope, firstDay, lastDay, cancellationToken);

    public ValueTask<PendingApprovalPage> GetPendingApprovalsAsync(TenantScope scope, int limit, PendingApprovalCursor? after, CancellationToken cancellationToken) =>
        RunAggregates.ScanPendingAsync(this, scope, limit, after, cancellationToken);

    public ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Scope(scope, create: false) is not { } data)
            {
                return ValueTask.FromResult<IReadOnlyList<RunGroup>>([]);
            }

            IReadOnlyList<RunGroup> groups =
            [
                .. data.Runs.Values
                    .Where(r => r.History.Count > 0)
                    .GroupBy(r => (r.WorkflowType, r.BuildId))
                    .OrderBy(g => g.Key.WorkflowType, StringComparer.Ordinal).ThenBy(g => g.Key.BuildId, StringComparer.Ordinal)
                    .Select(g => new RunGroup(
                        g.Key.WorkflowType, g.Key.BuildId, g.Count(r => r.State == RunState.Running),
                        g.Count(r => r.State != RunState.Running && (closedSince is not { } since || r.History[0].Timestamp >= since)))),
            ];
            return ValueTask.FromResult(groups);
        }
    }

    public ValueTask<WorkBacklog?> GetBacklogAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Scope(scope, create: false) is not { } data)
            {
                return ValueTask.FromResult<WorkBacklog?>(new WorkBacklog(0, 0, 0));
            }

            int workflow = data.WorkflowTasks.Values.Count(t => !t.Leased && !t.Stuck && t.VisibleAt <= now);
            int activity = data.ActivityTasks.Values.Count(t => !t.Leased && t.VisibleAt <= now);
            int timers = data.Timers.Values.Count(due => due <= now);
            Dictionary<string, int> byBuild = data.WorkflowTasks.Values
                .Where(t => !t.Leased && !t.Stuck && t.VisibleAt <= now)
                .GroupBy(t => data.Runs[t.RunId].BuildId)
                .ToDictionary(g => g.Key, g => g.Count());
            return ValueTask.FromResult<WorkBacklog?>(new WorkBacklog(workflow, activity, timers) { WorkflowTasksByBuild = byBuild });
        }
    }

    /// <summary>The login the in-memory store reports for every connection: there is no database to ask.</summary>
    public const string SessionUserName = "in-memory";

    public ValueTask<IReadOnlyList<HistoryEvent>?> ExportHistoryAsync(
        RunKey run, string claimedActor, string? verifiedPrincipal, string reason, bool metadataOnly, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StoreRules.ValidateExport(claimedActor, reason);
        lock (_gate)
        {
            if (FindRun(run) is not { } r || r.History.Count == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<HistoryEvent>?>(null);
            }

            Scope(run.Scope, create: false)!.Audit.Add(new AuditEntry(
                run.TenantId, run.NamespaceId, run.RunId, StoreRules.ExportAuditAction, verifiedPrincipal ?? "db:" + SessionUserName, now, StoreRules.ExportDetail(reason, metadataOnly, r.History))
            {
                SessionUser = SessionUserName,
                ClaimedActor = claimedActor,
                VerifiedPrincipal = verifiedPrincipal,
            });
            return ValueTask.FromResult<IReadOnlyList<HistoryEvent>?>([.. r.History]);
        }
    }

    public ValueTask<IReadOnlyList<StuckWorkflowTask>> GetStuckWorkflowTasksAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var stuck = new List<StuckWorkflowTask>();
            if (Scope(scope, create: false) is { } data)
            {
                foreach (WorkflowTaskRow row in data.WorkflowTasks.Values)
                {
                    if (row.Stuck)
                    {
                        Run run = data.Runs[row.RunId];
                        stuck.Add(new StuckWorkflowTask(run.Key, run.WorkflowType, run.BuildId, row.Attempt - 1));
                    }
                }
            }

            stuck.Sort(static (a, b) => a.Run.RunId.CompareTo(b.Run.RunId));
            return ValueTask.FromResult<IReadOnlyList<StuckWorkflowTask>>(stuck);
        }
    }

    public ValueTask<bool> ResumeWorkflowTaskAsync(RunKey run, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        ResumeCore(run, actor, null, now);

    public ValueTask<bool> ResumeWorkflowTaskVerifiedAsync(RunKey run, Principal actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StoreRules.RequirePrincipalInScope(run, actor);
        return ResumeCore(run, null, actor.AuditName, now);
    }

    /// <summary>Who an entry names: the verified principal, else the store's view of the connection. A supplied name is only ever <see cref="AuditEntry.ClaimedActor"/>.</summary>
    private static string ActorOf(string? verified) => verified ?? "db:" + SessionUserName;

    private ValueTask<bool> ResumeCore(RunKey run, string? claimed, string? verified, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Scope(run.Scope, create: false) is not { } data ||
                !data.WorkflowTasks.TryGetValue(run.RunId, out WorkflowTaskRow? row) || !row.Stuck)
            {
                return ValueTask.FromResult(false);
            }

            row.Stuck = false;
            row.Leased = false;
            row.Attempt = 1;
            row.VisibleAt = now;
            data.Audit.Add(new AuditEntry(run.TenantId, run.NamespaceId, run.RunId, StoreRules.ResumeAuditAction, ActorOf(verified), now, "unparked after repeated lease expiry")
            {
                SessionUser = SessionUserName,
                ClaimedActor = claimed,
                VerifiedPrincipal = verified,
            });
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> FailRunAsync(RunKey run, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        FailCore(run, reason, actor, null, now);

    public ValueTask<bool> FailRunVerifiedAsync(RunKey run, string reason, Principal actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StoreRules.RequirePrincipalInScope(run, actor);
        return FailCore(run, reason, null, actor.AuditName, now);
    }

    private ValueTask<bool> FailCore(RunKey run, string reason, string? claimed, string? verified, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Scope(run.Scope, create: false) is not { } data || FindRun(run) is not { State: RunState.Running } r)
            {
                return ValueTask.FromResult(false);
            }

            Append(r, new RunTerminated(run.TenantId, run.NamespaceId, run.RunId, 0, now, reason));
            Close(data, r, RunState.Failed);
            data.WorkflowTasks.Remove(run.RunId);
            data.Audit.Add(new AuditEntry(run.TenantId, run.NamespaceId, run.RunId, StoreRules.FailAuditAction, ActorOf(verified), now, reason)
            {
                SessionUser = SessionUserName,
                ClaimedActor = claimed,
                VerifiedPrincipal = verified,
            });
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> CancelRunAsync(RunKey run, string reason, Principal actor, string traceContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StoreRules.RequirePrincipalInScope(run, actor);
        StoreRules.ValidateOptionalReason(reason);
        ArgumentNullException.ThrowIfNull(traceContext);
        lock (_gate)
        {
            if (FindRun(run) is not { State: RunState.Running } r)
            {
                return ValueTask.FromResult(false);
            }

            // One transaction in PostgreSQL; one lock here. The signal id is fixed, so a second request adds an audit entry but no second signal.
            if (r.SignalIds.Add(ReservedSignals.CancelSignalId))
            {
                Append(r, new SignalReceived(run.TenantId, run.NamespaceId, run.RunId, 0, now, ReservedSignals.Cancel, [])
                {
                    SignalId = ReservedSignals.CancelSignalId,
                    TraceContext = traceContext,
                });
                EnsureWorkflowTask(Scope(run.Scope, create: false)!, r, now);
            }

            Scope(run.Scope, create: false)!.Audit.Add(new AuditEntry(
                run.TenantId, run.NamespaceId, run.RunId, StoreRules.CancelAuditAction, actor.AuditName, now, reason.Length == 0 ? "cancel requested" : reason)
            {
                SessionUser = SessionUserName,
                VerifiedPrincipal = actor.AuditName,
            });
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<AuditEntry>> GetAuditLogAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<AuditEntry> entries = Scope(scope, create: false) is { } data ? [.. data.Audit] : [];
            return ValueTask.FromResult(entries);
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

    public ValueTask<HistoryPage> GetHistoryAfterAsync(RunKey run, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        HistoryPage.Check(afterSequence, limit);
        lock (_gate)
        {
            return ValueTask.FromResult(FindRun(run) is { } r ? Slice(r.History, afterSequence, limit) : new HistoryPage([], 0));
        }
    }

    public ValueTask<HistoryPage> GetHistoryTailAsync(RunKey run, int count, CancellationToken cancellationToken)
    {
        HistoryPage.Check(0, count);
        lock (_gate)
        {
            return ValueTask.FromResult(FindRun(run) is { } r ? Slice(r.History, Math.Max(0, r.History.Count - count), count) : new HistoryPage([], 0));
        }
    }

    public async ValueTask<bool> WaitForHistoryAsync(RunKey run, long afterSequence, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        while (true)
        {
            Task changed = _updates.Next; // taken before looking, so an append between the look and the wait is not missed
            lock (_gate)
            {
                if (FindRun(run) is { } r && r.History.Count > afterSequence)
                {
                    return true;
                }
            }

            try
            {
                await changed.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private static HistoryPage Slice(List<HistoryEvent> history, long afterSequence, int limit)
    {
        int skip = (int)Math.Min(afterSequence, history.Count);
        int take = Math.Min(limit, history.Count - skip);
        return new HistoryPage(history.GetRange(skip, take), history.Count);
    }

    public ValueTask<RunKey?> GetCurrentRunAsync(TenantScope scope, string workflowId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RunKey? current = Scope(scope, create: false) is { } data && data.WorkflowIds.TryGetValue(workflowId, out Guid id)
                ? new RunKey(scope.TenantId, scope.NamespaceId, id)
                : null;
            return ValueTask.FromResult(current);
        }
    }

    public ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FindRun(run)?.State);
        }
    }

    public ValueTask<RunIdentity?> GetRunIdentityAsync(RunKey run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FindRun(run) is { } found ? new RunIdentity(found.WorkflowId, found.WorkflowType) : null);
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
                    case SignalReceived signalEvent:
                        run.SignalIds.Add(signalEvent.SignalId);
                        newExternal = true;
                        break;
                    case WorkflowStarted:
                        newExternal = true;
                        break;
                    case WorkflowTaskCompleted:
                        newExternal = false;
                        break;
                    case WorkflowCompleted:
                        run.State = RunState.Completed;
                        break;
                    case WorkflowFailed or RunTerminated:
                        run.State = RunState.Failed;
                        break;
                    case WorkflowContinued:
                        run.State = RunState.ContinuedAsNew;
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

    /// <summary>Resource counters for bounded-resource checks: everything should drain to zero once runs close. Optionally for one scope only.</summary>
    public StoreStats GetStats(TenantScope? only = null)
    {
        lock (_gate)
        {
            int runs = 0, wf = 0, act = 0, timers = 0, open = 0, stuck = 0;
            foreach ((TenantScope key, ScopeData s) in _scopes)
            {
                if (only is { } wanted && key != wanted)
                {
                    continue;
                }

                runs += s.Runs.Count;
                wf += s.WorkflowTasks.Count;
                foreach (WorkflowTaskRow t in s.WorkflowTasks.Values)
                {
                    if (t.Stuck)
                    {
                        stuck++;
                    }
                }

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

            return new StoreStats(runs, open, wf, act, timers, stuck);
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
                if (!row.Stuck)
                {
                    Consider(row.Leased ? row.LeaseExpires : row.VisibleAt);
                }
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

    private HistoryEvent Append(Run run, HistoryEvent template)
    {
        StoreRules.RequireSameRun(run.Key, template);
        ReadOnlySpan<byte> prev = run.History.Count == 0 ? HashChain.GenesisHash : run.History[^1].Hash;
        HistoryEvent sealedEvent = StoreRules.Seal(template, run.History.Count + 1, prev);
        run.History.Add(sealedEvent);
        _updates.Pulse();
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

        public List<AuditEntry> Audit { get; } = [];
    }

    private sealed class Run(RunKey key, string workflowType, string buildId)
    {
        public RunKey Key { get; } = key;

        public string WorkflowId { get; init; } = "";

        public HashSet<string> SignalIds { get; } = new(StringComparer.Ordinal);

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

        public bool Stuck { get; set; }

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
