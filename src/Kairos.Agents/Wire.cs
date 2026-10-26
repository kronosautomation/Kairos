using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Kairos.Agents;

// What the agent steps write into history. These are Kairos' own shapes, not the library's, so a recorded run replays the same
// after the Microsoft.Extensions.AI package is upgraded. All of it travels as activity input/result, so client-side encryption seals it.

internal sealed record WireContent
{
    /// <summary>text, reasoning, call or result.</summary>
    public string Kind { get; init; } = "text";

    public string? Text { get; init; }

    public string? CallId { get; init; }

    public string? Name { get; init; }

    /// <summary>The arguments of a call, or the value of a result, as JSON text.</summary>
    public string? Json { get; init; }
}

internal sealed record WireMessage
{
    public string Role { get; init; } = "user";

    public string? Author { get; init; }

    public WireContent[] Contents { get; init; } = [];
}

internal sealed record WireUsage(long? Input, long? Output, long? Total);

internal sealed record WireChatOptions(float? Temperature, int? MaxOutputTokens, long? Seed);

internal sealed record ChatStepRequest
{
    public string Model { get; init; } = "";

    public WireMessage[] Messages { get; init; } = [];

    public string[] Tools { get; init; } = [];

    public WireChatOptions? Options { get; init; }
}

internal sealed record ChatStepResponse
{
    public WireMessage[] Messages { get; init; } = [];

    public string? FinishReason { get; init; }

    public WireUsage? Usage { get; init; }

    public string? ModelId { get; init; }
}

internal sealed record WireApprover
{
    public string Subject { get; init; } = "";

    public string? Name { get; init; }

    public string[] Roles { get; init; } = [];

    /// <summary>Opaque proof the host kept of how it authenticated the approver (an SSO assertion id, for example).</summary>
    public string? Evidence { get; init; }
}

internal sealed record WireApprovalRequirement
{
    public string[] Approvers { get; init; } = [];

    public string[] Roles { get; init; } = [];

    public double? TimeoutSeconds { get; init; }

    public bool ForbidSelfApproval { get; init; } = true;
}

internal sealed record WireApprovalDecision
{
    public string ApprovalId { get; init; } = "";

    public bool Approved { get; init; }

    public WireApprover Approver { get; init; } = new();

    public string? Comment { get; init; }

    public DateTimeOffset DecidedAt { get; init; }
}

internal sealed record WireApprovalRequest
{
    public string ApprovalId { get; init; } = "";

    public string Action { get; init; } = "";

    public string? Details { get; init; }

    public string? Tool { get; init; }

    public string? RequestedBy { get; init; }

    public WireApprovalRequirement Requirement { get; init; } = new();

    public DateTimeOffset RequestedAt { get; init; }

    public string RunId { get; init; } = "";
}

internal sealed record ToolStepRequest
{
    public string Name { get; init; } = "";

    public string CallId { get; init; } = "";

    public string ArgumentsJson { get; init; } = "{}";

    public string? OnBehalfOf { get; init; }

    /// <summary>Present on the second call, after a person approved this exact call.</summary>
    public WireApprovalDecision? Approval { get; init; }
}

internal sealed record ApprovalVerifyRequest
{
    public WireApprovalRequest Request { get; init; } = new();

    public WireApprovalDecision Decision { get; init; } = new();

    /// <summary>Workflow time when the decision arrived. Token expiry is judged at this moment, not when a worker happens to get to it.</summary>
    public DateTimeOffset ReceivedAt { get; init; }
}

internal sealed record ApprovalVerifyResult
{
    public bool Ok { get; init; }

    public string Subject { get; init; } = "";

    public string[] Roles { get; init; } = [];

    public string? Reason { get; init; }
}

internal static class ToolOutcomes
{
    public const string Completed = "completed";
    public const string ApprovalRequired = "approval-required";
    public const string Denied = "denied";
}

internal sealed record ToolStepResponse
{
    public string Outcome { get; init; } = ToolOutcomes.Completed;

    public string? Content { get; init; }

    public bool IsError { get; init; }

    public string? Reason { get; init; }

    public WireApprovalRequirement? Requirement { get; init; }

    /// <summary>What was issued to the tool for this call (never the token), so an audit can say what a tool was allowed to do and until when.</summary>
    public WireCredentialGrant? Credential { get; init; }
}

internal sealed record WireCredentialGrant
{
    public string[] Scopes { get; init; } = [];

    public DateTimeOffset ExpiresAt { get; init; }

    public string? OnBehalfOf { get; init; }
}

