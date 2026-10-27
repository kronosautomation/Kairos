using Microsoft.Agents.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>Settings of the adapter that are not about one agent.</summary>
public sealed class AgentFrameworkOptions
{
    /// <summary>
    /// The largest session snapshot a step will take or return (UTF-8 bytes). Every step carries the whole snapshot, so a conversation that grows past this fails the step with a clear error instead of
    /// moving ever larger payloads through history. Default 512 KiB. Agent Framework's own compaction strategies run inside the step and keep a long conversation under it.
    /// </summary>
    public int MaxSnapshotBytes { get; set; } = 512 * 1024;

    /// <summary>The most model calls one message may cause (the same idea as the 40 iterations Agent Framework allows a request). Reaching it ends the conversation's run with an error. Default 40.</summary>
    public int MaxStepsPerTurn { get; set; } = 40;

    /// <summary>A conversation ends when nobody writes for this long. Null (the default) keeps it open: a waiting conversation holds no compute.</summary>
    public TimeSpan? ConversationIdleTimeout { get; set; }

    /// <summary>
    /// Hands a conversation over to a fresh run once its history has this many events (carrying the session with it). Null uses the engine's own recommendation
    /// (<see cref="IWorkflowContext.ShouldContinueAsNew"/>), which is the right value outside tests.
    /// </summary>
    public int? ContinueAfterEvents { get; set; }

    /// <summary>How soon <see cref="DurableAIAgent"/> first looks for the answer to a message; the interval then grows to <see cref="MaxReplyPollInterval"/>. Default 25 ms.</summary>
    public TimeSpan MinReplyPollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>The longest wait between two looks for an answer. Default one second.</summary>
    public TimeSpan MaxReplyPollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Collects the agents that become durable, and how their tools are treated. <c>services.AddKairos(...).AddAgentFramework(af =&gt; af.AddAgent(agent))</c> is the usual use.
/// </summary>
public sealed class AgentFrameworkBuilder
{
    private readonly List<AgentRegistration> _agents = [];
    private readonly List<Action<AgentsBuilder>> _configure = [];

    internal IReadOnlyList<AgentRegistration> Agents => _agents;

    internal IReadOnlyList<Action<AgentsBuilder>> Configurations => _configure;

    public AgentFrameworkOptions Options { get; } = new();

    /// <summary>
    /// Makes <paramref name="agent"/> durable. The agent must have a <see cref="AIAgent.Name"/> (the durable agent is found by it), built the usual way: a <c>ChatClientAgent</c> with its instructions, tools and chat client.
    /// Tools are treated as side-effecting (held for approval unless a policy allows them) unless marked in <paramref name="configure"/>.
    /// </summary>
    public AgentFrameworkBuilder AddAgent(AIAgent agent, Action<AgentRegistration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var registration = new AgentRegistration(agent);
        configure?.Invoke(registration);
        _agents.Add(registration);
        return this;
    }

    /// <summary>
    /// Configures what the adapter shares with native Kairos agents: the tool policy, model-call guard (budgets), redactor, credential issuer, approver verifier, approval notifier and retry settings.
    /// Models and tools are not registered here: the agent brings its own chat client and tools.
    /// </summary>
    public AgentFrameworkBuilder ConfigureAgents(Action<AgentsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure.Add(configure);
        return this;
    }

    /// <summary>Adjusts <see cref="Options"/>.</summary>
    public AgentFrameworkBuilder Configure(Action<AgentFrameworkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }
}

/// <summary>
/// How one agent is registered. Classification of tools lives here, not in the agent: Agent Framework tools say only whether a call needs approval, Kairos also needs to know whether a tool changes
/// the outside world.
/// </summary>
public sealed class AgentRegistration
{
    internal AgentRegistration(AIAgent agent) => Agent = agent;

    internal AIAgent Agent { get; }

