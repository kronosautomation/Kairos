using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

public sealed partial class AgentFrameworkRuntime
{
    /// <summary>What one run of the agent produced, before it is shaped into a recorded result.</summary>
    private sealed record AgentRun(AgentResponse Response, JsonElement Snapshot, UsageRecord? Usage, string? ModelId);

    /// <summary>
    /// Runs the real agent once, in an activity: Agent Framework code (the chat client, middleware, context providers, memory, compaction) executes only here, never inside the replayed workflow, so replay can neither
    /// re-run it nor be disturbed by how it schedules its own continuations. When <paramref name="stepped"/> the agent's tools are handed to it as declarations only, so the run is exactly one model call and ends
    /// with the calls the model asked for; the workflow runs them as Kairos tool steps.
    /// </summary>
    private async Task<byte[]> StepAsync(ActivityContext context, byte[] input, bool stepped, CancellationToken cancellationToken)
    {
        AfStepRequest request = AfCodec.Read(input, AfJson.Default.AfStepRequest);
        AgentDefinition definition = Definition(request.Agent);
        string alias = definition.Model ?? definition.Name;
        using System.Diagnostics.Activity? span = AgentTelemetry.StartFrameworkStep(context, alias, definition.Name, stepped);
        long started = AgentTelemetry.Now();
        try
        {
            byte[] reply = await StepCoreAsync(context, request, definition, stepped, span, cancellationToken).ConfigureAwait(false);
            AgentTelemetry.Finish("chat", alias, span, started, error: null);
            return reply;
        }
        catch (Exception ex)
        {
            AgentTelemetry.Finish("chat", alias, span, started, ex);
            throw;
        }
    }

    private async Task<byte[]> StepCoreAsync(
        ActivityContext context, AfStepRequest request, AgentDefinition definition, bool stepped, System.Diagnostics.Activity? span, CancellationToken cancellationToken)
    {
        if (definition.Stepped != stepped)
        {
            throw new InvalidOperationException($"Agent '{definition.Name}' is registered as {(definition.Stepped ? "stepped" : "turn level")} and was asked to run the other way. Was it re-registered while a run was in flight?");
        }

        if (request.Hash is { } pinned && !string.Equals(pinned, definition.Hash, StringComparison.Ordinal))
        {
            // Not a failure: a recorded outcome the workflow turns into an approval, so a person decides whether the run may go on with the new definition.
            return AfCodec.Write(
                new AfStepResult { Outcome = AfOutcomes.DefinitionChanged, TurnId = request.TurnId, PinnedHash = pinned, Hash = definition.Hash }, AfJson.Default.AfStepResult);
        }

        int limit = Options.MaxSnapshotBytes;
        if (request.Snapshot is { } incoming && incoming.GetRawText().Length > limit)
        {
            throw new SnapshotTooLargeException(
                $"The session of agent '{definition.Name}' is larger than the {limit} bytes allowed (MaxSnapshotBytes). Compact its history (Agent Framework's compaction strategies run inside the step) or raise the limit.");
        }

        List<ChatMessage> messages = Messages(request);
        GuardedCall<AgentRun> guarded = await Agents.GuardedModelCallAsync(
            context, definition.Model ?? definition.Name, definition.MaxOutputTokens, request.BudgetAllowance,
            token => RunAgentAsync(definition, request, messages, context, stepped, token), run => run.Usage, cancellationToken).ConfigureAwait(false);
        if (!guarded.Allowed)
        {
            return AfCodec.Write(
                new AfStepResult
                {
                    Outcome = AfOutcomes.BudgetExceeded,
                    TurnId = request.TurnId,
                    Hash = definition.Hash,
                    BudgetLimit = guarded.Limit,
                    OnExceed = guarded.OnExceed,
                    BudgetApproval = guarded.Approval is null ? null : AgentsRuntime.ToWire(guarded.Approval),
                },
                AfJson.Default.AfStepResult);
        }

        AgentRun run = guarded.Value!;
        JsonElement responseMessages = JsonSerializer.SerializeToElement(run.Response.Messages.ToList(), AIJsonUtilities.DefaultOptions);
        JsonElement snapshot = run.Snapshot;
        if (Redactor is { } redact)
        {
            responseMessages = SnapshotRedactor.RedactMessages(responseMessages, redact);
            snapshot = SnapshotRedactor.RedactSnapshot(snapshot, redact, definition.SnapshotState);
        }

        if (snapshot.GetRawText().Length > limit)
        {
            throw new SnapshotTooLargeException(
                $"The session of agent '{definition.Name}' grew past the {limit} bytes allowed (MaxSnapshotBytes). Compact its history or raise the limit.");
        }

        var toolCalls = new List<AfToolCall>();
        var approvals = new List<AfApprovalRequest>();
        Pending(responseMessages, stepped, toolCalls, approvals);
        UsageDetails? usage = run.Response.Usage;
        string? finish = (run.Response.FinishReason ?? (run.Response.RawRepresentation as ChatResponse)?.FinishReason)?.Value;
        AgentTelemetry.FrameworkResponse(span, definition.Model ?? definition.Name, run.ModelId, finish, usage?.InputTokenCount, usage?.OutputTokenCount, run.Response.Messages.Count);
        return AfCodec.Write(
            new AfStepResult
            {
                TurnId = request.TurnId,
                Hash = definition.Hash,
                Snapshot = snapshot,
                Messages = responseMessages,
                ToolCalls = [.. toolCalls],
                ApprovalRequests = [.. approvals],
                Final = toolCalls.Count == 0 && approvals.Count == 0,
                InputTokens = usage?.InputTokenCount,
                OutputTokens = usage?.OutputTokenCount,
                TotalTokens = usage?.TotalTokenCount,
                ModelId = run.ModelId,
                FinishReason = finish,
            },
            AfJson.Default.AfStepResult);
    }