internal sealed record SagaMark
{
    public string Kind { get; init; } = "";

    public string? Name { get; init; }

    public string[] Names { get; init; } = [];

    public bool Succeeded { get; init; } = true;

    public string[] Failures { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatStepRequest))]
[JsonSerializable(typeof(ChatStepResponse))]
[JsonSerializable(typeof(ToolStepRequest))]
[JsonSerializable(typeof(ToolStepResponse))]
[JsonSerializable(typeof(WireApprovalRequest))]
[JsonSerializable(typeof(WireApprovalDecision))]
[JsonSerializable(typeof(SagaMark))]
[JsonSerializable(typeof(ApprovalVerifyRequest))]
[JsonSerializable(typeof(ApprovalVerifyResult))]
[JsonSerializable(typeof(WireMessage[]))]
internal sealed partial class AgentJson : JsonSerializerContext;

internal static class WireCodec
{
    public static byte[] Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);

    public static T Read<T>(ReadOnlySpan<byte> json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json, info) ?? throw new FormatException("Empty agent payload.");

    // ---- messages ------------------------------------------------------------------------------------------

    public static WireMessage ToWire(ChatMessage message, Func<string, string>? redact)
    {
        var contents = new List<WireContent>(message.Contents.Count);
        foreach (AIContent content in message.Contents)
        {
            contents.Add(content switch
            {
                TextContent t => new WireContent { Kind = "text", Text = Redact(t.Text, redact) },
                TextReasoningContent r => new WireContent { Kind = "reasoning", Text = Redact(r.Text, redact) },
                FunctionCallContent c => new WireContent
                {
                    Kind = "call", CallId = c.CallId, Name = c.Name, Json = Redact(ArgumentsToJson(c.Arguments), redact),
                },
                FunctionResultContent f => new WireContent
                {
                    Kind = "result", CallId = f.CallId, Json = Redact(ValueToJson(f.Result), redact),
                },
                _ => throw new NotSupportedException(
                    $"Content of type {content.GetType().Name} cannot be recorded in a durable agent step. Supported: text, reasoning, function calls and results."),
            });
        }

        return new WireMessage { Role = message.Role.Value, Author = message.AuthorName, Contents = [.. contents] };
    }

    public static ChatMessage FromWire(WireMessage wire)
    {
        var contents = new List<AIContent>(wire.Contents.Length);
        foreach (WireContent c in wire.Contents)
        {
            switch (c.Kind)
            {
                case "text":
                    contents.Add(new TextContent(c.Text));
                    break;
                case "reasoning":
                    contents.Add(new TextReasoningContent(c.Text));
                    break;
                case "call":
                    contents.Add(new FunctionCallContent(c.CallId ?? "", c.Name ?? "", JsonToArguments(c.Json)));
                    break;
                case "result":
                    contents.Add(new FunctionResultContent(c.CallId ?? "", c.Json is null ? null : JsonToValue(c.Json)));
                    break;
                default:
                    throw new FormatException($"Unknown recorded content kind '{c.Kind}'.");
            }
        }

        return new ChatMessage(new ChatRole(wire.Role), contents) { AuthorName = wire.Author };
    }

    /// <summary>Converts what a model returned into recordable content. Anything that cannot be recorded becomes a visible placeholder, never silence.</summary>
    public static WireMessage ResponseToWire(ChatMessage message, Func<string, string>? redact)
    {
        var contents = new List<WireContent>(message.Contents.Count);
        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case TextContent or TextReasoningContent or FunctionCallContent or FunctionResultContent:
                    contents.Add(ToWire(new ChatMessage(message.Role, [content]), redact).Contents[0]);
                    break;
                case UsageContent:
                    break; // reported through the usage field instead
                default:
                    contents.Add(new WireContent { Kind = "text", Text = $"[content of type {content.GetType().Name} was not recorded]" });
                    break;
            }
        }

        return new WireMessage { Role = message.Role.Value, Author = message.AuthorName, Contents = [.. contents] };
    }

    private static string Redact(string? text, Func<string, string>? redact) =>
        text is null ? "" : redact is null ? text : redact(text);

    // ---- JSON values (no reflection: models hand back JsonElement) ----------------------------------------------

    public static string ArgumentsToJson(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach ((string key, object? value) in arguments)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public static string ValueToJson(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, value);
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement e:
                e.WriteTo(writer);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    public static Dictionary<string, object?>? JsonToArguments(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Recorded tool arguments are not a JSON object.");
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            result[p.Name] = p.Value.Clone();
        }

        return result;
    }

    public static object JsonToValue(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