    internal HashSet<string> ReadOnlyTools { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> AllowedWithoutApproval { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, ApprovalRequirement> RequiredApproval { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> OpaqueNesting { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> SnapshotState { get; } = new(StringComparer.Ordinal);

    internal bool TurnLevel { get; private set; }

    internal bool HostedTools { get; private set; }

    internal string? ModelAlias { get; private set; }

    internal int? MaxOutputTokensValue { get; private set; }

    internal ApprovalRequirement DefinitionChangeApproval { get; private set; } = new();

    internal ApprovalRequirement FrameworkApprovalRequirement { get; private set; } = new();

    /// <summary>Marks tools that only read. They run without approval and receive a credential that cannot write.</summary>
    public AgentRegistration ReadOnly(params string[] tools)
    {
        Add(ReadOnlyTools, tools);
        return this;
    }

    /// <summary>
    /// Lets the named tools run without a person's approval, as Agent Framework would run a tool that is not wrapped in an approval requirement. This is a deliberate decision by the application's owner and is
    /// recorded in the audit trail with every call. It lifts the hold the free policy puts on side-effecting tools; it does not override a denial, or a rule of a policy engine that matches the call.
    /// </summary>
    public AgentRegistration AllowWithoutApproval(params string[] tools)
    {
        Add(AllowedWithoutApproval, tools);
        return this;
    }

    /// <summary>Requires approval for the tool by the given people, whatever else allows it.</summary>
    public AgentRegistration RequireApproval(string tool, ApprovalRequirement? requirement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        RequiredApproval[tool] = requirement ?? new ApprovalRequirement();
        return this;
    }

    /// <summary>
    /// Allows an agent that cannot be run one model call at a time (an A2A agent, a custom agent, <c>LoopAgent</c> and <c>ToolApprovalAgent</c> wrappers) to be recorded one <b>turn</b> at a time instead.
    /// This weakens the guarantee: tools run inside the turn, outside the tool policy and without their own recorded steps, and a crash during a turn runs the whole turn again.
    /// </summary>
    public AgentRegistration AllowTurnLevel()
    {
        TurnLevel = true;
        return this;
    }

    /// <summary>
    /// Allows the named tool to be an agent run by <c>AsAIFunction()</c> that Kairos cannot see into: its own tools run without Kairos' policy, approvals or budgets. Outside policy, by choice.
    /// Use <c>durableAgents.Get(name).AsAIFunction()</c> instead to run the nested agent as a durable child run that is governed like any other.
    /// </summary>
    public AgentRegistration AllowOpaqueNesting(params string[] tools)
    {
        Add(OpaqueNesting, tools);
        return this;
    }

    /// <summary>Allows tools that run at the model provider (hosted web search, hosted MCP servers, code interpreters). They are outside the Kairos tool policy; approvals they ask for are still turned into Kairos approvals.</summary>
    public AgentRegistration AllowHostedTools()
    {
        HostedTools = true;
        return this;
    }

    /// <summary>
    /// Declares that the session keeps state under these keys that holds no free text, so a redactor may leave it alone. Without it, a redactor refuses a session that keeps state it does not know.
    /// </summary>
    public AgentRegistration AllowSnapshotState(params string[] stateKeys)
    {
        Add(SnapshotState, stateKeys);
        return this;
    }

    /// <summary>
    /// The model alias budgets and prices are looked up by, and the cap on the reply the guard reserves for. Defaults to the agent's own <c>ChatOptions.ModelId</c> / <c>MaxOutputTokens</c>, or the chat client's
    /// default model.
    /// </summary>
    public AgentRegistration Model(string alias, int? maxOutputTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ModelAlias = alias;
        MaxOutputTokensValue = maxOutputTokens ?? MaxOutputTokensValue;
        return this;
    }

    /// <summary>
    /// Who may let a run continue after the agent's definition (instructions, tools, schemas) changed since the run started. The decision is an approval: it names the person the approver verifier established, and it is recorded
    /// in the audit trail. Default: any approver the host accepts.
    /// </summary>
    public AgentRegistration OnDefinitionChange(ApprovalRequirement approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        DefinitionChangeApproval = approval;
        return this;
    }

    /// <summary>
    /// Who may decide the approvals the agent's own framework asks for (a hosted tool that wants approval, an approval wrapper around a turn-level agent). Calls to the agent's function tools use the tool policy's requirement instead.
    /// Default: any approver the host accepts.
    /// </summary>
    public AgentRegistration OnFrameworkApproval(ApprovalRequirement approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        FrameworkApprovalRequirement = approval;
        return this;
    }

    private static void Add(HashSet<string> set, string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (string name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            set.Add(name);
        }
    }
}
