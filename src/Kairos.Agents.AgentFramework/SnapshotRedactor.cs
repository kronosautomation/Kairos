using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kairos.Agents.AgentFramework;

/// <summary>An Agent Framework session snapshot (or a message) has a shape the redactor does not know, so it cannot promise to have redacted it.</summary>
public sealed class SnapshotShapeException : Exception
{
    public SnapshotShapeException()
        : base("A snapshot has a shape the redactor does not know.")
    {
    }

    public SnapshotShapeException(string message)
        : base(message)
    {
    }

    public SnapshotShapeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Applies the redactor to the text inside Agent Framework's own JSON (a session snapshot, or messages) by walking its structure: it rewrites the string <b>values</b> of the members that carry
/// text (<c>text</c>, <c>arguments</c>, <c>result</c>, <c>message</c>, <c>details</c>, <c>reason</c>), never keys, roles, ids or types. It <b>refuses</b> a shape it does not know (an unknown member, content type or
/// state key) with a <see cref="SnapshotShapeException"/>, because a redactor that silently skipped what it did not understand would promise more than it does. The shapes it knows are pinned by
/// a contract test against the Agent Framework version the package references, so a new version that changes them fails CI, not production.
/// </summary>
internal static class SnapshotRedactor
{
    /// <summary>The state key of the built-in in-memory chat history provider.</summary>
    public const string InMemoryHistoryKey = "InMemoryChatHistoryProvider";

    /// <summary>The state key under which the agent's function-invoking client keeps the approval requests it has asked a person for.</summary>
    public const string PendingApprovalsKey = "_pendingApprovalRequests";

    private static readonly HashSet<string> RootMembers = new(StringComparer.Ordinal) { "stateBag", "conversationId" };

    private static readonly HashSet<string> MessageMembers = new(StringComparer.Ordinal) { "authorName", "createdAt", "role", "contents", "messageId", "additionalProperties" };

    private static readonly HashSet<string> HistoryMembers = new(StringComparer.Ordinal) { "messages" };

    /// <summary>Content types the walker knows, with the members that hold text (<c>T</c>), JSON of any shape that may hold text (<c>J</c>), a nested call (<c>C</c>) or nothing sensitive (<c>K</c>).</summary>
    private static readonly Dictionary<string, Dictionary<string, char>> Contents = new(StringComparer.Ordinal)
    {
        ["text"] = new() { ["text"] = 'T' },
        ["reasoning"] = new() { ["text"] = 'T', ["protectedData"] = 'K' },
        ["functionCall"] = new() { ["name"] = 'K', ["arguments"] = 'J', ["informationalOnly"] = 'K', ["callId"] = 'K' },
        ["functionResult"] = new() { ["result"] = 'J', ["callId"] = 'K' },
        ["error"] = new() { ["message"] = 'T', ["errorCode"] = 'K', ["details"] = 'T' },
        ["data"] = new() { ["uri"] = 'K', ["mediaType"] = 'K', ["name"] = 'K' },
        ["uri"] = new() { ["uri"] = 'K', ["mediaType"] = 'K' },
        ["toolApprovalRequest"] = new() { ["toolCall"] = 'C', ["requiresConfirmation"] = 'K', ["requestId"] = 'K' },
        ["toolApprovalResponse"] = new() { ["approved"] = 'K', ["toolCall"] = 'C', ["reason"] = 'T', ["requestId"] = 'K' },
    };

    /// <summary>The content types this redactor handles. A contract test compares them with the ones the pinned library defines.</summary>
    public static IReadOnlyCollection<string> KnownContentTypes => Contents.Keys;

    /// <summary>Redacts a session snapshot. <paramref name="knownState"/> names the state keys (other than the in-memory history) the application declared free of text.</summary>
    public static JsonElement RedactSnapshot(JsonElement snapshot, Func<string, string> redact, IReadOnlySet<string> knownState)
    {
        JsonNode root = JsonNode.Parse(snapshot.GetRawText()) ?? throw new SnapshotShapeException("The snapshot is empty.");
        if (root is not JsonObject obj)
        {
            throw new SnapshotShapeException("The snapshot is not a JSON object.");
        }

        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (!RootMembers.Contains(member.Key))
            {
                throw new SnapshotShapeException($"The snapshot has a member '{member.Key}' the redactor does not know.");
            }
        }

