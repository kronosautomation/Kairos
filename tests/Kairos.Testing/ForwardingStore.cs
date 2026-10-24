namespace Kairos.Testing;

/// <summary>Base for test decorators that break one thing in an otherwise correct store.</summary>
public abstract class ForwardingStore(IKairosStore inner) : IKairosStore
{
    public virtual ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken) =>
        inner.StartRunAsync(request, cancellationToken);

    public virtual ValueTask<bool> SignalRunAsync(RunKey run, string signalName, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.SignalRunAsync(run, signalName, payload, now, cancellationToken);

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

    public virtual ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.FireDueTimersAsync(scope, now, cancellationToken);

    public virtual ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken) =>
        inner.GetHistoryAsync(run, cancellationToken);

    public virtual ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken) =>
        inner.GetRunStateAsync(run, cancellationToken);
}
