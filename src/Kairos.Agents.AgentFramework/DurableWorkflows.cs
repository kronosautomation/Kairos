using System.Text;
using System.Text.Json;

namespace Kairos.Agents.AgentFramework;

/// <summary>Where a durable workflow run stands.</summary>
public enum DurableWorkflowStatus
{
    /// <summary>Supersteps are being run.</summary>
    Running,

    /// <summary>Nothing is running: the workflow waits for requests to be answered (it holds no compute).</summary>
    Waiting,

    /// <summary>The workflow finished; its outputs are complete.</summary>
    Completed,

    /// <summary>The run ended with an error (<see cref="WorkflowView.Error"/>).</summary>
    Failed,

    /// <summary>The run was cancelled.</summary>
    Cancelled,

    /// <summary>An operator terminated the run.</summary>
    Terminated,
}

/// <summary>A value the workflow yielded.</summary>
/// <param name="ExecutorId">The executor that yielded it.</param>
/// <param name="TypeName">The value's .NET type, without assembly.</param>
/// <param name="Data">The value as JSON (redacted when a redactor is configured).</param>
public sealed record WorkflowOutput(string ExecutorId, string TypeName, JsonElement Data)
{
    /// <summary>The value as <typeparamref name="T"/>.</summary>
    public T? As<T>(JsonSerializerOptions? options = null) => Data.Deserialize<T>(options ?? WorkflowJson.Default);
}

/// <summary>What is known of a workflow run from its history.</summary>
/// <param name="Status">Where the run stands.</param>
/// <param name="Outputs">Everything the workflow has yielded so far.</param>
/// <param name="Pending">The requests the workflow waits for.</param>
/// <param name="Steps">How many steps (of <see cref="AgentFrameworkOptions.SuperstepsPerStep"/> supersteps) have been recorded.</param>
/// <param name="Error">For a run that failed: the kind of failure and its message.</param>
public sealed record WorkflowView(DurableWorkflowStatus Status, IReadOnlyList<WorkflowOutput> Outputs, IReadOnlyList<WorkflowRequest> Pending, int Steps, string? Error)
{
    /// <summary>True once the run has ended, however it ended.</summary>
    public bool IsClosed => Status is DurableWorkflowStatus.Completed or DurableWorkflowStatus.Failed or DurableWorkflowStatus.Cancelled or DurableWorkflowStatus.Terminated;
}

/// <summary>
/// The durable workflows of an application: <c>Get(name)</c> starts runs of a workflow registered with <c>AddWorkflow</c> and finds the ones that exist. Everything the workflow does happens on the workers, recorded superstep by
/// superstep; this object holds no state and can be created anywhere the application has a Kairos client.
/// </summary>
public sealed class DurableWorkflows
{
    private readonly AgentFrameworkRuntime _runtime;
    private readonly KairosClient _client;
    private readonly TenantScope _scope;
    private readonly TimeProvider _time;

    public DurableWorkflows(AgentFrameworkRuntime runtime, KairosClient client, TenantScope scope, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        _runtime = runtime;
        _client = client;
        _scope = scope;
        _time = time;
    }

    /// <summary>The names of the durable workflows.</summary>
    public IReadOnlyCollection<string> Names => _runtime.WorkflowNames;

    /// <summary>The durable workflow registered as <paramref name="name"/>.</summary>
    /// <exception cref="UnknownWorkflowException">No workflow is registered under that name.</exception>
    public DurableWorkflow Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new DurableWorkflow(this, _runtime.WorkflowDefinitionOf(name));
    }

    internal AgentFrameworkRuntime Runtime => _runtime;

    internal KairosClient Client => _client;

    internal TenantScope Scope => _scope;

    internal TimeProvider Time => _time;
}

/// <summary>A registered durable workflow: start a run, or pick up one that exists.</summary>
public sealed class DurableWorkflow
{
    private readonly DurableWorkflows _owner;
    private readonly WorkflowDefinition _definition;

    internal DurableWorkflow(DurableWorkflows owner, WorkflowDefinition definition)
    {
        _owner = owner;
        _definition = definition;
    }

    public string Name => _definition.Name;

