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
            StartRunResult started = await _client.StartAsync(_scope, workflowId, AgentFrameworkNames.Conversation, start, cancellationToken: cancellationToken).ConfigureAwait(false);
            var run = new RunKey(_scope.TenantId, _scope.NamespaceId, started.RunId);
            if (started.Started)
            {
                return new DurableTurn(workflowId, run, turnId);
            }

            // The conversation exists: its current run takes the message as a signal. The id makes a repeated delivery harmless.
            if (await _client.SignalAsync(run, AgentFrameworkNames.MessageSignal, AfCodec.Write(turn, AfJson.Default.AfTurn), turnId, cancellationToken).ConfigureAwait(false))
            {
                return new DurableTurn(workflowId, run, turnId);
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

    /// <summary>The answer to a message, or null while the agent has not finished it. Throws when the conversation's run failed.</summary>
    internal async ValueTask<AgentResponse?> TryReadAsync(AgentDefinition agent, DurableTurn turn, CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoryEvent> history = await _client.GetOpenedHistoryAsync(turn.Run, cancellationToken).ConfigureAwait(false);
        if (Read(agent, turn, history) is { } reply)
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
                throw Ended(agent, history);
        }

        // The conversation moved on to a fresh run, and the message went with it if it was not answered yet. The answer is in the current run or in one of the runs between: asking to start the same
        // workflow id finds the current run (and starts nothing), and each run names the one before it.
        StartRunResult found = await _client.StartAsync(
            _scope, turn.WorkflowId, AgentFrameworkNames.Conversation, AfCodec.Write(new AfDriverInput { Agent = agent.Name }, AfJson.Default.AfDriverInput), cancellationToken: cancellationToken).ConfigureAwait(false);
        RunKey current = new(_scope.TenantId, _scope.NamespaceId, found.RunId);
        RunKey? at = current;
        IReadOnlyList<HistoryEvent> latest = [];
        for (int hop = 0; hop < 64 && at is { } run && run != turn.Run; hop++)
        {
            IReadOnlyList<HistoryEvent> there = await _client.GetOpenedHistoryAsync(run, cancellationToken).ConfigureAwait(false);
            if (hop == 0)
            {
                latest = there;
            }

            if (Read(agent, turn, there) is { } answer)
            {
                turn.Run = run;
                return answer;
            }

            at = there.Count > 0 && there[0] is WorkflowStarted { PreviousRunId: var previous } && previous != Guid.Empty ? new RunKey(_scope.TenantId, _scope.NamespaceId, previous) : null;
        }

        return await _client.GetStateAsync(current, cancellationToken).ConfigureAwait(false) is RunState.Running or RunState.ContinuedAsNew or null ? null : throw Ended(agent, latest);
    }

    private static AgentResponse? Read(AgentDefinition agent, DurableTurn turn, IReadOnlyList<HistoryEvent> history)
    {
        var mine = new HashSet<int>();
        long input = 0, output = 0, total = 0;
        AfStepResult? last = null;
        foreach (HistoryEvent e in history)
        {
            if (e is ActivityScheduled s && (s.ActivityType == AgentFrameworkNames.Step || s.ActivityType == AgentFrameworkNames.Turn))
            {
                if (AfCodec.Read(s.Input, AfJson.Default.AfStepRequest).TurnId == turn.TurnId)
                {
                    mine.Add(s.ActivityId);
                }
            }
            else if (e is ActivityCompleted c && mine.Contains(c.ActivityId))
            {
                AfStepResult result = AfCodec.Read(c.Result, AfJson.Default.AfStepResult);
                if (result.Outcome != AfOutcomes.Done)
                {
                    continue;
                }

                input += result.InputTokens ?? 0;
                output += result.OutputTokens ?? 0;
                total += result.TotalTokens ?? 0;
                if (result.Final)
                {
                    last = result;
                }
            }
        }

        if (last?.Messages is not { } messages)
        {
            return null;
        }

        List<ChatMessage> produced = JsonSerializer.Deserialize<List<ChatMessage>>(messages, AIJsonUtilities.DefaultOptions) ?? [];
        return new AgentResponse(produced)
        {
            AgentId = agent.Name,
            ResponseId = turn.TurnId,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total },
            FinishReason = last.FinishReason is { } f ? new ChatFinishReason(f) : null,
        };
    }

    private static Exception Ended(AgentDefinition agent, IReadOnlyList<HistoryEvent> history)
    {
        if (history.Count > 0 && history[^1] is WorkflowFailed failed)
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
        StartRunResult current = await _client.StartAsync(
            _scope, WorkflowId(agent.Name, session.SessionId), AgentFrameworkNames.Conversation, AfCodec.Write(new AfDriverInput { Agent = agent.Name }, AfJson.Default.AfDriverInput), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (current.Started)
        {
            // Nothing had been said: the run just created has nothing to wait for. End it.
            await _client.SignalAsync(new RunKey(_scope.TenantId, _scope.NamespaceId, current.RunId), AgentFrameworkNames.CloseSignal, [], "close", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _client.SignalAsync(new RunKey(_scope.TenantId, _scope.NamespaceId, current.RunId), AgentFrameworkNames.CloseSignal, [], "close", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A message that was sent to a conversation, to be asked about until it is answered.</summary>
public sealed class DurableTurn
{
    internal DurableTurn(string workflowId, RunKey run, string turnId)
    {
        WorkflowId = workflowId;
        Run = run;
        TurnId = turnId;
    }

    internal string WorkflowId { get; }

    /// <summary>The run that holds the answer now (a conversation moves to a fresh run now and then).</summary>
    internal RunKey Run { get; set; }

    /// <summary>Identifies the message; sending it again with this id is harmless.</summary>
    public string TurnId { get; }
}
