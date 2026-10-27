using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>A conversation with a durable agent: its id is the Kairos workflow id of the conversation's run, so a session survives the process and can be resumed from its id.</summary>
public sealed class DurableAgentSession : AgentSession
{
    private string? _id;

    public DurableAgentSession(string agent, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Agent = agent;
        _id = sessionId;
    }

    /// <summary>A session in an executor of a durable workflow: it gets its id with its first message, from the step and the message, so a step that is run again finds the conversation it started.</summary>
    internal DurableAgentSession(string agent)
    {
        Agent = agent;
    }

    /// <summary>The durable agent this conversation is with.</summary>
    public string Agent { get; }

    /// <summary>The conversation's id.</summary>
    public string SessionId => _id ?? throw new InvalidOperationException("This session has no id yet: inside a workflow executor a session gets its id with its first message.");

    internal bool HasId => _id is not null;

    internal void Assign(string id) => _id ??= id;

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

    /// <summary>The Kairos run that holds the conversation now (its approvals are in that run's history), or null when nothing has been said yet.</summary>
    public ValueTask<RunKey?> FindRunAsync(DurableAgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _owner.FindRunAsync(_definition, session, cancellationToken);
    }

    /// <summary>
    /// Asks the conversation to stop. It stops at its next step (the model call or tool call in flight finishes and is recorded), at once if it is waiting for a message or an approval; the nested agents it asked stop with it; and the
    /// conversation's run ends as failed with the cancellation. Asking twice is one request. False when the conversation has not started or has ended.
    /// </summary>
    public ValueTask<bool> CancelAsync(DurableAgentSession session, string reason = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _owner.CancelAsync(_definition, session, reason, cancellationToken);
    }

    /// <summary>
    /// Terminates the conversation's run now, as an operator (the principal must be allowed to terminate runs). A terminated run runs no more workflow code, so the nested agents it asked are cancelled first, from here. A nested agent
    /// that is missed this way finds out by itself within <see cref="AgentFrameworkOptions.ParentWatchInterval"/> and the intervals that follow.
    /// </summary>
    public ValueTask<bool> TerminateAsync(DurableAgentSession session, Principal principal, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(principal);
        return _owner.TerminateAsync(_definition, session, principal, reason, cancellationToken);
    }

    /// <summary>Ends the conversation. Its history stays; further messages need a new session.</summary>
    public ValueTask CloseAsync(DurableAgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _owner.CloseAsync(_definition, session, cancellationToken);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        WorkflowStepScope.Current is not null
            ? new(new DurableAgentSession(_definition.Name)) // in an executor of a durable workflow: named by the step and the first message, see RunCoreAsync
            : new(new DurableAgentSession(_definition.Name, Guid.CreateVersion7(_owner.Time.GetUtcNow()).ToString("N")));

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not DurableAgentSession durable)
        {
            throw new InvalidOperationException($"A session of type {session.GetType().Name} does not belong to a durable agent.");
        }

        if (!durable.HasId && WorkflowStepScope.Current is { } step)
        {
            durable.Assign($"{WorkflowStepScope.OwnedPrefix}{step.Name("session", _definition.Name, "", "serialized")}"); // a session saved before its first message: named by the step, in the order it is asked
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

        // Inside an executor of a durable workflow the step may be run again after a crash. The conversation and the message are then named by the step and what is said, so the repeated call finds the turn it already
        // sent (and its answer) instead of asking the agent again. The wait is bounded: an activity that waits for a conversation holds a worker.
        WorkflowStepScope? step = WorkflowStepScope.Current;
        var list = messages.ToList();
        string? messageId = null;
        if (step is not null)
        {
            string content = JsonSerializer.Serialize(list, AIJsonUtilities.DefaultOptions);
            string named = step.Name("message", _definition.Name, conversation.HasId ? conversation.SessionId : "", content);
            if (!conversation.HasId)
            {
                conversation.Assign($"{WorkflowStepScope.OwnedPrefix}{named}");
            }

            messageId = $"wf-{named}";
            if (conversation.SessionId.StartsWith(WorkflowStepScope.OwnedPrefix, StringComparison.Ordinal))
            {
                step.Own(DurableAgents.WorkflowId(_definition.Name, conversation.SessionId));
            }
        }

        DurableTurn turn = await SendAsync(list, conversation, messageId, cancellationToken).ConfigureAwait(false);
        using IDisposable? waiting = step?.StartWaiting();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Look at what the run has written since the last look, and when there is no answer yet ask the engine to say when the run is written to again: the wait ends at the next write, not at the next poll.
        while (true)
        {
            AgentResponse? reply = await TryReadAsync(turn, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply;
            }

            TimeSpan? left = null;
            if (step is not null)
            {
                left = step.MaxWait - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (left <= TimeSpan.Zero)
                {
                    throw new WorkflowAgentWaitException(
                        $"Workflow '{step.Workflow}': an executor waited more than {step.MaxWait} for the answer of the conversation '{DurableAgents.WorkflowId(_definition.Name, conversation.SessionId)}' with agent '{_definition.Name}'. " +
                        "A worker is held for as long as an executor waits. A conversation that may wait for a person (an approval, a decision) should not be waited for inside an executor: have the executor raise a request through a RequestPort, " +
                        "answer it through Kairos, and let the workflow wait for it without holding a worker (see AgentFrameworkOptions.WorkflowAgentWait).");
                }
            }

            await _owner.WaitForUpdateAsync(turn, left, cancellationToken).ConfigureAwait(false);
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
