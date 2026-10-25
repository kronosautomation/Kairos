using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairos.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kairos.Mcp;

/// <summary>How the result and the failure of a run become the JSON an MCP task reports.</summary>
public interface IRunTaskMapper
{
    /// <summary>The task result for a run that completed with <paramref name="result"/>.</summary>
    JsonElement MapResult(byte[] result);

    /// <summary>The task error for a run that failed.</summary>
    JsonElement MapError(string errorType, string message);
}

/// <summary>Reports a run's result as it is when it is JSON, and as text content otherwise; errors as <c>{"type","message"}</c>.</summary>
public sealed class DefaultRunTaskMapper : IRunTaskMapper
{
    public JsonElement MapResult(byte[] result)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // not JSON: reported as text below
        }

        return Text(Encoding.UTF8.GetString(result));
    }

    public JsonElement MapError(string errorType, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", errorType);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement Text(string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}

/// <summary>What an attached run is waiting for a person or a client to supply, read from its history (for example pending approvals).</summary>
public interface IRunInputProjection
{
    /// <summary>The open input requests, keyed by a stable id the client answers with.</summary>
    IReadOnlyDictionary<string, InputRequest> PendingInputs(IReadOnlyList<HistoryEvent> history);
}

/// <summary>
/// Delivers a client's answer to an attached run. The implementation decides who answered from the <b>authenticated caller</b> and
/// never from anything inside the response: a client must not be able to name its own approver.
/// </summary>
public interface IRunInputResponder
{
    ValueTask RespondAsync(RunKey run, string requestKey, InputResponse response, CancellationToken cancellationToken);
}

public sealed class McpTaskStoreOptions
{
    /// <summary>When set (32 bytes or more), task ids carry an HMAC so a guessed or altered id is refused before any lookup.</summary>
    public byte[]? IdKey { get; init; }

    /// <summary>The poll interval reported to clients.</summary>
    public long PollIntervalMs { get; init; } = 1000;

    /// <summary>The retention reported to clients. The run's history is kept according to the store's own retention, not this value.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>
    /// A plain task that has been working or waiting for input this long without any update is recorded as failed ("abandoned"). The SDK runs
    /// a task's work inside the server process, so when that process dies the task's state survives in Kairos but nothing is left to finish
    /// it. Set this to a little more than your longest quiet period. Null (the default) never abandons a task. Tasks attached to workflow
    /// runs are never abandoned: their run is durable.
    /// </summary>
    public TimeSpan? AbandonedAfter { get; init; }

    /// <summary>The clock used to judge <see cref="AbandonedAfter"/>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IRunTaskMapper Mapper { get; init; } = new DefaultRunTaskMapper();

    /// <summary>Open input requests of attached runs. Null: attached runs never report input_required.</summary>
    public IRunInputProjection? Inputs { get; init; }

    /// <summary>Answers for attached runs. Null: answering an attached task is refused.</summary>
    public IRunInputResponder? Responder { get; init; }
}

/// <summary>
/// An <see cref="IMcpTaskStore"/> whose tasks are Kairos runs. A task survives a restart of the server because its state is the run's
/// history. Two kinds of task:
/// <list type="bullet">
/// <item><description><b>Plain tasks</b> (<see cref="CreateTaskAsync"/>) are runs of <see cref="McpTaskWorkflow"/>: a durable record of the
/// updates your server makes. The work itself runs wherever your server runs it.</description></item>
/// <item><description><b>Attached tasks</b> (<see cref="AttachRunAsync"/>) are an existing workflow run, for tools that are Kairos workflows:
/// the task's status is the run's status, its result is the run's result, and cancelling it asks the run to roll back.</description></item>
/// </list>
/// A task id belongs to one tenant and namespace; an id from another one is indistinguishable from an unknown id.
/// </summary>
public sealed class KairosMcpTaskStore : IMcpTaskStore
{
    private const string IdPrefix = "kmt1_";
    private const int MaxContinuations = 64;

    private readonly KairosClient _client;
    private readonly TenantScope _scope;
    private readonly McpTaskStoreOptions _options;

    public KairosMcpTaskStore(KairosClient client, TenantScope scope, McpTaskStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _scope = scope;
        _options = options ?? new McpTaskStoreOptions();
        if (_options.IdKey is { Length: < 32 })
        {
            throw new ArgumentException("The id key must be at least 32 bytes.", nameof(options));
        }
    }

    /// <summary>Raised for each answer delivered with <see cref="ResolveInputRequestsAsync"/>.</summary>
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    // ---- creating and attaching ----------------------------------------------------------------------------

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken)
    {
        StartRunResult started = await _client
            .StartAsync(_scope, "mcp-task-" + Guid.NewGuid().ToString("N"), McpTaskWorkflow.WorkflowType, [], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        string id = EncodeId(new RunKey(_scope.TenantId, _scope.NamespaceId, started.RunId));
        return await GetTaskAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The task run could not be read back.");
    }

    /// <summary>Makes an existing workflow run reportable as a task. Calling it again for the same run returns the same task id.</summary>
    public async Task<McpTaskInfo> AttachRunAsync(RunKey run, CancellationToken cancellationToken)
    {
        if (run.Scope != _scope)
        {
            throw new ArgumentException("The run belongs to another tenant or namespace.", nameof(run));
        }

        string id = EncodeId(run);
        return await GetTaskAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ArgumentException("The run does not exist.", nameof(run));
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        TaskView? view = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        return view?.Info;
    }

    /// <summary>The answer a client gave to an input request of a plain task, read from the run (so a server that restarted can still find it).</summary>
    public async Task<InputResponse?> TryGetInputResponseAsync(string taskId, string requestId, CancellationToken cancellationToken)
    {
        TaskView? view = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (view?.Responses.TryGetValue(requestId, out JsonElement raw) != true)
        {
            return null;
        }

        return JsonSerializer.Deserialize(raw, McpJsonUtilities.DefaultOptions.GetTypeInfo<InputResponse>());
    }

    // ---- updating plain tasks -------------------------------------------------------------------------------

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, new TaskUpdate { Kind = TaskUpdate.Complete, Payload = result }, cancellationToken);

    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken) =>
        UpdateAsync(taskId, new TaskUpdate { Kind = TaskUpdate.Fail, Payload = error }, cancellationToken);

    public async Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputRequests);
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string key, InputRequest request) in inputRequests)
        {
            map[key] = JsonSerializer.SerializeToElement(request, McpJsonUtilities.DefaultOptions.GetTypeInfo<InputRequest>());
        }

        await UpdateAsync(taskId, new TaskUpdate { Kind = TaskUpdate.Requests, Payload = JsonSerializer.SerializeToElement(map, TaskJson.Default.DictionaryStringJsonElement) }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken)
    {
        TaskView? view = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (view is null || view.Info.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled)
        {
            return false;
        }

        if (view.Attached)
        {
            // The run's own saga rolls back; the task reports Cancelled once the run has ended that way.
            return await _client.SignalAsync(view.Current, AgentClient.CancelSignalName, Encoding.UTF8.GetBytes("cancelled through an MCP task"), "mcp-cancel", cancellationToken)
                .ConfigureAwait(false);
        }

        return await _client.SignalAsync(view.Current, McpTaskWorkflow.UpdateSignal, new TaskUpdate { Kind = TaskUpdate.Cancel }.ToBytes(), signalId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputResponses);
        TaskView view = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task '{taskId}' not found.");
        if (view.Attached)
        {
            if (_options.Responder is null)
            {
                throw new InvalidOperationException("This task is a workflow run that takes no input through MCP.");
            }

            foreach ((string key, InputResponse response) in inputResponses)
            {
                await _options.Responder.RespondAsync(view.Current, key, response, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach ((string key, InputResponse response) in inputResponses)
            {
                map[key] = response.RawValue.Clone();
            }

            await UpdateAsync(taskId, new TaskUpdate { Kind = TaskUpdate.Response, Payload = JsonSerializer.SerializeToElement(map, TaskJson.Default.DictionaryStringJsonElement) }, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach ((string key, InputResponse response) in inputResponses)
        {
            InputResponseReceived?.Invoke(new InputResponseReceivedEventArgs { TaskId = taskId, RequestId = key, Response = response });
        }
    }

    private async Task UpdateAsync(string taskId, TaskUpdate update, CancellationToken cancellationToken)
    {
        TaskView view = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Task '{taskId}' not found.");
        if (view.Attached)
        {
            throw new InvalidOperationException("This task is a workflow run; its workflow decides its outcome.");
        }

        // A closed run takes no more updates: the first terminal update wins, and later ones are ignored (as the reference store does).
        await _client.SignalAsync(view.Current, McpTaskWorkflow.UpdateSignal, update.ToBytes(), signalId: null, cancellationToken).ConfigureAwait(false);
    }

    // ---- ids --------------------------------------------------------------------------------------------------

    private string EncodeId(RunKey run)
    {
        byte[] bytes = new byte[48];
        run.TenantId.TryWriteBytes(bytes.AsSpan(0, 16), bigEndian: true, out _);
        run.NamespaceId.TryWriteBytes(bytes.AsSpan(16, 16), bigEndian: true, out _);
        run.RunId.TryWriteBytes(bytes.AsSpan(32, 16), bigEndian: true, out _);
        byte[] all = _options.IdKey is null ? bytes : [.. bytes, .. HMACSHA256.HashData(_options.IdKey, bytes)];
        return IdPrefix + Convert.ToBase64String(all).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private RunKey? DecodeId(string taskId)
    {
        if (taskId is null || taskId.Length > 200 || !taskId.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            string padded = taskId[IdPrefix.Length..].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            byte[] all = Convert.FromBase64String(padded);
            int expected = _options.IdKey is null ? 48 : 80;
            if (all.Length != expected)
            {
                return null;
            }

            if (_options.IdKey is not null && !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_options.IdKey, all.AsSpan(0, 48)), all.AsSpan(48)))
            {
                return null;
            }

            var key = new RunKey(new Guid(all.AsSpan(0, 16), bigEndian: true), new Guid(all.AsSpan(16, 16), bigEndian: true), new Guid(all.AsSpan(32, 16), bigEndian: true));
            return key.Scope == _scope ? key : null; // another tenant's or namespace's id looks exactly like an unknown id
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- projecting a run into a task ---------------------------------------------------------------------

    private sealed record TaskView(McpTaskInfo Info, RunKey Current, bool Attached, IReadOnlyDictionary<string, JsonElement> Responses);

    private async Task<TaskView?> LoadAsync(string taskId, CancellationToken cancellationToken)
    {
        RunKey? start = DecodeId(taskId);
        if (start is null)
        {
            return null;
        }

        RunKey current = start.Value;
        IReadOnlyList<HistoryEvent> history = await _client.GetOpenedHistoryAsync(current, cancellationToken).ConfigureAwait(false);
        for (int hops = 0; history.Count > 0 && history[^1] is WorkflowContinued continued && hops < MaxContinuations; hops++)
        {
            current = new RunKey(current.TenantId, current.NamespaceId, continued.NewRunId);
            history = await _client.GetOpenedHistoryAsync(current, cancellationToken).ConfigureAwait(false);
        }

        if (history.Count == 0 || history[0] is not WorkflowStarted started)
        {
            return null;
        }

        DateTimeOffset created = history[0].Timestamp;
        if (start.Value != current)
        {
            IReadOnlyList<HistoryEvent> first = await _client.GetOpenedHistoryAsync(start.Value, cancellationToken).ConfigureAwait(false);
            created = first.Count > 0 ? first[0].Timestamp : created;
        }

        if (!string.Equals(started.WorkflowType, McpTaskWorkflow.WorkflowType, StringComparison.Ordinal))
        {
            return ProjectAttached(taskId, current, history, created);
        }

        TaskView plain = ProjectPlain(taskId, current, history, created);
        if (_options.AbandonedAfter is { } limit && plain.Info.Status is McpTaskStatus.Working or McpTaskStatus.InputRequired &&
            _options.Time.GetUtcNow() - history[^1].Timestamp > limit)
        {
            // Recorded, not just reported, so a late completion from a server that came back cannot flip the answer afterwards.
            var error = _options.Mapper.MapError("TaskAbandoned", "The server that was running this task stopped before it finished.");
            await _client.SignalAsync(current, McpTaskWorkflow.UpdateSignal, new TaskUpdate { Kind = TaskUpdate.Fail, Payload = error }.ToBytes(), signalId: "abandoned", cancellationToken).ConfigureAwait(false);
            history = await _client.GetOpenedHistoryAsync(current, cancellationToken).ConfigureAwait(false);
            plain = ProjectPlain(taskId, current, history, created);
        }

        return plain;
    }

    private TaskView ProjectPlain(string taskId, RunKey current, IReadOnlyList<HistoryEvent> history, DateTimeOffset created)
    {
        var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var responses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        McpTaskStatus status = McpTaskStatus.Working;

        foreach (SignalReceived signal in history.OfType<SignalReceived>())
        {
            if (!string.Equals(signal.SignalName, McpTaskWorkflow.UpdateSignal, StringComparison.Ordinal) || TaskUpdate.TryParse(signal.Payload) is not { } update)
            {
                continue;
            }

            switch (update.Kind)
            {
                case TaskUpdate.Requests when update.Payload is { ValueKind: JsonValueKind.Object } requests:
                    pending.Clear();
                    foreach (JsonProperty p in requests.EnumerateObject())
                    {
                        pending[p.Name] = p.Value.Clone();
                    }

                    status = pending.Count > 0 ? McpTaskStatus.InputRequired : McpTaskStatus.Working;
                    break;
                case TaskUpdate.Response when update.Payload is { ValueKind: JsonValueKind.Object } answers:
                    foreach (JsonProperty p in answers.EnumerateObject())
                    {
                        if (pending.Remove(p.Name))
                        {
                            responses[p.Name] = p.Value.Clone();
                        }
                    }

                    if (status == McpTaskStatus.InputRequired && pending.Count == 0)
                    {
                        status = McpTaskStatus.Working;
                    }

                    break;
                case TaskUpdate.Complete:
                    return Finish(McpTaskStatus.Completed, update.Payload ?? JsonSerializer.SerializeToElement((string?)null, TaskJson.Default.String), null);
                case TaskUpdate.Fail:
                    return Finish(McpTaskStatus.Failed, null, update.Payload ?? JsonSerializer.SerializeToElement((string?)null, TaskJson.Default.String));
                case TaskUpdate.Cancel:
                    return Finish(McpTaskStatus.Cancelled, null, null);
            }
        }

        // The run can also end without an update, for instance when an operator terminates it or the history cap ends it.
        if (history[^1] is RunTerminated terminated)
        {
            return Finish(McpTaskStatus.Failed, null, _options.Mapper.MapError(nameof(RunTerminated), terminated.Reason));
        }

        if (history[^1] is WorkflowFailed failed)
        {
            return Finish(McpTaskStatus.Failed, null, _options.Mapper.MapError(failed.ErrorType, failed.Message));
        }

        IReadOnlyDictionary<string, InputRequest> inputs = status == McpTaskStatus.InputRequired
            ? pending.ToDictionary(kv => kv.Key, kv => JsonSerializer.Deserialize(kv.Value, McpJsonUtilities.DefaultOptions.GetTypeInfo<InputRequest>())!, StringComparer.Ordinal)
            : new Dictionary<string, InputRequest>();
        return View(status, null, null, inputs);

        TaskView Finish(McpTaskStatus final, JsonElement? finalResult, JsonElement? finalError) => View(final, finalResult, finalError, new Dictionary<string, InputRequest>());

        TaskView View(McpTaskStatus s, JsonElement? r, JsonElement? e, IReadOnlyDictionary<string, InputRequest> inputRequests) =>
            new(Info(taskId, s, created, history[^1].Timestamp, r, e, inputRequests), current, Attached: false, responses);
    }

    private TaskView ProjectAttached(string taskId, RunKey current, IReadOnlyList<HistoryEvent> history, DateTimeOffset created)
    {
        McpTaskStatus status = McpTaskStatus.Working;
        JsonElement? result = null, error = null;
        IReadOnlyDictionary<string, InputRequest> inputs = new Dictionary<string, InputRequest>();

        switch (history[^1])
        {
            case WorkflowCompleted done:
                status = McpTaskStatus.Completed;
                result = _options.Mapper.MapResult(done.Result);
                break;
            case WorkflowFailed failed when failed.ErrorType == nameof(SagaCancelledException):
                status = McpTaskStatus.Cancelled;
                break;
            case WorkflowFailed failed:
                status = McpTaskStatus.Failed;
                error = _options.Mapper.MapError(failed.ErrorType, failed.Message);
                break;
            case RunTerminated terminated:
                status = McpTaskStatus.Failed;
                error = _options.Mapper.MapError(nameof(RunTerminated), terminated.Reason);
                break;
            default:
                if (_options.Inputs?.PendingInputs(history) is { Count: > 0 } open)
                {
                    status = McpTaskStatus.InputRequired;
                    inputs = open;
                }

                break;
        }

        return new TaskView(Info(taskId, status, created, history[^1].Timestamp, result, error, inputs), current, Attached: true, new Dictionary<string, JsonElement>());
    }

    private McpTaskInfo Info(
        string taskId, McpTaskStatus status, DateTimeOffset created, DateTimeOffset updated, JsonElement? result, JsonElement? error,
        IReadOnlyDictionary<string, InputRequest> inputs) =>
        new(taskId, status, created, updated, _options.TimeToLive, _options.PollIntervalMs, null, result, error, inputs);
}
