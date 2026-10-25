using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kairos.Agents.McpClient;

/// <summary>What a remote tool answered.</summary>
/// <param name="IsError">The server says the tool failed. This is a result for the agent to read, not an exception.</param>
/// <param name="Text">The text content, joined.</param>
/// <param name="StructuredJson">The structured content as JSON, when the server sent any.</param>
public sealed record McpToolResult(bool IsError, string Text, string? StructuredJson = null);

public enum McpRemoteStatus
{
    Working,
    InputRequired,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>The answer to starting a call: a result now, or a remote task to poll.</summary>
public sealed record McpCallOutcome(McpToolResult? Result, string? TaskId, long PollIntervalMs);

/// <summary>The state of a remote task.</summary>
public sealed record McpTaskPoll(McpRemoteStatus Status, McpToolResult? Result, string? Error, int InputRequests);

/// <summary>One connection to an MCP server, reduced to what a durable step needs. <see cref="McpClientEndpoint"/> is the SDK-backed implementation.</summary>
public interface IMcpToolEndpoint
{
    /// <param name="tool">The remote tool name.</param>
    /// <param name="argumentsJson">A JSON object.</param>
    /// <param name="idempotencyKey">Stable across attempts of one step; sent in the request metadata so a server that honours it does not repeat the work.</param>
    /// <param name="useTasks">Ask for the call to run as a remote task when the server supports it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<McpCallOutcome> CallAsync(string tool, string argumentsJson, string idempotencyKey, bool useTasks, CancellationToken cancellationToken);

    ValueTask<McpTaskPoll> PollAsync(string taskId, CancellationToken cancellationToken);

    ValueTask CancelAsync(string taskId, CancellationToken cancellationToken);
}

/// <summary>Adapts an SDK <see cref="McpClient"/>. The caller owns the client and its transport (stdio, HTTP).</summary>
public sealed class McpClientEndpoint(ModelContextProtocol.Client.McpClient client) : IMcpToolEndpoint
{
    /// <summary>The request metadata key that carries the step's idempotency key.</summary>
    public const string IdempotencyMetaKey = "kairos/idempotencyKey";

    public async ValueTask<McpCallOutcome> CallAsync(string tool, string argumentsJson, string idempotencyKey, bool useTasks, CancellationToken cancellationToken)
    {
        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
        var request = new CallToolRequestParams { Name = tool, Arguments = arguments.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) };
        request.Meta = new System.Text.Json.Nodes.JsonObject { [IdempotencyMetaKey] = idempotencyKey };

        if (!useTasks)
        {
            return new McpCallOutcome(Map(await client.CallToolAsync(request, cancellationToken).ConfigureAwait(false)), null, 0);
        }

        ResultOrCreatedTask<CallToolResult> outcome = await client.CallToolAsTaskAsync(request, cancellationToken).ConfigureAwait(false);
        return outcome.IsTask
            ? new McpCallOutcome(null, outcome.TaskCreated!.TaskId, outcome.TaskCreated.PollIntervalMs ?? 1000)
            : new McpCallOutcome(Map(outcome.Result!), null, 0);
    }

    public async ValueTask<McpTaskPoll> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        GetTaskResult state = await client.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        return state switch
        {
            CompletedTaskResult done => new McpTaskPoll(
                McpRemoteStatus.Completed, Map(done.Result.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>()) ?? new CallToolResult()), null, 0),
            FailedTaskResult failed => new McpTaskPoll(McpRemoteStatus.Failed, null, failed.Error.GetRawText(), 0),
            CancelledTaskResult => new McpTaskPoll(McpRemoteStatus.Cancelled, null, null, 0),
            InputRequiredTaskResult input => new McpTaskPoll(McpRemoteStatus.InputRequired, null, null, input.InputRequests?.Count ?? 0),
            _ => new McpTaskPoll(McpRemoteStatus.Working, null, null, 0),
        };
    }

    public async ValueTask CancelAsync(string taskId, CancellationToken cancellationToken) =>
        await client.CancelTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

    private static McpToolResult Map(CallToolResult result) => new(
        result.IsError ?? false,
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text)),
        result.StructuredContent?.GetRawText());
}

/// <summary>No server is registered under that name.</summary>
public sealed class UnknownMcpServerException : Exception
{
    public UnknownMcpServerException()
        : base("Unknown MCP server.")
    {
    }

    public UnknownMcpServerException(string message)
        : base(message)
    {
    }

