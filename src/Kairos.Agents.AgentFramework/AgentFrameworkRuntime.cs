using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>The activity and workflow types the adapter registers. Register <see cref="AgentFrameworkRuntime"/>'s activities on every worker that can run durable agents.</summary>
public static class AgentFrameworkNames
{
    /// <summary>The prefix of the workflow types that run a conversation, one type per agent: <see cref="ConversationType"/>.</summary>
    public const string Conversation = "kairos.af.agent";

    /// <summary>
    /// The workflow type that runs conversations with the agent called <paramref name="agent"/>. There is one per agent so that a worker that does not have the agent does not register it: the engine then leaves the
    /// run for a worker that does, which is what a rolling deployment of a new agent needs. It also shows which agent a run belongs to in a list of runs.
    /// </summary>
    public static string ConversationType(string agent) => $"{Conversation}/{agent}";

    /// <summary>One model call of an agent (the agent runs one step; Kairos runs the loop).</summary>
    public const string Step = AgentActivityNames.FrameworkStep;

    /// <summary>One whole turn of an agent that cannot be run a step at a time.</summary>
    public const string Turn = AgentActivityNames.FrameworkTurn;

    internal const string Override = "kairos.af.override";
    internal const string ChildCheck = "kairos.af.child.check";
    internal const string ChildNotify = "kairos.af.child.notify";
    internal const string ChildCancel = "kairos.af.child.cancel";
    internal const string ParentWatch = "kairos.af.parent.watch";

    /// <summary>The signal a conversation waits on for its next message. (Not under the engine's <c>kairos.</c> prefix: callers may send it.)</summary>
    public const string MessageSignal = "af.message";

    /// <summary>The signal that ends a conversation.</summary>
    public const string CloseSignal = "af.close";
}

/// <summary>
/// The configured adapter: the registered agents, the Kairos agent runtime they share with native agents (tool policy, budgets, redaction, approvals), and the activities and the workflow that make them durable.
/// Normally created by <c>AddAgentFramework</c>; tests and hosts that wire workers by hand use <see cref="Create"/>.
/// </summary>
public sealed partial class AgentFrameworkRuntime
{
    private readonly Dictionary<string, AgentDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActivityFunc> _activities;
    private KairosClient? _client;

    private AgentFrameworkRuntime(AgentsRuntime agents, AgentFrameworkOptions options, IReadOnlyList<AgentDefinition> definitions, IReadOnlyList<string> unclassified)
    {
        Agents = agents;
        Options = options;
        foreach (AgentDefinition d in definitions)
        {
            _definitions.Add(d.Name, d);
        }

        Unclassified = unclassified;
        Snapshots = new SnapshotCache(options.SnapshotCacheBytes);
        _activities = new Dictionary<string, ActivityFunc>(agents.Activities, StringComparer.Ordinal)
        {
            [AgentFrameworkNames.Step] = (context, input, ct) => StepAsync(context, input, stepped: true, ct),
            [AgentFrameworkNames.Turn] = (context, input, ct) => StepAsync(context, input, stepped: false, ct),
            [AgentFrameworkNames.Override] = (_, input, _) => Task.FromResult(input),
            [AgentFrameworkNames.ChildCheck] = ChildCheckAsync,
            [AgentFrameworkNames.ChildNotify] = ChildNotifyAsync,
            [AgentFrameworkNames.ChildCancel] = ChildCancelAsync,
            [AgentFrameworkNames.ParentWatch] = ParentWatchAsync,
        };
        Conversation = agents.Workflow(new AgentFrameworkDriver(this).RunAsync);
    }

    internal AgentsRuntime Agents { get; }

    internal AgentFrameworkOptions Options { get; }

    /// <summary>The sessions of the steps this worker ran, so that the next step of a conversation need not be sent its session again.</summary>
    internal SnapshotCache Snapshots { get; }

    internal IReadOnlyDictionary<string, AgentDefinition> Definitions => _definitions;

