using System.Text;

namespace Kairos;

/// <summary>
/// Applies an <see cref="IPayloadProtector"/> to history events: seals every payload field of an event before it is sent to the store,
/// and opens them after the hash chain has been verified. Each field is bound to its run, field name and id, so a ciphertext copied to
/// another event, field or run does not open. Metadata (types, ids, names, timestamps) stays readable so the server can route and index.
/// <para>
/// With blob offload on, a payload longer than the threshold is sealed, put in the blob store (which therefore only ever holds ciphertext, bound to the same run, field and id), and the field in the history is
/// the <b>sealed reference</b> to it: the blob's SHA-256 and length, sealed like any payload, so the server learns neither that a payload was offloaded nor what it hashes to. The reference is inside the event, so the
/// hash chain covers it; reading verifies the blob's length and SHA-256 against it again, here, whatever the blob store claims. The blob is stored before the event is, so a crash leaves at worst an unreferenced blob,
/// never a reference to nothing. A blob that is missing or does not match fails the task with <see cref="PayloadBlobException"/>; it is never replaced by an empty or partial payload.
/// </para>
/// </summary>
public sealed class PayloadCrypto
{
    private const string TextPrefix = "enc:";
    private readonly IPayloadProtector _protector;
    private readonly BlobOffloadOptions? _offload;

    public PayloadCrypto(IPayloadProtector protector, BlobOffloadOptions? offload = null)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
        _offload = offload?.Validated();
    }

    /// <summary>The authentication field of a signal payload: name and the client-generated id, so equal-named signals are not interchangeable.</summary>
    public static string SignalField(string signalName, string signalId) => $"signal:{signalName}:{signalId}";

    public static byte[] Context(Guid runId, string field, long id = 0) =>
        Encoding.UTF8.GetBytes($"kairos/v1|{runId:N}|{field}|{id}");

    public async ValueTask<byte[]> SealAsync(TenantScope scope, Guid runId, string field, long id, byte[] plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] context = Context(runId, field, id);
        byte[] body = BlobReferences.Escape(plaintext);
        byte[] sealedBody = await _protector.ProtectAsync(scope, body, context, ct).ConfigureAwait(false);
        if (_offload is null || plaintext.Length <= _offload.ThresholdBytes)
        {
            return sealedBody;
        }

        // The blob is the ciphertext, stored before anything refers to it. What goes into the history is a small sealed reference.
        BlobRef reference = await _offload.Store.PutAsync(scope, sealedBody, ct).ConfigureAwait(false);
        return await _protector.ProtectAsync(scope, BlobReferences.Encode(reference), context, ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> OpenAsync(TenantScope scope, Guid runId, string field, long id, byte[] sealedBytes, CancellationToken ct)
    {
        byte[] context = Context(runId, field, id);
        byte[] body = await _protector.UnprotectAsync(scope, sealedBytes, context, ct).ConfigureAwait(false);
        if (!BlobReferences.TryDecode(body, out BlobRef reference))
        {
            return BlobReferences.Unescape(body);
        }

        if (_offload is null)
        {
            throw new PayloadBlobException("The history refers to a payload in the blob store, and blob offload is not configured here.");
        }

        byte[]? blob;
        try
        {
            blob = await _offload.Store.GetAsync(scope, reference, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            throw new BlobMismatchException(reference, e);
        }

        if (blob is null)
        {
            throw new BlobMissingException(reference);
        }

        // The store was asked to verify; this does not rely on it.
        if (blob.LongLength != reference.Length || !string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blob)), reference.Sha256Hex, StringComparison.Ordinal))
        {
            throw new BlobMismatchException(reference);
        }

        byte[] opened = await _protector.UnprotectAsync(scope, blob, context, ct).ConfigureAwait(false);
        return BlobReferences.TryDecode(opened, out _) ? throw new PayloadBlobException("A blob holds a reference to another blob; that is never written.") : BlobReferences.Unescape(opened);
    }

    /// <summary>Opens every payload in a verified history. The returned events keep their original hash fields (now stale: verify before opening).</summary>
    public async ValueTask<IReadOnlyList<HistoryEvent>> OpenHistoryAsync(IReadOnlyList<HistoryEvent> history, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(history);
        var opened = new HistoryEvent[history.Count];
        for (int i = 0; i < opened.Length; i++)
        {
            opened[i] = await TransformAsync(history[i], open: true, ct).ConfigureAwait(false);
        }

        return opened;
    }

    /// <summary>Seals the payload fields of an event about to be appended.</summary>
    public ValueTask<HistoryEvent> SealEventAsync(HistoryEvent plain, CancellationToken ct) => TransformAsync(plain, open: false, ct);

    private async ValueTask<HistoryEvent> TransformAsync(HistoryEvent e, bool open, CancellationToken ct)
    {
        var scope = new TenantScope(e.TenantId, e.NamespaceId);
        Guid run = e.RunId;

        async ValueTask<byte[]> Bytes(byte[] value, string field, long id) =>
            open ? await OpenAsync(scope, run, field, id, value, ct).ConfigureAwait(false) : await SealAsync(scope, run, field, id, value, ct).ConfigureAwait(false);

        async ValueTask<string> Text(string value, string field, long id)
        {
            if (!open)
            {
                return TextPrefix + Convert.ToBase64String(await _protector.ProtectAsync(scope, Encoding.UTF8.GetBytes(value), Context(run, field, id), ct).ConfigureAwait(false));
            }

            // Everything a worker writes is sealed. Unsealed text is refused: accepting it would let a compromised server author
            // failure messages that workflow code (and the models behind agent workflows) would trust.
            return value.StartsWith(TextPrefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(await _protector.UnprotectAsync(scope, FromBase64(value[TextPrefix.Length..]), Context(run, field, id), ct).ConfigureAwait(false))
                : throw new PayloadProtectionException();
        }

        return e switch
        {
            WorkflowStarted s => s with { Input = await Bytes(s.Input, "start", 0).ConfigureAwait(false) },
            ActivityScheduled s => s with { Input = await Bytes(s.Input, "act-in", s.ActivityId).ConfigureAwait(false) },
            ActivityCompleted c => c with { Result = await Bytes(c.Result, "act-out", c.ActivityId).ConfigureAwait(false) },
            ActivityFailed f when f.ErrorType == StoreRules.LeaseExpiredError =>
                // The one record the store writes itself, with fixed text. A different message under this error type is a forgery.
                open && !string.Equals(f.Message, StoreRules.LeaseExpiredMessage, StringComparison.Ordinal) ? throw new PayloadProtectionException() : f,
            ActivityFailed f =>
                f with { Message = await Text(f.Message, $"act-fail-{f.Attempt}", f.ActivityId).ConfigureAwait(false) },
            // The cancel request written by the store itself has no payload to seal; an empty one is accepted as is. It grants nothing a server cannot already do (it can close a run).
            SignalReceived { SignalName: ReservedSignals.Cancel, Payload.Length: 0 } s when open => s,
            SignalReceived s => s with { Payload = await Bytes(s.Payload, SignalField(s.SignalName, s.SignalId), 0).ConfigureAwait(false) },
            WorkflowCompleted c => c with { Result = await Bytes(c.Result, "result", 0).ConfigureAwait(false) },
            WorkflowFailed f => f with { Message = await Text(f.Message, "fail", 0).ConfigureAwait(false) },
            WorkflowContinued c => c with { Input = await Bytes(c.Input, "continue", 0).ConfigureAwait(false) },
            _ => e,
        };
    }

    private static byte[] FromBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new PayloadProtectionException("The payload could not be opened.", ex);
        }
    }
}
