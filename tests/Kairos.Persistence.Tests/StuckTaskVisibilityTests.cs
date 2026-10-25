using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Persistence.Tests;

/// <summary>
/// F1: a parked run must not be a silent stall. The host logs it once, exports a gauge, reports Degraded health, and the operator
/// can resume or fail it. A bad deploy (replay failures that retry forever) is visible through a log and a counter as well.
/// </summary>
public sealed class StuckTaskVisibilityTests
{
    private static readonly TenantScope Scope = KairosOptions.DefaultScope;

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }

    private static IHost BuildHost(InMemoryKairosStore store, CapturingLogger logs, Action<KairosOptions> configure)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddKairos(o =>
        {
            o.UseStore(store);
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(50);
            configure(o);
        });
        return builder.Build();
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        T value = await probe();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            value = await probe();
        }

        Assert.True(done(value), "Condition not reached within 15 s.");
        return value;
    }

    [Fact]
    public async Task A_parked_run_is_logged_metered_reported_degraded_and_recoverable()
    {
        var store = new InMemoryKairosStore();
        var logs = new CapturingLogger();

        // Park a run before the host starts: its workflow task expires repeatedly (a worker that dies on every attempt).
        DateTimeOffset t = DateTimeOffset.UtcNow;
        var client = new KairosClient(store, TimeProvider.System);
        StartRunResult start = await client.StartAsync(Scope, "poison", "wf", []);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        for (int i = 0; i < StoreRules.MaxWorkflowTaskAttempts + 2; i++)
        {
            await store.PollWorkflowTaskAsync(Scope, [KairosClient.DefaultBuildId], t, TimeSpan.FromSeconds(30), CancellationToken.None);
            t += TimeSpan.FromSeconds(31);
            await store.ReapExpiredLeasesAsync(Scope, t, CancellationToken.None);
            t += TimeSpan.FromSeconds(70);
        }

        using IHost host = BuildHost(store, logs, o => o.AddWorkflow("wf", (_, _) => Task.FromResult(new byte[] { 1 })));
        double gauge = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KairosMetrics.MeterName && instrument.Name == "kairos.workflow_tasks.stuck")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => gauge = value);
        listener.Start();
        await host.StartAsync();

        var monitor = host.Services.GetRequiredService<StuckTaskMonitor>();
        var health = host.Services.GetRequiredService<KairosHealthCheck>();

        await EventuallyAsync(() => Task.FromResult(monitor.Current.Count), c => c == 1);
        listener.RecordObservableInstruments();
        Assert.Equal(1, gauge);
        Assert.Equal(HealthStatus.Degraded, (await health.CheckHealthAsync(new HealthCheckContext())).Status);

        await Task.Delay(300); // several more monitor passes: still exactly one warning for this run
        (LogLevel Level, string Message)[] parked = [.. logs.Entries.Where(e => e.Message.Contains("is parked", StringComparison.Ordinal))];
        Assert.Single(parked);
        Assert.Equal(LogLevel.Warning, parked[0].Level);
        Assert.Contains(run.RunId.ToString(), parked[0].Message, StringComparison.Ordinal);
        Assert.Equal(RunState.Running, await client.GetStateAsync(run)); // parked, not dropped

        // The operator fixes the cause and resumes it; the run completes and the alarm clears.
        Assert.True(await store.ResumeWorkflowTaskAsync(run, "ops@example.com", DateTimeOffset.UtcNow, CancellationToken.None));
        await EventuallyAsync(async () => await client.GetStateAsync(run), s => s == RunState.Completed);
        await EventuallyAsync(() => Task.FromResult(monitor.Current.Count), c => c == 0);
        listener.RecordObservableInstruments();
        Assert.Equal(0, gauge);
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new HealthCheckContext())).Status);

        await host.StopAsync();
    }

    [Fact]
    public async Task A_parked_run_that_is_failed_closes_and_the_alarm_clears()
    {
        var store = new InMemoryKairosStore();
        var logs = new CapturingLogger();
        DateTimeOffset t = DateTimeOffset.UtcNow;
        var client = new KairosClient(store, TimeProvider.System);
        StartRunResult start = await client.StartAsync(Scope, "poison-2", "wf", []);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        for (int i = 0; i < StoreRules.MaxWorkflowTaskAttempts + 2; i++)
        {
            await store.PollWorkflowTaskAsync(Scope, [KairosClient.DefaultBuildId], t, TimeSpan.FromSeconds(30), CancellationToken.None);
            t += TimeSpan.FromSeconds(31);
            await store.ReapExpiredLeasesAsync(Scope, t, CancellationToken.None);
            t += TimeSpan.FromSeconds(70);
        }

        using IHost host = BuildHost(store, logs, o => o.AddWorkflow("wf", (_, _) => Task.FromResult(new byte[] { 1 })));
        await host.StartAsync();
        var monitor = host.Services.GetRequiredService<StuckTaskMonitor>();
        await EventuallyAsync(() => Task.FromResult(monitor.Current.Count), c => c == 1);

        Assert.True(await store.FailRunAsync(run, "abandoned after the incident", "ops@example.com", DateTimeOffset.UtcNow, CancellationToken.None));

        await EventuallyAsync(() => Task.FromResult(monitor.Current.Count), c => c == 0);
        Assert.Equal(RunState.Failed, await client.GetStateAsync(run));
        StoreStats stats = store.GetStats();
        Assert.Equal((0, 0, 0, 0, 0), (stats.OpenRuns, stats.WorkflowTasks, stats.ActivityTasks, stats.Timers, stats.StuckWorkflowTasks));
        await host.StopAsync();
    }

    [Fact]
    public async Task A_bad_deploy_shows_up_as_logged_errors_and_a_fault_counter_while_the_run_is_kept()
    {
        var store = new InMemoryKairosStore();
        var logs = new CapturingLogger();
        var client = new KairosClient(store, TimeProvider.System);

        // History written by the previous build: it scheduled activity "charge" and the result is already in.
        WorkflowRegistry oldBuild = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, input) => await ctx.ExecuteActivityAsync("charge", input));
        ActivityRegistry activities = new ActivityRegistry().Register("charge", (_, i, _) => Task.FromResult(i));
        StartRunResult start = await client.StartAsync(Scope, "deploy", "wf", [1]);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        var oldWorker = new WorkerRuntime(store, Scope, oldBuild, activities, TimeProvider.System);
        await oldWorker.ProcessWorkflowTaskAsync(CancellationToken.None);
        await oldWorker.ProcessActivityTaskAsync(CancellationToken.None); // the workflow task is now pending, awaiting the new build

        long faults = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KairosMetrics.MeterName && instrument.Name == "kairos.workflow_task.faults")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref faults, value));
        listener.Start();

        // The new build changed the activity name without a patch: replay diverges.
        using IHost host = BuildHost(store, logs, o => o.AddWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("charge-v2", input)));
        await host.StartAsync();

        await EventuallyAsync(() => Task.FromResult(Interlocked.Read(ref faults)), f => f >= 1);
        // The counter is bumped just before the worker raises the fault that the host logs, so give the log line a moment.
        await EventuallyAsync(
            () => Task.FromResult(logs.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains(run.RunId.ToString(), StringComparison.Ordinal))),
            logged => logged);
        Assert.Equal(RunState.Running, await client.GetStateAsync(run)); // kept, retrying until a fixed build is deployed
        Assert.Equal(0, store.GetStats().StuckWorkflowTasks); // replay failures retry; they never park
        await host.StopAsync();
    }
}
