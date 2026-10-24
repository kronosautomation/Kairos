using System.Globalization;
using System.Text.Json;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// CloudEvents 1.0 (JSON format) for the run-event stream and for inbound starts and signals, so Kairos can talk to any bus or
/// gateway that speaks CloudEvents. Outbound events carry metadata only, never payloads. Inbound parsing is strict: it accepts
/// only the two documented types, only for the scope the caller names, within size limits, and reports every problem as a
/// <see cref="FormatException"/> and nothing else.
/// </summary>
public static class CloudEventsFormat
{
    /// <summary>Content type of one event in structured mode.</summary>
    public const string ContentType = "application/cloudevents+json";

    /// <summary>Content type of a JSON array of events (batch mode).</summary>
    public const string BatchContentType = "application/cloudevents-batch+json";

    public const string TypePrefix = "com.kronosautomation.kairos.";

    /// <summary>Inbound: start a workflow. Data: tenantId, namespaceId, workflowId, workflowType, input (base64).</summary>
    public const string StartWorkflowType = TypePrefix + "workflow.start";

    /// <summary>Inbound: signal a run. Data: tenantId, namespaceId, runId, signalName, payload (base64).</summary>
    public const string SignalRunType = TypePrefix + "run.signal";

    private const string RunTypePrefix = TypePrefix + "run.event.";
    private const int MaxIdLength = 256;
    private const int MaxNameLength = 256;

    /// <summary>Default cap on an inbound event's size.</summary>
    public const int DefaultMaxBytes = 1024 * 1024;

    /// <summary>The CloudEvents <c>type</c> attribute for a run event of <paramref name="type"/>.</summary>
    public static string TypeOf(HistoryEventType type) => RunTypePrefix + type;

    /// <summary>One run event in structured mode.</summary>
    public static byte[] Serialize(RunEventNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            Write(w, notice);
        }

