using System.Text.Json;

namespace Kairos.Agents.McpClient;

/// <summary>Settings for one MCP call.</summary>
public sealed class McpCallOptions
{
    /// <summary>Retry policy for the step that starts the call. Null: three attempts.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>Ask for the call to run as a remote task when the server supports tasks, so a long call is polled instead of held open.</summary>
    public bool UseTasks { get; init; } = true;

    /// <summary>Workflow time after which a remote task is cancelled and the call fails with <see cref="TimeoutException"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The shortest wait between polls, whatever the server suggests.</summary>
    public TimeSpan MinimumPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between polls.</summary>
    public TimeSpan MaximumPollInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>The remote task failed; the message carries the server's error.</summary>
public sealed class McpToolFailedException : Exception
{
    public McpToolFailedException()
        : base("The MCP task failed.")
    {
    }

    public McpToolFailedException(string message)
        : base(message)
    {
    }

    public McpToolFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The remote task was cancelled by someone else.</summary>
public sealed class McpToolCancelledException : Exception
{
    public McpToolCancelledException()
        : base("The MCP task was cancelled.")
    {
    }

    public McpToolCancelledException(string message)
        : base(message)
    {
    }

    public McpToolCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The remote task asked for input (a question for a person). An agent cannot answer that, so the call stops here.</summary>
public sealed class McpInputRequiredException : Exception
{
    public McpInputRequiredException()
        : base("The MCP task needs input.")
    {
    }

    public McpInputRequiredException(string message)
        : base(message)
    {
    }

    public McpInputRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Policy refused the call.</summary>
public sealed class McpToolDeniedException : Exception
{
    public McpToolDeniedException()
        : base("The MCP call was refused by policy.")
    {
    }

    public McpToolDeniedException(string message)
        : base(message)
    {
    }

    public McpToolDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class AgentContextMcpExtensions
{
    /// <summary>
    /// Calls a tool on an MCP server as a durable, idempotent step. The result is recorded once and returned on every replay without calling
    /// the server again. Every attempt carries the step's idempotency key in the request metadata. When the server runs the call as a task,
    /// its task id is recorded and polled with durable timers, so a restart resumes polling and never starts the call again.
    /// A tool not declared read-only waits for an approval unless the policy allows it; a rejected approval throws
    /// <see cref="ApprovalRejectedException"/> (inside a saga, that rolls the work back).
    /// </summary>
    /// <exception cref="McpToolDeniedException">Policy refused the call.</exception>
    /// <exception cref="ApprovalRejectedException">A person rejected the call, or nobody approved in time.</exception>
    /// <exception cref="McpToolFailedException">The remote task failed.</exception>
    /// <exception cref="McpToolCancelledException">The remote task was cancelled.</exception>
    /// <exception cref="McpInputRequiredException">The remote task asked for input.</exception>
    /// <exception cref="TimeoutException">The remote task did not finish within <see cref="McpCallOptions.Timeout"/>.</exception>
    public static async Task<McpToolResult> CallMcpToolAsync(
        this IAgentContext context, string server, string tool, IReadOnlyDictionary<string, object?>? arguments = null, McpCallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentException.ThrowIfNullOrEmpty(tool);
        options ??= new McpCallOptions();
        IWorkflowContext workflow = context.Workflow;
        DateTimeOffset started = workflow.Now;
        string argumentsJson = ArgumentsToJson(arguments);
        RetryPolicy retry = options.Retry ?? new RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(2),
            MaximumInterval = TimeSpan.FromSeconds(30),
            NonRetryableErrorTypes = [nameof(UnknownMcpServerException)],
        };

        CallReply reply = await StartAsync(workflow, server, tool, argumentsJson, options, retry, approval: null);
        if (reply.Kind == "denied")
        {
            throw new McpToolDeniedException($"The call to {server}.{tool} was refused by policy: {reply.Reason}.");
        }

        if (reply.Kind == "approval-required")
        {
            ApprovalDecision decision = await context.RequestApprovalAsync(new ApprovalRequest
            {
                Action = $"Call MCP tool '{tool}' on '{server}'",
                Details = argumentsJson,
                Tool = $"{server}.{tool}",
                Requirement = new ApprovalRequirement(
                    reply.Approvers, reply.Roles, reply.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null, reply.ForbidSelfApproval),
            });
            decision.ThrowIfNotApproved();
            reply = await StartAsync(workflow, server, tool, argumentsJson, options, retry, new ApprovalEvidence(decision.ApprovalId, decision.Approver?.Subject ?? ""));
            if (reply.Kind is "denied" or "approval-required")
            {
                throw new McpToolDeniedException($"The call to {server}.{tool} is still not allowed after approval: {reply.Reason}.");
            }
        }

        if (reply.Kind == "result")
        {
            return reply.Result!;
        }

        return await PollAsync(workflow, server, reply.TaskId!, reply.PollIntervalMs, started, options);
    }