    public UnknownMcpServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Collects the MCP servers an agent may call. <see cref="Build"/> makes the runtime.</summary>
public sealed class McpClientsBuilder
{
    private readonly Dictionary<string, Func<RunKey, CancellationToken, ValueTask<IMcpToolEndpoint>>> _servers = new(StringComparer.Ordinal);
    private readonly HashSet<(string Server, string Tool)> _readOnly = [];

    internal IToolPolicy Policy { get; private set; } = new BaselineToolPolicy();

    internal Func<string, string>? Redactor { get; private set; }

    internal TimeSpan CallTimeout { get; private set; } = TimeSpan.FromSeconds(60);

    internal Dictionary<string, Func<RunKey, CancellationToken, ValueTask<IMcpToolEndpoint>>> Servers => _servers;

    internal HashSet<(string Server, string Tool)> ReadOnly => _readOnly;

    /// <summary>Registers a server. The function is asked for a connection when a step needs one (per run, so it can pick the tenant's server).</summary>
    public McpClientsBuilder AddServer(string name, Func<RunKey, CancellationToken, ValueTask<IMcpToolEndpoint>> connect)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(connect);
        _servers[name] = connect;
        return this;
    }

    public McpClientsBuilder AddServer(string name, IMcpToolEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return AddServer(name, (_, _) => ValueTask.FromResult(endpoint));
    }

    public McpClientsBuilder AddServer(string name, ModelContextProtocol.Client.McpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return AddServer(name, new McpClientEndpoint(client));
    }

    /// <summary>
    /// Declares tools that only read. A remote tool is trusted only as far as you say: every tool not listed here is treated as side-effecting,
    /// and waits for an approval unless the policy allows it. (A server's own "read only" hint is not believed.)
    /// </summary>
    public McpClientsBuilder ReadOnlyTools(string server, params string[] tools)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentNullException.ThrowIfNull(tools);
        foreach (string tool in tools)
        {
            _readOnly.Add((server, tool));
        }

        return this;
    }

    /// <summary>The policy that decides which calls run, wait for approval or are refused. Share the instance you gave <see cref="AgentsBuilder"/>. Tools are named <c>server.tool</c>.</summary>
    public McpClientsBuilder UsePolicy(IToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        return this;
    }

    public McpClientsBuilder UseRedactor(Func<string, string> redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        Redactor = redactor;
        return this;
    }

    /// <summary>How long one attempt of a step may take (starting a call, or one poll).</summary>
    public McpClientsBuilder WithCallTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        CallTimeout = timeout;
        return this;
    }

    public McpClientRuntime Build() => new(this);
}

/// <summary>The worker side of the MCP client steps. Register <see cref="Activities"/> on every worker that runs workflows calling MCP tools.</summary>
public sealed class McpClientRuntime
{
    public const string CallActivity = "kairos.mcp.call";
    public const string PollActivity = "kairos.mcp.poll";
    public const string CancelActivity = "kairos.mcp.cancel";

    private readonly McpClientsBuilder _config;
    private readonly Dictionary<string, ActivityFunc> _activities;

    internal McpClientRuntime(McpClientsBuilder config)
    {
        _config = config;
        _activities = new Dictionary<string, ActivityFunc>(StringComparer.Ordinal)
        {
            [CallActivity] = CallAsync,
            [PollActivity] = PollAsync,
            [CancelActivity] = CancelAsync,
        };
    }

    public IReadOnlyDictionary<string, ActivityFunc> Activities => _activities;

    public void RegisterInto(ActivityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach ((string name, ActivityFunc activity) in _activities)
        {
            registry.Register(name, activity);
        }
    }

    private async ValueTask<IMcpToolEndpoint> ConnectAsync(string server, RunKey run, CancellationToken cancellationToken) =>
        _config.Servers.TryGetValue(server, out var connect)
            ? await connect(run, cancellationToken).ConfigureAwait(false)
            : throw new UnknownMcpServerException($"No MCP server is registered as '{server}'.");

    private async Task<byte[]> CallAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        CallStep request = Read(input, McpJson.Default.CallStep);
        if (!_config.Servers.ContainsKey(request.Server))
        {
            throw new UnknownMcpServerException($"No MCP server is registered as '{request.Server}'.");
        }

        string toolName = $"{request.Server}.{request.Tool}";
        var tool = new AgentTool(
            toolName, "remote MCP tool", """{"type":"object"}""",
            _config.ReadOnly.Contains((request.Server, request.Tool)) ? ToolAccess.ReadOnly : ToolAccess.SideEffecting,
            (_, _) => ValueTask.FromResult(""));
        ApprovalDecision? approval = request.Approval is null
            ? null
            : new ApprovalDecision(request.Approval.ApprovalId, ApprovalOutcome.Approved, new ApproverIdentity(request.Approval.Subject), null, null);

