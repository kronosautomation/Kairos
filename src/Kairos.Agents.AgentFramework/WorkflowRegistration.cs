using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.InProc;

namespace Kairos.Agents.AgentFramework;

/// <summary>What a workflow factory is given when Kairos builds the workflow for a step.</summary>
public sealed class WorkflowBuildContext
{
    internal WorkflowBuildContext(DurableAgents agents, IServiceProvider? services)
    {
        Agents = agents;
        Services = services;
    }

    /// <summary>The durable agents, for executors that talk to one. A call to a durable agent from an executor is answered from the conversation if the superstep is run again after a crash.</summary>
    public DurableAgents Agents { get; }

    /// <summary>The application's services when the host runs workers (<c>AddAgentFramework</c>), otherwise null.</summary>
    public IServiceProvider? Services { get; }
}

/// <summary>A request an executor raised and nobody has answered yet.</summary>
/// <param name="RequestId">The id Agent Framework gave the request; stable across restarts.</param>
/// <param name="PortId">The id of the <c>RequestPort</c> that raised it.</param>
/// <param name="Data">The request as JSON (redacted when a redactor is configured).</param>
public sealed record WorkflowRequest(string RequestId, string PortId, JsonElement Data)
{
    /// <summary>True when the request is an approval (its port is registered with <c>RequestsAsApprovals</c>): an approver decides it in the approvals inbox, and it cannot be answered with <c>RespondAsync</c>.</summary>
    public bool IsApproval { get; init; }

    /// <summary>The request data as <typeparamref name="T"/>.</summary>
    public T? As<T>(JsonSerializerOptions? options = null) => Data.Deserialize<T>(options ?? WorkflowJson.Default);
}

/// <summary>What a person is asked when a request becomes an approval.</summary>
/// <param name="Action">One line: what is being decided.</param>
/// <param name="Details">More, shown to the approver (redacted by the configured redactor).</param>
public sealed record ApprovalDescription(string Action, string? Details = null);

/// <summary>Serializer settings used when the registration gives none.</summary>
internal static class WorkflowJson
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// How one workflow is registered: how its values are serialized, which of its request ports become approvals, what a redactor needs to know, and who may let a run continue on a changed workflow.
/// </summary>
public sealed class WorkflowRegistration
{
    internal WorkflowRegistration(string name, Type inputType, Func<WorkflowBuildContext, Workflow> factory, Func<InProcessExecutionEnvironment, Workflow, JsonElement, string, CancellationToken, ValueTask<StreamingRun>> start)
    {
        Name = name;
        InputType = inputType;
        Factory = factory;
        Start = start;
    }

    internal string Name { get; }

    internal Type InputType { get; }

    internal Func<WorkflowBuildContext, Workflow> Factory { get; }

    internal Func<InProcessExecutionEnvironment, Workflow, JsonElement, string, CancellationToken, ValueTask<StreamingRun>> Start { get; }

    internal JsonSerializerOptions Options { get; private set; } = WorkflowJson.Default;

    internal Dictionary<string, ApprovalPort> Approvals { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> FreeTypes { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, Func<JsonNode?, Func<string, string>, JsonNode?>> TextTypes { get; } = new(StringComparer.Ordinal);

    internal ApprovalRequirement StructureChangeApproval { get; private set; } = new();

    /// <summary>
    /// How the customer's types are written and read: the messages between executors, executor state, request and response data, and outputs. The workflow's own checkpoint serializer uses these options for the
    /// customer's types. A source-generated context keeps the registration free of reflection. Default: web defaults.
    /// </summary>
    public WorkflowRegistration JsonOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        return this;
    }

    /// <summary>
    /// Makes the requests raised through the port <paramref name="portId"/> approvals: they appear in the approvals inbox, wait without holding a worker, and are decided by a person the approver verifier established, who is
    /// recorded in history. The port's response type must be <see cref="bool"/> (approved or not). Requests through any other port are answered by a signal: <see cref="DurableWorkflowRun.RespondAsync"/>.
    /// </summary>
    /// <param name="portId">The id of the request port.</param>
    /// <param name="requirement">Who may decide. Default: any approver the host accepts.</param>
    /// <param name="describe">The text the approver sees. Default: the port's name and the (redacted) request.</param>
    public WorkflowRegistration RequestsAsApprovals(string portId, ApprovalRequirement? requirement = null, Func<WorkflowRequest, ApprovalDescription>? describe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portId);
        Approvals[portId] = new ApprovalPort(requirement ?? new ApprovalRequirement(), describe);
        return this;
    }