        return stream.ToArray();
    }

    /// <summary>Run events as a batch (a JSON array).</summary>
    public static byte[] SerializeBatch(IReadOnlyList<RunEventNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (RunEventNotice notice in notices)
            {
                Write(w, notice);
            }

            w.WriteEndArray();
        }

        return stream.ToArray();
    }

    /// <summary>Parses a single event or a batch produced by <see cref="Serialize"/> or <see cref="SerializeBatch"/>.</summary>
    public static IReadOnlyList<RunEventNotice> ParseRunEvents(ReadOnlySpan<byte> body)
    {
        try
        {
            using JsonDocument doc = Open(body, DefaultMaxBytes);
            var result = new List<RunEventNotice>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in doc.RootElement.EnumerateArray())
                {
                    result.Add(ReadRunEvent(e));
                }
            }
            else
            {
                result.Add(ReadRunEvent(doc.RootElement));
            }

            return result;
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            throw new FormatException("Malformed CloudEvent.", ex);
        }
    }

    /// <summary>
    /// Parses one inbound CloudEvent into a start or signal request. <paramref name="expected"/> is the scope the transport
    /// authenticated the sender for; an event that names any other scope is rejected, so a sender cannot reach across tenants.
    /// </summary>
    public static InboundEvent ParseInbound(ReadOnlySpan<byte> body, TenantScope expected, int maxBytes = DefaultMaxBytes)
    {
        try
        {
            using JsonDocument doc = Open(body, maxBytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("A CloudEvent is a JSON object.");
            }

            Dictionary<string, JsonElement> context = Members(root);
            RequireSpecVersion(context);
            string id = Text(context, "id", MaxIdLength);
            string type = Text(context, "type", MaxNameLength);
            if (!context.TryGetValue("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The event has no data object.");
            }

            Dictionary<string, JsonElement> data = Members(dataElement);
            var scope = new TenantScope(Guid(data, "tenantId"), Guid(data, "namespaceId"));
            if (scope != expected)
            {
                throw new FormatException("The event does not belong to this scope.");
            }

            return type switch
            {
                StartWorkflowType => new StartWorkflowEvent(id, scope, Text(data, "workflowId", MaxNameLength), Text(data, "workflowType", MaxNameLength), Bytes(data, "input")),
                SignalRunType => new SignalRunEvent(id, scope, Guid(data, "runId"), Text(data, "signalName", MaxNameLength), Bytes(data, "payload")),
                _ => throw new FormatException("Unsupported event type."),
            };
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            throw new FormatException(ex is FormatException { Message: { Length: > 0 } m } ? m : "Malformed CloudEvent.", ex);
        }
    }

    /// <summary>An inbound start request as a CloudEvent. For senders, tests and bridges.</summary>
    public static byte[] SerializeStart(StartWorkflowEvent start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return WriteInbound(start.EventId, StartWorkflowType, start.Scope, w =>
        {
            w.WriteString("workflowId", start.WorkflowId);
            w.WriteString("workflowType", start.WorkflowType);
            w.WriteString("input", Convert.ToBase64String(start.Input));
        });
    }

    /// <summary>An inbound signal request as a CloudEvent. For senders, tests and bridges.</summary>
    public static byte[] SerializeSignal(SignalRunEvent signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return WriteInbound(signal.EventId, SignalRunType, signal.Scope, w =>
        {
            w.WriteString("runId", signal.RunId);
            w.WriteString("signalName", signal.SignalName);
            w.WriteString("payload", Convert.ToBase64String(signal.Payload));
        });
    }

    private static byte[] WriteInbound(string id, string type, TenantScope scope, Action<Utf8JsonWriter> data)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            w.WriteString("id", id);
            w.WriteString("source", "/kairos/inbound");
            w.WriteString("type", type);
            w.WriteString("datacontenttype", "application/json");
            w.WriteStartObject("data");
            w.WriteString("tenantId", scope.TenantId);
            w.WriteString("namespaceId", scope.NamespaceId);
            data(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter w, RunEventNotice n)
    {
        w.WriteStartObject();
        w.WriteString("specversion", "1.0");
        w.WriteString("id", n.EventId);
        w.WriteString("source", $"/kairos/{n.Run.TenantId:D}/{n.Run.NamespaceId:D}");
        w.WriteString("type", TypeOf(n.Type));
        w.WriteString("subject", n.Run.RunId.ToString("D"));
        w.WriteString("time", n.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
        w.WriteString("datacontenttype", "application/json");
        w.WriteStartObject("data");
        w.WriteString("tenantId", n.Run.TenantId);
        w.WriteString("namespaceId", n.Run.NamespaceId);
        w.WriteString("runId", n.Run.RunId);
        w.WriteNumber("sequence", n.Sequence);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static RunEventNotice ReadRunEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("A CloudEvent is a JSON object.");
        }

        Dictionary<string, JsonElement> context = Members(e);
        RequireSpecVersion(context);
        string type = Text(context, "type", MaxNameLength);
        if (!type.StartsWith(RunTypePrefix, StringComparison.Ordinal)
            || !Enum.TryParse(type.AsSpan(RunTypePrefix.Length), ignoreCase: false, out HistoryEventType eventType)
            || !Enum.IsDefined(eventType))
        {
            throw new FormatException("Unknown event type.");
        }

        if (!context.TryGetValue("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The event has no data object.");
        }

        Dictionary<string, JsonElement> data = Members(dataElement);
        if (!DateTimeOffset.TryParse(Text(context, "time", 64), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at))
        {
            throw new FormatException("Invalid time.");
        }

        if (!data.TryGetValue("sequence", out JsonElement seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out long sequence) || sequence < 1)
        {
            throw new FormatException("Invalid sequence.");
        }

        return new RunEventNotice(
            Text(context, "id", MaxIdLength), new RunKey(Guid(data, "tenantId"), Guid(data, "namespaceId"), Guid(data, "runId")), eventType, sequence, at);
    }

    private static JsonDocument Open(ReadOnlySpan<byte> body, int maxBytes)
    {
        if (body.Length == 0 || body.Length > maxBytes)
        {
            throw new FormatException("The event is empty or too large.");
        }

        return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
    }

    /// <summary>Reads an object's members, refusing duplicate names (which would let two readers see different events).</summary>
    private static Dictionary<string, JsonElement> Members(JsonElement obj)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (!members.TryAdd(p.Name, p.Value))
            {
                throw new FormatException("Duplicate attribute.");
            }
        }

        return members;
    }

    private static void RequireSpecVersion(Dictionary<string, JsonElement> context)
    {
        if (Text(context, "specversion", 8) != "1.0")
        {
            throw new FormatException("Only CloudEvents 1.0 is supported.");
        }
    }

    private static string Text(Dictionary<string, JsonElement> members, string name, int maxLength)
    {
        if (!members.TryGetValue(name, out JsonElement v) || v.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Missing or invalid '{name}'.");
        }

        string s = v.GetString()!;
        if (s.Length == 0 || s.Length > maxLength)
        {
            throw new FormatException($"Missing or invalid '{name}'.");
        }

        return s;
    }

    private static Guid Guid(Dictionary<string, JsonElement> members, string name) =>
        System.Guid.TryParseExact(Text(members, name, 36), "D", out Guid g) ? g : throw new FormatException($"Missing or invalid '{name}'.");

    private static byte[] Bytes(Dictionary<string, JsonElement> members, string name)
    {
        if (!members.TryGetValue(name, out JsonElement v) || v.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Missing or invalid '{name}'.");
        }

        try
        {
            return v.GetBytesFromBase64();
        }
        catch (FormatException)
        {
            throw new FormatException($"Missing or invalid '{name}'.");
        }
    }

    private static bool IsParseFailure(Exception ex) =>
        ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException;
}
