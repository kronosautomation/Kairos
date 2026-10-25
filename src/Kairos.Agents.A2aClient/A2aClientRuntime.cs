using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using A2A;
using Kairos.A2A;
using A2ARole = global::A2A.Role;

namespace Kairos.Agents.A2aClient;

public enum A2aState
{
    /// <summary>The agent answered with a message and no task.</summary>
    Message,
    Submitted,
    Working,
    Completed,
    Failed,
    Canceled,
    InputRequired,
    Rejected,
    AuthRequired,
}

/// <summary>What a remote agent said: a message, or the state of a task with the text and data it has produced so far.</summary>
public sealed record A2aReply(A2aState State, string? Text, string? DataJson, string? TaskId, string? ContextId, string? StatusText);

/// <summary>One remote agent, reduced to what a durable step needs. <see cref="A2aClientEndpoint"/> is the SDK-backed implementation.</summary>
public interface IA2aAgentEndpoint
{
    /// <param name="messageId">Stable across attempts of one step. A2A servers de-duplicate on the message id, so a retry does not start the work twice.</param>
    /// <param name="text">The text part, if any.</param>
    /// <param name="dataJson">A structured (JSON object) part, if any.</param>
    /// <param name="taskId">Set to continue an existing task (for example to answer an input-required question).</param>
    /// <param name="contextId">Set to continue an existing conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<A2aReply> SendAsync(string messageId, string? text, string? dataJson, string? taskId, string? contextId, CancellationToken cancellationToken);

    ValueTask<A2aReply> GetTaskAsync(string taskId, CancellationToken cancellationToken);

    ValueTask CancelTaskAsync(string taskId, CancellationToken cancellationToken);
}

/// <summary>Adapts an SDK <see cref="IA2AClient"/>. The call returns as soon as the agent has accepted the work; progress is read by polling.</summary>
public sealed class A2aClientEndpoint(IA2AClient client) : IA2aAgentEndpoint
{
    public async ValueTask<A2aReply> SendAsync(string messageId, string? text, string? dataJson, string? taskId, string? contextId, CancellationToken cancellationToken)
    {
        var parts = new List<Part>();
        if (text is not null)
        {
            parts.Add(Part.FromText(text));
        }

        if (dataJson is not null)
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            parts.Add(Part.FromData(doc.RootElement.Clone()));
        }

        var request = new SendMessageRequest
        {
            Message = new Message { Role = A2ARole.User, MessageId = messageId, Parts = parts, TaskId = taskId, ContextId = contextId },
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        };
        SendMessageResponse response = await client.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
        return response.PayloadCase == SendMessageResponseCase.Task
            ? Map(response.Task!)
            : new A2aReply(A2aState.Message, TextOf(response.Message!.Parts), DataOf(response.Message.Parts), response.Message.TaskId, response.Message.ContextId, null);
    }

    public async ValueTask<A2aReply> GetTaskAsync(string taskId, CancellationToken cancellationToken) =>
        Map(await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken).ConfigureAwait(false));

    public async ValueTask CancelTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cancellationToken).ConfigureAwait(false);
        }
        catch (A2AException ex) when (ex.ErrorCode is A2AErrorCode.TaskNotCancelable or A2AErrorCode.TaskNotFound)
        {
            // Already finished, or gone: there is nothing left to cancel, which is what the caller wanted.
        }
    }

    private static A2aReply Map(AgentTask task)
    {
        IEnumerable<Part> artifactParts = task.Artifacts?.SelectMany(a => a.Parts) ?? [];
        List<Part> parts = [.. artifactParts];
        A2aState state = task.Status.State switch
        {
            TaskState.Submitted => A2aState.Submitted,
            TaskState.Working => A2aState.Working,
            TaskState.Completed => A2aState.Completed,
            TaskState.Failed => A2aState.Failed,
            TaskState.Canceled => A2aState.Canceled,
            TaskState.InputRequired => A2aState.InputRequired,
            TaskState.Rejected => A2aState.Rejected,
            TaskState.AuthRequired => A2aState.AuthRequired,
            _ => A2aState.Working,
        };
        return new A2aReply(state, TextOf(parts), DataOf(parts), task.Id, task.ContextId, task.Status.Message is { } m ? TextOf(m.Parts) : null);
    }

    private static string? TextOf(IEnumerable<Part> parts)
    {
        string text = string.Join("\n", parts.Where(p => p.Text is not null).Select(p => p.Text));
        return text.Length == 0 ? null : text;
    }

    private static string? DataOf(IEnumerable<Part> parts) => parts.FirstOrDefault(p => p.Data is not null)?.Data!.Value.GetRawText();
}

