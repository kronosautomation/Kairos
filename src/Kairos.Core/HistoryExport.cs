namespace Kairos;

/// <summary>An exported run: the history, how it was exported, and the file text.</summary>
/// <param name="Run">The run exported.</param>
/// <param name="History">The events as exported (payloads removed when <paramref name="MetadataOnly"/>).</param>
/// <param name="MetadataOnly">True when payloads and failure texts were removed.</param>
/// <param name="FileText">The history file (<see cref="HistoryFile"/>).</param>
public sealed record HistoryExport(RunKey Run, IReadOnlyList<HistoryEvent> History, bool MetadataOnly, string FileText);

/// <summary>
/// Copies one run's history out of the system for a person: to look at it, or to replay it under a debugger (<c>kairos history export</c>,
/// <c>KairosReplayer</c>). An export is a privileged read. It needs <see cref="Operation.ExportHistory"/>, a reason, and it is written to the audit log with who and why in
/// the same transaction that reads the history. The history is verified before it is handed over, and the hash of its last event is written to the audit log beside the reason, so a file can later be compared with the system.
/// </summary>
public static class HistoryExporter
{
    /// <summary>
    /// Exports <paramref name="run"/> as <paramref name="caller"/>.
    /// </summary>
    /// <param name="store">The store holding the run.</param>
    /// <param name="caller">Who is exporting. Their role must allow <see cref="Operation.ExportHistory"/> in the run's scope (operator and above).</param>
    /// <param name="run">The run to export.</param>
    /// <param name="reason">Why. Required, recorded in the audit log, at most <see cref="StoreRules.MaxReasonLength"/> characters. Say why; do not paste data.</param>
    /// <param name="metadataOnly">Remove payloads (and failure texts) from the file. What remains is event types, sequence, names, ids and timestamps, plus a digest of each removed field; the file is tamper-evident through a digest chain but cannot be replayed.</param>
    /// <param name="time">The clock stamped into the audit entry.</param>
    /// <param name="recordedBy">A tag written into the file header.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The export, or null when the run does not exist in the caller's scope.</returns>
    /// <exception cref="UnauthorizedAccessException">The caller's role does not allow exporting in this scope. Nothing was read or recorded.</exception>
    /// <exception cref="ArgumentException">The reason is missing or too long.</exception>
    /// <exception cref="HistoryIntegrityException">The stored history does not verify. The attempt is in the audit log; no file is produced.</exception>
    public static async Task<HistoryExport?> ExportAsync(
        IKairosStore store, Principal caller, RunKey run, string reason, bool metadataOnly, TimeProvider time, string recordedBy = "export", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(time);
        if (!AccessPolicy.IsAllowed(caller, Operation.ExportHistory, run.Scope))
        {
            throw new UnauthorizedAccessException($"A {caller.Role} may not export histories in this namespace.");
        }

        StoreRules.ValidateExport(caller.Subject, reason);
        IReadOnlyList<HistoryEvent>? stored = await store.ExportHistoryAsync(run, caller.Subject, reason, metadataOnly, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        HashChain.Verify(stored); // before anything leaves: a file must never vouch for a history that does not verify
        IReadOnlyList<HistoryEvent> exported = metadataOnly ? HistoryRedaction.MetadataOnly(stored) : stored;
        string note = $"exported run {run.RunId:D}" + (metadataOnly ? " (metadata only)" : "");
        string text = metadataOnly ? HistoryFile.SerializeMetadataOnly(stored, recordedBy, note) : HistoryFile.Serialize(stored, recordedBy, note);
        return new HistoryExport(run, exported, metadataOnly, text);
    }
}

/// <summary>Removes what is not metadata from a history, keeping the hashes it had, and binds what was removed with digests.</summary>
public static class HistoryRedaction
{
    private const string DigestChainLabel = "kairos-metadata-chain-v1";

    /// <summary>
    /// A copy of <paramref name="history"/> with every payload emptied and every failure text and termination reason blanked. Types, sequence numbers, ids, names,
    /// timestamps and the stored hashes remain.
    /// </summary>
    public static IReadOnlyList<HistoryEvent> MetadataOnly(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var result = new HistoryEvent[history.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = history[i] switch
            {
                WorkflowStarted e => e with { Input = [] },
                ActivityScheduled e => e with { Input = [] },
                ActivityCompleted e => e with { Result = [] },
                ActivityFailed e => e with { Message = "" },
                SignalReceived e => e with { Payload = [] },
                WorkflowCompleted e => e with { Result = [] },
                WorkflowFailed e => e with { Message = "" },
                WorkflowContinued e => e with { Input = [] },
                RunTerminated e => e with { Reason = "" },
                HistoryEvent other => other,
            };
        }

        return result;
    }

    /// <summary>
    /// SHA-256 of the one field <see cref="MetadataOnly"/> removes from <paramref name="e"/> (its payload bytes, or the UTF-8 text of a failure message or reason);
    /// empty for events that have none. A digest says nothing about the content but pins it: it cannot be changed without the digest chain noticing.
    /// </summary>
    public static byte[] PayloadDigest(HistoryEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        byte[]? content = e switch
        {
            WorkflowStarted x => x.Input,
            ActivityScheduled x => x.Input,
            ActivityCompleted x => x.Result,
            ActivityFailed x => System.Text.Encoding.UTF8.GetBytes(x.Message),
            SignalReceived x => x.Payload,
            WorkflowCompleted x => x.Result,
            WorkflowFailed x => System.Text.Encoding.UTF8.GetBytes(x.Message),
            WorkflowContinued x => x.Input,
            RunTerminated x => System.Text.Encoding.UTF8.GetBytes(x.Reason),
            _ => null,
        };
        return content is null ? [] : System.Security.Cryptography.SHA256.HashData(content);
    }

    /// <summary>
    /// The digest chain of a metadata-only export: <c>C[i] = SHA-256(C[i-1] || label || redacted event i || its stored previous hash || its stored hash || payload digest i)</c>,
    /// starting from 32 zero bytes. It covers every metadata field, the hashes the system stored and the digests of what was removed, so editing any of them, or reordering,
    /// dropping or inserting an event, breaks it. (The stored chain itself cannot be recomputed without the payloads, which is why this second chain exists.)
    /// </summary>
    /// <param name="redacted">The events as <see cref="MetadataOnly"/> returns them.</param>
    /// <param name="digests">One <see cref="PayloadDigest"/> per event.</param>
    public static IReadOnlyList<byte[]> DigestChain(IReadOnlyList<HistoryEvent> redacted, IReadOnlyList<byte[]> digests)
    {
        ArgumentNullException.ThrowIfNull(redacted);
        ArgumentNullException.ThrowIfNull(digests);
        if (redacted.Count != digests.Count)
        {
            throw new ArgumentException("One digest per event is needed.", nameof(digests));
        }

        var chain = new byte[redacted.Count][];
        byte[] previous = new byte[HashChain.HashSize];
        for (int i = 0; i < chain.Length; i++)
        {
            HistoryEvent e = redacted[i];
            byte[] label = System.Text.Encoding.ASCII.GetBytes(DigestChainLabel);
            byte[] body = HistoryEventCodec.Encode(e);
            chain[i] = System.Security.Cryptography.SHA256.HashData([.. previous, .. label, .. body, .. e.PrevHash, .. e.Hash, .. digests[i]]);
            previous = chain[i];
        }

        return chain;
    }
}
