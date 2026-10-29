using System.Diagnostics.Metrics;
using Kairos.Agents;
using Kairos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Kairos.Samples.Operations;

/// <summary>Host configuration for running Kairos inside your own application. The operations pages quote each region; Operations.Tests runs them against PostgreSQL.</summary>
public static class ProductionSetup
{
    #region migrate-on-start
    // The application connects as kairos_dataplane. The migration connection is a second credential, kairos_migrator, used once at start
    // and then disposed. Instances that start together queue on a database lock, so the schema is migrated once and nobody fails.
    public static KairosOptions MigrateAtStart(KairosOptions options, string dataPlaneConnection, string migratorConnection) =>
        options
            .UsePostgres(dataPlaneConnection)
            .MigrateOnStart(migratorConnection, lockTimeout: TimeSpan.FromMinutes(2));
    #endregion

    #region client-only
    // This instance starts runs, signals them and reads them, and runs no workflow or activity code. It still registers Kairos, so
    // KairosClient, the health check and the telemetry are there; WorkerCount = 0 means no task loop is started.
    public static KairosOptions ClientOnly(KairosOptions options, string dataPlaneConnection, IPayloadProtector protector)
    {
        options.UsePostgres(dataPlaneConnection).UsePayloadProtector(protector);
        options.WorkerCount = 0;
        return options;
    }
    #endregion

    #region pool-size
    // Npgsql's pool is 100 connections per data source unless you change it. Kairos uses one connection for each task loop that is
    // working, one that stays open to listen for new work, and one for each call your own code makes at the same moment.
    // Choose a number for this instance so that all instances together stay below the server's max_connections.
    public static KairosOptions WithPoolSize(KairosOptions options, string dataPlaneConnection, int maximumConnections) =>
        options.UsePostgres(dataPlaneConnection, builder => builder.ConnectionStringBuilder.MaxPoolSize = maximumConnections);
    #endregion

    #region shutdown-time
    // On a stop signal every task loop is cancelled, and a task it holds is given back at once. The host waits at most ShutdownTimeout
    // (30 seconds unless changed) for that; give it longer than your slowest activity takes to notice its cancellation token.
    public static IServiceCollection WaitLongerForShutdown(IServiceCollection services) =>
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
    #endregion

    #region hardened
    // Encryption on, approvals verified, personal data redacted from agent messages, the application logging in as kairos_dataplane
    // (a privileged login is refused at start). Nothing here calls an AllowXxx method.
    public static IServiceCollection AddHardenedKairos(
        IServiceCollection services, string dataPlaneConnection, IKeyProvider keys, IApproverVerifier approvers, Func<string, string> redactPersonalData)
    {
        var protector = new EnvelopePayloadProtector(keys, TimeProvider.System, new EnvelopeOptions { KeyUseObserver = new KeyUseMetrics() });

        AgentsRuntime agents = new AgentsBuilder()
            .UseApproverVerifier(approvers)
            .UseRedactor(redactPersonalData)
            .Build();

        return services.AddKairos(options => options
            .UsePostgres(dataPlaneConnection)
            .UsePayloadProtector(protector)
            .AddActivities(agents.Activities));
    }
    #endregion

    #region ssl
    // A connection string that insists on TLS (Require, VerifyCA or VerifyFull) negotiates TLS 1.3 only. Prefer VerifyFull.
    public static string RequireTls(string dataPlaneConnection) =>
        new NpgsqlConnectionStringBuilder(dataPlaneConnection) { SslMode = SslMode.VerifyFull }.ConnectionString;
    #endregion
}

#region key-use-metrics
/// <summary>Counts every call the protector makes to the key provider, and which of them failed. Alert on a failure rate above zero.</summary>
public sealed class KeyUseMetrics : IKeyUseObserver
{
    public const string MeterName = "MyCompany.Kairos";

    public const string CounterName = "myapp.kairos.key_use";

    private static readonly Meter AppMeter = new(MeterName);

    private static readonly Counter<long> Calls = AppMeter.CreateCounter<long>(CounterName, "{call}", "Calls to the key provider");

    public void KeyUsed(KeyUseEvent use) =>
        Calls.Add(1, new KeyValuePair<string, object?>("kind", use.Kind.ToString()), new KeyValuePair<string, object?>("succeeded", use.Succeeded));
}
#endregion

/// <summary>The instruments and log texts the alert recommendations rest on. A test checks that each exists.</summary>
public static class AlertSignals
{
    #region signals
    public const string MeterName = "Kairos";

    // Counters: alert on their rate.
    public const string LeaseExpiries = "kairos.lease.expiries";
    public const string ActivityRetries = "kairos.activity.retries";
    public const string ActivityAttempts = "kairos.activity.attempts";
    public const string ReplayFaults = "kairos.workflow_task.faults";
    public const string StoreRetries = "kairos.store.retries";
    public const string RunsClosed = "kairos.runs.closed";

    // Gauges: alert on their value.
    public const string StuckWorkflowTasks = "kairos.workflow_tasks.stuck";
    public const string QueueDepth = "kairos.queue.depth";

    // The warning written at every start when payload encryption is switched off.
    public const string PlaintextWarning = "Payload encryption is OFF";
    #endregion
}