/// <summary>The Agent Card was missing, unsigned or signed by nobody you trust, and the connection requires a signed card.</summary>
public sealed class UntrustedAgentCardException : Exception
{
    public UntrustedAgentCardException()
        : base("The Agent Card is not trusted.")
    {
    }

    public UntrustedAgentCardException(string message)
        : base(message)
    {
    }

    public UntrustedAgentCardException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No agent is registered under that name.</summary>
public sealed class UnknownA2aAgentException : Exception
{
    public UnknownA2aAgentException()
        : base("Unknown A2A agent.")
    {
    }

    public UnknownA2aAgentException(string message)
        : base(message)
    {
    }

    public UnknownA2aAgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reaches a remote agent from its address: fetches its Agent Card, requires a valid signature from a key you trust, and builds an SDK client for
/// the interface the card advertises. The card is cached for <see cref="CardLifetime"/>.
/// <para>
/// A signed card is the default and the only thing accepted unless you call <see cref="AllowUnsignedCard"/>. Even then a card that carries a
/// signature must carry a valid one: an unsigned card is merely unvouched for, a card with a bad signature is a forgery.
/// </para>
/// </summary>
public sealed class A2aAgentConnector(Uri baseAddress, HttpClient http, AgentCardVerifier? verifier = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _allowUnsigned;
    private IA2aAgentEndpoint? _endpoint;
    private DateTimeOffset _fetched;

    public TimeSpan CardLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>True unless <see cref="AllowUnsignedCard"/> was called: a card without a valid signature from a trusted key is refused.</summary>
    public bool RequireSignedCard => !_allowUnsigned;

    /// <summary>
    /// Accepts a card that has no signature at all, for an agent whose publisher does not sign (a prototype, a partner who has not caught up).
    /// This is a decision about one agent, made in code where it can be reviewed. A card that has a signature that does not verify is still refused.
    /// </summary>
    public A2aAgentConnector AllowUnsignedCard()
    {
        _allowUnsigned = true;
        return this;
    }

    private static bool HasSignatures(byte[] raw)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("signatures", out JsonElement s) &&
                !(s.ValueKind == JsonValueKind.Array && s.GetArrayLength() == 0) && s.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return true; // not even JSON: let the verifier refuse it
        }
    }

    public void Dispose() => _gate.Dispose();

    public async ValueTask<IA2aAgentEndpoint> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoint is not null && Time.GetUtcNow() - _fetched < CardLifetime)
            {
                return _endpoint;
            }

            var cardUri = new Uri(baseAddress, ".well-known/agent-card.json");
            if (verifier is null && RequireSignedCard)
            {
                // Refused before anything is fetched: there is no way this card could be accepted.
                throw new UntrustedAgentCardException(
                    $"Signed Agent Cards are required for {baseAddress} and no verifier with trusted keys was given. Give the connector a verifier, or call AllowUnsignedCard() to accept this agent without one.");
            }

            byte[] raw = await http.GetByteArrayAsync(cardUri, cancellationToken).ConfigureAwait(false);
            if (verifier is not null && (RequireSignedCard || HasSignatures(raw)))
            {
                // Checked on the bytes as received, so fields this SDK version does not model are part of what was signed.
                AgentCardVerification result = verifier.VerifyJson(raw);
                if (!result.IsValid)
                {
                    throw new UntrustedAgentCardException($"The Agent Card at {cardUri} is not trusted: {result.Reason}.");
                }
            }

            AgentCard card = JsonSerializer.Deserialize(raw, (JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)))
                ?? throw new UntrustedAgentCardException("The Agent Card is empty.");
            _endpoint = new A2aClientEndpoint(A2AClientFactory.Create(card, http, new A2AClientOptions()));
            _fetched = Time.GetUtcNow();
            return _endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Collects the remote agents a workflow may call. <see cref="Build"/> makes the runtime.</summary>
