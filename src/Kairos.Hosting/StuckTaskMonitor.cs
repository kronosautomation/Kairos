using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Kairos;

/// <summary>
/// Makes parked workflow tasks visible (I1: nothing stalls silently). Each time it looks, it logs every newly parked run once,
/// keeps the <c>kairos.workflow_tasks.stuck</c> gauge current, and feeds the health check. Resolve a parked run with
/// <see cref="IKairosStore.ResumeWorkflowTaskAsync"/> (after fixing the cause) or <see cref="IKairosStore.FailRunAsync"/>.
/// </summary>
public sealed partial class StuckTaskMonitor
{
    private readonly KairosOptions.StoreHandle _handle;
    private readonly KairosOptions _options;
    private readonly ILogger<StuckTaskMonitor> _logger;
    private readonly HashSet<Guid> _known = [];
    private volatile IReadOnlyList<StuckWorkflowTask> _current = [];

    internal StuckTaskMonitor(KairosOptions.StoreHandle handle, KairosOptions options, ILogger<StuckTaskMonitor> logger)
    {
        _handle = handle;
        _options = options;
        _logger = logger;
        KairosMetrics.Meter.CreateObservableGauge(
            "kairos.workflow_tasks.stuck", () => _current.Count, description: "Workflow tasks parked after repeated lease expiry");
    }

    /// <summary>Parked workflow tasks as of the last refresh.</summary>
    public IReadOnlyList<StuckWorkflowTask> Current => _current;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StuckWorkflowTask> stuck = await _handle.Store.GetStuckWorkflowTasksAsync(_options.Scope, cancellationToken).ConfigureAwait(false);
        var live = new HashSet<Guid>();
        foreach (StuckWorkflowTask task in stuck)
        {
            live.Add(task.Run.RunId);
            if (_known.Add(task.Run.RunId))
            {
                LogParked(_logger, task.Run.RunId, task.WorkflowType, task.BuildId, task.Attempts);
            }
        }

        _known.IntersectWith(live);
        _current = stuck;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Run {RunId} ({WorkflowType}, build {BuildId}) is parked: its workflow task expired {Attempts} times. It will not run again until an operator resumes or fails it.")]
    private static partial void LogParked(ILogger logger, Guid runId, string workflowType, string buildId, int attempts);
}

/// <summary>Degraded while any workflow task is parked. Register with <c>AddKairosHealthCheck</c>.</summary>
public sealed class KairosHealthCheck(StuckTaskMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StuckWorkflowTask> stuck = monitor.Current;
        return Task.FromResult(stuck.Count == 0
            ? HealthCheckResult.Healthy("No parked workflow tasks.")
            : HealthCheckResult.Degraded(
                $"{stuck.Count} workflow task(s) are parked after repeated lease expiry.",
                data: new Dictionary<string, object> { ["stuckRuns"] = string.Join(",", stuck.Select(t => t.Run.RunId)) }));
    }
}

public static class KairosHealthCheckExtensions
{
    public static IHealthChecksBuilder AddKairosHealthCheck(this IHealthChecksBuilder builder, string name = "kairos")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<KairosHealthCheck>(name);
    }
}
