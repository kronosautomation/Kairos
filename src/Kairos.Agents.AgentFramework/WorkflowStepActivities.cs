using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Kairos.Agents.AgentFramework;

public sealed partial class AgentFrameworkRuntime
{
    /// <summary>The retry settings of a workflow step. Fixed in code (workflow code must not read the worker's options), and a failure that is the caller's to fix is never retried.</summary>
    internal static readonly RetryPolicy WorkflowStepRetry = new()
    {
        MaximumAttempts = 3,
        InitialInterval = TimeSpan.FromSeconds(1),
        MaximumInterval = TimeSpan.FromSeconds(30),
        NonRetryableErrorTypes =
        [
            nameof(SnapshotTooLargeException), nameof(SnapshotShapeException), nameof(SnapshotIntegrityException), nameof(UnknownWorkflowException),
            nameof(WorkflowAgentWaitException), nameof(WorkflowIncompatibleException), nameof(FormatException),
        ],
    };

    private static readonly System.Diagnostics.Metrics.Counter<long> Supersteps = Kairos.KairosMetrics.Meter.CreateCounter<long>(
        "kairos.af.workflow.supersteps", "{superstep}", "Supersteps of durable workflows run by this process");

    /// <summary>
    /// Runs the supersteps of one step of a workflow, in an activity: Agent Framework code (the executors, the routing, the checkpoint manager) executes only here, never inside the replayed Kairos workflow. The step restores
    /// the checkpoint it is given into a fresh workflow, runs until it has run as many supersteps as asked (or until the workflow is idle or waits for a request), and returns the new checkpoint, the outputs and the
    /// requests the workflow is now waiting for. If the worker dies in the middle, the next attempt runs the same supersteps again from the same checkpoint: executors must be safe to run twice.
    /// </summary>
    private async Task<byte[]> WorkflowStepAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        AfwStepRequest request = AfCodec.Read(input, AfwJson.Default.AfwStepRequest);
        WorkflowDefinition definition = WorkflowDefinitionOf(request.Workflow);
        WorkflowRegistration registration = definition.Registration;

        int limit = Options.MaxCheckpointBytes;
        SessionText? before = null;
        if (request.Checkpoint is { } sent)
        {
            before = SessionText.Of(SnapshotCodec.Canonical(sent));
            if (request.CheckpointHash is { } expected && !string.Equals(before.Hash, expected, StringComparison.Ordinal))
            {
                throw new SnapshotIntegrityException("The checkpoint a step was sent is not the one its hash names.");
            }

            Snapshots.Put(context.Run.TenantId, context.Run.NamespaceId, before.Hash, before.Text);
        }
        else if (request.CheckpointHash is { } hash)
        {
            if (!Snapshots.TryGet(context.Run.TenantId, context.Run.NamespaceId, hash, out string held))
            {
                return AfCodec.Write(new AfwStepResult { Outcome = AfwOutcomes.NeedSnapshot, CheckpointHash = hash }, AfwJson.Default.AfwStepResult);
            }

            before = new SessionText(JsonNode.Parse(held)!, held, hash);
        }

        if (before is not null && before.Bytes > limit)
        {
            throw new SnapshotTooLargeException(
                $"The checkpoint of workflow '{definition.Name}' is larger than the {limit} bytes allowed (MaxCheckpointBytes). Keep large values out of executor state and messages (pass references), or raise the limit.");
        }

        if (before is null && request.Input is null)
        {
            throw new FormatException("A workflow step was given neither an input nor a checkpoint.");
        }

