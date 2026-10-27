using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>One tool of an agent as Kairos treats it.</summary>
internal sealed class ToolBinding
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string SchemaJson { get; init; }

    public required AIFunction Function { get; init; }

    public ToolAccess Access { get; init; } = ToolAccess.SideEffecting;

    /// <summary>The author wrapped the tool in <see cref="ApprovalRequiredAIFunction"/>: every call needs a person's approval.</summary>
    public bool AuthorRequiresApproval { get; init; }

    /// <summary>The application owner let the tool run without approval.</summary>
    public bool AllowWithoutApproval { get; init; }

    /// <summary>The application owner required approval, by these people.</summary>
    public ApprovalRequirement? RequiredApproval { get; init; }

    /// <summary>For a nested durable agent: the agent that answers.</summary>
    public string? ChildAgent { get; init; }

    /// <summary>The tool was marked at registration (read-only, allowed without approval, approval required) or the author required approval. Others are logged at startup.</summary>
    public bool Classified { get; init; }
}

/// <summary>What the adapter knows about one registered agent: how it is run, its tools, and a hash of everything that decides what a step does.</summary>
internal sealed partial class AgentDefinition
{
    private static readonly Regex NamePattern = NameRegex();

    private AgentDefinition(AgentRegistration registration, string name)
    {
        Registration = registration;
        Name = name;
    }

    public AgentRegistration Registration { get; }

    public string Name { get; }

    public AIAgent Agent => Registration.Agent;

    /// <summary>True when the agent is run one model call at a time; false when it is recorded a turn at a time.</summary>
    public bool Stepped { get; private init; }

    public IReadOnlyList<ToolBinding> Tools { get; private init; } = [];

    public string Hash { get; private set; } = "";

    public string? Model { get; private init; }

    public int? MaxOutputTokens { get; private init; }

    public IReadOnlySet<string> SnapshotState => Registration.SnapshotState;

    public ApprovalRequirement DefinitionChangeApproval => Registration.DefinitionChangeApproval;

    public ApprovalRequirement FrameworkApproval => Registration.FrameworkApprovalRequirement;

    /// <summary>Names of the nested durable agents this agent can call.</summary>
    public IEnumerable<string> Children => Tools.Where(t => t.ChildAgent is not null).Select(t => t.ChildAgent!);

    public static AgentDefinition Analyze(AgentRegistration registration)
    {
        AIAgent agent = registration.Agent;
        string name = agent.Name ?? "";
        if (!NamePattern.IsMatch(name))
        {
            throw new InvalidOperationException(
                $"A durable agent needs a stable name of 1 to 64 letters, digits, '.', '_' or '-' (the agent's Name is '{name}'). The name finds the agent after a restart, so it must not change; give the agent one with its Name option.");
        }

        ChatClientAgent? inner = agent.GetService<ChatClientAgent>();
        AITool? hosted = agent.GetService<ChatOptions>()?.Tools?.FirstOrDefault(t => t is not AIFunctionDeclaration);
        string? why = inner is null
            ? "it is not a ChatClientAgent"
            : agent.GetService<ToolApprovalAgent>() is not null
                ? "it is wrapped in a ToolApprovalAgent, which approves and re-runs calls by itself"
                : agent.GetService<LoopAgent>() is not null
                    ? "it is wrapped in a LoopAgent, which re-runs the agent by itself"
                    : hosted is not null
                        ? $"it has a hosted tool '{hosted.Name}' ({hosted.GetType().Name}), which runs at the model provider, outside the Kairos tool policy"
                        : null;
        if (why is not null && !registration.TurnLevel)
        {
            throw new InvalidOperationException(
                $"Agent '{name}' cannot be run one model call at a time: {why}. Register it with AllowTurnLevel() to record it one turn at a time instead. " +
                "That weakens the guarantee: its tools run inside the turn, outside the Kairos tool policy, and a crash during a turn runs the whole turn again.");
        }

        bool stepped = why is null;
        var tools = new List<ToolBinding>();
        if (stepped)
        {
            ChatOptions options = agent.GetService<ChatOptions>() ?? new ChatOptions();
            foreach (AITool tool in options.Tools ?? [])
            {
                tools.Add(Bind(registration, name, tool));
            }

            foreach (string mentioned in registration.ReadOnlyTools.Concat(registration.AllowedWithoutApproval).Concat(registration.RequiredApproval.Keys).Concat(registration.OpaqueNesting))
            {
                if (!tools.Exists(t => t.Name == mentioned))
                {
                    throw new InvalidOperationException($"Agent '{name}' is registered with settings for a tool named '{mentioned}', which it does not have. Its tools: {string.Join(", ", tools.Select(t => t.Name))}.");
                }
            }
        }

        ChatOptions? chat = agent.GetService<ChatOptions>();
        string? model = registration.ModelAlias ?? chat?.ModelId ?? inner?.ChatClient.GetService<ChatClientMetadata>()?.DefaultModelId;
        var definition = new AgentDefinition(registration, name)
        {
            Stepped = stepped,
            Tools = tools,
            Model = model,
            MaxOutputTokens = registration.MaxOutputTokensValue ?? chat?.MaxOutputTokens,
        };
        definition.Hash = definition.ComputeHash(inner?.Instructions ?? "", chat?.Instructions ?? "");
        return definition;
    }

