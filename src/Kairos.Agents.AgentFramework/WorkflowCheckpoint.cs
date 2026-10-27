using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Kairos.Agents.AgentFramework;

/// <summary>
/// A checkpoint store that holds only what a step was given and what it wrote: the carrier between the Kairos history (where checkpoints really live) and the Agent Framework checkpoint manager the step runs.
/// </summary>
internal sealed class CarrierStore : JsonCheckpointStore
{
    public const string Session = "kairos";

    private readonly List<(CheckpointInfo Info, JsonElement Value)> _items = [];

    /// <summary>The checkpoints written since the store was created, oldest first (a seeded one is not among them).</summary>
    public int Written { get; private set; }

    public JsonElement Last => _items[^1].Value;

    public CheckpointInfo Seed(JsonElement checkpoint)
    {
        var info = new CheckpointInfo(Session, $"seed{_items.Count}");
        _items.Add((info, checkpoint.Clone()));
        return info;
    }

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var info = new CheckpointInfo(sessionId, $"cp{_items.Count}");
        _items.Add((info, value.Clone()));
        Written++;
        return new(info);
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key) =>
        new(_items.Single(i => i.Info.CheckpointId == key.CheckpointId).Value);

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null) =>
        new(_items.Select(i => i.Info).ToList());
}

/// <summary>
/// What a redactor can promise about the values in a workflow's checkpoint, messages and outputs. Agent Framework's checkpoint holds the <b>customer's</b> types in its own JSON, so the walker can know only the types
/// it is told about: strings, chat messages, a few lists of those and plain scalars are built in; every other type must be declared by the registration (free of text, or with a function that redacts it). A type it does not
/// know is refused, never skipped, for the reason <see cref="SnapshotRedactor"/> gives.
/// </summary>
internal sealed partial class WorkflowTypePolicy
{
    private const string ChatMessage = "Microsoft.Extensions.AI.ChatMessage";
    private const string ChatMessageList = "System.Collections.Generic.List`1[[Microsoft.Extensions.AI.ChatMessage]]";
    private const string StringList = "System.Collections.Generic.List`1[[System.String]]";
    private const string WrappedRequests = "System.Collections.Generic.Dictionary`2[[System.String],[Microsoft.Agents.AI.Workflows.ExternalRequest]]";