        if (request.Structure is { } pinned && (!string.Equals(pinned, definition.Structure, StringComparison.Ordinal) || !string.Equals(request.Version, WorkflowPackageVersion, StringComparison.Ordinal)))
        {
            // A person decides whether a run may go on with a changed workflow, but only when it can: if Agent Framework would refuse the checkpoint (it requires the same executors and edges) no approval helps,
            // so that is found out first and ends the run with the reason. Not a failure otherwise: a recorded outcome the workflow turns into an approval.
            if (before is not null)
            {
                await TryRestoreAsync(context, definition, before, cancellationToken).ConfigureAwait(false);
            }

            return AfCodec.Write(
                new AfwStepResult
                {
                    Outcome = AfwOutcomes.StructureChanged,
                    PinnedStructure = pinned,
                    PinnedVersion = request.Version,
                    Structure = definition.Structure,
                    Version = WorkflowPackageVersion,
                },
                AfwJson.Default.AfwStepResult);
        }

        var scope = new WorkflowStepScope(definition.Name, context.IdempotencyKey, Options.WorkflowAgentWait);
        using IDisposable entered = WorkflowStepScope.Enter(scope);
        var carrier = new CarrierStore();
        JsonSerializerOptions json = registration.Options;
        CheckpointManager manager = CheckpointManager.CreateJson(carrier, json);
        var agents = new DurableAgents(this, Client, new TenantScope(context.Run.TenantId, context.Run.NamespaceId), Agents.Config.Time);
        Workflow workflow = registration.Factory(new WorkflowBuildContext(agents, Services));
        if (!string.Equals(WorkflowDefinition.StructureOf(workflow), definition.Structure, StringComparison.Ordinal))
        {
            throw new UnknownWorkflowException($"The factory of workflow '{definition.Name}' built a different workflow than it did when the workflow was registered. It must build the same graph every time.");
        }

        var outputs = new List<AfwOutput>();
        Exception? failure = null;
        string? failedExecutor = null;
        SuperStepCompletionInfo? last = null;
        int ran = 0;
        var environment = InProcessExecution.Lockstep.WithCheckpointing(manager);
        StreamingRun run = before is null
            ? await registration.Start(environment, workflow, request.Input!.Value, CarrierStore.Session, cancellationToken).ConfigureAwait(false)
            : await ResumeAsync(environment, workflow, carrier.Seed(SnapshotCodec.Element(before.Text)), definition, cancellationToken).ConfigureAwait(false);
        await using (run.ConfigureAwait(false))
        {
            foreach (AfwResponse response in request.Responses)
            {
                await run.SendResponseAsync(ToResponse(workflow, definition, response, json)).ConfigureAwait(false);
            }

            await foreach (WorkflowEvent e in run.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken).ConfigureAwait(false))
            {
                switch (e)
                {
                    case WorkflowOutputEvent output when output.Data is { } value:
                        outputs.Add(ToOutput(definition, output.ExecutorId, value));
                        break;
                    case ExecutorFailedEvent failed:
                        failure ??= failed.Data;
                        failedExecutor ??= failed.ExecutorId;
                        break;
                    case WorkflowErrorEvent error:
                        failure ??= error.Exception;
                        break;
                    case SuperStepCompletedEvent completed:
                        if (completed.CompletionInfo is not { } info)
                        {
                            break;
                        }

                        ran++;
                        last = info;

                        // Stop here when the asked-for supersteps have run and there is more to do: the next ones are the next step's. (The stream is pulled, so nothing past this point has started.)
                        if (failure is null && ran >= Math.Max(1, request.Supersteps) && info.HasPendingMessages)
                        {
                            goto Stopped;
                        }

                        break;
                }
            }

