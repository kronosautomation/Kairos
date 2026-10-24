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

    /// <summary>Store operations retried after a deadlock or serialization failure. A steady trickle is fine; growth is contention.</summary>
    public static Counter<long> StoreRetries { get; } =
        Meter.CreateCounter<long>("kairos.store.retries", description: "Store transactions retried after a deadlock or serialization failure");
}
