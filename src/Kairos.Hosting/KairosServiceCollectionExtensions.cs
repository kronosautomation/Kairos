using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(sp => options.StoreFactory(sp));
        services.AddSingleton(sp => sp.GetRequiredService<KairosOptions.StoreHandle>().Store);
        services.AddSingleton(sp => new KairosClient(sp.GetRequiredService<IKairosStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHostedService, KairosWorkerService>();
        return services;
    }
}

internal sealed partial class KairosWorkerService(
    KairosOptions.StoreHandle handle, KairosOptions options, TimeProvider time, ILogger<KairosWorkerService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new Task[options.WorkerCount];
        for (int i = 0; i < loops.Length; i++)
        {
            var worker = new WorkerRuntime(handle.Store, options.Scope, options.Workflows, options.Activities, time, options.Worker)
            {
                OnFault = fault => LogReplayFault(logger, fault.Error, fault.Run.RunId),
            };
            loops[i] = Task.Run(() => RunLoopAsync(worker, stoppingToken), stoppingToken);
        }

        return Task.WhenAll(loops);
    }

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
    private async Task RunLoopAsync(WorkerRuntime worker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool didWork = await worker.ProcessWorkflowTaskAsync(ct).ConfigureAwait(false);
                didWork |= await worker.ProcessActivityTaskAsync(ct).ConfigureAwait(false);
                didWork |= await worker.FireTimersAsync(ct).ConfigureAwait(false) > 0;
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
