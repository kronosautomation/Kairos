namespace Kairos;

/// <summary>Start/signal/inspect runs. Run ids come from an injectable factory so simulations stay reproducible.</summary>
public sealed class KairosClient
{
    public const string DefaultBuildId = "default";

    private readonly IKairosStore _store;
    private readonly TimeProvider _time;
    private readonly Func<Guid> _newRunId;

    private readonly PayloadCrypto? _crypto;

    public KairosClient(IKairosStore store, TimeProvider time, Func<Guid>? newRunId = null, IPayloadProtector? payloadProtector = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _newRunId = newRunId ?? (() => Guid.CreateVersion7(time.GetUtcNow()));
        _crypto = payloadProtector is null ? null : new PayloadCrypto(payloadProtector);
    }

    public async ValueTask<StartRunResult> StartAsync(
        TenantScope scope, string workflowId, string workflowType, byte[] input, string buildId = DefaultBuildId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        ArgumentNullException.ThrowIfNull(input);
        var run = new RunKey(scope.TenantId, scope.NamespaceId, _newRunId());
        byte[] sent = _crypto is null ? input : await _crypto.SealAsync(scope, run.RunId, "start", 0, input, cancellationToken).ConfigureAwait(false);
        return await _store.StartRunAsync(new StartRunRequest(run, workflowId, workflowType, buildId, sent, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> SignalAsync(RunKey run, string signalName, byte[] payload, CancellationToken cancellationToken = default)
    {
        byte[] sent = _crypto is null
            ? payload
            : await _crypto.SealAsync(run.Scope, run.RunId, "signal:" + signalName, 0, payload, cancellationToken).ConfigureAwait(false);
        return await _store.SignalRunAsync(run, signalName, sent, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<RunState?> GetStateAsync(RunKey run, CancellationToken cancellationToken = default) =>
        _store.GetRunStateAsync(run, cancellationToken);

    /// <summary>The history exactly as stored: with encryption on, payloads are ciphertext.</summary>
    public ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken = default) =>
        _store.GetHistoryAsync(run, cancellationToken);

    /// <summary>The history with payloads opened by this client's protector (verifies the hash chain over the stored form first).</summary>
    public async ValueTask<IReadOnlyList<HistoryEvent>> GetOpenedHistoryAsync(RunKey run, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HistoryEvent> stored = await _store.GetHistoryAsync(run, cancellationToken).ConfigureAwait(false);
        if (_crypto is null)
        {
            return stored;
        }

        HashChain.Verify(stored);
        return await _crypto.OpenHistoryAsync(stored, cancellationToken).ConfigureAwait(false);
    }
}