        if (obj["stateBag"] is JsonObject bag)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in bag)
            {
                if (entry.Key == InMemoryHistoryKey)
                {
                    RedactHistory(entry.Value, redact);
                }
                else if (entry.Key == PendingApprovalsKey)
                {
                    RedactPendingApprovals(entry.Value, redact);
                }
                else if (!knownState.Contains(entry.Key))
                {
                    throw new SnapshotShapeException(
                        $"The session keeps state under '{entry.Key}', a shape the redactor does not know. Declare it free of text with AllowSnapshotState(\"{entry.Key}\") if it is, or remove the redactor.");
                }
            }
        }
        else if (obj.ContainsKey("stateBag"))
        {
            throw new SnapshotShapeException("The snapshot's state bag is not a JSON object.");
        }

        return ToElement(root);
    }

    /// <summary>Redacts a JSON array of chat messages.</summary>
    public static JsonElement RedactMessages(JsonElement messages, Func<string, string> redact)
    {
        JsonNode root = JsonNode.Parse(messages.GetRawText()) ?? throw new SnapshotShapeException("The messages are empty.");
        RedactMessageArray(root, redact);
        return ToElement(root);
    }

    private static void RedactHistory(JsonNode? history, Func<string, string> redact)
    {
        if (history is not JsonObject obj)
        {
            throw new SnapshotShapeException("The in-memory chat history is not a JSON object.");
        }

        foreach (KeyValuePair<string, JsonNode?> member in obj)
        {
            if (!HistoryMembers.Contains(member.Key))
            {
                throw new SnapshotShapeException($"The in-memory chat history has a member '{member.Key}' the redactor does not know.");
            }
        }

        RedactMessageArray(obj["messages"], redact);
    }

    private static void RedactPendingApprovals(JsonNode? pending, Func<string, string> redact)
    {
        if (pending is not JsonArray array)
        {
            throw new SnapshotShapeException("The pending approval requests are not a JSON array.");
        }

        foreach (JsonNode? request in array)
        {
            if (request is not JsonObject r)
            {
                throw new SnapshotShapeException("A pending approval request is not a JSON object.");
            }

            foreach (KeyValuePair<string, JsonNode?> member in r)
            {
                if (member.Key is not ("toolCall" or "requiresConfirmation" or "requestId"))
                {
                    throw new SnapshotShapeException($"A pending approval request has a member '{member.Key}' the redactor does not know.");
                }
            }

            RedactContent(r["toolCall"], redact);
        }
    }

    private static void RedactMessageArray(JsonNode? messages, Func<string, string> redact)
    {
        if (messages is not JsonArray array)
        {
            throw new SnapshotShapeException("Messages are not a JSON array.");
        }

        foreach (JsonNode? message in array)
        {
            if (message is not JsonObject m)
            {
                throw new SnapshotShapeException("A message is not a JSON object.");
            }

            foreach (KeyValuePair<string, JsonNode?> member in m)
            {
                if (!MessageMembers.Contains(member.Key))
                {
                    throw new SnapshotShapeException($"A message has a member '{member.Key}' the redactor does not know.");
                }
            }

            if (m["additionalProperties"] is { } extra)
            {
                CheckAttribution(extra);
            }

            if (m["contents"] is not JsonArray contents)
            {
                throw new SnapshotShapeException("A message has no contents array.");
            }

            foreach (JsonNode? content in contents)
            {
                RedactContent(content, redact);
            }
        }
    }

    /// <summary>
    /// Messages Agent Framework loads back from its history carry a note of where they came from: <c>{"_attribution":{"sourceType":{"value":..},"sourceId":..}}</c>. That is the one extra the redactor knows is free of
    /// text. Anything else a message carries under <c>additionalProperties</c> could be anything, so it is refused.
    /// </summary>
    private static void CheckAttribution(JsonNode extra)
    {
        if (extra is not JsonObject bag || bag.Count != 1 || bag["_attribution"] is not JsonObject attribution || attribution.Count != 2
            || attribution["sourceId"] is not JsonValue || attribution["sourceType"] is not JsonObject { Count: 1 } type || type["value"] is not JsonValue)
        {
            throw new SnapshotShapeException("A message carries additionalProperties other than Agent Framework's own attribution, which the redactor does not know.");
        }
    }

    private static void RedactContent(JsonNode? content, Func<string, string> redact)
    {
        if (content is not JsonObject c || c["$type"] is not JsonValue kind || !kind.TryGetValue(out string? type))
        {
            throw new SnapshotShapeException("A content item has no type.");
        }

        if (!Contents.TryGetValue(type, out Dictionary<string, char>? members))
        {
            throw new SnapshotShapeException($"A message has content of type '{type}', which the redactor does not know.");
        }

        foreach (KeyValuePair<string, JsonNode?> member in c.ToArray())
        {
            if (member.Key == "$type")
            {
                continue;
            }

            if (!members.TryGetValue(member.Key, out char how))
            {
                throw new SnapshotShapeException($"Content of type '{type}' has a member '{member.Key}' the redactor does not know.");
            }

            switch (how)
            {
                case 'T':
                case 'J':
                    if (Replacement(member.Value, redact) is { } replaced)
                    {
                        c[member.Key] = replaced;
                    }
                    else if (how == 'J')
                    {
                        RedactInPlace(member.Value, redact);
                    }

                    break;
                case 'C':
                    RedactContent(member.Value, redact);
                    break;
            }
        }
    }

    /// <summary>The redacted string when <paramref name="node"/> is a string value, else null (nothing to replace).</summary>
    private static JsonValue? Replacement(JsonNode? node, Func<string, string> redact) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? JsonValue.Create(redact(s)) : null;

    /// <summary>Redacts every string value inside a JSON value of any shape, in place. (A value that is itself a string is replaced by its parent, with <see cref="Replacement"/>.)</summary>
    private static void RedactInPlace(JsonNode? node, Func<string, string> redact)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> member in o.ToArray())
                {
                    if (Replacement(member.Value, redact) is { } replaced)
                    {
                        o[member.Key] = replaced;
                    }
                    else
                    {
                        RedactInPlace(member.Value, redact);
                    }
                }

                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (Replacement(a[i], redact) is { } replaced)
                    {
                        a[i] = replaced;
                    }
                    else
                    {
                        RedactInPlace(a[i], redact);
                    }
                }

                break;
        }
    }

    private static JsonElement ToElement(JsonNode node)
    {
        using JsonDocument doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
