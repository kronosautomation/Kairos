using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents;

/// <summary>What a tool is allowed to do to the outside world. The policy and the credential both follow from it.</summary>
public enum ToolAccess
{
    /// <summary>Only reads. Runs without asking and receives a credential that cannot write.</summary>
    ReadOnly = 0,

    /// <summary>Changes something outside Kairos. Runs only when a policy allows it or a person approves the call.</summary>
    SideEffecting = 1,
}

/// <summary>A person who decided an approval. The host application authenticates them; Kairos records who the host said it was.</summary>
/// <param name="Subject">Stable identifier from the host's identity provider.</param>
/// <param name="Name">Display name, for the record.</param>
/// <param name="Roles">Roles the host vouches for; checked against the request's allowed roles.</param>
/// <param name="Evidence">Anything the host wants kept to show how the person was authenticated (an assertion id, a session id). Never a password.</param>
public sealed record ApproverIdentity(string Subject, string? Name = null, IReadOnlyList<string>? Roles = null, string? Evidence = null);

/// <summary>What one tool call looks like to the code behind the tool.</summary>
public sealed class ToolInvocation
{
    internal ToolInvocation(
        RunKey run, string toolName, string callId, JsonElement arguments, string idempotencyKey, ToolCredential? credential,
        ApprovalDecision? approval, string? onBehalfOf)
    {
        Run = run;
        ToolName = toolName;
        CallId = callId;
        Arguments = arguments;
        IdempotencyKey = idempotencyKey;
        Credential = credential;
        Approval = approval;
        OnBehalfOf = onBehalfOf;
    }

    public RunKey Run { get; }

    public string ToolName { get; }

    /// <summary>The model's id for this call.</summary>
    public string CallId { get; }

    /// <summary>The arguments the model supplied, as a JSON object.</summary>
    public JsonElement Arguments { get; }

    /// <summary>
    /// Stable for every attempt of this step (run id and step number). Pass it to the service you call so a retry after a lost
    /// result does not do the work twice.
    /// </summary>
    public string IdempotencyKey { get; }

    /// <summary>A short-lived credential limited to this run and this tool, or null when none is configured. Never log it.</summary>
    public ToolCredential? Credential { get; }

    /// <summary>The approval that allowed this call, when it needed one.</summary>
    public ApprovalDecision? Approval { get; }

    /// <summary>Who the agent acts for, when the workflow said so.</summary>
    public string? OnBehalfOf { get; }
}

/// <summary>Thrown by a tool to report a failure the model should see and can react to (bad arguments, record not found). Not retried.</summary>
public sealed class ToolException : Exception
{
    public ToolException()
    {
    }

    public ToolException(string message)
        : base(message)
    {
    }

    public ToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public delegate ValueTask<string> ToolHandler(ToolInvocation invocation, CancellationToken cancellationToken);

/// <summary>A tool the model may call: what the model sees (name, description, argument schema), what it may do, and the code behind it.</summary>
public sealed class AgentTool
{
    public AgentTool(string name, string description, string argumentsSchemaJson, ToolAccess access, ToolHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsSchemaJson);
        ArgumentNullException.ThrowIfNull(handler);
        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Tool names may not contain ':' (it separates the scope in credentials).", nameof(name));
        }

        Name = name;
        Description = description;
        Access = access;
        Handler = handler;
        using JsonDocument schema = JsonDocument.Parse(argumentsSchemaJson);
        Declaration = AIFunctionFactory.CreateDeclaration(name, description, schema.RootElement.Clone());
    }

    public string Name { get; }

    public string Description { get; }

    public ToolAccess Access { get; }

    internal ToolHandler Handler { get; }

    internal AIFunctionDeclaration Declaration { get; }
}

/// <summary>The tools a model call may use, chosen by name. Create with <see cref="IAgentContext.Tools"/>.</summary>
public sealed class AgentToolSet
{
    internal AgentToolSet(IReadOnlyList<string> names) => Names = names;

    public IReadOnlyList<string> Names { get; }
}
