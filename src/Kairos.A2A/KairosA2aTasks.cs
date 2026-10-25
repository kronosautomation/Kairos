using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A;
using Kairos.Agents;
using A2ARole = global::A2A.Role;
using A2ATaskStatus = global::A2A.TaskStatus;

namespace Kairos.A2A;

/// <summary>What an open run is waiting for someone to supply, read from its history (for example pending approvals). Makes the task <c>input-required</c>.</summary>
public interface IA2aInputProjection
{
    /// <summary>The questions that are open, keyed by a stable id. Empty when the run is not waiting for anyone.</summary>
    IReadOnlyDictionary<string, string> OpenQuestions(IReadOnlyList<HistoryEvent> history);
}

public sealed class KairosA2aTasksOptions
{
    /// <summary>When set (32 bytes or more), task ids carry an HMAC so a guessed or altered id is refused before any lookup.</summary>
    public byte[]? IdKey { get; init; }

    /// <summary>Open questions of a run. Null: tasks never report input-required.</summary>
    public IA2aInputProjection? Inputs { get; init; }
}

/// <summary>
/// Reports Kairos runs as A2A tasks, and nothing else: the run is the task, so a task survives restarts, deployments and moving between
/// servers, and its state is read from the run's history on every request. A task id belongs to one tenant and namespace; an id from another one is
/// indistinguishable from an id that does not exist.
/// </summary>
/// <remarks>
/// Mapping: an open run is <c>working</c> (or <c>input-required</c> while <see cref="KairosA2aTasksOptions.Inputs"/> reports open questions);
/// a completed run is <c>completed</c> with its result as an artifact; a run a saga cancelled is <c>canceled</c>; a run that ended because an approval was
/// refused is <c>rejected</c>; any other failure, and a run an operator terminated, is <c>failed</c>. A run that continues as new stays the same task.
/// </remarks>
public sealed class KairosA2aTasks
{
    private const string IdPrefix = "kat1_";
    private const int MaxContinuations = 64;
    private const string ResultArtifactId = "result";

    private readonly KairosClient _client;
    private readonly TenantScope _scope;
    private readonly KairosA2aTasksOptions _options;

    public KairosA2aTasks(KairosClient client, TenantScope scope, KairosA2aTasksOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _scope = scope;
        _options = options ?? new KairosA2aTasksOptions();
        if (_options.IdKey is { Length: < 32 })
        {
            throw new ArgumentException("The id key must be at least 32 bytes.", nameof(options));
        }
    }

