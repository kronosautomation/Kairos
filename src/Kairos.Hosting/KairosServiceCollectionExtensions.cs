using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Kairos;

public static class KairosServiceCollectionExtensions
{
    /// <summary>Adds the engine, a <see cref="KairosClient"/> and in-process workers to the host.</summary>
    public static IServiceCollection AddKairos(this IServiceCollection services, Action<KairosOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KairosOptions();
        configure(options);
        if (options.StoreFactory is null)
        {
            throw new InvalidOperationException("Call UsePostgres(...) or UseStore(...) when adding Kairos.");
        }

        options.RequirePayloadProtectionDecision();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(sp =>
        {
            KairosOptions.StoreHandle created = options.StoreFactory(sp);
            if (created.Store is IPayloadPostureReporting reporting)
            {
                reporting.ReportPayloadProtection(options.PayloadProtector is not null); // a store that talks to a server tells it whether payloads are encrypted here
                if (options.PostureSources.Any(s => s.ApproversUnverified))
                {
                    reporting.ReportUnverifiedApprovers();
                }
            }

            return created;
        });
        services.AddSingleton(sp => sp.GetRequiredService<KairosOptions.StoreHandle>().Store);
        services.AddSingleton(sp => new KairosClient(sp.GetRequiredService<IKairosStore>(), sp.GetRequiredService<TimeProvider>(), payloadProtector: options.PayloadProtector, blobOffload: options.BlobOffload));
        services.AddSingleton(sp => new StuckTaskMonitor(
            sp.GetRequiredService<KairosOptions.StoreHandle>(), options, sp.GetRequiredService<ILogger<StuckTaskMonitor>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new KairosWorkerPulse(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<KairosHealthCheck>();

        // The store and the workers are reported by the host's health checks without any setup.
        services.AddHealthChecks().AddKairosHealthCheck();

        if (options.RegisterTelemetry)
        {
            // Subscribes whichever OpenTelemetry SDK the host uses (Aspire's service defaults included) to Kairos spans and metrics. Without an SDK it does nothing.
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(KairosTelemetry.SourceName));
            services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(KairosMetrics.MeterName));
        }

        if (options.Migration is not null)
        {
            // Registered before the worker service: hosted services start in registration order, so the schema is current before the first loop polls.
            services.AddSingleton<IHostedService, KairosMigrationService>();
        }

        services.AddSingleton<IHostedService, KairosWorkerService>();
        return services;
    }
}

internal sealed partial class KairosWorkerService(
    KairosOptions.StoreHandle handle, KairosOptions options, TimeProvider time, StuckTaskMonitor monitor, KairosWorkerPulse pulse, ILogger<KairosWorkerService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.PayloadProtector is null)
        {
            LogUnencrypted(logger);
        }

        foreach (string warning in options.PostureSources.SelectMany(s => s.StartupWarnings))
        {
            LogPosture(logger, warning);
        }

        var loops = new Task[options.WorkerCount];
        for (int i = 0; i < loops.Length; i++)
        {
            var worker = new WorkerRuntime(handle.Store, options.Scope, options.Workflows, options.Activities, time, options.Worker, options.PayloadProtector, options.BlobOffload)
            {
                OnFault = fault => LogReplayFault(logger, fault.Error, fault.Run.RunId),
            };
            StuckTaskMonitor? watcher = i == 0 ? monitor : null; // one loop watches for parked work
            loops[i] = Task.Run(() => RunLoopAsync(worker, watcher, stoppingToken), stoppingToken);
        }

        return Task.WhenAll(loops);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payload encryption is OFF (AllowUnencryptedPayloads): workflow inputs, results and signals are stored readable in the database")]
    private static partial void LogUnencrypted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning}")]
    private static partial void LogPosture(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Workflow task for run {RunId} failed replay and was released")]
    private static partial void LogReplayFault(ILogger logger, Exception error, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kairos worker loop error; retrying")]
    private static partial void LogLoopError(ILogger logger, Exception error);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (handle.Dispose is not null)
        {
            await handle.Dispose().ConfigureAwait(false);
        }
    }

#pragma warning disable CA1031 // A worker loop must survive any store/activity error; it is logged and retried.
    private async Task RunLoopAsync(WorkerRuntime worker, StuckTaskMonitor? watcher, CancellationToken ct)
    {
        pulse.LoopStarted();
        try
        {
            await LoopAsync(worker, watcher, ct).ConfigureAwait(false);
        }
        finally
        {
            pulse.LoopStopped();
        }
    }

    private async Task LoopAsync(WorkerRuntime worker, StuckTaskMonitor? watcher, CancellationToken ct)
    {
        long lastWatch = long.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (watcher is not null && (lastWatch == long.MinValue || time.GetElapsedTime(lastWatch) >= TimeSpan.FromSeconds(1)))
                {
                    lastWatch = time.GetTimestamp();
                    await watcher.RefreshAsync(ct).ConfigureAwait(false);
                }

                pulse.WorkStarted();
                bool didWork;
                try
                {
                    didWork = await worker.ProcessWorkflowTaskAsync(ct).ConfigureAwait(false);
                    didWork |= await worker.ProcessActivityTaskAsync(ct).ConfigureAwait(false);
                    didWork |= await worker.FireTimersAsync(ct).ConfigureAwait(false) > 0;
                }
                finally
                {
                    pulse.WorkFinished();
                }

                if (didWork)
                {
                    continue;
                }

                if (handle.WaitForWork is not null)
                {
                    await handle.WaitForWork(options.PollFallbackInterval, ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(options.PollFallbackInterval, time, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLoopError(logger, ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
#pragma warning restore CA1031
}
