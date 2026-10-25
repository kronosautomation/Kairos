using System.Text;

namespace Kairos;

/// <summary>A run's history as read from or written to a history file: the events as the store held them, and the file's own headers.</summary>
/// <param name="History">The events, with their hash chain fields as stored (payloads are ciphertext when encryption was on).</param>
/// <param name="ReplayWith">A key naming the code that replays this history, for files kept next to the code (empty otherwise).</param>
/// <param name="Note">A free-text line for the reader.</param>
public sealed record HistoryFileContents(IReadOnlyList<HistoryEvent> History, string ReplayWith, string Note);

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

    /// <summary>Reads a history file. Throws <see cref="FormatException"/> when it is malformed; the hash chain is not checked here (see <see cref="HashChain.Verify"/>).</summary>
    public static HistoryFileContents Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string replayWith = "";
        string note = "";
        var events = new List<HistoryEvent>();
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
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    string[] parts = line["event: ".Length..].Split(' ');
                    if (parts.Length != 3)
                    {
                        throw new FormatException("An event line has three fields: body, previous hash, hash.");
                    }

                    events.Add(HistoryEventCodec.Decode(Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2])));
                }
            }
        }
        catch (ArgumentException ex)
        {
            throw new FormatException("Malformed history file.", ex);
        }

        return events.Count == 0 ? throw new FormatException("The file holds no events.") : new HistoryFileContents(events, replayWith, note);
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
