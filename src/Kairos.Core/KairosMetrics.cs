using System.Diagnostics.Metrics;

namespace Kairos;

/// <summary>OpenTelemetry-compatible instruments (meter name <c>Kairos</c>). Core only creates them; hosts export them.</summary>
public static class KairosMetrics
{
    public const string MeterName = "Kairos";

    public static Meter Meter { get; } = new(MeterName);

    /// <summary>Workflow tasks that failed replay (non-determinism, integrity) and were released for retry. A burst means a bad deploy.</summary>
    public static Counter<long> WorkflowTaskFaults { get; } =
        Meter.CreateCounter<long>("kairos.workflow_task.faults", description: "Workflow tasks released after a replay failure");

    /// <summary>Runs started by this process's clients.</summary>
    public static Counter<long> RunsStarted { get; } =
        Meter.CreateCounter<long>("kairos.runs.started", "{run}", "Runs started");

    /// <summary>Runs that reached a closing event in a task this process completed, by outcome (completed, failed, continued).</summary>
    public static Counter<long> RunsClosed { get; } =
        Meter.CreateCounter<long>("kairos.runs.closed", "{run}", "Runs that closed");

    /// <summary>Workflow tasks claimed by this process, by outcome (completed, conflict, stale, faulted).</summary>
    public static Counter<long> WorkflowTasks { get; } =
        Meter.CreateCounter<long>("kairos.workflow_tasks.processed", "{task}", "Workflow tasks processed");

    /// <summary>Time to replay a history and run the workflow code to its next blocking point, in seconds.</summary>
    public static Histogram<double> ReplayDuration { get; } =
        Meter.CreateHistogram<double>("kairos.replay.duration", "s", "Time to replay a run's history on a workflow task");

    /// <summary>Events in the history of a run when a workflow task replays it. Growth toward the continue-as-new threshold is visible here.</summary>
    public static Histogram<long> HistoryEvents { get; } =
        Meter.CreateHistogram<long>("kairos.history.events", "{event}", "Events in the history replayed by a workflow task");

    /// <summary>Payload bytes in the history of a run when a workflow task replays it (the sealed size when encryption is on).</summary>
    public static Histogram<long> HistoryBytes { get; } =
        Meter.CreateHistogram<long>("kairos.history.size", "By", "Payload bytes in the history replayed by a workflow task");

    /// <summary>Activity attempts finished by this process, by outcome (completed, failed, retry, lease_lost, cancelled).</summary>
    public static Counter<long> ActivityAttempts { get; } =
        Meter.CreateCounter<long>("kairos.activity.attempts", "{attempt}", "Activity attempts");

    /// <summary>Attempts that failed and were scheduled to run again.</summary>
    public static Counter<long> ActivityRetries { get; } =
        Meter.CreateCounter<long>("kairos.activity.retries", "{retry}", "Activity attempts that failed and will be retried");

    /// <summary>Wall time of an activity attempt in seconds.</summary>
    public static Histogram<double> ActivityDuration { get; } =
        Meter.CreateHistogram<double>("kairos.activity.duration", "s", "Duration of activity attempts");

    /// <summary>Task leases that expired and were handled (a worker died, stalled or lost the network). A burst means workers are unhealthy.</summary>
    public static Counter<long> LeaseExpiries { get; } =
        Meter.CreateCounter<long>("kairos.lease.expiries", "{lease}", "Expired task leases handled");

    public static Counter<long> TimersFired { get; } =
        Meter.CreateCounter<long>("kairos.timers.fired", "{timer}", "Timers fired");

    public static Counter<long> SignalsReceived { get; } =
        Meter.CreateCounter<long>("kairos.signals.received", "{signal}", "Signals delivered to runs");

    /// <summary>Store operations retried after a deadlock or serialization failure. A steady trickle is fine; growth is contention.</summary>
    public static Counter<long> StoreRetries { get; } =
        Meter.CreateCounter<long>("kairos.store.retries", description: "Store transactions retried after a deadlock or serialization failure");
}
