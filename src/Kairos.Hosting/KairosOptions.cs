using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos;

/// <summary>Configuration for embedded mode: <c>services.AddKairos(o =&gt; o.UsePostgres(...).AddWorkflow(...))</c>.</summary>
public sealed class KairosOptions
{
    /// <summary>The single tenant/namespace used by an embedded application unless told otherwise.</summary>
    public static readonly TenantScope DefaultScope = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000001"));

    internal Func<IServiceProvider, StoreHandle>? StoreFactory { get; private set; }

    internal WorkflowRegistry Workflows { get; } = new();

    /// <summary>The workflow code registered so far, per type and build. Read by tools that replay histories against a build (<c>kairos replay-check</c>).</summary>
    public WorkflowRegistry WorkflowRegistry => Workflows;

    internal ActivityRegistry Activities { get; } = new();

    public TenantScope Scope { get; set; } = DefaultScope;

    /// <summary>Concurrent task loops in this process.</summary>
    public int WorkerCount { get; set; } = 2;

    /// <summary>How long an idle worker sleeps before re-checking the store when no notification arrives.</summary>
    public TimeSpan PollFallbackInterval { get; set; } = TimeSpan.FromSeconds(2);

    public WorkerOptions Worker { get; set; } = new();

    /// <summary>
    /// True (the default) makes <c>AddKairos</c> subscribe the host's OpenTelemetry providers to the <c>Kairos</c> activity source and meter, so runs, tasks and metrics
    /// appear in any OpenTelemetry pipeline (and the Aspire dashboard) with no further setup. Set false to subscribe yourself.
    /// </summary>
    public bool RegisterTelemetry { get; set; } = true;

    /// <summary>
    /// How long every worker loop may be idle without completing an iteration before the health check reports the workers as stalled. Loops that are running
    /// a long activity count as alive. Default 30 seconds; keep it well above <see cref="PollFallbackInterval"/>.
    /// </summary>
    public TimeSpan WorkerStallThreshold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The payload protector set with <see cref="UsePayloadProtector"/>, or null.</summary>
    public IPayloadProtector? PayloadProtector { get; private set; }

    /// <summary>
    /// Client-side payload encryption: inputs, results, signals and failure messages are sealed before they reach the database and opened
    /// only here. Use <c>Kairos.Worker.EnvelopePayloadProtector</c> over your key provider.
    /// </summary>
    public KairosOptions UsePayloadProtector(IPayloadProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        PayloadProtector = protector;
        return this;
    }

    /// <summary>Postgres only: no cluster, no sidecar. Connect as the <c>kairos_dataplane</c> role.</summary>
    public KairosOptions UsePostgres(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        StoreFactory = _ =>
        {
            NpgsqlDataSource ds = NpgsqlDataSource.Create(connectionString);
            PostgresWorkNotifier notifier = PostgresWorkNotifier.Start(ds);
            var store = new PostgresKairosStore(ds);
            store.AttachNotifier(notifier);
            return new StoreHandle(store, notifier.WaitAsync, async () =>
            {
                await notifier.DisposeAsync().ConfigureAwait(false);
                await ds.DisposeAsync().ConfigureAwait(false);
            });
        };
        return this;
    }

    /// <summary>
    /// Postgres, with the connection string read from configuration under <c>ConnectionStrings:{name}</c>. This is where .NET Aspire puts the string when the app
    /// references the database (<c>builder.AddKairos("kairos").WithReference(postgres)</c> in the AppHost).
    /// </summary>
    public KairosOptions UsePostgresFromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration, string name = "kairos")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return UsePostgres(configuration["ConnectionStrings:" + name]
            ?? throw new InvalidOperationException($"No connection string named '{name}' is configured (ConnectionStrings:{name})."));
    }

    /// <summary>Any store implementation (tests, simulators). Workers fall back to timed polling.</summary>
    public KairosOptions UseStore(IKairosStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        StoreFactory = _ => new StoreHandle(store, null, null);
        return this;
    }

    /// <summary>
    /// Any store, with a hook the worker loops wait on when there is nothing to do (it should complete when work may be available, or when the given time has passed). A store that
    /// can be told about new work, such as a remote one, supplies it; <see cref="UseStore(IKairosStore)"/> makes the loops poll on a timer instead.
    /// </summary>
    public KairosOptions UseStore(IKairosStore store, Func<TimeSpan, CancellationToken, Task> waitForWork)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(waitForWork);
        StoreFactory = _ => new StoreHandle(store, waitForWork, null);
        return this;
    }

    public KairosOptions AddWorkflow(string workflowType, WorkflowFunc workflow, string buildId = KairosClient.DefaultBuildId)
    {
        Workflows.Register(workflowType, buildId, workflow);
        return this;
    }

    public KairosOptions AddActivity(string activityType, ActivityFunc activity)
    {
        Activities.Register(activityType, activity);
        return this;
    }

    /// <summary>Registers several activities at once, for example <c>AgentsRuntime.Activities</c>.</summary>
    public KairosOptions AddActivities(IEnumerable<KeyValuePair<string, ActivityFunc>> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        foreach ((string type, ActivityFunc activity) in activities)
        {
            Activities.Register(type, activity);
        }

        return this;
    }

    internal sealed record StoreHandle(
        IKairosStore Store, Func<TimeSpan, CancellationToken, Task>? WaitForWork, Func<ValueTask>? Dispose);
}
