using System.Diagnostics;

namespace Kairos;

/// <summary>Start/signal/inspect runs. Run ids come from an injectable factory so simulations stay reproducible.</summary>
/// <summary>Signal names the engine keeps for itself.</summary>
public static class ReservedSignals
{
    /// <summary>Approval decisions are signals named <c>kairos.approval.&lt;approval id&gt;</c>. Only <see cref="KairosClient.DecideApprovalAsync"/> sends them.</summary>
    public const string ApprovalPrefix = "kairos.approval.";
}

public sealed class KairosClient
{
    public const string DefaultBuildId = "default";

    private readonly IKairosStore _store;
    private readonly TimeProvider _time;
    private readonly Func<Guid> _newRunId;

    private readonly PayloadCrypto? _crypto;
    private readonly Func<string> _newSignalId;

    public KairosClient(IKairosStore store, TimeProvider time, Func<Guid>? newRunId = null, IPayloadProtector? payloadProtector = null, Func<string>? newSignalId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
        _newRunId = newRunId ?? (() => Guid.CreateVersion7(time.GetUtcNow()));
        _crypto = payloadProtector is null ? null : new PayloadCrypto(payloadProtector);
        _newSignalId = newSignalId ?? (() => Guid.CreateVersion7(time.GetUtcNow()).ToString("N"));
    }

