using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairos.Agents.AgentFramework;

// What the adapter writes into history. These are Kairos' own shapes. Agent Framework's own JSON (the session snapshot, and the messages it
// produces) travels inside them as opaque JSON values, and the driver workflow never looks inside: it reads only the fields defined here.
// All of it is activity input/result or workflow input/signal payload, so client-side encryption seals it.

/// <summary>One message sent to the agent: the messages the caller gave it, as <c>Microsoft.Extensions.AI</c> JSON, under an id the caller chose.</summary>
internal sealed record AfTurn
{
    public string TurnId { get; init; } = "";

    /// <summary>A JSON array of chat messages.</summary>
    public JsonElement Messages { get; init; }
}

/// <summary>The run that asked for a nested agent's answer, and the signal to answer it with.</summary>
internal sealed record AfParent
{
    public Guid TenantId { get; init; }

    public Guid NamespaceId { get; init; }

    public Guid RunId { get; init; }

    public string Signal { get; init; } = "";
}

/// <summary>The input of a conversation run: which agent, where the conversation stands, and what has not been answered yet.</summary>
internal sealed record AfDriverInput
{
    public string Agent { get; init; } = "";

    /// <summary>The definition of the agent this conversation is pinned to (set by the first step), or null before it.</summary>
    public string? Hash { get; init; }

    /// <summary>The Agent Framework session snapshot, or null for a conversation that has not started.</summary>
    public JsonElement? Snapshot { get; init; }

    public AfTurn[] Pending { get; init; } = [];

    public string? OnBehalfOf { get; init; }

    /// <summary>Set when this run answers one question for a parent run: it ends after that turn and tells the parent.</summary>
    public AfParent? Parent { get; init; }

    /// <summary>The conversation ends when nobody writes for this long. Null waits indefinitely.</summary>
    public double? IdleSeconds { get; init; }

    /// <summary>Hands over to a fresh run when the history is this long (test knob; the engine's own limit applies regardless).</summary>
    public int? ContinueAfterEvents { get; init; }

    /// <summary>The ids of the most recent messages taken, so a message sent twice (a caller that retried after an answer was lost) is answered once.</summary>
    public string[] Recent { get; init; } = [];
}

internal sealed record AfToolResult
{
    public string CallId { get; init; } = "";

    public string Content { get; init; } = "";
}

internal sealed record AfApprovalResponse
{
    /// <summary>The request, exactly as the step returned it (<c>Microsoft.Extensions.AI</c> JSON).</summary>
    public JsonElement Request { get; init; }

    public bool Approved { get; init; }

    public string? Reason { get; init; }
}

internal sealed record AfStepRequest
{
    public string Agent { get; init; } = "";

    /// <summary>The definition this run is pinned to, or null for the first step (which adopts the worker's).</summary>
    public string? Hash { get; init; }

    public string TurnId { get; init; } = "";

    public JsonElement? Snapshot { get; init; }

    /// <summary>What the agent is told in this step: a JSON array of chat messages (the caller's, for the first step of a turn).</summary>
    public JsonElement? Messages { get; init; }

    public AfToolResult[] ToolResults { get; init; } = [];

    public AfApprovalResponse[] ApprovalResponses { get; init; } = [];

    /// <summary>The approval that let this one call exceed a budget.</summary>
    public string? BudgetAllowance { get; init; }
}

internal sealed record AfToolCall
{
    public string CallId { get; init; } = "";

    public string Name { get; init; } = "";

    public string ArgumentsJson { get; init; } = "{}";
}

internal sealed record AfApprovalRequest
{
    public string Id { get; init; } = "";

    public string Tool { get; init; } = "";

    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>The request content (<c>Microsoft.Extensions.AI</c> JSON), handed back with the decision.</summary>
    public JsonElement Request { get; init; }
}

internal static class AfOutcomes
{
    public const string Done = "done";
    public const string DefinitionChanged = "definition-changed";
    public const string BudgetExceeded = "budget-exceeded";
}

internal sealed record AfStepResult
{
    public string Outcome { get; init; } = AfOutcomes.Done;

    public string TurnId { get; init; } = "";

    /// <summary>The definition the worker has (the one the run is pinned to after this step).</summary>
    public string? Hash { get; init; }

    /// <summary>With <see cref="AfOutcomes.DefinitionChanged"/>: the definition the run was pinned to.</summary>
    public string? PinnedHash { get; init; }

    public JsonElement? Snapshot { get; init; }

    /// <summary>The messages the agent produced in this step (a JSON array of chat messages).</summary>
    public JsonElement? Messages { get; init; }

    public AfToolCall[] ToolCalls { get; init; } = [];

    public AfApprovalRequest[] ApprovalRequests { get; init; } = [];

    /// <summary>True when the agent answered: there is nothing left to do for this turn.</summary>
    public bool Final { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? TotalTokens { get; init; }

    public string? ModelId { get; init; }

    public string? FinishReason { get; init; }

    public string? BudgetLimit { get; init; }

    public string? OnExceed { get; init; }

    public WireApprovalRequirement? BudgetApproval { get; init; }
}

/// <summary>Marks that a person let a run continue on a changed agent definition. Read back by <see cref="AgentFrameworkAudit"/>.</summary>
internal sealed record AfOverrideMark
{
    public string Agent { get; init; } = "";

    public string FromHash { get; init; } = "";

    public string ToHash { get; init; } = "";

    public string ApprovalId { get; init; } = "";

    /// <summary>The subject the approver verifier established.</summary>
    public string Approver { get; init; } = "";

    public string[] Roles { get; init; } = [];
}

/// <summary>Starts a nested agent's conversation for a parent run.</summary>
internal sealed record AfChildStart
{
    public string WorkflowId { get; init; } = "";

    public byte[] Input { get; init; } = [];
}

internal sealed record AfChildStartResult
{
    public Guid RunId { get; init; }
}

internal sealed record AfChildCheck
{
    public Guid RunId { get; init; }
}

internal sealed record AfChildCheckResult
{
    /// <summary>running, completed, failed or other (closed some other way).</summary>
    public string State { get; init; } = "running";
}

/// <summary>What a nested agent answers its parent (the signal payload), and what the parent turns into the tool result.</summary>
internal sealed record AfChildAnswer
{
    public bool Ok { get; init; } = true;

    public string Text { get; init; } = "";
}

/// <summary>Tells a parent run what its nested agent answered.</summary>
internal sealed record AfNotify
{
    public AfParent Parent { get; init; } = new();

    public AfChildAnswer Answer { get; init; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AfDriverInput))]
[JsonSerializable(typeof(AfTurn))]
[JsonSerializable(typeof(AfStepRequest))]
[JsonSerializable(typeof(AfStepResult))]
[JsonSerializable(typeof(AfOverrideMark))]
[JsonSerializable(typeof(AfChildStart))]
[JsonSerializable(typeof(AfChildStartResult))]
[JsonSerializable(typeof(AfChildCheck))]
[JsonSerializable(typeof(AfChildCheckResult))]
[JsonSerializable(typeof(AfChildAnswer))]
[JsonSerializable(typeof(AfNotify))]
internal sealed partial class AfJson : JsonSerializerContext;

internal static class AfCodec
{
    public static byte[] Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);

    public static T Read<T>(ReadOnlySpan<byte> json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json, info) ?? throw new FormatException("Empty agent framework payload.");
}
