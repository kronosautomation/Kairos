using System.Diagnostics;
using DiagContext = System.Diagnostics.ActivityContext;

namespace Kairos;

/// <summary>
/// The spans Kairos emits (activity source <c>Kairos</c>, the same name as the meter in <see cref="KairosMetrics"/>). <c>AddKairos</c> registers both with OpenTelemetry
/// when the host uses it; hosts without it can listen with <see cref="ActivityListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a span may carry.</b> Ids, names, counts, sizes, outcomes and timings. Never a payload, never an error <i>message</i> (messages can quote payloads; only the exception type
/// is recorded), never a credential, never an argument or result of a tool or a prompt or a reply of a model. There is no switch that turns content capture on: the OpenTelemetry
/// GenAI content-capture settings do not apply to Kairos spans. A test (<c>TelemetryConfidentialityTests</c>) runs real workflows with canary payloads and fails if any of them
/// reaches a span, a log or a metric.
/// </para>
/// <para>
/// <b>The trace follows the run.</b> The span that starts a run is recorded in its history (<see cref="WorkflowStarted.TraceContext"/>), the span of the workflow task that
/// schedules an activity in the activity's event, and the span that sends a signal in the signal's event. Every worker, every retry and every restart therefore continues the same
/// trace from stored data, not from memory.
/// </para>
/// </remarks>
public static class KairosTelemetry
{
    /// <summary>The name of the activity source and of the meter.</summary>
    public const string SourceName = "Kairos";

    /// <summary>The source of every Kairos span.</summary>
    public static ActivitySource Source { get; } = new(SourceName, "1.0.0");

    private const int TraceParentLength = 55; // "00-" + 32 hex + "-" + 16 hex + "-" + 2 hex

    /// <summary>The W3C <c>traceparent</c> of the current activity, or an empty string when there is none (or it is not in W3C form).</summary>
    public static string CurrentTraceContext() => ContextOf(Activity.Current);

    /// <summary>The W3C <c>traceparent</c> of <paramref name="activity"/>, or empty.</summary>
    public static string ContextOf(Activity? activity) =>
        activity is { IdFormat: ActivityIdFormat.W3C, Id: { } id } && id.Length == TraceParentLength ? id : "";

    /// <summary>Returns <paramref name="traceparent"/> when it is a well-formed W3C trace parent, otherwise an empty string. Stored values are checked once, here.</summary>
    public static string Sanitize(string? traceparent) =>
        traceparent is { Length: TraceParentLength } && DiagContext.TryParse(traceparent, null, out _) ? traceparent : "";

    /// <summary>Parses a stored trace context. False (and a default context) when it is empty or malformed.</summary>
    public static bool TryParseContext(string? traceparent, out DiagContext context)
    {
        if (traceparent is { Length: TraceParentLength } && DiagContext.TryParse(traceparent, null, out context))
        {
            return true;
        }

        context = default;
        return false;
    }

    internal static DiagContext ParseOrDefault(string? traceparent) => TryParseContext(traceparent, out DiagContext context) ? context : default;

    /// <summary>The context of <paramref name="span"/>, or, when nobody listens and there is no span, the (sanitized) <paramref name="fallback"/> so the trace is not lost.</summary>
    internal static string OrElse(Activity? span, string fallback)
    {
        string own = ContextOf(span);
        return own.Length > 0 ? own : Sanitize(fallback);
    }

    internal static Activity? Start(string name, ActivityKind kind, string parentTraceContext, DateTimeOffset? start = null, string runId = "")
    {
        if (!Source.HasListeners())
        {
            return null;
        }

        bool linked = TryParseContext(parentTraceContext, out DiagContext parent);
        Activity? span = Source.StartActivity(
            name, kind, linked ? parent : default, tags: null, links: null, startTime: start ?? default);
        if (span is not null && runId.Length > 0)
        {
            span.SetTag(KairosTags.RunId, runId);
        }

        return span;
    }

    /// <summary>Marks a span as failed with the type of <paramref name="error"/>. For code outside Core that holds an exception.</summary>
    public static void FailFrom(Activity? span, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Fail(span, error.GetType().Name);
    }

    /// <summary>Marks a span as failed with the exception <i>type</i> only. The message is never recorded (it may quote a payload).</summary>
    internal static void Fail(Activity? span, string errorType)
    {
        if (span is null)
        {
            return;
        }

        span.SetStatus(ActivityStatusCode.Error);
        span.SetTag(KairosTags.ErrorType, errorType);
    }
}

/// <summary>Names of the attributes Kairos puts on spans. All values are ids, names, counts or outcomes.</summary>
public static class KairosTags
{
    public const string TenantId = "kairos.tenant.id";
    public const string NamespaceId = "kairos.namespace.id";
    public const string RunId = "kairos.run.id";
    public const string WorkflowType = "kairos.workflow.type";
    public const string WorkflowId = "kairos.workflow.id";
    public const string BuildId = "kairos.build.id";
    public const string Outcome = "kairos.outcome";
    public const string ActivityType = "kairos.activity.type";
    public const string ActivityId = "kairos.activity.id";
    public const string Attempt = "kairos.activity.attempt";
    public const string TimerId = "kairos.timer.id";
    public const string TimerSeconds = "kairos.timer.duration_seconds";
    public const string SignalName = "kairos.signal.name";
    public const string ApprovalId = "kairos.approval.id";
    public const string HistoryEvents = "kairos.history.events";
    public const string NewEvents = "kairos.history.new_events";
    public const string PayloadBytes = "kairos.payload.size";
    public const string ErrorType = "error.type";
}
