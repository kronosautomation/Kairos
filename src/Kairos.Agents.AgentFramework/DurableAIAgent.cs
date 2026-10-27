using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>A conversation with a durable agent: its id is the Kairos workflow id of the conversation's run, so a session survives the process and can be resumed from its id.</summary>
public sealed class DurableAgentSession : AgentSession
{
    public DurableAgentSession(string agent, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Agent = agent;
        SessionId = sessionId;
    }

    /// <summary>The durable agent this conversation is with.</summary>
    public string Agent { get; }

    /// <summary>The conversation's id.</summary>
    public string SessionId { get; }

    /// <summary>
    /// Who the agent acts for in this conversation (the requester of its approvals, whom the approver may not be, and the identity credentials are issued on behalf of). Read when the conversation's run starts: set it before the first message.
    /// </summary>
    public string? OnBehalfOf { get; set; }
}

/// <summary>
/// A registered agent as callers see it: a real <see cref="AIAgent"/> whose <c>RunAsync</c> sends the messages to the agent's Kairos conversation (starting it on the first message) and returns the answer. Everything the agent does
/// happens on the workers, recorded step by step; this object holds no state and can be created anywhere the application has a Kairos client.
/// </summary>
public sealed class DurableAIAgent : AIAgent
{
    private readonly DurableAgents _owner;
    private readonly AgentDefinition _definition;

    internal DurableAIAgent(DurableAgents owner, AgentDefinition definition)
    {
        _owner = owner;
        _definition = definition;
    }

    public override string? Name => _definition.Name;

    protected override string? IdCore => _definition.Name;

    public override string? Description => _definition.Agent.Description;

    /// <summary>A session for the conversation with this id (starting a new conversation, or resuming one that exists).</summary>
    public DurableAgentSession Session(string sessionId) => new(_definition.Name, sessionId);

    /// <summary>
    /// Sends the messages to the conversation and returns at once; ask <see cref="TryReadAsync"/> for the answer. A caller that does not know whether a send arrived (the call threw after the message may have been stored)
    /// sends again with the same <paramref name="messageId"/>: the conversation answers a message id once.
    /// </summary>
    public ValueTask<DurableTurn> SendAsync(IEnumerable<ChatMessage> messages, DurableAgentSession session, string? messageId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(session);
        return _owner.SendAsync(_definition, session, messages, messageId, cancellationToken);
    }

    /// <summary>The answer to a message sent with <see cref="SendAsync"/>, or null while the agent is still working on it.</summary>
    public ValueTask<AgentResponse?> TryReadAsync(DurableTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return _owner.TryReadAsync(_definition, turn, cancellationToken);
    }

    /// <summary>Ends the conversation. Its history stays; further messages need a new session.</summary>
    public ValueTask CloseAsync(DurableAgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _owner.CloseAsync(_definition, session, cancellationToken);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new DurableAgentSession(_definition.Name, Guid.CreateVersion7(_owner.Time.GetUtcNow()).ToString("N")));

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not DurableAgentSession durable)
        {
            throw new InvalidOperationException($"A session of type {session.GetType().Name} does not belong to a durable agent.");
        }

        return new(JsonSerializer.SerializeToElement(new SessionJson(durable.Agent, durable.SessionId, durable.OnBehalfOf), SessionJsonContext.Default.SessionJson));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        SessionJson? saved = serializedState.Deserialize(SessionJsonContext.Default.SessionJson);
        return saved is null || string.IsNullOrWhiteSpace(saved.SessionId) || saved.Agent != _definition.Name
            ? throw new JsonException($"This is not a saved session of the durable agent '{_definition.Name}'.")
            : new(new DurableAgentSession(saved.Agent, saved.SessionId) { OnBehalfOf = saved.OnBehalfOf });
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        DurableAgentSession conversation = session switch
        {
            null => (DurableAgentSession)await CreateSessionAsync(cancellationToken).ConfigureAwait(false),
            DurableAgentSession durable when durable.Agent == _definition.Name => durable,
            _ => throw new InvalidOperationException($"This session does not belong to the durable agent '{_definition.Name}'."),
        };
        DurableTurn turn = await SendAsync(messages, conversation, cancellationToken: cancellationToken).ConfigureAwait(false);

        // There is no channel from the engine back to the caller, and the engine has no wait-for-run operation to ask: look at the history, quickly at first and then less often.
        TimeSpan wait = _owner.Options.MinReplyPollInterval;
        while (true)
        {
            AgentResponse? reply = await TryReadAsync(turn, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply;
            }

            await Task.Delay(wait, _owner.Time, cancellationToken).ConfigureAwait(false);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 3 / 2 + 1, _owner.Options.MaxReplyPollInterval.Ticks));
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming is not durable in this release: the answer is recorded whole, so it is delivered whole.
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        foreach (AgentResponseUpdate update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }

}

internal sealed record SessionJson(string Agent, string SessionId, string? OnBehalfOf);

[System.Text.Json.Serialization.JsonSerializable(typeof(SessionJson))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SessionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

public static class DurableAgentExtensions
{
    /// <summary>The durable twin of an agent that was registered with <c>AddAgentFramework</c> (found by its name): the one line that turns the agent you have into a durable one.</summary>
    public static DurableAIAgent AsDurable(this AIAgent agent, DurableAgents durableAgents)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(durableAgents);
        return durableAgents.Of(agent);
    }

    /// <summary>
    /// Offers a durable agent to another agent as a tool. When the calling agent is registered, a call to it runs the nested agent as a durable child run: its tools go through the tool policy, its approvals appear in the
    /// approvals inbox and its model calls count against the budgets, like any other agent's.
    /// </summary>
    public static AIFunction AsAIFunction(this DurableAIAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new DurableAgentFunction(agent.Name!, agent.Description);
    }
}