public sealed class A2aClientsBuilder
{
    private readonly Dictionary<string, Func<RunKey, CancellationToken, ValueTask<IA2aAgentEndpoint>>> _agents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnly = new(StringComparer.Ordinal);

    internal IToolPolicy Policy { get; private set; } = new BaselineToolPolicy();

    internal Func<string, string>? Redactor { get; private set; }

    internal TimeSpan CallTimeout { get; private set; } = TimeSpan.FromSeconds(60);

    internal Dictionary<string, Func<RunKey, CancellationToken, ValueTask<IA2aAgentEndpoint>>> Agents => _agents;

    internal HashSet<string> ReadOnly => _readOnly;

    public A2aClientsBuilder AddAgent(string name, Func<RunKey, CancellationToken, ValueTask<IA2aAgentEndpoint>> connect)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(connect);
        _agents[name] = connect;
        return this;
    }

    public A2aClientsBuilder AddAgent(string name, IA2aAgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return AddAgent(name, (_, _) => ValueTask.FromResult(endpoint));
    }

    /// <summary>Registers an agent by address. Its Agent Card must carry a valid signature from a key in <paramref name="verifier"/>.</summary>
    public A2aClientsBuilder AddAgent(string name, Uri baseAddress, HttpClient http, AgentCardVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier, nameof(verifier) + " (to accept an agent without signed cards, register new A2aAgentConnector(...).AllowUnsignedCard())");
        return AddAgent(name, new A2aAgentConnector(baseAddress, http, verifier));
    }

    /// <summary>Registers an agent through a connector, for options such as <see cref="A2aAgentConnector.AllowUnsignedCard"/>.</summary>
    public A2aClientsBuilder AddAgent(string name, A2aAgentConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        return AddAgent(name, (_, ct) => connector.ConnectAsync(ct));
    }

    /// <summary>Declares agents that only answer questions. Every agent not listed here is treated as acting on the world and waits for an approval unless the policy allows it.</summary>
    public A2aClientsBuilder ReadOnlyAgents(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (string n in names)
        {
            _readOnly.Add(n);
        }

        return this;
    }

    /// <summary>The policy for calls; agents are named <c>a2a.&lt;name&gt;</c>. Share the instance you gave <see cref="AgentsBuilder"/>.</summary>
    public A2aClientsBuilder UsePolicy(IToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        return this;
    }

    public A2aClientsBuilder UseRedactor(Func<string, string> redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        Redactor = redactor;
        return this;
    }

    public A2aClientsBuilder WithCallTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        CallTimeout = timeout;
        return this;
    }

    public A2aClientRuntime Build() => new(this);
}

/// <summary>The worker side of the A2A client steps. Register <see cref="Activities"/> on every worker that runs workflows calling A2A agents.</summary>
public sealed class A2aClientRuntime
{
    public const string SendActivity = "kairos.a2a.send";
    public const string PollActivity = "kairos.a2a.poll";
    public const string CancelActivity = "kairos.a2a.cancel";

    private readonly A2aClientsBuilder _config;
    private readonly Dictionary<string, ActivityFunc> _activities;