    /// <summary>
    /// Starts a run of the workflow with <paramref name="input"/>. Starting with an <paramref name="instanceId"/> that exists returns the run that has it (and starts nothing), so a caller that does not know whether a start
    /// arrived can start again.
    /// </summary>
    /// <param name="input">The workflow's input.</param>
    /// <param name="instanceId">A name for the run, chosen by the caller. Default: a new unique id.</param>
    /// <param name="onBehalfOf">Whom the workflow acts for: the requester of its approvals, whom the approver may not be.</param>
    /// <param name="cancellationToken">Cancels the start (not the run).</param>
    public async ValueTask<DurableWorkflowRun> StartAsync<TInput>(TInput input, string? instanceId = null, string? onBehalfOf = null, CancellationToken cancellationToken = default)
    {
        if (!_definition.Registration.InputType.IsAssignableFrom(typeof(TInput)))
        {
            throw new ArgumentException($"Workflow '{Name}' takes {_definition.Registration.InputType.Name} as its input, not {typeof(TInput).Name}.", nameof(input));
        }

        string id = instanceId ?? Guid.CreateVersion7(_owner.Time.GetUtcNow()).ToString("N");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var start = new AfwInput
        {
            Workflow = Name,
            Input = JsonSerializer.SerializeToElement(input, _definition.Registration.InputType, _definition.Registration.Options),
            OnBehalfOf = onBehalfOf,
            SuperstepsPerStep = Math.Max(1, _owner.Runtime.Options.SuperstepsPerStep),
            ContinueAfterEvents = _owner.Runtime.Options.ContinueAfterEvents,
        };
        if (_owner.Runtime.Redactor is { } redact && start.Input is { } raw)
        {
            // The client side of the promise the step keeps: the redactor sees the input before it is recorded.
            start = start with { Input = JsonSerializer.SerializeToElement(_definition.Policy.Redact(_definition.Registration.InputType.FullName ?? _definition.Registration.InputType.Name, System.Text.Json.Nodes.JsonNode.Parse(raw.GetRawText()), redact)) };
        }

        string workflowId = WorkflowId(Name, id);
        StartRunResult started = await _owner.Client.StartAsync(
            _owner.Scope, workflowId, AgentFrameworkNames.WorkflowType(Name), AfCodec.Write(start, AfwJson.Default.AfwInput), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new DurableWorkflowRun(_owner, _definition, id, new RunKey(_owner.Scope.TenantId, _owner.Scope.NamespaceId, started.RunId));
    }

    /// <summary>The run with this instance id, which may have been started by another process. Null when there is none.</summary>
    public async ValueTask<DurableWorkflowRun?> FindAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return await _owner.Client.GetCurrentRunAsync(_owner.Scope, WorkflowId(Name, instanceId), cancellationToken).ConfigureAwait(false) is { } run
            ? new DurableWorkflowRun(_owner, _definition, instanceId, run)
            : null;
    }

    internal static string WorkflowId(string workflow, string instance) => $"afw/{workflow}/{instance}";
}

/// <summary>
/// One run of a durable workflow. Reading it costs what is new in its history, not the length of the run, and waiting for it is woken by the engine when the run is written to. A run object is for one caller at a time (it
/// holds the position it has read up to).
/// </summary>
public sealed class DurableWorkflowRun
{
    private const int PageSize = 200;

    private readonly DurableWorkflows _owner;
    private readonly WorkflowDefinition _definition;
    private WorkflowReader _reader = new();
    private long _cursor;

    internal DurableWorkflowRun(DurableWorkflows owner, WorkflowDefinition definition, string instanceId, RunKey run)
    {
        _owner = owner;
        _definition = definition;
        InstanceId = instanceId;
        Run = run;
    }

    /// <summary>The name the run was started under.</summary>
    public string InstanceId { get; }

    /// <summary>The Kairos run that holds the workflow now (a long workflow hands over to a fresh run now and then). Its approvals are in that run's history.</summary>
    public RunKey Run { get; private set; }

    /// <summary>Reads what the run has written since the last look and says where it stands.</summary>
    public async ValueTask<WorkflowView> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            while (true)
            {
                HistoryPage page = await _owner.Client.GetOpenedHistoryAfterAsync(Run, _cursor, PageSize, cancellationToken).ConfigureAwait(false);
                _reader.Add(page.Events);
                if (page.Events.Count > 0)
                {
                    _cursor = page.Events[^1].Sequence;
                }

                if (page.Events.Count < PageSize)
                {
                    break;
                }
            }

            if (_reader.Closed || await _owner.Client.GetStateAsync(Run, cancellationToken).ConfigureAwait(false) is not RunState.ContinuedAsNew)
            {
                return _reader.View(_definition);
            }

