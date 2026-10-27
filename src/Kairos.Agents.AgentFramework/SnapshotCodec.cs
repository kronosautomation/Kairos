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
/// that form, and a change (<see cref="Diff(JsonNode?, JsonNode?)"/>) that <see cref="Apply(JsonNode?, JsonNode)"/> turns back into the new snapshot. The hash is what makes a reference to a snapshot checkable: whoever applies changes compares the result with the hash that was
/// recorded for it.
/// </summary>
/// <remarks>
/// A change is a JSON object: <c>{"e": 1}</c> is no change at all; <c>{"s": value}</c> replaces the value; <c>{"o": {name: change}, "d": [names]}</c> changes members of an object (a member that is new carries <c>{"s": value}</c>) and removes the listed ones;
/// <c>{"f": d, "k": n, "t": [values]}</c> drops the first d elements of an array (none when "f" is absent), keeps the next n and appends the tail. A conversation grows by appending messages, so its change is the new messages;
/// one kept to a window also drops its oldest. A checkpoint of a workflow may also use <c>{"m": path, "c": change}</c> (the value at that path of the snapshot the change applies to, with the change applied to it: a message handed on from one executor to the next),
/// <c>{"w": {name: change}}</c> (a new object some of whose members are such references) and <c>{"a": [changes]}</c> (the same for a new array).
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
    public static JsonNode? Diff(JsonNode? before, JsonNode? after) => Diff(before, after, allowMoves: false);

    /// <summary>
    /// The same, and with <paramref name="allowMoves"/> a value that is new at its place but is already in <paramref name="before"/> somewhere else (a message handed from one executor's queue to the next) is
    /// written as a reference to where it was, with what was added to it, instead of being written out again. Only a checkpoint of a workflow uses this; a session's changes are written as they always were.
    /// </summary>
    public static JsonNode? Diff(JsonNode? before, JsonNode? after, bool allowMoves) => Diff(before, after, allowMoves ? MoveIndex.Of(before) : null);

    private static JsonNode? Diff(JsonNode? before, JsonNode? after, MoveIndex? moves)
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
                    members[member.Key] = Written(member.Value, moves);
                }
                else if (Diff(old, member.Value, moves) is { } change)
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

        return Written(after, moves);
    }

    /// <summary>A value with nothing at its place to compare with: written out, or (when moves are allowed) as references to the parts of it that are elsewhere in the snapshot it replaces.</summary>
    private static JsonNode Written(JsonNode? value, MoveIndex? moves)
    {
        if (moves is not null && value is JsonObject or JsonArray)
        {
            bool moved = false;
            JsonNode expression = Express(value, moves, ref moved);
            if (moved)
            {
                return expression;
            }
        }

        return new JsonObject { ["s"] = value?.DeepClone() };
    }

    /// <summary>The smallest value, as compact text, that is worth a reference.</summary>
    private const int MoveMinimum = 256;

    private static JsonNode Express(JsonNode? value, MoveIndex moves, ref bool moved)
    {
        if (value is not (JsonObject or JsonArray))
        {
            return new JsonObject { ["s"] = value?.DeepClone() };
        }

        string text = Text(value);
        if (text.Length >= MoveMinimum)
        {
            if (moves.Find(text) is { } path)
            {
                moved = true;
                return new JsonObject { ["m"] = path.DeepClone() };
            }

            if (value is JsonArray array && array.Count > 1)
            {
                // The same list with a few more at the end (a conversation that grew by a message while it moved on).
                string[] items = [.. array.Select(item => item is null ? "null" : Text(item))];
                for (int drop = 1; drop <= 4 && drop < array.Count; drop++)
                {
                    string prefix = "[" + string.Join(',', items.Take(array.Count - drop)) + "]";
                    if (prefix.Length >= MoveMinimum && moves.Find(prefix) is { } source)
                    {
                        var tail = new JsonArray();
                        for (int i = array.Count - drop; i < array.Count; i++)
                        {
                            tail.Add(array[i]?.DeepClone());
                        }

                        moved = true;
                        return new JsonObject { ["m"] = source.DeepClone(), ["c"] = new JsonObject { ["k"] = array.Count - drop, ["t"] = tail } };
                    }
                }
            }

            // A small wrapper around something that is elsewhere: look inside.
            bool inside = false;
            JsonNode built;
            if (value is JsonObject o)
            {
                var members = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> member in o)
                {
                    members[member.Key] = Express(member.Value, moves, ref inside);
                }

                built = new JsonObject { ["w"] = members };
            }
            else
            {
                var elements = new JsonArray();
                foreach (JsonNode? item in (JsonArray)value)
                {
                    elements.Add(Express(item, moves, ref inside));
                }

                built = new JsonObject { ["a"] = elements };
            }

            if (inside)
            {
                moved = true;
                return built;
            }
        }

        return new JsonObject { ["s"] = value.DeepClone() };
    }

    /// <summary>The containers of a snapshot that are large enough to be worth a reference, found by what they say, and where each is.</summary>
    private sealed class MoveIndex
    {
        private readonly Dictionary<string, JsonArray> _paths = new(StringComparer.Ordinal);

        public static MoveIndex? Of(JsonNode? root)
        {
            var index = new MoveIndex();
            if (root is JsonObject or JsonArray)
            {
                index.Add(root, []);
            }

            return index;
        }

        public JsonArray? Find(string text) => _paths.TryGetValue(Hash(text), out JsonArray? path) ? path : null;

        private void Add(JsonNode node, List<JsonNode> path)
        {
            if (Text(node).Length >= MoveMinimum)
            {
                _paths.TryAdd(Hash(Text(node)), new JsonArray([.. path.Select(p => p.DeepClone())]));
            }

            if (node is JsonObject o)
            {
                foreach (KeyValuePair<string, JsonNode?> member in o)
                {
                    if (member.Value is JsonObject or JsonArray)
                    {
                        path.Add(JsonValue.Create(member.Key));
                        Add(member.Value, path);
                        path.RemoveAt(path.Count - 1);
                    }
                }
            }
            else if (node is JsonArray a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonObject or JsonArray)
                    {
                        path.Add(JsonValue.Create(i));
                        Add(a[i]!, path);
                        path.RemoveAt(path.Count - 1);
                    }
                }
            }
        }
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
    public static JsonNode? Apply(JsonNode? before, JsonNode change) => Apply(before, change, before);

    private static JsonNode? Apply(JsonNode? before, JsonNode change, JsonNode? root)
    {
        if (change is not JsonObject c)
        {
            throw new SnapshotIntegrityException("A change is not a JSON object.");
        }

        if (c.TryGetPropertyValue("s", out JsonNode? set))
        {
            return set?.DeepClone();
        }

        if (c.TryGetPropertyValue("m", out JsonNode? from))
        {
            // A value that is in the snapshot this change applies to, somewhere else, with what was added to it.
            JsonNode? found = Resolve(root, from);
            return c["c"] is { } added ? Apply(found, added, root) : found?.DeepClone();
        }

        if (c.TryGetPropertyValue("w", out JsonNode? built))
        {
            if (built is not JsonObject written)
            {
                throw new SnapshotIntegrityException("A written object is not an object.");
            }

            var made = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> member in written)
            {
                made[member.Key] = member.Value is null ? null : Apply(null, member.Value, root);
            }

            return Sort(made);
        }

        if (c.TryGetPropertyValue("a", out JsonNode? elements))
        {
            if (elements is not JsonArray written)
            {
                throw new SnapshotIntegrityException("A written array is not an array.");
            }

            var made = new JsonArray();
            foreach (JsonNode? item in written)
            {
                made.Add(item is null ? null : Apply(null, item, root));
            }

            return made;
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

            changed[member.Key] = members is not null && members.TryGetPropertyValue(member.Key, out JsonNode? inner) && inner is not null ? Apply(member.Value, inner, root) : member.Value?.DeepClone();
        }

        if (members is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> member in members)
            {
                if (!obj.ContainsKey(member.Key) && member.Value is not null)
                {
                    changed[member.Key] = Apply(null, member.Value, root);
                }
            }
        }

        // Members are kept in canonical order, whatever order they were added in.
        return Sort(changed);
    }

    private static JsonNode? Resolve(JsonNode? root, JsonNode? path)
    {
        if (path is not JsonArray steps)
        {
            throw new SnapshotIntegrityException("A reference to the snapshot has no path.");
        }

        JsonNode? at = root;
        foreach (JsonNode? step in steps)
        {
            at = at switch
            {
                JsonObject o when step is JsonValue key && key.TryGetValue(out string? name) && o.TryGetPropertyValue(name, out JsonNode? member) => member,
                JsonArray a when step is JsonValue index && index.TryGetValue(out int position) && position >= 0 && position < a.Count => a[position],
                _ => throw new SnapshotIntegrityException("A reference to the snapshot points at nothing."),
            };
        }

        return at;
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
