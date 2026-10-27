using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kairos.Agents.AgentFramework;

/// <summary>
/// The session of a conversation as the driver follows it from what history recorded: the last session recorded whole, and the changes recorded since. Nothing here is a second copy of the session in history; it is
/// rebuilt from the results on replay. The session is put together only when it must travel (a worker that does not hold it, a hand-over), and what comes out is checked against the hash the step recorded.
/// </summary>
internal sealed class SessionChain
{
    private string? _baseText;
    private readonly List<JsonNode> _changes = [];

    /// <summary>The content hash of the current session, or null before the first step.</summary>
    public string? Hash { get; private set; }

    /// <summary>The step that recorded the current session, or null when it came with the run's input.</summary>
    public string? From { get; private set; }

    /// <summary>How many steps ago the session was recorded whole.</summary>
    public int StepsSinceFull { get; private set; }

    public bool IsEmpty => Hash is null;

    /// <summary>The conversation starts from this session (a hand-over from an earlier run).</summary>
    public void StartFrom(JsonElement snapshot)
    {
        string text = SnapshotCodec.Text(SnapshotCodec.Canonical(snapshot));
        _baseText = text;
        _changes.Clear();
        Hash = SnapshotCodec.Hash(text);
        From = null;
        StepsSinceFull = 0;
    }

    /// <summary>Takes the session a step recorded: whole, or as a change against the session before it.</summary>
    /// <exception cref="SnapshotIntegrityException">A whole session is not the one its hash names, or a change is not against the session this chain holds.</exception>
    public void Record(AfStepResult result) => Record(result.SnapshotHash, result.Snapshot, result.Delta, result.BaseHash, result.StepId);

    /// <summary>The same for any result that records a JSON document by hash (a workflow's checkpoint is recorded the way a session is).</summary>
    public void Record(string? snapshotHash, JsonElement? snapshot, JsonElement? delta, string? baseHash, string? stepId)
    {
        if (snapshotHash is not { } hash)
        {
            return;
        }

        if (snapshot is { } whole)
        {
            string text = SnapshotCodec.Text(SnapshotCodec.Canonical(whole));
            if (!string.Equals(SnapshotCodec.Hash(text), hash, StringComparison.Ordinal))
            {
                throw new SnapshotIntegrityException("The session a step recorded is not the one its hash names.");
            }

            _baseText = text;
            _changes.Clear();
            StepsSinceFull = 0;
        }
        else if (delta is { } change && _baseText is not null && string.Equals(baseHash, Hash, StringComparison.Ordinal))
        {
            _changes.Add(JsonNode.Parse(change.GetRawText()) ?? throw new SnapshotIntegrityException("A recorded change to the session is empty."));
            StepsSinceFull++;
        }
        else
        {
            throw new SnapshotIntegrityException("A step recorded a change to a session other than the one the conversation holds.");
        }

        Hash = hash;
        From = stepId;
    }

    /// <summary>The current session, put together from the last whole one and the changes since, and checked against its hash.</summary>
    /// <exception cref="SnapshotIntegrityException">What results is not the session the last step recorded.</exception>
    public JsonElement Materialize()
    {
        if (_baseText is null || Hash is null)
        {
            throw new InvalidOperationException("The conversation has no session yet.");
        }

        string text = _baseText;
        if (_changes.Count > 0)
        {
            JsonNode? node = JsonNode.Parse(_baseText);
            foreach (JsonNode change in _changes)
            {
                node = SnapshotCodec.Apply(node, change);
            }

            text = SnapshotCodec.Text(node ?? throw new SnapshotIntegrityException("The session is empty."));
        }

        return string.Equals(SnapshotCodec.Hash(text), Hash, StringComparison.Ordinal)
            ? SnapshotCodec.Element(text)
            : throw new SnapshotIntegrityException("The session put together from the recorded changes is not the one the last step recorded.");
    }
}