    private static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.Decimal", "System.Guid", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
        "Microsoft.Agents.AI.Workflows.TurnToken",
    };

    private readonly WorkflowRegistration _registration;

    public WorkflowTypePolicy(WorkflowRegistration registration) => _registration = registration;

    /// <summary>A type name without assembly qualifications ("A, asm, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" becomes "A"), so that names compare across assembly versions.</summary>
    public static string Normalize(string typeName) => AssemblyQualification().Replace(typeName, "");

    /// <summary>Agent Framework writes a checkpoint's customer values in camel case; this reads them back, and writes them back the same way.</summary>
    public static JsonSerializerOptions CheckpointOptions(JsonSerializerOptions user) =>
        new(user) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public bool Knows(Type type) => Knows(Normalize(type.FullName ?? type.Name));

    public bool Knows(string normalized)
    {
        if (Scalars.Contains(normalized) || normalized is "System.String" or ChatMessage or ChatMessageList or StringList or "System.String[]" or "Microsoft.Extensions.AI.ChatMessage[]" or WrappedRequests)
        {
            return true;
        }

        if (_registration.FreeTypes.Contains(normalized) || _registration.TextTypes.ContainsKey(normalized))
        {
            return true;
        }

        const string Nullable = "System.Nullable`1[[";
        return normalized.StartsWith(Nullable, StringComparison.Ordinal) && normalized.EndsWith("]]", StringComparison.Ordinal) && Knows(normalized[Nullable.Length..^2]);
    }

    /// <summary>Redacts a value of the named type, given as JSON.</summary>
    /// <exception cref="SnapshotShapeException">The type is not one the redactor knows.</exception>
    public JsonNode? Redact(string typeName, JsonNode? value, Func<string, string> redact)
    {
        string normalized = Normalize(typeName);
        if (value is null || _registration.FreeTypes.Contains(normalized) || Scalars.Contains(normalized))
        {
            return value;
        }

        if (_registration.TextTypes.TryGetValue(normalized, out Func<JsonNode?, Func<string, string>, JsonNode?>? custom))
        {
            return custom(value, redact);
        }

        switch (normalized)
        {
            case "System.String":
                return value is JsonValue v && v.TryGetValue(out string? text) ? JsonValue.Create(redact(text)) : throw Shape(typeName, "a string that is not a JSON string");
            case ChatMessage:
                return (RedactMessages(new JsonArray(value.DeepClone()), redact) as JsonArray)?[0]?.DeepClone();
            case ChatMessageList:
            case "Microsoft.Extensions.AI.ChatMessage[]":
                return RedactMessages(value, redact);
            case StringList:
            case "System.String[]":
                if (value is not JsonArray strings)
                {
                    throw Shape(typeName, "a list that is not a JSON array");
                }

                for (int i = 0; i < strings.Count; i++)
                {
                    strings[i] = strings[i] is JsonValue s && s.TryGetValue(out string? item) ? JsonValue.Create(redact(item)) : throw Shape(typeName, "a list of something other than strings");
                }

                return strings;
            case WrappedRequests:
                RedactWrappedRequests(value, redact);
                return value;
        }

        const string Nullable = "System.Nullable`1[[";
        if (normalized.StartsWith(Nullable, StringComparison.Ordinal) && normalized.EndsWith("]]", StringComparison.Ordinal))
        {
            return Redact(normalized[Nullable.Length..^2], value, redact);
        }

        throw Shape(typeName, null);
    }

    private static JsonNode? RedactMessages(JsonNode messages, Func<string, string> redact)
    {
        JsonElement redacted = SnapshotRedactor.RedactMessages(JsonSerializer.SerializeToElement(messages), redact);
        return JsonNode.Parse(redacted.GetRawText());
    }

    private void RedactWrappedRequests(JsonNode value, Func<string, string> redact)
    {
        if (value is not JsonObject requests)
        {
            throw Shape("the requests an executor wrapped", "not a JSON object");
        }

        foreach (KeyValuePair<string, JsonNode?> entry in requests)
        {
            if (entry.Value is not JsonObject request)
            {
                throw Shape("the requests an executor wrapped", "an entry that is not a JSON object");
            }

            if (request["data"] is JsonObject data)
            {
                RedactPortable(data, redact);
            }
        }
    }

    /// <summary>Redacts a checkpoint in place. Refuses a checkpoint whose shape it does not know.</summary>
    /// <exception cref="SnapshotShapeException">The checkpoint has a member, or a value of a type, that the redactor does not know.</exception>
    public void RedactCheckpoint(JsonNode root, Func<string, string> redact)
    {
        if (root is not JsonObject checkpoint)
        {
            throw Shape("the checkpoint", "not a JSON object");
        }

        foreach (KeyValuePair<string, JsonNode?> member in checkpoint.ToList())
        {
            switch (member.Key)
            {
                case "stepNumber":
                case "workflow":
                case "parent":
                    break; // the numbers, ids and type names of the structure: no text
                case "runnerData":
                    RedactRunnerData(member.Value, redact);
                    break;
                case "stateData":
                    foreach (KeyValuePair<string, JsonNode?> entry in Members(member.Value, "state"))
                    {
                        RedactPortable(entry.Value, redact);
                    }

                    break;
                case "edgeStateData":
                    foreach (KeyValuePair<string, JsonNode?> entry in Members(member.Value, "edge state"))
                    {
                        RedactPortable(entry.Value, redact);
                    }

                    break;
                default:
                    throw Shape("the checkpoint", $"a member '{member.Key}' the redactor does not know");
            }
        }
    }

    private void RedactRunnerData(JsonNode? runner, Func<string, string> redact)
    {
        foreach (KeyValuePair<string, JsonNode?> member in Members(runner, "runner data"))
        {
            switch (member.Key)
            {
                case "instantiatedExecutors":
                    break;
                case "queuedMessages":
                    foreach (KeyValuePair<string, JsonNode?> queue in Members(member.Value, "queued messages"))
                    {
                        foreach (JsonNode? item in queue.Value as JsonArray ?? throw Shape("the queued messages", "not a JSON array"))
                        {
                            foreach (KeyValuePair<string, JsonNode?> part in Members(item, "queued message"))
                            {
                                switch (part.Key)
                                {
                                    case "messageType":
                                    case "source":
                                        break;
                                    case "message":
                                        RedactPortable(part.Value, redact);
                                        break;
                                    default:
                                        throw Shape("a queued message", $"a member '{part.Key}' the redactor does not know");
                                }
                            }
                        }
                    }

                    break;
                case "outstandingRequests":
                    foreach (JsonNode? request in member.Value as JsonArray ?? throw Shape("the outstanding requests", "not a JSON array"))
                    {
                        foreach (KeyValuePair<string, JsonNode?> part in Members(request, "outstanding request"))
                        {
                            switch (part.Key)
                            {
                                case "portInfo":
                                case "requestId":
                                    break;
                                case "data":
                                    RedactPortable(part.Value, redact);
                                    break;
                                default:
                                    throw Shape("an outstanding request", $"a member '{part.Key}' the redactor does not know");
                            }
                        }
                    }

                    break;
                default:
                    throw Shape("the runner data", $"a member '{member.Key}' the redactor does not know");
            }
        }
    }

    /// <summary>A value Agent Framework wrote as <c>{ typeId, value }</c>.</summary>
    private void RedactPortable(JsonNode? node, Func<string, string> redact)
    {
        if (node is not JsonObject portable || portable["typeId"] is not JsonObject typeId || typeId["typeName"]?.GetValue<string>() is not { } typeName || !portable.ContainsKey("value") || portable.Count != 2)
        {
            throw Shape("a value in the checkpoint", "it is not a typed value ({ typeId, value })");
        }

        portable["value"] = Redact(typeName, portable["value"], redact);
    }

    private static List<KeyValuePair<string, JsonNode?>> Members(JsonNode? node, string what) =>
        node is JsonObject obj ? obj.ToList() : throw Shape(what, "not a JSON object");

    private static SnapshotShapeException Shape(string what, string? problem) =>
        new(problem is null
            ? $"A redactor is configured, and {what} has a type the redactor does not know. Declare the type free of text with AllowStateType<T>() if it is, give the redactor its text with RedactState<T>(...), or remove the redactor."
            : $"A redactor is configured, and {what} has a shape the redactor does not know ({problem}). Refusing to record it, since the redactor could not promise to have redacted it.");

    [GeneratedRegex(@", [^,\[\]]+, Version=[^,\]]+, Culture=[^,\]]+, PublicKeyToken=[^,\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyQualification();
}