    /// <summary>
    /// Declares that values of type <typeparamref name="T"/> (messages between executors, executor state, request data, outputs) hold no free text, so that a redactor may leave them alone. Without it, or
    /// <see cref="RedactState{T}"/>, a configured redactor refuses a workflow that passes values of a type it does not know.
    /// </summary>
    public WorkflowRegistration AllowStateType<T>()
    {
        FreeTypes.Add(WorkflowTypePolicy.Normalize(typeof(T).FullName!));
        return this;
    }

    /// <summary>
    /// Gives the redactor a way into values of type <typeparamref name="T"/> that do hold text: it is handed the value as it is recorded, and returns the value to record. (Use <see cref="AllowStateType{T}"/> for types without text.)
    /// </summary>
    public WorkflowRegistration RedactState<T>(Func<T, T> redact)
    {
        ArgumentNullException.ThrowIfNull(redact);
        TextTypes[WorkflowTypePolicy.Normalize(typeof(T).FullName!)] = (node, _) =>
        {
            JsonSerializerOptions options = WorkflowTypePolicy.CheckpointOptions(Options);
            T? value = node.Deserialize<T>(options);
            return value is null ? node : System.Text.Json.JsonSerializer.SerializeToNode(redact(value), options);
        };
        return this;
    }

    /// <summary>
    /// Who may let a run continue after the workflow's structure (executors, edges, ports) or the Agent Framework package version changed since the run started. The decision is an approval, recorded with the person
    /// the approver verifier established. Default: any approver the host accepts.
    /// </summary>
    public WorkflowRegistration OnStructureChange(ApprovalRequirement approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        StructureChangeApproval = approval;
        return this;
    }
}

internal sealed record ApprovalPort(ApprovalRequirement Requirement, Func<WorkflowRequest, ApprovalDescription>? Describe);

