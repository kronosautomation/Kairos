using System.Diagnostics;
using DiagContext = System.Diagnostics.ActivityContext;

namespace Kairos;

/// <summary>
/// Spans and counters that describe things which already happened in a run (a timer fired, a signal or an approval decision arrived, the run closed). They are derived from the
/// events a workflow task consumed, with their recorded timestamps, and emitted once, after the task is recorded: replay, which runs the same history again and again, never reports
/// the same event twice. Only ids, names, counts and outcomes are used.
/// </summary>
internal static class TelemetryEvents
{
    /// <summary>Sum of the payload sizes in <paramref name="history"/>. With encryption on this is the sealed size, which is what the server stores.</summary>
    public static long PayloadBytes(IReadOnlyList<HistoryEvent> history)
    {
        long total = 0;
        for (int i = 0; i < history.Count; i++)
        {
            total += history[i] switch
            {
                WorkflowStarted e => e.Input.Length,
                ActivityScheduled e => e.Input.Length,
                ActivityCompleted e => e.Result.Length,
                SignalReceived e => e.Payload.Length,
                WorkflowCompleted e => e.Result.Length,
                WorkflowContinued e => e.Input.Length,
                _ => 0,
            };
        }

        return total;
    }

    public static void Emit(WorkflowTask task, IReadOnlyList<WorkflowCommand> commands, string runTrace, DateTimeOffset now, Activity? taskSpan)
    {
        IReadOnlyList<HistoryEvent> history = task.History;
        int first = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i] is WorkflowTaskCompleted)
            {
                first = i + 1;
                break;
            }
        }

        bool listening = KairosTelemetry.Source.HasListeners();
        DiagContext parent = taskSpan is not null ? taskSpan.Context : KairosTelemetry.ParseOrDefault(runTrace);

        string workflowType = task.WorkflowType;
        for (int i = first; i < history.Count; i++)
        {
            switch (history[i])
            {
                case TimerFired fired:
                    if (listening && FindTimer(history, fired.TimerId) is { } started)
                    {
                        using Activity? timer = Begin("kairos.timer", ActivityKind.Internal, parent, started.Timestamp, link: default);
                        if (timer is not null)
                        {
                            Identify(timer, task);
                            timer.SetTag(KairosTags.TimerId, fired.TimerId);
                            timer.SetTag(KairosTags.TimerSeconds, started.Duration.TotalSeconds);
                            timer.SetEndTime(fired.Timestamp.UtcDateTime);
                        }
                    }

                    break;
                case SignalReceived signal:
                    bool approval = signal.SignalName.StartsWith(ReservedSignals.ApprovalPrefix, StringComparison.Ordinal);
                    KairosMetrics.SignalsReceived.Add(
                        1, new KeyValuePair<string, object?>("workflow.type", workflowType), new KeyValuePair<string, object?>("kind", approval ? "approval" : "signal"));
                    if (listening)
                    {
                        // The sender's span is linked, not parented: the run's trace stays one tree, and the sender's request is one click away.
                        DiagContext sender = KairosTelemetry.ParseOrDefault(signal.TraceContext);
                        using Activity? span = Begin(approval ? "kairos.approval" : "kairos.signal", ActivityKind.Consumer, parent, signal.Timestamp, sender);
                        if (span is not null)
                        {
                            Identify(span, task);
                            span.SetTag(approval ? KairosTags.ApprovalId : KairosTags.SignalName, approval ? signal.SignalName[ReservedSignals.ApprovalPrefix.Length..] : signal.SignalName);
                            span.SetTag(KairosTags.PayloadBytes, signal.Payload.Length);
                            span.SetEndTime(signal.Timestamp.UtcDateTime);
                        }
                    }

                    break;
            }
        }

        foreach (WorkflowCommand command in commands)
        {
            string? outcome = command switch
            {
                CompleteWorkflowCommand => "completed",
                FailWorkflowCommand => "failed",
                ContinueAsNewCommand => "continued",
                _ => null,
            };
            if (outcome is null)
            {
                continue;
            }

            KairosMetrics.RunsClosed.Add(
                1, new KeyValuePair<string, object?>("workflow.type", workflowType), new KeyValuePair<string, object?>(KairosTags.Outcome, outcome));
            if (listening)
            {
                // The run as a whole: from its start event to now, a child of whatever started it.
                DiagContext origin = KairosTelemetry.ParseOrDefault(runTrace);
                using Activity? run = Begin("kairos.run", ActivityKind.Internal, origin, history[0].Timestamp, link: default);
                if (run is not null)
                {
                    Identify(run, task);
                    run.SetTag(KairosTags.Outcome, outcome);
                    run.SetTag(KairosTags.HistoryEvents, history.Count);
                    if (command is FailWorkflowCommand failed)
                    {
                        KairosTelemetry.Fail(run, failed.ErrorType);
                    }

                    run.SetEndTime(now.UtcDateTime);
                }
            }
        }
    }

    private static TimerStarted? FindTimer(IReadOnlyList<HistoryEvent> history, int timerId)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i] is TimerStarted s && s.TimerId == timerId)
            {
                return s;
            }
        }

        return null;
    }

    private static Activity? Begin(string name, ActivityKind kind, DiagContext parent, DateTimeOffset start, DiagContext link)
    {
        IEnumerable<ActivityLink>? links = link == default ? null : [new ActivityLink(link)];
        return KairosTelemetry.Source.StartActivity(name, kind, parent, tags: null, links, start.UtcDateTime);
    }

    private static void Identify(Activity span, WorkflowTask task)
    {
        span.SetTag(KairosTags.TenantId, task.Run.TenantId.ToString("D"));
        span.SetTag(KairosTags.NamespaceId, task.Run.NamespaceId.ToString("D"));
        span.SetTag(KairosTags.RunId, task.Run.RunId.ToString("D"));
        span.SetTag(KairosTags.WorkflowType, task.WorkflowType);
        span.SetTag(KairosTags.BuildId, task.BuildId);
    }
}
