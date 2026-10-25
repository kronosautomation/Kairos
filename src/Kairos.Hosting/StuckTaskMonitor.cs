using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Kairos;

/// <summary>
/// Makes parked workflow tasks visible (nothing stalls silently). Each time it looks, it logs every newly parked run once,
/// keeps the <c>kairos.workflow_tasks.stuck</c> gauge current, and feeds the health check. Resolve a parked run with
/// <see cref="IKairosStore.ResumeWorkflowTaskVerifiedAsync"/> (after fixing the cause) or <see cref="IKairosStore.FailRunVerifiedAsync"/>.
/// </summary>
public sealed partial class StuckTaskMonitor
{
    private readonly KairosOptions.StoreHandle _handle;
    private readonly KairosOptions _options;
    private readonly ILogger<StuckTaskMonitor> _logger;
    private readonly HashSet<Guid> _known = [];
    private readonly TimeProvider _time;
    private volatile IReadOnlyList<StuckWorkflowTask> _current = [];
    private volatile WorkBacklog? _backlog;
    private volatile bool _storeFailing;

    internal StuckTaskMonitor(KairosOptions.StoreHandle handle, KairosOptions options, ILogger<StuckTaskMonitor> logger, TimeProvider? time = null)
    {
        _handle = handle;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        KairosMetrics.Meter.CreateObservableGauge(
            "kairos.workflow_tasks.stuck", () => _current.Count, description: "Workflow tasks parked after repeated lease expiry");
        KairosMetrics.Meter.CreateObservableGauge(
            "kairos.queue.depth", ReadBacklog, "{task}", "Work that is ready to be claimed but has not been: workflow tasks, activity tasks and due timers");
    }

    private IEnumerable<Measurement<long>> ReadBacklog()
    {
        if (_backlog is not { } backlog)
        {
            return [];
        }

        return
        [
            new Measurement<long>(backlog.WorkflowTasks, new KeyValuePair<string, object?>("queue", "workflow_task")),
            new Measurement<long>(backlog.ActivityTasks, new KeyValuePair<string, object?>("queue", "activity_task")),
            new Measurement<long>(backlog.DueTimers, new KeyValuePair<string, object?>("queue", "timer")),
        ];
    }

    /// <summary>The ready work as of the last refresh, or null when the store cannot say or has not been asked yet.</summary>
    public WorkBacklog? Backlog => _backlog;

    /// <summary>True when the last refresh could not reach the store.</summary>
    public bool StoreFailing => _storeFailing;

    /// <summary>Parked workflow tasks as of the last refresh.</summary>
    public IReadOnlyList<StuckWorkflowTask> Current => _current;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StuckWorkflowTask> stuck;
        try
        {
            stuck = await _handle.Store.GetStuckWorkflowTasksAsync(_options.Scope, cancellationToken).ConfigureAwait(false);
            _backlog = await _handle.Store.GetBacklogAsync(_options.Scope, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _storeFailing = false;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _storeFailing = true;
            throw;
        }

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

/// <summary>What the worker loops of this process are doing, for the health check. Updated by the loops; read by anyone.</summary>
public sealed class KairosWorkerPulse(TimeProvider time)
{
    private long _lastIteration = time.GetTimestamp();
    private int _busy;
    private int _running;

    internal void LoopStarted() => Interlocked.Increment(ref _running);

    internal void LoopStopped() => Interlocked.Decrement(ref _running);

    internal void Iteration() => Interlocked.Exchange(ref _lastIteration, time.GetTimestamp());

    internal void WorkStarted() => Interlocked.Increment(ref _busy);

    internal void WorkFinished()
    {
        Interlocked.Decrement(ref _busy);
        Iteration();
    }

    /// <summary>Loops that have started and not stopped.</summary>
    public int RunningLoops => Volatile.Read(ref _running);

    /// <summary>Loops that are executing a task right now (a long activity keeps its loop busy, which is alive, not stalled).</summary>
    public int BusyLoops => Volatile.Read(ref _busy);

    /// <summary>Time since any loop last finished an iteration or a task.</summary>
    public TimeSpan IdleFor => time.GetElapsedTime(Interlocked.Read(ref _lastIteration));
}

/// <summary>
/// Reports the store and the workers. Unhealthy when the store cannot be reached or when this process has worker loops configured and none of them has made progress
/// for <see cref="KairosOptions.WorkerStallThreshold"/> while none is busy; degraded while any workflow task is parked. Added to the host health checks by
/// <c>AddKairos</c> under the name <c>kairos</c>.
/// </summary>
public sealed class KairosHealthCheck(StuckTaskMonitor monitor, KairosWorkerPulse pulse, KairosOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (monitor.StoreFailing)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The store cannot be reached."));
        }

        if (options.WorkerCount > 0)
        {
            if (pulse.RunningLoops == 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("No worker loop is running."));
            }

            if (pulse.BusyLoops == 0 && pulse.IdleFor > options.WorkerStallThreshold)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"No worker loop has made progress for {pulse.IdleFor.TotalSeconds:0} seconds."));
            }
        }

        IReadOnlyList<StuckWorkflowTask> stuck = monitor.Current;
        return Task.FromResult(stuck.Count == 0
            ? HealthCheckResult.Healthy("Store reachable, workers running, no parked workflow tasks.")
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