    internal A2aClientRuntime(A2aClientsBuilder config)
    {
        _config = config;
        _activities = new Dictionary<string, ActivityFunc>(StringComparer.Ordinal)
        {
            [SendActivity] = SendAsync,
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

    private async ValueTask<IA2aAgentEndpoint> ConnectAsync(string agent, RunKey run, CancellationToken cancellationToken) =>
        _config.Agents.TryGetValue(agent, out var connect)
            ? await connect(run, cancellationToken).ConfigureAwait(false)
            : throw new UnknownA2aAgentException($"No A2A agent is registered as '{agent}'.");

    private async Task<byte[]> SendAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        SendStep request = Read(input, A2aJson.Default.SendStep);
        if (!_config.Agents.ContainsKey(request.Agent))
        {
            throw new UnknownA2aAgentException($"No A2A agent is registered as '{request.Agent}'.");
        }

        var tool = new AgentTool(
            "a2a." + request.Agent, "remote A2A agent", """{"type":"object"}""",
            _config.ReadOnly.Contains(request.Agent) ? ToolAccess.ReadOnly : ToolAccess.SideEffecting, (_, _) => ValueTask.FromResult(""));
        ApprovalDecision? approval = request.Approval is null
            ? null
            : new ApprovalDecision(request.Approval.ApprovalId, ApprovalOutcome.Approved, new ApproverIdentity(request.Approval.Subject), null, null);

        ToolDecision decision = await _config.Policy.EvaluateAsync(new ToolPolicyContext(context.Run, tool, request.Text ?? request.DataJson ?? "", approval), cancellationToken).ConfigureAwait(false);
        switch (decision.Verdict)
        {
            case ToolVerdict.Deny:
                return Write(new SendReply { Kind = "denied", Reason = decision.Reason }, A2aJson.Default.SendReply);
            case ToolVerdict.RequireApproval:
                return Write(new SendReply
                {
                    Kind = "approval-required",
                    Reason = decision.Reason,
                    Approvers = [.. decision.Approval?.Approvers ?? []],
                    Roles = [.. decision.Approval?.Roles ?? []],
                    TimeoutSeconds = decision.Approval?.Timeout?.TotalSeconds,
                    ForbidSelfApproval = decision.Approval?.ForbidSelfApproval ?? true,
                }, A2aJson.Default.SendReply);
        }

        IA2aAgentEndpoint endpoint = await ConnectAsync(request.Agent, context.Run, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.CallTimeout);
        A2aReply reply;
        try
        {
            // The message id is the step's idempotency key: every attempt sends the same message, and a server that de-duplicates on it does the work once.
            reply = await endpoint.SendAsync(context.IdempotencyKey, request.Text, request.DataJson, request.TaskId, request.ContextId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"A2A agent '{request.Agent}' did not answer within {_config.CallTimeout.TotalSeconds:0} seconds.");
        }

        return Write(new SendReply { Kind = "reply", Reply = Redact(reply) }, A2aJson.Default.SendReply);
    }

    private async Task<byte[]> PollAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        PollStep request = Read(input, A2aJson.Default.PollStep);
        IA2aAgentEndpoint endpoint = await ConnectAsync(request.Agent, context.Run, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.CallTimeout);
        return Write(new SendReply { Kind = "reply", Reply = Redact(await endpoint.GetTaskAsync(request.TaskId, timeout.Token).ConfigureAwait(false)) }, A2aJson.Default.SendReply);
    }

    private async Task<byte[]> CancelAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        PollStep request = Read(input, A2aJson.Default.PollStep);
        IA2aAgentEndpoint endpoint = await ConnectAsync(request.Agent, context.Run, cancellationToken).ConfigureAwait(false);
        await endpoint.CancelTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
        return input;
    }

    private A2aReply Redact(A2aReply r) => _config.Redactor is { } redact
        ? r with { Text = r.Text is null ? null : redact(r.Text), DataJson = r.DataJson is null ? null : redact(r.DataJson), StatusText = r.StatusText is null ? null : redact(r.StatusText) }
        : r;

    private static T Read<T>(byte[] json, JsonTypeInfo<T> info) => JsonSerializer.Deserialize(json, info) ?? throw new FormatException("Empty A2A step payload.");

    private static byte[] Write<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);
}

internal sealed record ApprovalEvidence(string ApprovalId, string Subject);

internal sealed record SendStep
{
    public string Agent { get; init; } = "";

    public string? Text { get; init; }

    public string? DataJson { get; init; }

    public string? TaskId { get; init; }

    public string? ContextId { get; init; }

    public ApprovalEvidence? Approval { get; init; }
}

internal sealed record SendReply
{
    /// <summary>reply, approval-required or denied.</summary>
    public string Kind { get; init; } = "reply";

    public A2aReply? Reply { get; init; }

    public string? Reason { get; init; }

    public string[] Approvers { get; init; } = [];

    public string[] Roles { get; init; } = [];

    public double? TimeoutSeconds { get; init; }

    public bool ForbidSelfApproval { get; init; } = true;
}

internal sealed record PollStep(string Agent, string TaskId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SendStep))]
[JsonSerializable(typeof(SendReply))]
[JsonSerializable(typeof(PollStep))]
internal sealed partial class A2aJson : JsonSerializerContext;
