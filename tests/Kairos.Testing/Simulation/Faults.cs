namespace Kairos.Testing.Simulation;

public enum FaultKind
{
    None,

    /// <summary>The call is lost before the store applied it (request lost / process killed before commit).</summary>
    Before,

    /// <summary>The store applied the call but the caller never saw the reply (killed after commit / ack lost).</summary>
    After,
}

public sealed class SimulatedFaultException(string operation, FaultKind kind)
    : Exception($"Simulated {kind} fault in {operation}")
{
    public FaultKind Kind { get; } = kind;
}

public sealed class InvariantViolationException(string invariant, string message)
    : Exception($"[Invariant.{invariant}] {message}")
{
    public string Invariant { get; } = invariant;
}

public sealed class SimFaultOptions
{
    public double CrashProbability { get; init; } = 0.03;

    public double DelayProbability { get; init; } = 0.04;

    public double DuplicateProbability { get; init; } = 0.04;
}

/// <summary>Counters proving the faults actually happened (a green simulation that injected nothing proves nothing).</summary>
public sealed class SimStats
{
    public long Steps { get; set; }

    public long Crashes { get; set; }

    public long DelayedMessages { get; set; }

    public long DuplicateMessages { get; set; }

    public long StaleRejections { get; set; }

    public long SequenceConflicts { get; set; }

    public long RetriedAttempts { get; set; }

    public long ClockJumps { get; set; }

    public long NonDeterminismFaults { get; set; }

    public long LeaseExpirations { get; set; }

    public void Add(SimStats o)
    {
        Steps += o.Steps;
        Crashes += o.Crashes;
        DelayedMessages += o.DelayedMessages;
        DuplicateMessages += o.DuplicateMessages;
        StaleRejections += o.StaleRejections;
        SequenceConflicts += o.SequenceConflicts;
        RetriedAttempts += o.RetriedAttempts;
        ClockJumps += o.ClockJumps;
        NonDeterminismFaults += o.NonDeterminismFaults;
        LeaseExpirations += o.LeaseExpirations;
    }

    public override string ToString() =>
        $"steps={Steps} crashes={Crashes} delayed={DelayedMessages} duplicates={DuplicateMessages} stale={StaleRejections} " +
        $"seqConflicts={SequenceConflicts} retries={RetriedAttempts} clockJumps={ClockJumps} nonDeterminism={NonDeterminismFaults} leaseExpirations={LeaseExpirations}";
}

/// <summary>
/// Wraps a store and misbehaves the way networks and processes do: lost requests, lost replies (applied but unacknowledged),
/// delayed and duplicated completion messages. Every decision comes from the seeded <see cref="SimRandom"/> (or from
/// <see cref="ForcedFault"/> for exhaustive crash-at-every-step runs).
/// </summary>
public sealed class FaultInjectingStore(IKairosStore inner, SimRandom rng, SimFaultOptions options, SimStats stats) : IKairosStore
{
    private readonly List<Func<ValueTask>> _delayed = [];

    public bool FaultsEnabled { get; set; }

    public long OperationCount { get; private set; }

    /// <summary>When set, overrides random crashes: decides by operation index.</summary>
    public Func<long, FaultKind>? ForcedFault { get; set; }

    public int DelayedCount => _delayed.Count;

