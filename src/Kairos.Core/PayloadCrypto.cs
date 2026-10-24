using System.Text;

namespace Kairos;

/// <summary>
/// Applies an <see cref="IPayloadProtector"/> to history events: seals every payload field of an event before it is sent to the store,
/// and opens them after the hash chain has been verified. Each field is bound to its run, field name and id, so a ciphertext copied to
/// another event, field or run does not open. Metadata (types, ids, names, timestamps) stays readable so the server can route and index.
/// </summary>
public sealed class PayloadCrypto(IPayloadProtector protector)
{
    private const string TextPrefix = "enc:";

    public static byte[] Context(Guid runId, string field, long id = 0) =>
        Encoding.UTF8.GetBytes($"kairos/v1|{runId:N}|{field}|{id}");

    public async ValueTask<byte[]> SealAsync(TenantScope scope, Guid runId, string field, long id, byte[] plaintext, CancellationToken ct) =>
        await protector.ProtectAsync(scope, plaintext, Context(runId, field, id), ct).ConfigureAwait(false);

    public async ValueTask<byte[]> OpenAsync(TenantScope scope, Guid runId, string field, long id, byte[] sealedBytes, CancellationToken ct) =>
        await protector.UnprotectAsync(scope, sealedBytes, Context(runId, field, id), ct).ConfigureAwait(false);

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
                return TextPrefix + Convert.ToBase64String(await SealAsync(scope, run, field, id, Encoding.UTF8.GetBytes(value), ct).ConfigureAwait(false));
            }

            // Unprefixed text is store-written diagnostics (e.g. a lease-expiry record); everything a worker writes is prefixed.
            return value.StartsWith(TextPrefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(await OpenAsync(scope, run, field, id, FromBase64(value[TextPrefix.Length..]), ct).ConfigureAwait(false))
                : value;
        }

        return e switch
        {
            WorkflowStarted s => s with { Input = await Bytes(s.Input, "start", 0).ConfigureAwait(false) },
            ActivityScheduled s => s with { Input = await Bytes(s.Input, "act-in", s.ActivityId).ConfigureAwait(false) },
            ActivityCompleted c => c with { Result = await Bytes(c.Result, "act-out", c.ActivityId).ConfigureAwait(false) },
            ActivityFailed f when f.ErrorType != StoreRules.LeaseExpiredError =>
                f with { Message = await Text(f.Message, $"act-fail-{f.Attempt}", f.ActivityId).ConfigureAwait(false) },
            SignalReceived s => s with { Payload = await Bytes(s.Payload, "signal:" + s.SignalName, 0).ConfigureAwait(false) },
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