            // The workflow handed over to a fresh run, whose history starts with everything the old one carried: read it from the start.
            RunKey? current = await _owner.Client.GetCurrentRunAsync(_owner.Scope, DurableWorkflow.WorkflowId(_definition.Name, InstanceId), cancellationToken).ConfigureAwait(false);
            if (current is not { } next || next == Run)
            {
                return _reader.View(_definition);
            }

            Run = next;
            _cursor = 0;
            _reader = new WorkflowReader();
        }
    }

    /// <summary>Waits until <paramref name="until"/> is true of the run's view, or the run has ended. Between looks it waits for the engine to say the run was written to (at most <see cref="AgentFrameworkOptions.ReplyWait"/>).</summary>
    public async ValueTask<WorkflowView> WaitAsync(Func<WorkflowView, bool> until, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(until);
        while (true)
        {
            WorkflowView view = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (view.IsClosed || until(view))
            {
                return view;
            }

            await _owner.Client.WaitForHistoryAsync(Run, _cursor, _owner.Runtime.Options.ReplyWait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the workflow has finished (or the run has ended some other way): look at <see cref="WorkflowView.Status"/>.</summary>
    public ValueTask<WorkflowView> WaitForCompletionAsync(CancellationToken cancellationToken = default) => WaitAsync(_ => false, cancellationToken);

    /// <summary>Waits until the workflow is waiting for requests (see <see cref="WorkflowView.Pending"/>), or has finished.</summary>
    public ValueTask<WorkflowView> WaitForRequestsAsync(CancellationToken cancellationToken = default) =>
        WaitAsync(v => v.Status == DurableWorkflowStatus.Waiting && v.Pending.Count > 0, cancellationToken);

    /// <summary>
    /// Answers a request that is not an approval (a request port not registered with <c>RequestsAsApprovals</c>). The response is the port's response type. Answering twice is one answer. Approvals are decided by an approver in the
    /// approvals inbox (<see cref="AgentClient"/>), never here.
    /// </summary>
    /// <returns>False when the run has ended.</returns>
    /// <exception cref="InvalidOperationException">The request is an approval.</exception>
    public async ValueTask<bool> RespondAsync<TResponse>(string requestId, TResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        WorkflowView view = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (view.Pending.FirstOrDefault(r => r.RequestId == requestId) is { } request && request.IsApproval)
        {
            throw new InvalidOperationException($"The request '{requestId}' (port '{request.PortId}') is an approval: it is decided by an approver, who is verified, in the approvals inbox. It cannot be answered here.");
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response, _definition.Registration.Options);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            RunKey run = await _owner.Client.GetCurrentRunAsync(_owner.Scope, DurableWorkflow.WorkflowId(_definition.Name, InstanceId), cancellationToken).ConfigureAwait(false) ?? Run;
            if (await _owner.Client.SignalAsync(run, AgentFrameworkNames.ResponseSignal(requestId), payload, "response", cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (await _owner.Client.GetStateAsync(run, cancellationToken).ConfigureAwait(false) is null or RunState.Completed or RunState.Failed)
            {
                return false; // ended, or not there in this scope
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10 << attempt), _owner.Time, cancellationToken).ConfigureAwait(false); // it handed over to a fresh run between the lookup and the signal
        }

        throw new TimeoutException($"The workflow '{InstanceId}' could not take the answer: its run kept changing.");
    }

    /// <summary>
    /// Asks the workflow to stop. It stops at its next superstep boundary (the superstep in flight finishes and is recorded), at once if it is waiting for a request; the conversations its executors started stop with it. The run
    /// ends as failed with the cancellation. Asking twice is one request. False when the run has ended.
    /// </summary>
    public async ValueTask<bool> CancelAsync(string reason = "", CancellationToken cancellationToken = default)
    {
        RunKey run = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        return await _owner.Client.SignalAsync(run, ReservedSignals.Cancel, Encoding.UTF8.GetBytes(reason ?? ""), ReservedSignals.CancelSignalId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates the run now, as an operator: the principal must be a verified one allowed to terminate runs, and the audit entry names it. A terminated run runs no more workflow code, so the conversations its executors
    /// started are asked to stop first, from here.
    /// </summary>
    public async ValueTask<bool> TerminateAsync(Principal principal, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        RunKey run = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        await ReadAsync(cancellationToken).ConfigureAwait(false); // so that the conversations started so far are known
        foreach (string conversation in _reader.Conversations)
        {
            if (await _owner.Client.GetCurrentRunAsync(_owner.Scope, conversation, cancellationToken).ConfigureAwait(false) is { } child)
            {
                await _owner.Client.SignalAsync(child, ReservedSignals.Cancel, Encoding.UTF8.GetBytes("the workflow that started this conversation was terminated"), ReservedSignals.CancelSignalId, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _owner.Client.TerminateRunAsync(principal, run, reason, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RunKey> CurrentAsync(CancellationToken cancellationToken) =>
        await _owner.Client.GetCurrentRunAsync(_owner.Scope, DurableWorkflow.WorkflowId(_definition.Name, InstanceId), cancellationToken).ConfigureAwait(false) ?? Run;
}

/// <summary>What is known of a run from the events of its history seen so far. Events are added as they are read, so a look costs what is new.</summary>
internal sealed class WorkflowReader
{
    private readonly HashSet<int> _steps = [];
    private readonly List<WorkflowOutput> _outputs = [];
    private readonly SortedSet<string> _conversations = new(StringComparer.Ordinal);
    private List<AfwRequest> _pending = [];
    private int _count;
    private DurableWorkflowStatus? _closed;
    private string? _error;

    public bool Closed => _closed is not null;

    /// <summary>The conversations the executors of the workflow started, as far as the history read so far shows.</summary>
    public IReadOnlyCollection<string> Conversations => _conversations;

    public void Add(IReadOnlyList<HistoryEvent> events)
    {
        foreach (HistoryEvent e in events)
        {
            switch (e)
            {
                case WorkflowStarted started:
                    {
                        AfwInput input = AfCodec.Read(started.Input, AfwJson.Default.AfwInput);
                        _outputs.AddRange(input.Outputs.Select(ToOutput));
                        _count = input.Steps;
                        var answered = input.Answered.Select(a => a.RequestId).ToHashSet(StringComparer.Ordinal);
                        _pending = [.. input.Waiting.Where(w => !answered.Contains(w.RequestId))];
                        foreach (string conversation in input.Conversations)
                        {
                            _conversations.Add(conversation);
                        }

                        break;
                    }

                case ActivityScheduled { ActivityType: AgentFrameworkNames.WorkflowStep } scheduled:
                    {
                        AfwStepRequest request = AfCodec.Read(scheduled.Input, AfwJson.Default.AfwStepRequest);
                        _steps.Add(scheduled.ActivityId);
                        var delivered = request.Responses.Select(r => r.RequestId).ToHashSet(StringComparer.Ordinal);
                        _pending.RemoveAll(p => delivered.Contains(p.RequestId));
                        break;
                    }

                case ActivityCompleted completed when _steps.Contains(completed.ActivityId):
                    {
                        AfwStepResult result = AfCodec.Read(completed.Result, AfwJson.Default.AfwStepResult);
                        if (result.Outcome != AfwOutcomes.Done)
                        {
                            break;
                        }

                        _count++;
                        _outputs.AddRange(result.Outputs.Select(ToOutput));
                        _pending = [.. result.Requests];
                        foreach (string conversation in result.Conversations)
                        {
                            _conversations.Add(conversation);
                        }

                        break;
                    }

                case WorkflowCompleted done:
                    {
                        AfwResult result = AfCodec.Read(done.Result, AfwJson.Default.AfwResult);
                        _outputs.Clear();
                        _outputs.AddRange(result.Outputs.Select(ToOutput));
                        _count = result.Steps;
                        _pending = [];
                        _closed = DurableWorkflowStatus.Completed;
                        break;
                    }

                case WorkflowFailed failed:
                    _closed = failed.ErrorType == nameof(SagaCancelledException) ? DurableWorkflowStatus.Cancelled : DurableWorkflowStatus.Failed;
                    _error = $"{failed.ErrorType}: {failed.Message}";
                    break;
                case RunTerminated terminated:
                    _closed = DurableWorkflowStatus.Terminated;
                    _error = terminated.Reason;
                    break;
            }
        }
    }

    public WorkflowView View(WorkflowDefinition definition)
    {
        DurableWorkflowStatus status = _closed ?? (_pending.Count > 0 ? DurableWorkflowStatus.Waiting : DurableWorkflowStatus.Running);
        return new WorkflowView(
            status, [.. _outputs],
            [.. _pending.Select(p => new WorkflowRequest(p.RequestId, p.PortId, p.Data) { IsApproval = definition.Registration.Approvals.ContainsKey(p.PortId) })],
            _count, _error);
    }

    private static WorkflowOutput ToOutput(AfwOutput output) => new(output.ExecutorId, output.Type, output.Data);
}
