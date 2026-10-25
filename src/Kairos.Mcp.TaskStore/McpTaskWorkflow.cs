using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairos.Mcp;

/// <summary>
/// The workflow behind a task that has no workflow of its own: a run that records every update the MCP server makes to the task (input
/// requested, input answered, completed, failed, cancelled) and ends when the task does. Register it on the workers:
/// <c>options.AddWorkflow(McpTaskWorkflow.WorkflowType, McpTaskWorkflow.Run)</c>.
/// </summary>
public static class McpTaskWorkflow
{
    public const string WorkflowType = "kairos.mcp.task";

    /// <summary>The signal every update travels on.</summary>
    public const string UpdateSignal = "kairos.mcp.task.update";

    /// <summary>The workflow body.</summary>
    public static async Task<byte[]> Run(IWorkflowContext context, byte[] input)
    {
        ArgumentNullException.ThrowIfNull(context);
        while (true)
        {
            byte[] payload = await context.WaitForSignalAsync(UpdateSignal);
            TaskUpdate? update = TaskUpdate.TryParse(payload);
            switch (update?.Kind)
            {
                case TaskUpdate.Complete:
                    return Encoding.UTF8.GetBytes(update.Payload?.GetRawText() ?? "null");
                case TaskUpdate.Fail:
                    throw new McpTaskFailedException(update.Payload?.GetRawText() ?? "null");
                case TaskUpdate.Cancel:
                    throw new McpTaskCancelledException();
            }

            // requests and responses only change what the task is waiting for; the status is read from the recorded updates
        }
    }
}

/// <summary>The task failed; the message is the error the server reported, as JSON.</summary>
public sealed class McpTaskFailedException : Exception
{
    public McpTaskFailedException()
        : base("null")
    {
    }

    public McpTaskFailedException(string message)
        : base(message)
    {
    }

    public McpTaskFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The task was cancelled.</summary>
public sealed class McpTaskCancelledException : Exception
{
    public McpTaskCancelledException()
        : base("The task was cancelled.")
    {
    }

    public McpTaskCancelledException(string message)
        : base(message)
    {
    }

    public McpTaskCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record TaskUpdate
{
    public const string Requests = "requests";
    public const string Response = "response";
    public const string Complete = "complete";
    public const string Fail = "fail";
    public const string Cancel = "cancel";

    public string Kind { get; init; } = "";

    /// <summary>For requests and responses: an object of key to request or response. For complete and fail: the result or error.</summary>
    public JsonElement? Payload { get; init; }

    public static TaskUpdate? TryParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, TaskJson.Default.TaskUpdate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, TaskJson.Default.TaskUpdate);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TaskUpdate))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(string))]
internal sealed partial class TaskJson : JsonSerializerContext;