/// <summary>What the adapter knows about a registered workflow: a hash of its structure, its ports, and what a redactor would have to cover.</summary>
internal sealed class WorkflowDefinition
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private WorkflowDefinition(WorkflowRegistration registration, string structure, Dictionary<string, RequestPort> ports)
    {
        Registration = registration;
        Structure = structure;
        Ports = ports;
        Policy = new WorkflowTypePolicy(registration);
    }

    public WorkflowRegistration Registration { get; }

    public string Name => Registration.Name;

    /// <summary>A hash of the executors (id and type), edges, ports and start of the workflow, without assembly versions.</summary>
    public string Structure { get; }

    /// <summary>The version of the Agent Framework workflows package: its checkpoint format is the package's.</summary>
    public static string PackageVersion { get; } = typeof(Workflow).Assembly.GetName().Version?.ToString() ?? "unknown";

    public IReadOnlyDictionary<string, RequestPort> Ports { get; }

    public WorkflowTypePolicy Policy { get; }

    public static WorkflowDefinition Analyze(WorkflowRegistration registration, Func<WorkflowBuildContext> context, bool redactorConfigured)
    {
        string name = registration.Name;
        if (!NamePattern.IsMatch(name))
        {
            throw new InvalidOperationException($"A durable workflow needs a stable name of 1 to 64 letters, digits, '.', '_' or '-' (this one is '{name}'). The name finds the workflow after a restart, so it must not change.");
        }

        Workflow workflow = Build(registration, context);
        string first = StructureOf(workflow);

        // Executors must have stable ids: a checkpoint names them. The factory is called once per step, so a workflow whose ids change from one build to the next could never be resumed.
        string second = StructureOf(Build(registration, context));
        if (first != second)
        {
            throw new InvalidOperationException(
                $"Workflow '{name}' is not the same workflow twice: building it again gave different executor ids, edges or types. Executors must have explicit, stable ids (a checkpoint names them), and the factory must build the same graph every time.");
        }

        var ports = new Dictionary<string, RequestPort>(StringComparer.Ordinal);
        foreach (ExecutorBinding binding in workflow.ReflectExecutors().Values)
        {
            if (binding is RequestPortBinding port)
            {
                ports[port.Port.Id] = port.Port;
            }
        }

        foreach ((string portId, _) in registration.Approvals)
        {
            if (!ports.TryGetValue(portId, out RequestPort? port))
            {
                throw new InvalidOperationException(
                    $"Workflow '{name}' is registered with RequestsAsApprovals(\"{portId}\"), and it has no request port with that id. Its ports: {(ports.Count == 0 ? "none" : string.Join(", ", ports.Keys.Order(StringComparer.Ordinal)))}.");
            }

            if (port.Response != typeof(bool))
            {
                throw new InvalidOperationException(
                    $"Workflow '{name}': the request port '{portId}' is registered as an approval, and its response type is {port.Response.Name}. An approval is answered yes or no: make the port's response type bool, or answer the port by signal (remove RequestsAsApprovals).");
            }
        }

        var definition = new WorkflowDefinition(registration, first, ports);
        if (redactorConfigured)
        {
            definition.CheckRedactable(workflow);
        }

        return definition;
    }

    /// <summary>With a redactor configured, every type the workflow's protocol passes around must be one the redactor knows, so that it can promise to have redacted what it records.</summary>
    private void CheckRedactable(Workflow workflow)
    {
        var types = new HashSet<Type>();
        ProtocolDescriptor protocol = workflow.DescribeProtocolAsync().AsTask().GetAwaiter().GetResult();
        foreach (Type t in protocol.Accepts.Concat(protocol.Yields).Concat(protocol.Sends))
        {
            types.Add(t);
        }

        foreach (RequestPort port in Ports.Values)
        {
            types.Add(port.Request);
            types.Add(port.Response);
        }

        foreach (ExecutorBinding binding in workflow.ReflectExecutors().Values)
        {
            for (Type? t = binding.ExecutorType; t is not null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() is { } open && (open == typeof(Executor<>) || open == typeof(Executor<,>)))
                {
                    foreach (Type argument in t.GetGenericArguments())
                    {
                        types.Add(argument);
                    }
                }
            }
        }

        foreach (Type type in types.Where(t => t != typeof(void) && t != typeof(object)).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (!Policy.Knows(type))
            {
                throw new InvalidOperationException(
                    $"A redactor is configured, and workflow '{Name}' passes values of type '{type.FullName}' between its executors, a type the redactor does not know. " +
                    $"Declare it free of text with AllowStateType<{type.Name}>() if it is, give the redactor its text with RedactState<{type.Name}>(...), or remove the redactor.");
            }
        }
    }

    private static Workflow Build(WorkflowRegistration registration, Func<WorkflowBuildContext> context) =>
        registration.Factory(context()) ?? throw new InvalidOperationException($"The factory of workflow '{registration.Name}' returned no workflow.");

    internal static string StructureOf(Workflow workflow)
    {
        var text = new StringBuilder();
        text.Append("start=").Append(workflow.StartExecutorId).Append('\n');
        foreach ((string id, ExecutorBinding binding) in workflow.ReflectExecutors().OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            text.Append("executor ").Append(id).Append(' ').Append(WorkflowTypePolicy.Normalize(binding.ExecutorType.FullName ?? binding.ExecutorType.Name)).Append('\n');
        }

        foreach ((string source, HashSet<EdgeInfo> edges) in workflow.ReflectEdges().OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            foreach (string edge in edges.Select(Describe).Order(StringComparer.Ordinal))
            {
                text.Append("edge ").Append(source).Append(' ').Append(edge).Append('\n');
            }
        }

        foreach ((string id, RequestPortInfo port) in workflow.ReflectPorts().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append("port ").Append(id).Append(' ').Append(WorkflowTypePolicy.Normalize(port.RequestType.TypeName)).Append(' ').Append(WorkflowTypePolicy.Normalize(port.ResponseType.TypeName)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

        static string Describe(EdgeInfo edge)
        {
            string flag = edge switch
            {
                DirectEdgeInfo d => d.HasCondition ? "conditional" : "plain",
                FanOutEdgeInfo f => f.HasAssigner ? "assigned" : "all",
                _ => "",
            };
            return $"{edge.Kind}/{flag} {string.Join(',', edge.Connection.SourceIds.Order(StringComparer.Ordinal))}->{string.Join(',', edge.Connection.SinkIds.Order(StringComparer.Ordinal))}";
        }
    }
}