    /// <summary>The task id for a run of this tenant and namespace.</summary>
    public string TaskIdFor(RunKey run)
    {
        if (run.Scope != _scope)
        {
            throw new ArgumentException("The run belongs to another tenant or namespace.", nameof(run));
        }

        byte[] bytes = new byte[48];
        run.TenantId.TryWriteBytes(bytes.AsSpan(0, 16), bigEndian: true, out _);
        run.NamespaceId.TryWriteBytes(bytes.AsSpan(16, 16), bigEndian: true, out _);
        run.RunId.TryWriteBytes(bytes.AsSpan(32, 16), bigEndian: true, out _);
        byte[] all = _options.IdKey is null ? bytes : [.. bytes, .. HMACSHA256.HashData(_options.IdKey, bytes)];
        return IdPrefix + Convert.ToBase64String(all).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The run a task id names, or null for an id that is malformed, altered, or belongs to another tenant or namespace.</summary>
    public RunKey? RunFor(string taskId)
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
            if (all.Length != (_options.IdKey is null ? 48 : 80))
            {
                return null;
            }

            if (_options.IdKey is not null && !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_options.IdKey, all.AsSpan(0, 48)), all.AsSpan(48)))
            {
                return null;
            }

            var key = new RunKey(new Guid(all.AsSpan(0, 16), bigEndian: true), new Guid(all.AsSpan(16, 16), bigEndian: true), new Guid(all.AsSpan(32, 16), bigEndian: true));
            return key.Scope == _scope ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The task for a run, or null if the run does not exist.</summary>
    public async Task<AgentTask?> GetAsync(RunKey run, CancellationToken cancellationToken)
    {
        if (run.Scope != _scope)
        {
            return null;
        }

        return await LoadAsync(TaskIdFor(run), run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The task for an id, or null if there is none.</summary>
    public async Task<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        RunKey? run = RunFor(taskId);
        return run is null ? null : await LoadAsync(taskId, run.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the run behind a task to stop and roll back its saga. Returns the task as it now stands, or null for an unknown id. A task that
    /// has already ended is returned unchanged; the caller decides whether that is an error (A2A: <c>TaskNotCancelable</c>).
    /// </summary>
    public async Task<AgentTask?> RequestCancelAsync(string taskId, string reason, CancellationToken cancellationToken)
    {
        AgentTask? task = await GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || task.Status.State.IsTerminal())
        {
            return task;
        }

        RunKey current = await CurrentRunAsync(RunFor(taskId)!.Value, cancellationToken).ConfigureAwait(false);
        await _client.SignalAsync(current, AgentClient.CancelSignalName, Encoding.UTF8.GetBytes(reason), "a2a-cancel", cancellationToken).ConfigureAwait(false);
        return await GetAsync(taskId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run that is current for a task: the task's own run, or the latest after continue-as-new.</summary>
    public async Task<RunKey> CurrentRunAsync(RunKey run, CancellationToken cancellationToken)
    {
        RunKey current = run;
        for (int hops = 0; hops < MaxContinuations; hops++)
        {
            IReadOnlyList<HistoryEvent> history = await _client.GetOpenedHistoryAsync(current, cancellationToken).ConfigureAwait(false);
            if (history.Count == 0 || history[^1] is not WorkflowContinued continued)
            {
                break;
            }

            current = new RunKey(current.TenantId, current.NamespaceId, continued.NewRunId);
        }

        return current;
    }

    private async Task<AgentTask?> LoadAsync(string taskId, RunKey start, CancellationToken cancellationToken)
    {
        RunKey current = await CurrentRunAsync(start, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HistoryEvent> history = await _client.GetOpenedHistoryAsync(current, cancellationToken).ConfigureAwait(false);
        if (history.Count == 0)
        {
            return null;
        }

        IReadOnlyList<HistoryEvent> first = current == start ? history : await _client.GetOpenedHistoryAsync(start, cancellationToken).ConfigureAwait(false);
        return Project(taskId, first.Count > 0 ? first[0].Timestamp : history[0].Timestamp, history);
    }

    private AgentTask Project(string taskId, DateTimeOffset created, IReadOnlyList<HistoryEvent> history)
    {
        TaskState state = TaskState.Working;
        string? statusText = null;
        List<Artifact>? artifacts = null;

        switch (history[^1])
        {
            case WorkflowCompleted done:
                state = TaskState.Completed;
                artifacts = [ResultArtifact(done.Result)];
                break;
            case WorkflowFailed failed when failed.ErrorType == nameof(SagaCancelledException):
                state = TaskState.Canceled;
                statusText = failed.Message;
                break;
            case WorkflowFailed failed when failed.ErrorType == nameof(ApprovalRejectedException):
                state = TaskState.Rejected;
                statusText = failed.Message;
                break;
            case WorkflowFailed failed:
                state = TaskState.Failed;
                statusText = $"{failed.ErrorType}: {failed.Message}";
                break;
            case RunTerminated terminated:
                state = TaskState.Failed;
                statusText = terminated.Reason;
                break;
            default:
                if (_options.Inputs?.OpenQuestions(history) is { Count: > 0 } open)
                {
                    state = TaskState.InputRequired;
                    statusText = string.Join("\n", open.Values);
                }

                break;
        }

        return new AgentTask
        {
            Id = taskId,
            ContextId = "ctx-" + taskId,
            Status = new A2ATaskStatus
            {
                State = state,
                Timestamp = history[^1].Timestamp,
                Message = statusText is null ? null : new Message { Role = A2ARole.Agent, MessageId = "status-" + taskId + "-" + history[^1].Sequence, Parts = [Part.FromText(statusText)] },
            },
            Artifacts = artifacts,
            Metadata = new Dictionary<string, JsonElement> { ["createdAt"] = JsonSerializer.SerializeToElement(created, KairosA2aJson.DateTimeOffsetInfo) },
        };
    }

    private static Artifact ResultArtifact(byte[] result)
    {
        Part part;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(result);
            part = doc.RootElement.ValueKind == JsonValueKind.Object ? Part.FromData(doc.RootElement.Clone()) : Part.FromText(Encoding.UTF8.GetString(result));
        }
        catch (JsonException)
        {
            part = Part.FromText(Encoding.UTF8.GetString(result));
        }

        return new Artifact { ArtifactId = ResultArtifactId, Name = "result", Parts = [part] };
    }
}

internal static class KairosA2aJson
{
    public static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<DateTimeOffset> DateTimeOffsetInfo =
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<DateTimeOffset>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(DateTimeOffset));
}