    private static ToolBinding Bind(AgentRegistration registration, string agent, AITool tool)
    {
        if (tool is not AIFunction function)
        {
            // Only a declaration remains here: hosted tools make the agent turn level before its tools are looked at.
            throw new InvalidOperationException($"Agent '{agent}' has a tool '{tool.Name}' that is only a declaration: nothing can run it. Give it an implementation, or remove it.");
        }

        string name = function.Name;
        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Tool '{name}' of agent '{agent}' has a ':' in its name, which Kairos reserves.");
        }

        string? child = function.GetService<DurableAgentFunction>()?.AgentName;
        if (child is null && IsAgentAsFunction(function) && !registration.OpaqueNesting.Contains(name))
        {
            throw new InvalidOperationException(
                $"Tool '{name}' of agent '{agent}' runs another agent (AsAIFunction) that Kairos cannot see into: its own tools would run without the Kairos policy, approvals or budgets. " +
                $"Register the nested agent too and use durableAgents.Get(\"{name}\").AsAIFunction() to run it as a durable child run, or allow the opaque form with AllowOpaqueNesting(\"{name}\").");
        }

        bool readOnly = registration.ReadOnlyTools.Contains(name);
        bool allow = registration.AllowedWithoutApproval.Contains(name);
        bool author = function.GetService<ApprovalRequiredAIFunction>() is not null;
        registration.RequiredApproval.TryGetValue(name, out ApprovalRequirement? required);
        if (readOnly && (allow || required is not null || author))
        {
            throw new InvalidOperationException($"Tool '{name}' of agent '{agent}' is marked read-only and also as needing or not needing approval. Choose one.");
        }

        if (allow && (required is not null || author))
        {
            throw new InvalidOperationException($"Tool '{name}' of agent '{agent}' is allowed without approval, but it is also required to have it (by the agent's own approval requirement or RequireApproval). Choose one.");
        }

        return new ToolBinding
        {
            Name = name,
            Description = function.Description,
            SchemaJson = function.JsonSchema.GetRawText(),
            Function = function,
            Access = readOnly ? ToolAccess.ReadOnly : ToolAccess.SideEffecting,
            AuthorRequiresApproval = author,
            AllowWithoutApproval = allow,
            RequiredApproval = required,
            ChildAgent = child,
            Classified = readOnly || allow || author || required is not null,
        };
    }

    /// <summary>True for the function <c>AIAgent.AsAIFunction()</c> makes: it runs the nested agent inside the tool call.</summary>
    internal static bool IsAgentAsFunction(AIFunction function) =>
        function.UnderlyingMethod?.DeclaringType is { } type
        && type.Assembly == typeof(ChatClientAgent).Assembly
        && (type.FullName?.StartsWith("Microsoft.Agents.AI.AIAgentExtensions", StringComparison.Ordinal) ?? false);

    private string ComputeHash(string instructions, string optionInstructions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);
            writer.WriteBoolean("stepped", Stepped);
            writer.WriteString("instructions", instructions);
            writer.WriteString("optionInstructions", optionInstructions);
            writer.WriteString("model", Model ?? "");
            writer.WriteStartArray("tools");
            foreach (ToolBinding t in Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", t.Name);
                writer.WriteString("description", t.Description);
                writer.WriteString("schema", t.SchemaJson);
                writer.WriteEndObject(); // how a tool is classified is policy, decided when a call is made: changing it never disturbs a run in flight
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NameRegex();

    public override string ToString() => $"{Name} ({(Stepped ? "stepped" : "turn level")}, {Tools.Count} tools)";
}
