using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kairos.Agents.AgentFramework;

/// <summary>A snapshot was not the one it was recorded as (its hash differs), or a change does not apply to the snapshot it names.</summary>
public sealed class SnapshotIntegrityException : Exception
{
    public SnapshotIntegrityException()
        : base("A session snapshot is not the one that was recorded.")
    {
    }

    public SnapshotIntegrityException(string message)
        : base(message)
    {
    }

    public SnapshotIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What a session snapshot is made of, so that it can be recorded once and then only as changes: a canonical form (object members in ordinal order, so the same session is the same text whoever wrote it), a content hash of
/// that form, and a change (<see cref="Diff"/>) that <see cref="Apply"/> turns back into the new snapshot. The hash is what makes a reference to a snapshot checkable: whoever applies changes compares the result with the hash that was
/// recorded for it.
/// </summary>
/// <remarks>
/// A change is a JSON object: <c>{"e": 1}</c> is no change at all; <c>{"s": value}</c> replaces the value; <c>{"o": {name: change}, "d": [names]}</c> changes members of an object (a member that is new carries <c>{"s": value}</c>) and removes the listed ones;
/// <c>{"f": d, "k": n, "t": [values]}</c> drops the first d elements of an array (none when "f" is absent), keeps the next n and appends the tail. A conversation grows by appending messages, so its change is the new messages;
/// one kept to a window also drops its oldest.
/// </remarks>
internal static class SnapshotCodec
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The canonical form of <paramref name="element"/>: the same value with every object's members in ordinal order of their names.</summary>
    public static JsonNode Canonical(JsonElement element) => Sort(JsonNode.Parse(element.GetRawText())) ?? throw new SnapshotIntegrityException("A snapshot cannot be null.");

    /// <summary>The compact text of a canonical value.</summary>
    public static string Text(JsonNode node) => node.ToJsonString(Compact);

