using System.Text;

namespace Kairos;

/// <summary>A run's history as read from or written to a history file: the events as the store held them, and the file's own headers.</summary>
/// <param name="History">The events, with their hash chain fields as stored (payloads are ciphertext when encryption was on).</param>
/// <param name="ReplayWith">A key naming the code that replays this history, for files kept next to the code (empty otherwise).</param>
/// <param name="Note">A free-text line for the reader.</param>
/// <param name="MetadataOnly">True for a file whose payloads were removed at export: it cannot be replayed.</param>
/// <param name="PayloadDigests">For a metadata-only file, the digest of the field removed from each event (empty for events without one); empty otherwise.</param>
/// <param name="DigestChain">For a metadata-only file, the digest chain as stored (see <see cref="HistoryRedaction.DigestChain"/>); empty otherwise.</param>
public sealed record HistoryFileContents(
    IReadOnlyList<HistoryEvent> History, string ReplayWith, string Note, bool MetadataOnly = false,
    IReadOnlyList<byte[]>? PayloadDigests = null, IReadOnlyList<byte[]>? DigestChain = null)
{
    /// <summary>The hash of the last event as stored by the system. Compare it with the <c>ExportHistory</c> audit entry to tie a file to the run it came from.</summary>
    public string HeadHash => Convert.ToHexStringLower(History[^1].Hash);
}

/// <summary>
/// The text form of a history (<c>.khist</c>): one event per line as base64 of its canonical body and its two hashes, so changes are reviewable and the hash chain
/// can be verified from the file alone. A file holds payloads exactly as the store did: with client-side encryption on, only ciphertext.
/// </summary>
public static class HistoryFile
{
    /// <summary>The recorded-by tag written in the first line.</summary>
    public const string Header = "# kairos-history";

