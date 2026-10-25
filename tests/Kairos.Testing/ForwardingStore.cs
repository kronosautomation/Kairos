namespace Kairos.Testing;

/// <summary>Base for test decorators that break one thing in an otherwise correct store.</summary>
public abstract class ForwardingStore(IKairosStore inner) : IKairosStore
{
    public virtual ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken) =>
        inner.StartRunAsync(request, cancellationToken);

    public virtual ValueTask<bool> SignalRunAsync(RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.SignalRunAsync(run, signalName, signalId, payload, now, cancellationToken);

    public virtual ValueTask<bool> SignalRunTracedAsync(
        RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, string traceContext, CancellationToken cancellationToken) =>
        traceContext.Length == 0
            ? SignalRunAsync(run, signalName, signalId, payload, now, cancellationToken) // a decorator that overrides the plain call still sees untraced signals
            : inner.SignalRunTracedAsync(run, signalName, signalId, payload, now, traceContext, cancellationToken);

    public virtual ValueTask<WorkflowTask?> PollWorkflowTaskAsync(
        TenantScope scope, IReadOnlyCollection<string> buildIds, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        inner.PollWorkflowTaskAsync(scope, buildIds, now, lease, cancellationToken);

    public virtual ValueTask<CompletionOutcome> CompleteWorkflowTaskAsync(
        WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.CompleteWorkflowTaskAsync(task, newEvents, now, cancellationToken);

    public virtual ValueTask ReleaseWorkflowTaskAsync(WorkflowTask task, DateTimeOffset retryAt, DateTimeOffset now, bool countAttempt, CancellationToken cancellationToken) =>
        inner.ReleaseWorkflowTaskAsync(task, retryAt, now, countAttempt, cancellationToken);

    public virtual ValueTask<CompletionOutcome> HeartbeatActivityTaskAsync(ActivityTask task, DateTimeOffset now, TimeSpan extendBy, CancellationToken cancellationToken) =>
        inner.HeartbeatActivityTaskAsync(task, now, extendBy, cancellationToken);

    public virtual ValueTask ReleaseActivityTaskAsync(ActivityTask task, DateTimeOffset retryAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.ReleaseActivityTaskAsync(task, retryAt, now, cancellationToken);

    public virtual ValueTask<int> ReapExpiredLeasesAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.ReapExpiredLeasesAsync(scope, now, cancellationToken);

    public virtual ValueTask<ActivityTask?> PollActivityTaskAsync(TenantScope scope, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        inner.PollActivityTaskAsync(scope, now, lease, cancellationToken);

    public virtual ValueTask<CompletionOutcome> CompleteActivityTaskAsync(
        ActivityTask task, HistoryEvent outcome, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.CompleteActivityTaskAsync(task, outcome, retryAt, now, cancellationToken);

    public virtual ValueTask<IReadOnlyList<RunSummary>> ListRunsAsync(TenantScope scope, RunFilter filter, int limit, CancellationToken cancellationToken) =>
        inner.ListRunsAsync(scope, filter, limit, cancellationToken);

    public virtual ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken) =>
        inner.GetRunGroupsAsync(scope, closedSince, cancellationToken);

    public virtual ValueTask<WorkBacklog?> GetBacklogAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.GetBacklogAsync(scope, now, cancellationToken);

    public virtual ValueTask<IReadOnlyList<StuckWorkflowTask>> GetStuckWorkflowTasksAsync(TenantScope scope, CancellationToken cancellationToken) =>
        inner.GetStuckWorkflowTasksAsync(scope, cancellationToken);

    public virtual ValueTask<bool> ResumeWorkflowTaskAsync(RunKey run, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.ResumeWorkflowTaskAsync(run, actor, now, cancellationToken);

    public virtual ValueTask<bool> FailRunAsync(RunKey run, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.FailRunAsync(run, reason, actor, now, cancellationToken);

    public virtual ValueTask<IReadOnlyList<AuditEntry>> GetAuditLogAsync(TenantScope scope, CancellationToken cancellationToken) =>
        inner.GetAuditLogAsync(scope, cancellationToken);

    public virtual ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.FireDueTimersAsync(scope, now, cancellationToken);

    public virtual ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken) =>
        inner.GetHistoryAsync(run, cancellationToken);

    public virtual ValueTask<RunKey?> GetCurrentRunAsync(TenantScope scope, string workflowId, CancellationToken cancellationToken) =>
        inner.GetCurrentRunAsync(scope, workflowId, cancellationToken);

    public virtual ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken) =>
        inner.GetRunStateAsync(run, cancellationToken);
}
