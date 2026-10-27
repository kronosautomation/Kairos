using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>
/// The durable agents of an application: <c>Get(name)</c> returns a real <see cref="AIAgent"/> whose <c>RunAsync</c> sends the message to the agent's Kairos conversation and returns its answer, so everything that takes an
/// <see cref="AIAgent"/> (workflows, hosting layers, <c>AsAIFunction()</c>, A2A) works with it unchanged.
/// </summary>
public sealed class DurableAgents
{
    private readonly AgentFrameworkRuntime _runtime;
    private readonly KairosClient _client;
    private readonly TenantScope _scope;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DurableAIAgent> _agents = new(StringComparer.Ordinal);

    public DurableAgents(AgentFrameworkRuntime runtime, KairosClient client, TenantScope scope, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        _runtime = runtime;
        _client = client;
        _scope = scope;
        _time = time;
        foreach (KeyValuePair<string, AgentDefinition> d in runtime.Definitions)
        {
            _agents.Add(d.Key, new DurableAIAgent(this, d.Value));
        }
    }

    /// <summary>The names of the durable agents.</summary>
    public IReadOnlyCollection<string> Names => _agents.Keys;

    /// <summary>The durable agent registered as <paramref name="name"/>.</summary>
    /// <exception cref="UnknownAgentException">No agent is registered under that name.</exception>
    public DurableAIAgent Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _agents.TryGetValue(name, out DurableAIAgent? agent) ? agent : throw new UnknownAgentException($"No durable agent is registered as '{name}'.");
    }

    /// <summary>The durable twin of an agent that was registered: <c>supportAgent.AsDurable(durableAgents)</c> (found by its name).</summary>
    public DurableAIAgent Of(AIAgent registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        return Get(registered.Name ?? throw new ArgumentException("The agent has no name, so it cannot have been registered.", nameof(registered)));
    }

    internal AgentFrameworkOptions Options => _runtime.Options;

    internal TimeProvider Time => _time;

    internal async ValueTask<DurableTurn> SendAsync(
        AgentDefinition agent, DurableAgentSession session, IEnumerable<ChatMessage> messages, string? messageId, CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        JsonElement json = JsonSerializer.SerializeToElement(list, AIJsonUtilities.DefaultOptions);
        if (_runtime.Redactor is { } redact)
        {
            json = SnapshotRedactor.RedactMessages(json, redact); // the client side of the same promise the driver keeps: the redactor sees the text before it is recorded
        }

        string turnId = messageId ?? Guid.CreateVersion7(_time.GetUtcNow()).ToString("N");
        var turn = new AfTurn { TurnId = turnId, Messages = json };
        string workflowId = WorkflowId(agent.Name, session.SessionId);
        var input = new AfDriverInput
        {
            Agent = agent.Name,
            Pending = [turn],
            OnBehalfOf = session.OnBehalfOf,
            IdleSeconds = _runtime.Options.ConversationIdleTimeout?.TotalSeconds,
            ContinueAfterEvents = _runtime.Options.ContinueAfterEvents,
        };

        byte[] start = AfCodec.Write(input, AfJson.Default.AfDriverInput);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            StartRunResult started = await _client.StartAsync(_scope, workflowId, AgentFrameworkNames.ConversationType(agent.Name), start, cancellationToken: cancellationToken).ConfigureAwait(false);
            var run = new RunKey(_scope.TenantId, _scope.NamespaceId, started.RunId);
            if (started.Started)
            {
                return new DurableTurn(workflowId, run, turnId); // a run of its own: all of it is the answer's neighbourhood
            }

            // The conversation exists: its current run takes the message as a signal. The id makes a repeated delivery harmless. What the conversation did before the message was sent is none of this
            // message's business, so reading starts where the history stood a moment ago - unless the caller chose the id, in which case this may be a repeat of a message that was answered long ago, and the answer is
            // read from the start of the run.
            long from = messageId is null ? (await _client.GetHistoryTailAsync(run, 1, cancellationToken).ConfigureAwait(false)).LastSequence : 0;
            if (await _client.SignalAsync(run, AgentFrameworkNames.MessageSignal, AfCodec.Write(turn, AfJson.Default.AfTurn), turnId, cancellationToken).ConfigureAwait(false))
            {
                return new DurableTurn(workflowId, run, turnId) { Cursor = from };
            }

            RunState? state = await _client.GetStateAsync(run, cancellationToken).ConfigureAwait(false);
            if (state is RunState.Completed or RunState.Failed)
            {
                throw new ConversationEndedException(
                    $"The conversation '{session.SessionId}' with agent '{agent.Name}' has ended ({state}). Start a new session with CreateSessionAsync().");
            }

            // It handed over to a fresh run between the lookup and the signal: look again.
            await Task.Delay(TimeSpan.FromMilliseconds(10 << attempt), _time, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"The conversation '{session.SessionId}' could not take the message: its run kept changing.");
    }

    /// <summary>How many events one read of the history asks for.</summary>
    private const int PageSize = 200;

    /// <summary>Reads what the run has written since this message was last looked at, and nothing before: the cost of a look is the cost of what is new, not of the conversation.</summary>
    private async ValueTask<AgentResponse?> ScanAsync(AgentDefinition agent, DurableTurn turn, CancellationToken cancellationToken)
    {
        while (true)
        {
            HistoryPage page = await _client.GetOpenedHistoryAfterAsync(turn.Run, turn.Cursor, PageSize, cancellationToken).ConfigureAwait(false);
            turn.Reader.Add(page.Events);
            if (page.Events.Count > 0)
            {
                turn.Cursor = page.Events[^1].Sequence;
            }

            if (page.Events.Count < PageSize)
            {
                return turn.Reader.Reply(agent);
            }
        }
    }

    /// <summary>Completes when the conversation's run has something this message has not looked at, or after <see cref="AgentFrameworkOptions.ReplyWait"/>; the engine ends the wait the moment the run is written to.</summary>
    internal ValueTask<bool> WaitForUpdateAsync(DurableTurn turn, CancellationToken cancellationToken) =>
        _client.WaitForHistoryAsync(turn.Run, turn.Cursor, _runtime.Options.ReplyWait, cancellationToken);

    /// <summary>The answer to a message, or null while the agent has not finished it. Throws when the conversation's run failed.</summary>
    internal async ValueTask<AgentResponse?> TryReadAsync(AgentDefinition agent, DurableTurn turn, CancellationToken cancellationToken)
    {
        if (await ScanAsync(agent, turn, cancellationToken).ConfigureAwait(false) is { } reply)
        {
            return reply;
        }

        switch (await _client.GetStateAsync(turn.Run, cancellationToken).ConfigureAwait(false) ?? RunState.Running)
        {
            case RunState.Running:
                return null;
            case RunState.ContinuedAsNew:
                break;
            default:
                // Closed: what it wrote last may have come between the look and the state.
                return await ScanAsync(agent, turn, cancellationToken).ConfigureAwait(false) is { } late ? late : throw Ended(agent, turn.Reader.Last);
        }

        // The conversation moved on to a fresh run, and the message went with it if it was not answered yet. The answer is in the current run or in one of the runs between: asking to start the same
        // workflow id finds the current run (and starts nothing), and each run names the one before it.
        RunKey current = await _client.GetCurrentRunAsync(_scope, turn.WorkflowId, cancellationToken).ConfigureAwait(false) ?? turn.Run;
        RunKey? at = current;
        IReadOnlyList<HistoryEvent> latest = [];
        for (int hop = 0; hop < 64 && at is { } run && run != turn.Run; hop++)
        {
            IReadOnlyList<HistoryEvent> there = await _client.GetOpenedHistoryAsync(run, cancellationToken).ConfigureAwait(false);
            if (hop == 0)
            {
                latest = there;
            }

            var reader = new TurnReader(turn.TurnId);
            reader.Add(there);
            if (reader.Reply(agent) is { } answer)
            {
                turn.Follow(run, reader, there);
                return answer;
            }

            at = there.Count > 0 && there[0] is WorkflowStarted { PreviousRunId: var previous } && previous != Guid.Empty ? new RunKey(_scope.TenantId, _scope.NamespaceId, previous) : null;
        }

        // Not answered yet: from now on watch the run that holds the message, and read it from where this look ended.
        if (current != turn.Run)
        {
            var reader = new TurnReader(turn.TurnId);
            reader.Add(latest);
            turn.Follow(current, reader, latest);
        }

        return await _client.GetStateAsync(current, cancellationToken).ConfigureAwait(false) is RunState.Running or RunState.ContinuedAsNew or null ? null : throw Ended(agent, latest.Count > 0 ? latest[^1] : null);
    }

    private static Exception Ended(AgentDefinition agent, HistoryEvent? last)
    {
        if (last is WorkflowFailed failed)
        {
            return failed.ErrorType switch
            {
                nameof(BudgetExceededException) => new BudgetExceededException(failed.Message) { Limit = QuotedName(failed.Message) },
                nameof(DefinitionChangedException) => new DefinitionChangedException(failed.Message),
                nameof(AgentStepLimitException) => new AgentStepLimitException(failed.Message),
                _ => new ConversationEndedException($"The conversation with agent '{agent.Name}' failed ({failed.ErrorType}): {failed.Message}"),
            };
        }

        return new ConversationEndedException($"The conversation with agent '{agent.Name}' has ended.");
    }

    /// <summary>The name between single quotes in a message such as "The budget 'daily-tokens' was reached.", or null.</summary>
    private static string? QuotedName(string message)
    {
        int open = message.IndexOf('\'', StringComparison.Ordinal);
        int close = open < 0 ? -1 : message.IndexOf('\'', open + 1);
        return close > open ? message[(open + 1)..close] : null;
    }

    internal static string WorkflowId(string agent, string session) => $"af/{agent}/{session}";

    internal async ValueTask CloseAsync(AgentDefinition agent, DurableAgentSession session, CancellationToken cancellationToken)
    {
        // A conversation that never started has nothing to close.
        if (await _client.GetCurrentRunAsync(_scope, WorkflowId(agent.Name, session.SessionId), cancellationToken).ConfigureAwait(false) is { } run)
        {
            await _client.SignalAsync(run, AgentFrameworkNames.CloseSignal, [], "close", cancellationToken).ConfigureAwait(false);
        }
    }

    internal ValueTask<RunKey?> FindRunAsync(AgentDefinition agent, DurableAgentSession session, CancellationToken cancellationToken) =>
        _client.GetCurrentRunAsync(_scope, WorkflowId(agent.Name, session.SessionId), cancellationToken);
}

/// <summary>A message that was sent to a conversation, to be asked about until it is answered.</summary>
public sealed class DurableTurn
{
    internal DurableTurn(string workflowId, RunKey run, string turnId)
    {
        WorkflowId = workflowId;
        Run = run;
        TurnId = turnId;
        Reader = new TurnReader(turnId);
    }

    internal string WorkflowId { get; }

    /// <summary>The run that holds the answer now (a conversation moves to a fresh run now and then).</summary>
    internal RunKey Run { get; private set; }

    /// <summary>The sequence number of the last event of <see cref="Run"/> that was looked at: the next look reads what comes after it. Looking at a turn is for one caller at a time.</summary>
    internal long Cursor { get; set; }

    internal TurnReader Reader { get; private set; }

    /// <summary>The conversation moved on to <paramref name="run"/>, of which <paramref name="seen"/> has been looked at with <paramref name="reader"/>.</summary>
    internal void Follow(RunKey run, TurnReader reader, IReadOnlyList<HistoryEvent> seen)
    {
        Run = run;
        Reader = reader;
        Cursor = seen.Count > 0 ? seen[^1].Sequence : 0;
    }

    /// <summary>Identifies the message; sending it again with this id is harmless.</summary>
    public string TurnId { get; }
}
