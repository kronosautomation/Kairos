using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>No agent was registered under that name on this worker.</summary>
public sealed class UnknownAgentException : Exception
{
    public UnknownAgentException()
        : base("Unknown agent.")
    {
    }

    public UnknownAgentException(string message)
        : base(message)
    {
    }

    public UnknownAgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The agent's definition (instructions, tools, schemas) is not the one the run started with, and nobody allowed the run to continue on the new one.</summary>
public sealed class DefinitionChangedException : Exception
{
    public DefinitionChangedException()
        : base("The agent's definition changed since the run started.")
    {
    }

    public DefinitionChangedException(string message)
        : base(message)
    {
    }

    public DefinitionChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A session snapshot is larger than <see cref="AgentFrameworkOptions.MaxSnapshotBytes"/>.</summary>
public sealed class SnapshotTooLargeException : Exception
{
    public SnapshotTooLargeException()
        : base("The session snapshot is too large.")
    {
    }

    public SnapshotTooLargeException(string message)
        : base(message)
    {
    }

    public SnapshotTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One message caused more model calls than <see cref="AgentFrameworkOptions.MaxStepsPerTurn"/> allows.</summary>
public sealed class AgentStepLimitException : Exception
{
    public AgentStepLimitException()
        : base("The agent used more steps than allowed for one message.")
    {
    }

    public AgentStepLimitException(string message)
        : base(message)
    {
    }

    public AgentStepLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The conversation's run has ended (it was idle for too long, or it failed); start a new session.</summary>
public sealed class ConversationEndedException : Exception
{
    public ConversationEndedException()
        : base("The conversation has ended.")
    {
    }

    public ConversationEndedException(string message)
        : base(message)
    {
    }

    public ConversationEndedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What Kairos knows about the call a tool is serving, for tool code written as an Agent Framework <see cref="AIFunction"/> (which has no parameter for it): the idempotency key to pass to the service
/// the tool calls, the short-lived credential, and the approval that allowed the call. Set only while Kairos runs the tool.
/// </summary>
public static class AgentFrameworkToolContext
{
    private static readonly AsyncLocal<ToolInvocation?> Slot = new();

    /// <summary>The invocation being served, or null outside a Kairos tool step.</summary>
    public static ToolInvocation? Current => Slot.Value;

    internal static IDisposable Enter(ToolInvocation invocation)
    {
        ToolInvocation? previous = Slot.Value;
        Slot.Value = invocation;
        return new Scope(previous);
    }

    private sealed class Scope(ToolInvocation? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}

/// <summary>
/// A durable agent offered to another agent as a tool (<c>durableAgents.Get("researcher").AsAIFunction()</c>). When the calling agent is registered, a call to it starts the nested agent as a durable
/// child run: governed by the tool policy like any other tool call, with its own tools, approvals and budgets, and it holds no compute while it waits.
/// </summary>
public sealed class DurableAgentFunction : AIFunction
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"query":{"type":"string","description":"Input query to invoke the agent."}},"required":["query"]}""").RootElement.Clone();

    internal DurableAgentFunction(string agentName, string? description)
    {
        AgentName = agentName;
        Description = description ?? "";
    }

    /// <summary>
    /// The nested agent registered as <paramref name="agentName"/>, as a tool for another agent. Use it where the calling agent is built, which is before the durable agents exist
    /// (<c>durableAgents.Get(name).AsAIFunction()</c> is the same thing once they do).
    /// </summary>
    /// <param name="agentName">The name the nested agent is registered under.</param>
    /// <param name="description">What the model is told the tool does. Say when to ask the nested agent.</param>
    public static DurableAgentFunction For(string agentName, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        return new DurableAgentFunction(agentName, description);
    }

    /// <summary>The nested agent.</summary>
    public string AgentName { get; }

    public override string Name => AgentName;

    public override string Description { get; }

    public override JsonElement JsonSchema => Schema;

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : base.GetService(serviceType, serviceKey);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A durable agent is run by Kairos, as a child run of the agent that calls it. Register the calling agent with AddAgent; it cannot be invoked directly.");
}