    public static string Hash(string canonicalText) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));

    public static JsonElement Element(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var sorted = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> member in o.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    sorted[member.Key] = Sort(member.Value);
                }

                return sorted;
            case JsonArray a:
                var copy = new JsonArray();
                foreach (JsonNode? item in a)
                {
                    copy.Add(Sort(item));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>The change that turns <paramref name="before"/> into <paramref name="after"/> (both canonical), or null when they are equal.</summary>
    public static JsonNode? Diff(JsonNode? before, JsonNode? after)
    {
        if (JsonNode.DeepEquals(before, after))
        {
            return null;
        }

        if (before is JsonObject b && after is JsonObject a)
        {
            var members = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> member in a)
            {
                if (!b.TryGetPropertyValue(member.Key, out JsonNode? old))
                {
                    members[member.Key] = new JsonObject { ["s"] = member.Value?.DeepClone() };
                }
                else if (Diff(old, member.Value) is { } change)
                {
                    members[member.Key] = change;
                }
            }

            var removed = new JsonArray();
            foreach (KeyValuePair<string, JsonNode?> member in b)
            {
                if (!a.ContainsKey(member.Key))
                {
                    removed.Add(member.Key);
                }
            }

            var result = new JsonObject();
            if (members.Count > 0)
            {
                result["o"] = members;
            }

            if (removed.Count > 0)
            {
                result["d"] = removed;
            }

            return result;
        }

        if (before is JsonArray x && after is JsonArray y)
        {
            // Keep a run of the old elements (after dropping some from the front: a conversation kept to a window loses its oldest turn as it gains a new one), and append the rest.
            int drop = 0;
            int keep = CommonRun(x, 0, y);
            if (y.Count > 0)
            {
                for (int candidate = 1; candidate < x.Count; candidate++)
                {
                    int run = CommonRun(x, candidate, y);
                    if (run > keep + 1)
                    {
                        (drop, keep) = (candidate, run);
                    }
                }
            }

            var tail = new JsonArray();
            for (int i = keep; i < y.Count; i++)
            {
                tail.Add(y[i]?.DeepClone());
            }

            var change = new JsonObject();
            if (drop > 0)
            {
                change["f"] = drop;
            }

            change["k"] = keep;
            change["t"] = tail;
            return change;
        }

        return new JsonObject { ["s"] = after?.DeepClone() };
    }

    /// <summary>How many elements of <paramref name="after"/>, from its start, equal the elements of <paramref name="before"/> from <paramref name="from"/>.</summary>
    private static int CommonRun(JsonArray before, int from, JsonArray after)
    {
        int run = 0;
        while (from + run < before.Count && run < after.Count && JsonNode.DeepEquals(before[from + run], after[run]))
        {
            run++;
        }

        return run;
    }

    /// <summary>The value that results from applying <paramref name="change"/> to <paramref name="before"/>. Neither argument is changed.</summary>
    /// <exception cref="SnapshotIntegrityException">The change does not fit what it is applied to.</exception>
    public static JsonNode? Apply(JsonNode? before, JsonNode change)
    {
        if (change is not JsonObject c)
        {
            throw new SnapshotIntegrityException("A change is not a JSON object.");
        }

        if (c.TryGetPropertyValue("s", out JsonNode? set))
        {
            return set?.DeepClone();
        }

        if (c.ContainsKey("e"))
        {
            return before?.DeepClone(); // nothing changed
        }

        if (c.ContainsKey("k"))
        {
            int drop = 0;
            if (before is not JsonArray array || c["k"] is not JsonValue keepValue || !keepValue.TryGetValue(out int keep) || keep < 0
                || (c.ContainsKey("f") && (c["f"] is not JsonValue dropValue || !dropValue.TryGetValue(out drop) || drop < 0))
                || drop + keep > array.Count || c["t"] is not JsonArray tail)
            {
                throw new SnapshotIntegrityException("A change to an array does not fit the array.");
            }

            var result = new JsonArray();
            for (int i = drop; i < drop + keep; i++)
            {
                result.Add(array[i]?.DeepClone());
            }

            foreach (JsonNode? item in tail)
            {
                result.Add(item?.DeepClone());
            }

            return result;
        }

        if (before is not JsonObject obj)
        {
            throw new SnapshotIntegrityException("A change to an object does not fit the value.");
        }

        var changed = new JsonObject();
        JsonArray? removed = c["d"] as JsonArray;
        var gone = new HashSet<string>(removed?.Select(r => r?.GetValue<string>() ?? "") ?? [], StringComparer.Ordinal);
        JsonObject? members = c["o"] as JsonObject;
        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (gone.Contains(member.Key))
            {
                continue;
            }

            changed[member.Key] = members is not null && members.TryGetPropertyValue(member.Key, out JsonNode? inner) && inner is not null ? Apply(member.Value, inner) : member.Value?.DeepClone();
        }

        if (members is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> member in members)
            {
                if (!obj.ContainsKey(member.Key) && member.Value is not null)
                {
                    changed[member.Key] = Apply(null, member.Value);
                }
            }
        }

        // Members are kept in canonical order, whatever order they were added in.
        return Sort(changed);
    }
}

/// <summary>
/// Snapshots a worker has just produced or been given, kept so that the next step of the same session can start from them without being sent them again. Bounded by bytes, least recently used out first. A miss is not an
/// error: the step says it needs the snapshot and the workflow sends it.
/// </summary>
internal sealed class SnapshotCache(long maxBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Tenant, Guid Namespace, string Hash), LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = new();
    private long _bytes;

    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>The session with this content hash of this tenant and namespace. The key is the content hash, so the same session found by another conversation of the scope is the same session; another scope never finds it.</summary>
    public bool TryGet(Guid tenant, Guid ns, string hash, out string text)
    {
        lock (_gate)
        {
            if (_map.TryGetValue((tenant, ns, hash), out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                text = node.Value.Text;
                return true;
            }
        }

        text = "";
        return false;
    }

    public void Put(Guid tenant, Guid ns, string hash, string text)
    {
        long size = (long)text.Length * 2;
        if (size > maxBytes)
        {
            return;
        }

        lock (_gate)
        {
            if (_map.TryGetValue((tenant, ns, hash), out LinkedListNode<Entry>? existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            _map[(tenant, ns, hash)] = _order.AddFirst(new Entry(tenant, ns, hash, text, size));
            _bytes += size;
            while (_bytes > maxBytes && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove((oldest.Value.Tenant, oldest.Value.Namespace, oldest.Value.Hash));
                _bytes -= oldest.Value.Size;
            }
        }
    }

    private sealed record Entry(Guid Tenant, Guid Namespace, string Hash, string Text, long Size);
}