    private static async Task<CallReply> StartAsync(
        IWorkflowContext workflow, string server, string tool, string argumentsJson, McpCallOptions options, RetryPolicy retry, ApprovalEvidence? approval)
    {
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(
            new CallStep { Server = server, Tool = tool, ArgumentsJson = argumentsJson, UseTasks = options.UseTasks, Approval = approval }, McpJson.Default.CallStep);
        byte[] reply = await workflow.ExecuteActivityAsync(McpClientRuntime.CallActivity, input, retry);
        return JsonSerializer.Deserialize(reply, McpJson.Default.CallReply)!;
    }

    private static async Task<McpToolResult> PollAsync(
        IWorkflowContext workflow, string server, string taskId, long suggestedMs, DateTimeOffset started, McpCallOptions options)
    {
        byte[] pollInput = JsonSerializer.SerializeToUtf8Bytes(new PollStep(server, taskId), McpJson.Default.PollStep);
        TimeSpan wait = Clamp(TimeSpan.FromMilliseconds(suggestedMs), options);
        while (true)
        {
            if (workflow.Now - started >= options.Timeout)
            {
                try
                {
                    await workflow.ExecuteActivityAsync(McpClientRuntime.CancelActivity, pollInput, RetryPolicy.Default);
                }
                catch (ActivityFailureException)
                {
                    // best effort: the call has timed out either way
                }

                throw new TimeoutException($"The MCP task {taskId} on '{server}' did not finish within {options.Timeout}.");
            }

            await workflow.DelayAsync(wait);
            byte[] bytes = await workflow.ExecuteActivityAsync(McpClientRuntime.PollActivity, pollInput, RetryPolicy.Default);
            PollReply poll = JsonSerializer.Deserialize(bytes, McpJson.Default.PollReply)!;
            switch (poll.Status)
            {
                case nameof(McpRemoteStatus.Completed):
                    return poll.Result ?? new McpToolResult(false, "");
                case nameof(McpRemoteStatus.Failed):
                    throw new McpToolFailedException(poll.Error ?? "The MCP task failed.");
                case nameof(McpRemoteStatus.Cancelled):
                    throw new McpToolCancelledException();
                case nameof(McpRemoteStatus.InputRequired):
                    throw new McpInputRequiredException($"The MCP task {taskId} on '{server}' asks for input ({poll.InputRequests} request(s)); an agent cannot answer it.");
            }

            wait = Clamp(wait, options); // the server's suggestion stays the cadence; a slow task is not polled faster than it asked
        }
    }

    private static TimeSpan Clamp(TimeSpan interval, McpCallOptions options) =>
        interval < options.MinimumPollInterval ? options.MinimumPollInterval : interval > options.MaximumPollInterval ? options.MaximumPollInterval : interval;

    private static string ArgumentsToJson(IReadOnlyDictionary<string, object?>? arguments)
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
                    case decimal m:
                        writer.WriteNumberValue(m);
                        break;
                    default:
                        writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