        Stopped:;
        }

        for (Exception? inner = failure; inner is not null; inner = inner.InnerException)
        {
            if (inner is WorkflowAgentWaitException wait)
            {
                throw wait; // our own words (they name the conversation and say what to do instead), and not worth another attempt
            }
        }

        if (failure is not null || failedExecutor is not null)
        {
            // Agent Framework reports a failing executor as events. The checkpoint before the failing superstep is the last good one, so failing the step is enough: the next attempt runs the superstep again.
            throw new WorkflowExecutorFailedException(
                $"An executor{(failedExecutor is null ? "" : $" ('{failedExecutor}')")} of workflow '{definition.Name}' failed ({failure?.GetType().Name ?? "unknown"}).", failure ?? new InvalidOperationException());
        }

        if (last?.Checkpoint is not { } checkpointInfo)
        {
            throw new FormatException($"Workflow '{definition.Name}' ran no superstep in a step that was asked to run one: the checkpoint it was given has nothing left to do.");
        }

        JsonNode checkpoint = JsonNode.Parse((await carrier.RetrieveCheckpointAsync(CarrierStore.Session, checkpointInfo).ConfigureAwait(false)).GetRawText())!;
        if (Redactor is { } redact)
        {
            definition.Policy.RedactCheckpoint(checkpoint, redact);
        }

        SessionText after = SessionText.Of(SnapshotCodec.Canonical(JsonSerializer.SerializeToElement(checkpoint)));
        if (after.Bytes > limit)
        {
            throw new SnapshotTooLargeException(
                $"The checkpoint of workflow '{definition.Name}' grew past the {limit} bytes allowed (MaxCheckpointBytes). Keep large values out of executor state and messages (pass references), or raise the limit.");
        }

        AfwRequest[] requests = RequestsOf(checkpoint);
        JsonElement? whole = null;
        JsonElement? change = null;
        string? baseHash = null;
        bool recordWhole = before is null || request.StepsSinceFull + 1 >= Options.FullSnapshotEvery;
        if (!recordWhole)
        {
            string delta = (SnapshotCodec.Diff(before!.Node, after.Node) ?? new JsonObject { ["e"] = 1 }).ToJsonString();
            if (delta.Length < after.Text.Length)
            {
                change = SnapshotCodec.Element(delta);
                baseHash = before.Hash;
            }
        }

        if (change is null)
        {
            whole = SnapshotCodec.Element(after.Text);
        }

        Snapshots.Put(context.Run.TenantId, context.Run.NamespaceId, after.Hash, after.Text);
        Supersteps.Add(ran, new KeyValuePair<string, object?>("workflow", definition.Name));
        return AfCodec.Write(
            new AfwStepResult
            {
                Structure = definition.Structure,
                Version = WorkflowPackageVersion,
                Checkpoint = whole,
                Delta = change,
                CheckpointHash = after.Hash,
                BaseHash = baseHash,
                StepId = context.IdempotencyKey,
                CheckpointBytes = after.Bytes,
                Supersteps = ran,
                More = last.HasPendingMessages,
                Done = !last.HasPendingMessages && requests.Length == 0,
                Requests = requests,
                Outputs = [.. outputs],
                Conversations = scope.Owned,
            },
            AfwJson.Default.AfwStepResult);
    }

    /// <summary>The version of the Agent Framework workflows package: its checkpoint format is the package's. (A test can say it is another one, to stand for a deploy of a newer package.)</summary>
    internal string WorkflowPackageVersion { get; set; } = WorkflowDefinition.PackageVersion;

    private static async ValueTask<StreamingRun> ResumeAsync(
        Microsoft.Agents.AI.Workflows.InProc.InProcessExecutionEnvironment environment, Workflow workflow, CheckpointInfo checkpoint, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        try
        {
            return await environment.ResumeStreamingAsync(workflow, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new WorkflowIncompatibleException(
                $"Workflow '{definition.Name}': Agent Framework cannot restore the checkpoint the run was left at into the workflow this worker has ({ex.Message}). Agent Framework resumes a checkpoint only into a workflow with the same executors, edges and ports. " +
                "Let the runs in flight finish, or cancel them, before deploying a change to the graph, or register the changed graph under a new name.", ex);
        }
    }

    private async Task TryRestoreAsync(ActivityContext context, WorkflowDefinition definition, SessionText checkpoint, CancellationToken cancellationToken)
    {
        var carrier = new CarrierStore();
        var agents = new DurableAgents(this, Client, new TenantScope(context.Run.TenantId, context.Run.NamespaceId), Agents.Config.Time);
        Workflow workflow = definition.Registration.Factory(new WorkflowBuildContext(agents, Services));
        var environment = InProcessExecution.Lockstep.WithCheckpointing(CheckpointManager.CreateJson(carrier, definition.Registration.Options));
        StreamingRun run = await ResumeAsync(environment, workflow, carrier.Seed(SnapshotCodec.Element(checkpoint.Text)), definition, cancellationToken).ConfigureAwait(false);
        await run.DisposeAsync().ConfigureAwait(false); // restoring runs nothing; this was only to see that it can be done
    }

    private AfwOutput ToOutput(WorkflowDefinition definition, string executorId, object value)
    {
        Type type = value.GetType();
        JsonNode? node = JsonSerializer.SerializeToNode(value, type, definition.Registration.Options);
        if (Redactor is { } redact)
        {
            node = definition.Policy.Redact(type.FullName ?? type.Name, node, redact); // refuses a type the redactor does not know
        }

        return new AfwOutput { ExecutorId = executorId, Type = WorkflowTypePolicy.Normalize(type.FullName ?? type.Name), Data = node is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(node) };
    }

    private static ExternalResponse ToResponse(Workflow workflow, WorkflowDefinition definition, AfwResponse response, JsonSerializerOptions json)
    {
        if (!definition.Ports.TryGetValue(response.PortId, out RequestPort? port) || !workflow.ReflectPorts().TryGetValue(response.PortId, out RequestPortInfo? info))
        {
            throw new FormatException($"Workflow '{definition.Name}' has no request port '{response.PortId}'.");
        }

        object value = response.Data.Deserialize(port.Response, json) ?? throw new FormatException($"The response to request '{response.RequestId}' is empty, and a response cannot be null.");
        return new ExternalResponse(info, response.RequestId, new PortableValue(value));
    }

    /// <summary>The requests the checkpoint says are outstanding (already redacted when a redactor is configured). A checkpoint is the one source: a request restored from a checkpoint is announced again by the framework, and that is not read.</summary>
    private static AfwRequest[] RequestsOf(JsonNode checkpoint)
    {
        var requests = new List<AfwRequest>();
        if (checkpoint["runnerData"]?["outstandingRequests"] is JsonArray outstanding)
        {
            foreach (JsonNode? item in outstanding)
            {
                if (item is not JsonObject request || request["requestId"]?.GetValue<string>() is not { } id)
                {
                    continue;
                }

                requests.Add(new AfwRequest
                {
                    RequestId = id,
                    PortId = request["portInfo"]?["portId"]?.GetValue<string>() ?? "",
                    Data = JsonSerializer.SerializeToElement(request["data"]?["value"]),
                });
            }
        }

        return [.. requests.OrderBy(r => r.RequestId, StringComparer.Ordinal)];
    }

    internal WorkflowDefinition WorkflowDefinitionOf(string name) =>
        _workflowDefinitions.TryGetValue(name, out WorkflowDefinition? d) ? d : throw new UnknownWorkflowException($"No durable workflow is registered as '{name}' on this worker.");

    /// <summary>Asks the conversations a workflow's executors started to stop (the workflow was cancelled or failed). Idempotent: the cancel signal has a fixed id.</summary>
    private async Task<byte[]> WorkflowCancelAsync(ActivityContext context, byte[] input, CancellationToken cancellationToken)
    {
        AfwCancelConversations cancel = AfCodec.Read(input, AfwJson.Default.AfwCancelConversations);
        int sent = 0;
        foreach (string workflowId in cancel.WorkflowIds)
        {
            byte[] one = await ChildCancelAsync(
                context, AfCodec.Write(new AfChildCancel { WorkflowId = workflowId, Reason = cancel.Reason }, AfJson.Default.AfChildCancel), cancellationToken).ConfigureAwait(false);
            if (AfCodec.Read(one, AfJson.Default.AfChildCancelResult).Sent)
            {
                sent++;
            }
        }

        return AfCodec.Write(new AfChildCancelResult { Sent = sent > 0 }, AfJson.Default.AfChildCancelResult);
    }
}