        ToolDecision decision = await _config.Policy.EvaluateAsync(new ToolPolicyContext(context.Run, tool, request.ArgumentsJson, approval), cancellationToken).ConfigureAwait(false);
        switch (decision.Verdict)
        {
            case ToolVerdict.Deny:
                return Write(new CallReply { Kind = "denied", Reason = decision.Reason }, McpJson.Default.CallReply);
            case ToolVerdict.RequireApproval:
                return Write(new CallReply
                {
                    Kind = "approval-required",
                    Reason = decision.Reason,
                    Approvers = [.. decision.Approval?.Approvers ?? []],
                    Roles = [.. decision.Approval?.Roles ?? []],
                    TimeoutSeconds = decision.Approval?.Timeout?.TotalSeconds,
                    ForbidSelfApproval = decision.Approval?.ForbidSelfApproval ?? true,
                }, McpJson.Default.CallReply);
        }

        IMcpToolEndpoint endpoint = await ConnectAsync(request.Server, context.Run, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.CallTimeout);
        McpCallOutcome outcome;
        try
        {
            outcome = await endpoint.CallAsync(request.Tool, request.ArgumentsJson, context.IdempotencyKey, request.UseTasks, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP server '{request.Server}' did not answer within {_config.CallTimeout.TotalSeconds:0} seconds.");
        }

        return outcome.TaskId is { } taskId
            ? Write(new CallReply { Kind = "task", TaskId = taskId, PollIntervalMs = outcome.PollIntervalMs }, McpJson.Default.CallReply)
            : Write(new CallReply { Kind = "result", Result = Redact(outcome.Result!) }, McpJson.Default.CallReply);
    }

    private async Task<byte[]> PollAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        PollStep request = Read(input, McpJson.Default.PollStep);
        IMcpToolEndpoint endpoint = await ConnectAsync(request.Server, context.Run, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.CallTimeout);
        McpTaskPoll poll = await endpoint.PollAsync(request.TaskId, timeout.Token).ConfigureAwait(false);
        return Write(new PollReply
        {
            Status = poll.Status.ToString(),
            Result = poll.Result is null ? null : Redact(poll.Result),
            Error = poll.Error is null ? null : (_config.Redactor?.Invoke(poll.Error) ?? poll.Error),
            InputRequests = poll.InputRequests,
        }, McpJson.Default.PollReply);
    }

    private async Task<byte[]> CancelAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        PollStep request = Read(input, McpJson.Default.PollStep);
        IMcpToolEndpoint endpoint = await ConnectAsync(request.Server, context.Run, cancellationToken).ConfigureAwait(false);
        await endpoint.CancelAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
        return input;
    }

    private McpToolResult Redact(McpToolResult result) =>
        _config.Redactor is { } redact ? result with { Text = redact(result.Text), StructuredJson = result.StructuredJson is null ? null : redact(result.StructuredJson) } : result;

    private static T Read<T>(byte[] json, JsonTypeInfo<T> info) => JsonSerializer.Deserialize(json, info) ?? throw new FormatException("Empty MCP step payload.");

    private static byte[] Write<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);
}

// ---- what the steps record ---------------------------------------------------------------------------------

internal sealed record ApprovalEvidence(string ApprovalId, string Subject);

internal sealed record CallStep
{
    public string Server { get; init; } = "";

    public string Tool { get; init; } = "";

    public string ArgumentsJson { get; init; } = "{}";

    public bool UseTasks { get; init; }

    public ApprovalEvidence? Approval { get; init; }
}

internal sealed record CallReply
{
    /// <summary>result, task, approval-required or denied.</summary>
    public string Kind { get; init; } = "result";

    public McpToolResult? Result { get; init; }

    public string? TaskId { get; init; }

    public long PollIntervalMs { get; init; }

    public string? Reason { get; init; }

    public string[] Approvers { get; init; } = [];

    public string[] Roles { get; init; } = [];

    public double? TimeoutSeconds { get; init; }

    public bool ForbidSelfApproval { get; init; } = true;
}

internal sealed record PollStep(string Server, string TaskId);

internal sealed record PollReply
{
    public string Status { get; init; } = nameof(McpRemoteStatus.Working);

    public McpToolResult? Result { get; init; }

    public string? Error { get; init; }

    public int InputRequests { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CallStep))]
[JsonSerializable(typeof(CallReply))]
[JsonSerializable(typeof(PollStep))]
[JsonSerializable(typeof(PollReply))]
internal sealed partial class McpJson : JsonSerializerContext;