    public ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken) =>
        Faulty(nameof(StartRunAsync), () => inner.StartRunAsync(request, cancellationToken));

    public ValueTask<bool> SignalRunAsync(RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(SignalRunAsync), () => inner.SignalRunAsync(run, signalName, signalId, payload, now, cancellationToken));

    public ValueTask<bool> SignalRunTracedAsync(
        RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, string traceContext, CancellationToken cancellationToken) =>
        Faulty(nameof(SignalRunAsync), () => inner.SignalRunTracedAsync(run, signalName, signalId, payload, now, traceContext, cancellationToken));

    public ValueTask<WorkflowTask?> PollWorkflowTaskAsync(
        TenantScope scope, IReadOnlyCollection<string> buildIds, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        Faulty(nameof(PollWorkflowTaskAsync), () => inner.PollWorkflowTaskAsync(scope, buildIds, now, lease, cancellationToken));

    public async ValueTask<CompletionOutcome> CompleteWorkflowTaskAsync(
        WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, CancellationToken cancellationToken)
    {
        async ValueTask<CompletionOutcome> Call() => Count(await inner.CompleteWorkflowTaskAsync(task, newEvents, now, cancellationToken));
        if (TryDelay(Call))
        {
            return CompletionOutcome.Applied; // the worker believes it went through; delivery happens later
        }

        CompletionOutcome first = await Faulty(nameof(CompleteWorkflowTaskAsync), Call);
        if (ShouldDuplicate())
        {
            stats.DuplicateMessages++;
            CompletionOutcome second = await Call();
            if (first == CompletionOutcome.Applied && second == CompletionOutcome.Applied)
            {
                throw new InvariantViolationException("I2", $"Duplicate workflow-task completion for run {task.Run.RunId} was applied twice.");
            }
        }

        return first;
    }

    public ValueTask ReleaseWorkflowTaskAsync(WorkflowTask task, DateTimeOffset retryAt, DateTimeOffset now, bool countAttempt, CancellationToken cancellationToken) =>
        Faulty(nameof(ReleaseWorkflowTaskAsync), async () =>
        {
            await inner.ReleaseWorkflowTaskAsync(task, retryAt, now, countAttempt, cancellationToken);
            return true;
        }).AsNonGeneric();

    public ValueTask<CompletionOutcome> HeartbeatActivityTaskAsync(ActivityTask task, DateTimeOffset now, TimeSpan extendBy, CancellationToken cancellationToken) =>
        Faulty(nameof(HeartbeatActivityTaskAsync), async () => Count(await inner.HeartbeatActivityTaskAsync(task, now, extendBy, cancellationToken)));

    public ValueTask ReleaseActivityTaskAsync(ActivityTask task, DateTimeOffset retryAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(ReleaseActivityTaskAsync), async () =>
        {
            await inner.ReleaseActivityTaskAsync(task, retryAt, now, cancellationToken);
            return true;
        }).AsNonGeneric();

    public ValueTask<int> ReapExpiredLeasesAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(ReapExpiredLeasesAsync), () => inner.ReapExpiredLeasesAsync(scope, now, cancellationToken));

    public ValueTask<ActivityTask?> PollActivityTaskAsync(TenantScope scope, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        Faulty(nameof(PollActivityTaskAsync), () => inner.PollActivityTaskAsync(scope, now, lease, cancellationToken));

    public async ValueTask<CompletionOutcome> CompleteActivityTaskAsync(
        ActivityTask task, HistoryEvent outcome, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        async ValueTask<CompletionOutcome> Call() => Count(await inner.CompleteActivityTaskAsync(task, outcome, retryAt, now, cancellationToken));
        if (TryDelay(Call))
        {
            return CompletionOutcome.Applied;
        }

        CompletionOutcome first = await Faulty(nameof(CompleteActivityTaskAsync), Call);
        if (ShouldDuplicate())
        {
            stats.DuplicateMessages++;
            CompletionOutcome second = await Call();
            if (first == CompletionOutcome.Applied && second == CompletionOutcome.Applied)
            {
                throw new InvariantViolationException("I3", $"Duplicate activity completion {task.IdempotencyKey} was applied twice.");
            }
        }

        return first;
    }

    public ValueTask<IReadOnlyList<RunSummary>> ListRunsAsync(TenantScope scope, RunFilter filter, int limit, CancellationToken cancellationToken) =>
        Faulty(nameof(ListRunsAsync), () => inner.ListRunsAsync(scope, filter, limit, cancellationToken));

    public ValueTask<UsageSummary> GetUsageSummaryAsync(TenantScope scope, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        Faulty(nameof(GetUsageSummaryAsync), () => inner.GetUsageSummaryAsync(scope, firstDay, lastDay, cancellationToken));

    public ValueTask<PendingApprovalPage> GetPendingApprovalsAsync(TenantScope scope, int limit, PendingApprovalCursor? after, CancellationToken cancellationToken) =>
        Faulty(nameof(GetPendingApprovalsAsync), () => inner.GetPendingApprovalsAsync(scope, limit, after, cancellationToken));

    public ValueTask<int> ProjectAggregatesAsync(TenantScope scope, int maxRuns, CancellationToken cancellationToken) =>
        Faulty(nameof(ProjectAggregatesAsync), () => inner.ProjectAggregatesAsync(scope, maxRuns, cancellationToken));

    public ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken) =>
        Faulty(nameof(GetRunGroupsAsync), () => inner.GetRunGroupsAsync(scope, closedSince, cancellationToken));

    public ValueTask<IReadOnlyList<HistoryEvent>?> ExportHistoryAsync(
        RunKey run, string claimedActor, string? verifiedPrincipal, string reason, bool metadataOnly, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(ExportHistoryAsync), () => inner.ExportHistoryAsync(run, claimedActor, verifiedPrincipal, reason, metadataOnly, now, cancellationToken));

    public ValueTask<WorkBacklog?> GetBacklogAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(GetBacklogAsync), () => inner.GetBacklogAsync(scope, now, cancellationToken));

    public ValueTask<IReadOnlyList<StuckWorkflowTask>> GetStuckWorkflowTasksAsync(TenantScope scope, CancellationToken cancellationToken) =>
        inner.GetStuckWorkflowTasksAsync(scope, cancellationToken);

    public ValueTask<bool> ResumeWorkflowTaskAsync(RunKey run, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(ResumeWorkflowTaskAsync), () => inner.ResumeWorkflowTaskAsync(run, actor, now, cancellationToken));

    public ValueTask<bool> FailRunAsync(RunKey run, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(FailRunAsync), () => inner.FailRunAsync(run, reason, actor, now, cancellationToken));

    public ValueTask<bool> ResumeWorkflowTaskVerifiedAsync(RunKey run, Principal actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(ResumeWorkflowTaskVerifiedAsync), () => inner.ResumeWorkflowTaskVerifiedAsync(run, actor, now, cancellationToken));

    public ValueTask<bool> FailRunVerifiedAsync(RunKey run, string reason, Principal actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(FailRunVerifiedAsync), () => inner.FailRunVerifiedAsync(run, reason, actor, now, cancellationToken));

    public ValueTask<bool> CancelRunAsync(RunKey run, string reason, Principal actor, string traceContext, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(CancelRunAsync), () => inner.CancelRunAsync(run, reason, actor, traceContext, now, cancellationToken));

    public ValueTask<IReadOnlyList<AuditEntry>> GetAuditLogAsync(TenantScope scope, CancellationToken cancellationToken) =>
        inner.GetAuditLogAsync(scope, cancellationToken);

    public ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        Faulty(nameof(FireDueTimersAsync), () => inner.FireDueTimersAsync(scope, now, cancellationToken));

    public ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken) =>
        inner.GetHistoryAsync(run, cancellationToken);

    public ValueTask<HistoryPage> GetHistoryAfterAsync(RunKey run, long afterSequence, int limit, CancellationToken cancellationToken) =>
        inner.GetHistoryAfterAsync(run, afterSequence, limit, cancellationToken);

    public ValueTask<HistoryPage> GetHistoryTailAsync(RunKey run, int count, CancellationToken cancellationToken) =>
        inner.GetHistoryTailAsync(run, count, cancellationToken);

    public ValueTask<bool> WaitForHistoryAsync(RunKey run, long afterSequence, TimeSpan wait, CancellationToken cancellationToken) =>
        inner.WaitForHistoryAsync(run, afterSequence, wait, cancellationToken);

    public ValueTask<RunKey?> GetCurrentRunAsync(TenantScope scope, string workflowId, CancellationToken cancellationToken) =>
        inner.GetCurrentRunAsync(scope, workflowId, cancellationToken);

    public ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken) =>
        inner.GetRunStateAsync(run, cancellationToken);

    /// <summary>Delivers one random delayed message (possibly long after its lease expired).</summary>
    public async ValueTask<bool> DeliverOneDelayedAsync()
    {
        if (_delayed.Count == 0)
        {
            return false;
        }

        int i = rng.Next(_delayed.Count);
        Func<ValueTask> message = _delayed[i];
        _delayed.RemoveAt(i);
        await message();
        return true;
    }

    public async ValueTask DeliverAllDelayedAsync()
    {
        while (await DeliverOneDelayedAsync())
        {
        }
    }

    private async ValueTask<T> Faulty<T>(string operation, Func<ValueTask<T>> call)
    {
        long index = OperationCount++;
        FaultKind kind = Decide(index);
        if (kind == FaultKind.Before)
        {
            stats.Crashes++;
            throw new SimulatedFaultException(operation, kind);
        }

        T result = await call();
        if (kind == FaultKind.After)
        {
            stats.Crashes++;
            throw new SimulatedFaultException(operation, kind);
        }

        return result;
    }

    private FaultKind Decide(long index)
    {
        if (ForcedFault is not null)
        {
            return ForcedFault(index);
        }

        if (!FaultsEnabled || !rng.Chance(options.CrashProbability))
        {
            return FaultKind.None;
        }

        return rng.Chance(0.5) ? FaultKind.Before : FaultKind.After;
    }

    private bool TryDelay(Func<ValueTask<CompletionOutcome>> call)
    {
        if (!FaultsEnabled || ForcedFault is not null || !rng.Chance(options.DelayProbability))
        {
            return false;
        }

        stats.DelayedMessages++;
        _delayed.Add(async () => await call());
        return true;
    }

    private bool ShouldDuplicate() => FaultsEnabled && ForcedFault is null && rng.Chance(options.DuplicateProbability);

    private CompletionOutcome Count(CompletionOutcome outcome)
    {
        if (outcome == CompletionOutcome.StaleLease)
        {
            stats.StaleRejections++;
        }
        else if (outcome == CompletionOutcome.SequenceConflict)
        {
            stats.SequenceConflicts++;
        }

        return outcome;
    }
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsNonGeneric<T>(this ValueTask<T> task) => await task;
}
