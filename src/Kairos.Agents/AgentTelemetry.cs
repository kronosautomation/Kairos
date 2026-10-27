using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;

namespace Kairos.Agents;

/// <summary>
/// Spans and metrics for model and tool steps, following the OpenTelemetry GenAI semantic conventions (Development status: attribute names track the specification).
/// They are children of the activity attempt span that runs the step, so a model call sits inside its run's trace.
/// </summary>
/// <remarks>
/// <b>No content, by construction.</b> The conventions have opt-in attributes for prompts, replies, tool arguments and tool results. Kairos never sets them and has no switch
/// that does: a span carries the operation, the model alias and the model that answered, token counts, the finish reason, message and tool counts, payload sizes, and ids. It
/// never carries an error message (only the exception type) and never the identity the agent acts for.
/// </remarks>
internal static class AgentTelemetry
{
    private static readonly Histogram<double> OperationDuration = KairosMetrics.Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration", "s", "Duration of model and tool operations");

    private static readonly Histogram<long> TokenUsage = KairosMetrics.Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage", "{token}", "Tokens used by model operations");

    public static long Now() => Stopwatch.GetTimestamp();

    public static Activity? StartChat(ActivityContext context, ChatStepRequest request)
    {
        Activity? span = KairosTelemetry.Source.StartActivity($"chat {request.Model}", ActivityKind.Client);
        if (span is not null)
        {
            span.SetTag("gen_ai.operation.name", "chat");
            span.SetTag("gen_ai.request.model", request.Model); // the alias registered with the runtime
            span.SetTag("gen_ai.conversation.id", context.Run.RunId.ToString("D"));
            if (request.Options is { } o)
            {
                span.SetTag("gen_ai.request.temperature", o.Temperature);
                span.SetTag("gen_ai.request.max_tokens", o.MaxOutputTokens);
                span.SetTag("gen_ai.request.seed", o.Seed);
            }

            span.SetTag(KairosTags.RunId, context.Run.RunId.ToString("D"));
            span.SetTag("kairos.agent.messages", request.Messages.Length);
            span.SetTag("kairos.agent.tools", request.Tools.Length);
        }

        return span;
    }

    /// <summary>The span of one model call of an Agent Framework agent (Kairos.Agents.AgentFramework). The same conventions, and the same promise: no content.</summary>
    public static Activity? StartFrameworkStep(ActivityContext context, string alias, string agent, bool stepped)
    {
        Activity? span = KairosTelemetry.Source.StartActivity($"chat {alias}", ActivityKind.Client);
        if (span is not null)
        {
            span.SetTag("gen_ai.operation.name", "chat");
            span.SetTag("gen_ai.request.model", alias);
            span.SetTag("gen_ai.agent.name", agent); // configuration, not content
            span.SetTag("gen_ai.conversation.id", context.Run.RunId.ToString("D"));
            span.SetTag(KairosTags.RunId, context.Run.RunId.ToString("D"));
            span.SetTag("kairos.agent.framework", "agent-framework");
            span.SetTag("kairos.agent.stepped", stepped);
        }

        return span;
    }

    public static void FrameworkResponse(Activity? span, string alias, string? modelId, string? finish, long? inputTokens, long? outputTokens, int messages)
    {
        if (span is not null)
        {
            span.SetTag("gen_ai.response.model", modelId);
            if (finish is not null)
            {
                span.SetTag("gen_ai.response.finish_reasons", new[] { finish });
            }

            span.SetTag("gen_ai.usage.input_tokens", inputTokens);
            span.SetTag("gen_ai.usage.output_tokens", outputTokens);
            span.SetTag("kairos.agent.reply_messages", messages);
        }

        Record(alias, "input", inputTokens);
        Record(alias, "output", outputTokens);
    }

    public static void Provider(Activity? span, IChatClient client)
    {
        if (span is not null && client.GetService<ChatClientMetadata>()?.ProviderName is { Length: > 0 } provider)
        {
            span.SetTag("gen_ai.provider.name", provider); // never the endpoint address
        }
    }

    public static void Response(Activity? span, string alias, ChatResponse response)
    {
        string? finish = response.FinishReason?.Value;
        if (span is not null)
        {
            span.SetTag("gen_ai.response.model", response.ModelId);
            span.SetTag("gen_ai.response.id", response.ResponseId);
            if (finish is not null)
            {
                span.SetTag("gen_ai.response.finish_reasons", new[] { finish });
            }

            span.SetTag("gen_ai.usage.input_tokens", response.Usage?.InputTokenCount);
            span.SetTag("gen_ai.usage.output_tokens", response.Usage?.OutputTokenCount);
            span.SetTag("kairos.agent.reply_messages", response.Messages.Count);
        }

        if (response.Usage is { } usage)
        {
            Record(alias, "input", usage.InputTokenCount);
            Record(alias, "output", usage.OutputTokenCount);
        }
    }

    private static void Record(string model, string type, long? count)
    {
        if (count is { } n)
        {
            TokenUsage.Record(
                n, new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"), new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("gen_ai.token.type", type));
        }
    }

    public static Activity? StartTool(ActivityContext context, ToolStepRequest request)
    {
        Activity? span = KairosTelemetry.Source.StartActivity($"execute_tool {request.Name}", ActivityKind.Internal);
        if (span is not null)
        {
            span.SetTag("gen_ai.operation.name", "execute_tool");
            span.SetTag("gen_ai.tool.name", request.Name);
            span.SetTag("gen_ai.tool.call.id", request.CallId);
            span.SetTag("gen_ai.tool.type", "function");
            span.SetTag("gen_ai.conversation.id", context.Run.RunId.ToString("D"));
            span.SetTag(KairosTags.RunId, context.Run.RunId.ToString("D"));
            span.SetTag("kairos.agent.tool.argument_bytes", request.ArgumentsJson.Length);
            span.SetTag("kairos.agent.tool.approved", request.Approval is not null);
        }

        return span;
    }

    /// <summary>What the tool step decided, from its recorded reply: completed, denied or approval_required; and whether the tool reported an error. Never the content.</summary>
    public static void ToolOutcome(Activity? span, byte[] reply)
    {
        if (span is null)
        {
            return;
        }

        ToolStepResponse response = WireCodec.Read(reply, AgentJson.Default.ToolStepResponse);
        span.SetTag("kairos.agent.tool.outcome", response.Outcome);
        span.SetTag("kairos.agent.tool.is_error", response.IsError);
        span.SetTag(KairosTags.PayloadBytes, reply.Length);
    }

    public static void Finish(string operation, string model, Activity? span, long startedAt, Exception? error)
    {
        if (error is not null)
        {
            KairosTelemetry.FailFrom(span, error); // the exception type only
        }

        OperationDuration.Record(
            Stopwatch.GetElapsedTime(startedAt).TotalSeconds, new KeyValuePair<string, object?>("gen_ai.operation.name", operation),
            new KeyValuePair<string, object?>(operation == "chat" ? "gen_ai.request.model" : "gen_ai.tool.name", model),
            new KeyValuePair<string, object?>("error.type", error?.GetType().Name));
    }
}