    /// <summary>Writes <paramref name="history"/> in file form.</summary>
    public static string Serialize(IReadOnlyList<HistoryEvent> history, string recordedBy, string note = "", string replayWith = "")
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            throw new ArgumentException("A history file needs at least one event.", nameof(history));
        }

        var sb = new StringBuilder();
        sb.Append(Header).Append(' ').Append(recordedBy).Append('\n');
        sb.Append("# note: ").Append(note).Append('\n');
        sb.Append("replay-with: ").Append(replayWith).Append('\n');
        sb.Append("outcome: ").Append(Outcome(history[^1])).Append('\n');

        foreach (HistoryEvent e in history)
        {
            sb.Append("event: ").Append(Convert.ToBase64String(HistoryEventCodec.Encode(e)))
              .Append(' ').Append(Convert.ToBase64String(e.PrevHash))
              .Append(' ').Append(Convert.ToBase64String(e.Hash)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes <paramref name="fullHistory"/> as a metadata-only file: the events without payloads, each followed by the digest of what was removed and its place in the digest
    /// chain (<see cref="HistoryRedaction.DigestChain"/>). The file cannot be replayed, but any edit to it, or reordering, dropping or inserting an event, fails
    /// <see cref="ParseVerified"/>.
    /// </summary>
    public static string SerializeMetadataOnly(IReadOnlyList<HistoryEvent> fullHistory, string recordedBy, string note = "")
    {
        ArgumentNullException.ThrowIfNull(fullHistory);
        if (fullHistory.Count == 0)
        {
            throw new ArgumentException("A history file needs at least one event.", nameof(fullHistory));
        }

        IReadOnlyList<HistoryEvent> redacted = HistoryRedaction.MetadataOnly(fullHistory);
        byte[][] digests = [.. fullHistory.Select(HistoryRedaction.PayloadDigest)];
        IReadOnlyList<byte[]> chain = HistoryRedaction.DigestChain(redacted, digests);
        var sb = new StringBuilder();
        sb.Append(Header).Append(' ').Append(recordedBy).Append('\n');
        sb.Append("# note: ").Append(note).Append('\n');
        sb.Append("replay-with: \n");
        sb.Append("outcome: ").Append(Outcome(fullHistory[^1])).Append('\n');
        sb.Append("mode: metadata\n");
        for (int i = 0; i < redacted.Count; i++)
        {
            HistoryEvent e = redacted[i];
            sb.Append("event: ").Append(Convert.ToBase64String(HistoryEventCodec.Encode(e)))
              .Append(' ').Append(Convert.ToBase64String(e.PrevHash))
              .Append(' ').Append(Convert.ToBase64String(e.Hash))
              .Append(' ').Append(digests[i].Length == 0 ? "-" : Convert.ToBase64String(digests[i]))
              .Append(' ').Append(Convert.ToBase64String(chain[i])).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Reads a history file. Throws <see cref="FormatException"/> when it is malformed; the hash chain is not checked here (see <see cref="HashChain.Verify"/>).</summary>
    public static HistoryFileContents Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string replayWith = "";
        string note = "";
        bool metadataOnly = false;
        var events = new List<HistoryEvent>();
        var digests = new List<byte[]>();
        var chain = new List<byte[]>();
        try
        {
            foreach (string raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("# note: ", StringComparison.Ordinal))
                {
                    note = line["# note: ".Length..];
                }
                else if (line.StartsWith("replay-with: ", StringComparison.Ordinal))
                {
                    replayWith = line["replay-with: ".Length..];
                }
                else if (line == "mode: metadata")
                {
                    metadataOnly = true;
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    string[] parts = line["event: ".Length..].Split(' ');
                    if (parts.Length != 3 && parts.Length != 5)
                    {
                        throw new FormatException("An event line has three fields (body, previous hash, hash), or five in a metadata-only file (and the payload digest and chain value).");
                    }

                    events.Add(HistoryEventCodec.Decode(Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2])));
                    if (parts.Length == 5)
                    {
                        digests.Add(parts[3] == "-" ? [] : Convert.FromBase64String(parts[3]));
                        chain.Add(Convert.FromBase64String(parts[4]));
                    }
                }
            }
        }
        catch (ArgumentException ex)
        {
            throw new FormatException("Malformed history file.", ex);
        }

        if (events.Count == 0)
        {
            throw new FormatException("The file holds no events.");
        }

        if (metadataOnly != (digests.Count > 0) || (metadataOnly && digests.Count != events.Count))
        {
            throw new FormatException("A metadata-only file carries a payload digest and a chain value on every event line, and no other file does.");
        }

        return new HistoryFileContents(events, replayWith, note, metadataOnly, digests, chain);
    }

    /// <summary>
    /// Reads a history file and verifies it: the full hash chain, or, for a metadata-only file, sequence, linkage and the digest chain over the metadata and the digests of
    /// the removed payloads. Like any hash chain this proves the file is consistent and unedited since it was made, not who made it: compare
    /// <see cref="HistoryFileContents.HeadHash"/> with the <c>ExportHistory</c> audit entry to tie it to the system.
    /// </summary>
    /// <exception cref="FormatException">The file is malformed.</exception>
    /// <exception cref="HistoryIntegrityException">The history does not verify.</exception>
    public static HistoryFileContents ParseVerified(string text)
    {
        HistoryFileContents contents = Parse(text);
        if (contents.MetadataOnly)
        {
            HashChain.VerifyLinkage(contents.History);
            IReadOnlyList<byte[]> expected = HistoryRedaction.DigestChain(contents.History, contents.PayloadDigests!);
            for (int i = 0; i < expected.Count; i++)
            {
                if (!expected[i].AsSpan().SequenceEqual(contents.DigestChain![i]))
                {
                    throw new HistoryIntegrityException($"Event {contents.History[i].Sequence} of the metadata-only file does not match its digest chain.", contents.History[i].Sequence);
                }
            }
        }
        else
        {
            HashChain.Verify(contents.History);
        }

        return contents;
    }

    /// <summary>How a history ends, as the files name it.</summary>
    public static string Outcome(HistoryEvent last) => last switch
    {
        WorkflowCompleted => "Completed",
        WorkflowFailed => "Failed",
        WorkflowContinued => "ContinuedAsNew",
        RunTerminated => "Terminated",
        _ => "Open",
    };
}