    public async ValueTask<StartRunResult> StartAsync(
        TenantScope scope, string workflowId, string workflowType, byte[] input, string buildId = DefaultBuildId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        ArgumentNullException.ThrowIfNull(input);
        var run = new RunKey(scope.TenantId, scope.NamespaceId, _newRunId());

        // A child of whatever is current (for example the HTTP request that asked for the run). Its context is stored in the run's history,
        // so the run's tasks continue this trace wherever and whenever they run.
        using Activity? span = KairosTelemetry.Source.StartActivity("kairos.run.start", ActivityKind.Producer);
        if (span is not null)
        {
            span.SetTag(KairosTags.TenantId, scope.TenantId.ToString("D"));
            span.SetTag(KairosTags.NamespaceId, scope.NamespaceId.ToString("D"));
            span.SetTag(KairosTags.WorkflowType, workflowType);
            span.SetTag(KairosTags.WorkflowId, workflowId);
            span.SetTag(KairosTags.BuildId, buildId);
            span.SetTag(KairosTags.PayloadBytes, input.Length);
        }

        string trace = span is null ? KairosTelemetry.CurrentTraceContext() : KairosTelemetry.ContextOf(span);
        try
        {
            byte[] sent = _crypto is null ? input : await _crypto.SealAsync(scope, run.RunId, "start", 0, input, cancellationToken).ConfigureAwait(false);
            StartRunResult result = await _store.StartRunAsync(
                new StartRunRequest(run, workflowId, workflowType, buildId, sent, _time.GetUtcNow(), trace), cancellationToken).ConfigureAwait(false);
            span?.SetTag(KairosTags.RunId, result.RunId.ToString("D"));
            span?.SetTag("kairos.run.deduplicated", !result.Started);
            if (result.Started)
            {
                KairosMetrics.RunsStarted.Add(1, new KeyValuePair<string, object?>("workflow.type", workflowType));
            }

            return result;
        }
        catch (Exception ex)
        {
            KairosTelemetry.Fail(span, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>Delivers a signal to a running workflow under a fresh signal id. False when the run is closed or unknown.</summary>
    /// <param name="run">The run to signal.</param>
    /// <param name="signalName">The name the workflow waits on.</param>
    /// <param name="payload">The signal payload (sealed on the client when encryption is on).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public ValueTask<bool> SignalAsync(RunKey run, string signalName, byte[] payload, CancellationToken cancellationToken) =>
        SignalAsync(run, signalName, payload, signalId: null, cancellationToken);

    /// <summary>Delivers a signal to a running workflow. False when the run is closed or unknown.</summary>
    /// <param name="run">The run to signal.</param>
    /// <param name="signalName">The name the workflow waits on.</param>
    /// <param name="payload">The signal payload (sealed on the client when encryption is on).</param>
    /// <param name="signalId">
    /// Identifies this delivery. Retrying with the same id never signals twice; omit it for a fresh id. With encryption on the id also
    /// authenticates the payload, so two signals of one name cannot be swapped.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<bool> SignalAsync(
        RunKey run, string signalName, byte[] payload, string? signalId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signalName);
        if (signalName.StartsWith(ReservedSignals.ApprovalPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Signal names starting with '{ReservedSignals.ApprovalPrefix}' are reserved for approval decisions; use DecideApprovalAsync.", nameof(signalName));
        }

        return await SendSignalAsync(run, signalName, payload, signalId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers an approval decision. This is the only way to send a signal in the reserved approval namespace, and the operation a server
    /// authorizes as <see cref="Operation.DecideApproval"/> (a separate role grant from <see cref="Operation.SignalRun"/>).
    /// </summary>
    /// <param name="run">The run waiting on the approval.</param>
    /// <param name="approvalId">The approval's id.</param>
    /// <param name="payload">The decision, including the evidence a verifier checks.</param>
    /// <param name="signalId">Identifies this delivery; a retry with the same id signals once.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public ValueTask<bool> DecideApprovalAsync(RunKey run, string approvalId, byte[] payload, string signalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentException.ThrowIfNullOrEmpty(signalId);
        return SendSignalAsync(run, ReservedSignals.ApprovalPrefix + approvalId, payload, signalId, cancellationToken);
    }

    private async ValueTask<bool> SendSignalAsync(RunKey run, string signalName, byte[] payload, string? signalId, CancellationToken cancellationToken)
    {
        string id = signalId ?? _newSignalId();
        bool approval = signalName.StartsWith(ReservedSignals.ApprovalPrefix, StringComparison.Ordinal);
        using Activity? span = KairosTelemetry.Source.StartActivity(approval ? "kairos.approval.decide" : "kairos.signal.send", ActivityKind.Producer);
        if (span is not null)
        {
            span.SetTag(KairosTags.TenantId, run.TenantId.ToString("D"));
            span.SetTag(KairosTags.NamespaceId, run.NamespaceId.ToString("D"));
            span.SetTag(KairosTags.RunId, run.RunId.ToString("D"));
            span.SetTag(approval ? KairosTags.ApprovalId : KairosTags.SignalName, approval ? signalName[ReservedSignals.ApprovalPrefix.Length..] : signalName);
            span.SetTag(KairosTags.PayloadBytes, payload.Length);
        }

        string trace = span is null ? KairosTelemetry.CurrentTraceContext() : KairosTelemetry.ContextOf(span);
        try
        {
            byte[] sent = _crypto is null
                ? payload
                : await _crypto.SealAsync(run.Scope, run.RunId, PayloadCrypto.SignalField(signalName, id), 0, payload, cancellationToken).ConfigureAwait(false);
            return trace.Length == 0
                ? await _store.SignalRunAsync(run, signalName, id, sent, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false)
                : await _store.SignalRunTracedAsync(run, signalName, id, sent, _time.GetUtcNow(), trace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            KairosTelemetry.Fail(span, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// What the model steps of a run consumed so far, summed from the usage recorded in its history (readable metadata: it works with client-side encryption on and needs no key).
    /// Each step is counted once, however often the run was replayed. A run that continued as new reports only its own history.
    /// </summary>
    public async ValueTask<RunUsage> GetUsageAsync(RunKey run, CancellationToken cancellationToken = default) =>
        RunUsage.From(await _store.GetHistoryAsync(run, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports a run's history for a <b>verified</b> principal (see <see cref="HistoryExporter.ExportAsync"/>): operator or above in the run's namespace, a reason, one audited step.
    /// The audit entry names the principal; the database login and address come from the database. Null when the run does not exist in the principal's scope.
    /// </summary>
    public async Task<HistoryExport?> ExportHistoryAsync(
        Principal verifiedPrincipal, RunKey run, string reason, bool metadataOnly = false, CancellationToken cancellationToken = default) =>
        await HistoryExporter.ExportAsync(_store, verifiedPrincipal, run, reason, metadataOnly, _time, "client", cancellationToken).ConfigureAwait(false);

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
