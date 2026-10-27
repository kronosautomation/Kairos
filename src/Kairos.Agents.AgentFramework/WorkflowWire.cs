using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairos.Agents.AgentFramework;

// What a durable Agent Framework workflow writes into history. Like Wire.cs these are Kairos' own shapes; the workflow's checkpoint, its messages, its request data and its outputs are Agent Framework's (and the customer's)
// JSON and travel inside them as opaque values that the driver never looks into. All of it is activity input/result or workflow input/signal payload, so client-side encryption seals it.

/// <summary>A request the workflow is waiting for a person (or a system) to answer: the port, and the request as the executor raised it.</summary>
internal sealed record AfwRequest
{
    public string RequestId { get; init; } = "";

    public string PortId { get; init; } = "";

    /// <summary>The request data (redacted when a redactor is configured).</summary>
    public JsonElement Data { get; init; }
}

/// <summary>The answer to a request: the response as the port's response type, in JSON.</summary>
internal sealed record AfwResponse
{
    public string RequestId { get; init; } = "";

    public string PortId { get; init; } = "";

    public JsonElement Data { get; init; }
}

/// <summary>A value the workflow yielded as output.</summary>
internal sealed record AfwOutput
{
    public string ExecutorId { get; init; } = "";

    /// <summary>The .NET type of the value, without assembly (readable metadata inside the sealed payload).</summary>
    public string Type { get; init; } = "";

    public JsonElement Data { get; init; }
}

/// <summary>The input of a workflow run: which workflow, where it stands, and everything a hand-over has to carry.</summary>
internal sealed record AfwInput
{
    public string Workflow { get; init; } = "";

    /// <summary>The workflow's input (for the first step); null after it started.</summary>
    public JsonElement? Input { get; init; }

    public string? OnBehalfOf { get; init; }

    /// <summary>The structure of the workflow this run is pinned to (set by the first step), or null before it.</summary>
    public string? Structure { get; init; }

    /// <summary>The Agent Framework package version this run is pinned to.</summary>
    public string? Version { get; init; }

    /// <summary>A hand-over carries the whole checkpoint here, once.</summary>
    public JsonElement? Checkpoint { get; init; }

    public int Steps { get; init; }

    public AfwOutput[] Outputs { get; init; } = [];

    /// <summary>A hand-over: the requests that were open (their answers are waited for again in the new run, or are in <see cref="Answered"/>).</summary>
    public AfwRequest[] Waiting { get; init; } = [];

    /// <summary>A hand-over: answers that arrived and were not delivered to the workflow yet.</summary>
    public AfwResponse[] Answered { get; init; } = [];

    /// <summary>A hand-over: whether the checkpoint has messages queued (more supersteps to run without waiting for anything).</summary>
    public bool More { get; init; }

    /// <summary>The conversations with durable agents that executors of this workflow started: cancelled with it.</summary>
    public string[] Conversations { get; init; } = [];

    /// <summary>How many supersteps one step runs, as it was when the run started (workflow code does not read the worker's options).</summary>
    public int SuperstepsPerStep { get; init; } = 1;

    public int? ContinueAfterEvents { get; init; }
}

internal sealed record AfwStepRequest
{
    public string Workflow { get; init; } = "";

    /// <summary>The structure the run is pinned to, or null for the first step (which adopts the worker's).</summary>
    public string? Structure { get; init; }

    public string? Version { get; init; }

    /// <summary>The workflow's input; only the first step has one.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>The whole checkpoint, sent only when the worker said it did not have the one named by <see cref="CheckpointHash"/>.</summary>
    public JsonElement? Checkpoint { get; init; }

    public string? CheckpointHash { get; init; }

    public string? CheckpointFrom { get; init; }

    public int StepsSinceFull { get; init; }

    public AfwResponse[] Responses { get; init; } = [];

    public int Supersteps { get; init; } = 1;

    public string? OnBehalfOf { get; init; }

    /// <summary>The number of this step in the run (for telemetry and for naming conversations; not trusted for anything else).</summary>
    public int Number { get; init; }
}

internal static class AfwOutcomes
{
    public const string Done = "done";
    public const string StructureChanged = "structure-changed";
    public const string NeedSnapshot = AfOutcomes.NeedSnapshot;
}

internal sealed record AfwStepResult
{
    public string Outcome { get; init; } = AfwOutcomes.Done;

    public string? Structure { get; init; }

    public string? Version { get; init; }

    /// <summary>With <see cref="AfwOutcomes.StructureChanged"/>: what the run was pinned to.</summary>
    public string? PinnedStructure { get; init; }

    public string? PinnedVersion { get; init; }

    public JsonElement? Checkpoint { get; init; }

    public JsonElement? Delta { get; init; }

    public string? CheckpointHash { get; init; }

    public string? BaseHash { get; init; }

    public string? StepId { get; init; }

    public long? CheckpointBytes { get; init; }

    /// <summary>How many supersteps this step ran.</summary>
    public int Supersteps { get; init; }

    /// <summary>True when the checkpoint has messages queued: there is more to run without waiting for anyone.</summary>
    public bool More { get; init; }

    /// <summary>True when nothing is queued and nothing is outstanding: the workflow has finished.</summary>
    public bool Done { get; init; }

    /// <summary>Every request that is outstanding after this step (also those announced before).</summary>
    public AfwRequest[] Requests { get; init; } = [];

    public AfwOutput[] Outputs { get; init; } = [];

    /// <summary>The conversations with durable agents that executors used in this step and that belong to the workflow.</summary>
    public string[] Conversations { get; init; } = [];
}

/// <summary>The result of a workflow run.</summary>
internal sealed record AfwResult
{
    public AfwOutput[] Outputs { get; init; } = [];

    public int Steps { get; init; }
}

/// <summary>Asks conversations of durable agents (started by executors of a workflow that was cancelled or failed) to stop.</summary>
internal sealed record AfwCancelConversations
{
    public string[] WorkflowIds { get; init; } = [];

    public string Reason { get; init; } = "";
}

/// <summary>Marks that a person let a workflow continue on a changed structure (or package version). Read back by <see cref="AgentFrameworkAudit"/>.</summary>
internal sealed record AfwOverrideMark
{
    public string Workflow { get; init; } = "";

    public string FromStructure { get; init; } = "";

    public string ToStructure { get; init; } = "";

    public string FromVersion { get; init; } = "";

    public string ToVersion { get; init; } = "";

    public string ApprovalId { get; init; } = "";

    public string Approver { get; init; } = "";

    public string[] Roles { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AfwInput))]
[JsonSerializable(typeof(AfwStepRequest))]
[JsonSerializable(typeof(AfwStepResult))]
[JsonSerializable(typeof(AfwResult))]
[JsonSerializable(typeof(AfwCancelConversations))]
[JsonSerializable(typeof(AfwOverrideMark))]
[JsonSerializable(typeof(AfwResponse))]
internal sealed partial class AfwJson : JsonSerializerContext;
