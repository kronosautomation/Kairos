namespace Kairos;

/// <summary>Start/signal/inspect runs. Run ids come from an injectable factory so simulations stay reproducible.</summary>
public sealed class KairosClient
{
    public const string DefaultBuildId = "default";

    private readonly IKairosStore _store;
    private readonly TimeProvider _time;
    private readonly Func<Guid> _newRunId;

    public KairosClient(IKairosStore store, TimeProvider time, Func<Guid>? newRunId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _newRunId = newRunId ?? (() => Guid.CreateVersion7(time.GetUtcNow()));
    }

    public ValueTask<StartRunResult> StartAsync(
        TenantScope scope, string workflowId, string workflowType, byte[] input, string buildId = DefaultBuildId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        ArgumentNullException.ThrowIfNull(input);
        var run = new RunKey(scope.TenantId, scope.NamespaceId, _newRunId());
        return _store.StartRunAsync(new StartRunRequest(run, workflowId, workflowType, buildId, input, _time.GetUtcNow()), cancellationToken);
    }

    public ValueTask<bool> SignalAsync(RunKey run, string signalName, byte[] payload, CancellationToken cancellationToken = default) =>
        _store.SignalRunAsync(run, signalName, payload, _time.GetUtcNow(), cancellationToken);

    public ValueTask<RunState?> GetStateAsync(RunKey run, CancellationToken cancellationToken = default) =>
        _store.GetRunStateAsync(run, cancellationToken);

    public ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken = default) =>
        _store.GetHistoryAsync(run, cancellationToken);
}