    private static List<ChatMessage> Messages(AfStepRequest request)
    {
        var messages = new List<ChatMessage>();
        if (request.Messages is { } given)
        {
            messages.AddRange(JsonSerializer.Deserialize<List<ChatMessage>>(given, AIJsonUtilities.DefaultOptions) ?? []);
        }

        if (request.ToolResults.Length > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, [.. request.ToolResults.Select(r => (AIContent)new FunctionResultContent(r.CallId, r.Content))]));
        }

        foreach (AfApprovalResponse response in request.ApprovalResponses)
        {
            AIContent? content = JsonSerializer.Deserialize<AIContent>(response.Request, AIJsonUtilities.DefaultOptions);
            if (content is not ToolApprovalRequestContent asked)
            {
                throw new FormatException("A recorded approval request is not an approval request.");
            }

            messages.Add(new ChatMessage(ChatRole.User, [asked.CreateResponse(response.Approved, response.Reason)]));
        }

        return messages;
    }

    private static async Task<AgentRun> RunAgentAsync(
        AgentDefinition definition, AfStepRequest request, List<ChatMessage> messages, ActivityContext context, bool stepped, CancellationToken cancellationToken)
    {
        AIAgent agent = definition.Agent;
        AgentSession session = request.Snapshot is { } snapshot
            ? await agent.DeserializeSessionAsync(snapshot, null, cancellationToken).ConfigureAwait(false)
            : await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        string key = context.IdempotencyKey;
        var options = new ChatClientAgentRunOptions { ChatClientFactory = inner => new StepChatClient(inner, key, declarationsOnly: stepped) };
        AgentResponse response = await agent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        JsonElement after = await agent.SerializeSessionAsync(session, null, cancellationToken).ConfigureAwait(false);

        string? modelId = (response.RawRepresentation as ChatResponse)?.ModelId;
        UsageRecord? usage = response.Usage is { } u
            ? new UsageRecord(u.InputTokenCount ?? 0, u.OutputTokenCount ?? 0, Math.Min(u.CachedInputTokenCount ?? 0, u.InputTokenCount ?? 0), modelId ?? definition.Model ?? definition.Name)
            : null;
        return new AgentRun(response, after.Clone(), usage, modelId);
    }

    /// <summary>Reads what is left for the workflow to do out of the messages the agent produced: the tool calls it asked for (stepped agents) and the approvals it asks of a person.</summary>
    private static void Pending(JsonElement messages, bool stepped, List<AfToolCall> toolCalls, List<AfApprovalRequest> approvals)
    {
        foreach (JsonElement message in messages.EnumerateArray())
        {
            foreach (JsonElement content in message.GetProperty("contents").EnumerateArray())
            {
                switch (content.GetProperty("$type").GetString())
                {
                    case "functionCall" when stepped && !(content.TryGetProperty("informationalOnly", out JsonElement info) && info.ValueKind == JsonValueKind.True):
                        toolCalls.Add(new AfToolCall
                        {
                            CallId = content.TryGetProperty("callId", out JsonElement id) ? id.GetString() ?? "" : "",
                            Name = content.GetProperty("name").GetString() ?? "",
                            ArgumentsJson = content.TryGetProperty("arguments", out JsonElement args) ? args.GetRawText() : "{}",
                        });
                        break;
                    case "toolApprovalRequest":
                        JsonElement call = content.GetProperty("toolCall");
                        approvals.Add(new AfApprovalRequest
                        {
                            Id = content.GetProperty("requestId").GetString() ?? "",
                            Tool = call.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : call.GetProperty("$type").GetString() ?? "",
                            ArgumentsJson = call.TryGetProperty("arguments", out JsonElement callArgs) ? callArgs.GetRawText() : "{}",
                            Request = content.Clone(),
                        });
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Sits outside the agent's function-invoking client for one run: turns the tools the agent offers into declarations (so the agent stops at the first call the model makes, instead of running it) and stamps the model request with the step's idempotency key.
    /// </summary>
    private sealed class StepChatClient(IChatClient inner, string idempotencyKey, bool declarationsOnly) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, Shape(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, Shape(options), cancellationToken);

        private ChatOptions Shape(ChatOptions? options)
        {
            ChatOptions shaped = options?.Clone() ?? new ChatOptions();
            if (declarationsOnly && shaped.Tools is { Count: > 0 } tools)
            {
                // A plain declaration, not function.AsDeclarationOnly(): that keeps a link to the original function, and the function-invoking client finds an approval requirement through the link and asks for approval itself.
                shaped.Tools = [.. tools.Select(t => t is AIFunction function ? AIFunctionFactory.CreateDeclaration(function.Name, function.Description, function.JsonSchema, function.ReturnJsonSchema) : t)];
            }

            shaped.AdditionalProperties ??= [];
            shaped.AdditionalProperties[AgentsRuntime.IdempotencyKeyProperty] = idempotencyKey;
            return shaped;
        }
    }
}