    /// <summary>Tools nobody classified (not marked read-only, allowed without approval or required to have approval, and not wrapped in an approval requirement by their author): they are treated as side-effecting.</summary>
    public IReadOnlyList<string> Unclassified { get; }

    /// <summary>The names of the registered agents.</summary>
    public IReadOnlyCollection<string> AgentNames => _definitions.Keys;

    /// <summary>The activities to register on a worker: the adapter's and the agent runtime's.</summary>
    public IReadOnlyDictionary<string, ActivityFunc> Activities => _activities;

    /// <summary>The workflow that runs a conversation, registered as <see cref="AgentFrameworkNames.ConversationType"/>.</summary>
    public WorkflowFunc Conversation { get; }

    internal KairosClient Client => _client ?? throw new InvalidOperationException("The adapter is not connected to a Kairos client yet. In a host, AddAgentFramework does this when the host starts; otherwise call Bind.");

    /// <summary>Gives the runtime the client nested agents are started and answered with (the worker side of the adapter needs it only for nested agents).</summary>
    public void Bind(KairosClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Puts the activities into a registry (and the workflow into a workflow registry, when given).</summary>
    public void RegisterInto(ActivityRegistry activities, WorkflowRegistry? workflows = null)
    {
        ArgumentNullException.ThrowIfNull(activities);
        foreach ((string name, ActivityFunc activity) in _activities)
        {
            activities.Register(name, activity);
        }

        foreach (string agent in _definitions.Keys)
        {
            workflows?.Register(AgentFrameworkNames.ConversationType(agent), KairosClient.DefaultBuildId, Conversation);
        }
    }

    internal AgentDefinition Definition(string name) =>
        _definitions.TryGetValue(name, out AgentDefinition? d) ? d : throw new UnknownAgentException($"No durable agent is registered as '{name}' on this worker.");

    internal Func<string, string>? Redactor => Agents.Config.Redactor;

    /// <summary>Builds the adapter from a builder. Throws when an agent cannot be made durable as registered, naming the option that allows it.</summary>
    public static AgentFrameworkRuntime Create(AgentFrameworkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var definitions = new List<AgentDefinition>();
        foreach (AgentRegistration registration in builder.Agents)
        {
            AgentDefinition definition = AgentDefinition.Analyze(registration);
            if (definitions.Exists(d => d.Name == definition.Name))
            {
                throw new InvalidOperationException($"Two agents are registered as '{definition.Name}'.");
            }

            definitions.Add(definition);
        }

        foreach (AgentDefinition d in definitions)
        {
            foreach (string child in d.Children)
            {
                if (!definitions.Exists(x => x.Name == child))
                {
                    throw new InvalidOperationException($"Agent '{d.Name}' calls the durable agent '{child}', which is not registered.");
                }
            }
        }

        var agentsBuilder = new AgentsBuilder();
        foreach (Action<AgentsBuilder> configure in builder.Configurations)
        {
            configure(agentsBuilder);
        }

        // The failures that are the caller's to fix are never retried, so a model call's reservation is given back at once.
        agentsBuilder.WithModelRetry(agentsBuilder.ModelRetry with
        {
            NonRetryableErrorTypes =
            [
                .. agentsBuilder.ModelRetry.NonRetryableErrorTypes,
                nameof(SnapshotTooLargeException), nameof(SnapshotShapeException),
            ],
        });

        var bindings = new Dictionary<string, ToolBinding>(StringComparer.Ordinal);
        AgentFrameworkRuntime? self = null;
        foreach (AgentDefinition d in definitions)
        {
            foreach (ToolBinding tool in d.Tools)
            {
                if (bindings.TryGetValue(tool.Name, out ToolBinding? existing))
                {
                    if (!ReferenceEquals(existing.Function, tool.Function) || existing.Access != tool.Access || existing.ChildAgent != tool.ChildAgent
                        || existing.AllowWithoutApproval != tool.AllowWithoutApproval || existing.AuthorRequiresApproval != tool.AuthorRequiresApproval)
                    {
                        throw new InvalidOperationException(
                            $"Two agents have a tool named '{tool.Name}' that are not the same function, or are not classified the same way (agent '{d.Name}' and an earlier one). Kairos tools are found by name, so give them different names.");
                    }

                    continue;
                }

                bindings.Add(tool.Name, tool);
                ToolBinding captured = tool;
                agentsBuilder.AddTool(new AgentTool(tool.Name, tool.Description, tool.SchemaJson, tool.Access, (invocation, ct) => RunToolAsync(captured, invocation, self!, ct)));
            }
        }

        bool anyAllow = bindings.Values.Any(t => t.AllowWithoutApproval);
        agentsBuilder.DecoratePolicy(p => p is IRecordedToolPolicy || anyAllow
            ? new RecordedAgentFrameworkPolicy(p, bindings)
            : new AgentFrameworkPolicy(p, bindings));

        AgentsRuntime agents = agentsBuilder.Build();
        Validate(definitions, agents);
        var unclassified = bindings.Values.Where(t => !t.Classified && t.ChildAgent is null).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        self = new AgentFrameworkRuntime(agents, builder.Options, definitions, unclassified);
        return self;
    }

    private static void Validate(List<AgentDefinition> definitions, AgentsRuntime agents)
    {
        foreach (AgentDefinition d in definitions)
        {
            if (agents.Config.Guard is not null && d.Model is null)
            {
                throw new InvalidOperationException(
                    $"A budget (model call guard) is configured, and agent '{d.Name}' has no model to reserve and price its calls by. Say which in its registration: Model(\"alias\", maxOutputTokens), or give its ChatOptions a ModelId.");
            }

            if (agents.Config.Redactor is not null)
            {
                CheckRedactable(d);
            }
        }
    }

    /// <summary>With a redactor configured, the session must keep only state the redactor knows, so that it can promise to have redacted it.</summary>
    private static void CheckRedactable(AgentDefinition d)
    {
        if (d.Agent.GetService<ChatClientAgent>() is not { } inner)
        {
            return; // an agent that is not a ChatClientAgent keeps whatever session it keeps; the step refuses a snapshot it cannot read
        }

        var keys = new List<string>();
        if (inner.ChatHistoryProvider is { } history)
        {
            keys.AddRange(history.StateKeys);
        }

        foreach (AIContextProvider provider in inner.AIContextProviders ?? [])
        {
            keys.AddRange(provider.StateKeys);
        }

        foreach (string key in keys.Where(k => k != SnapshotRedactor.InMemoryHistoryKey && !d.SnapshotState.Contains(k)))
        {
            throw new InvalidOperationException(
                $"A redactor is configured, and agent '{d.Name}' keeps session state under '{key}', a shape the redactor does not know. Declare it free of text with AllowSnapshotState(\"{key}\") if it is, or remove the redactor.");
        }
    }

    // ---- tools ---------------------------------------------------------------------------------------------

    private static async ValueTask<string> RunToolAsync(ToolBinding tool, ToolInvocation invocation, AgentFrameworkRuntime runtime, CancellationToken cancellationToken)
    {
        if (tool.ChildAgent is { } child)
        {
            return await runtime.StartChildAsync(invocation, child, cancellationToken).ConfigureAwait(false);
        }

        var arguments = new AIFunctionArguments();
        if (invocation.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in invocation.Arguments.EnumerateObject())
            {
                arguments[p.Name] = p.Value;
            }
        }

        using IDisposable scope = AgentFrameworkToolContext.Enter(invocation);
        object? result = await tool.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        return ResultText(result, tool.Function);
    }

    private static string ResultText(object? result, AIFunction function) => result switch
    {
        null => "null",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => JsonSerializer.Serialize(result, function.JsonSerializerOptions.GetTypeInfo(result.GetType())),
    };
}
