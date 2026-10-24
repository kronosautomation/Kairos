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

    internal ActivityRegistry Activities { get; } = new();

    public TenantScope Scope { get; set; } = DefaultScope;

    /// <summary>Concurrent task loops in this process.</summary>
    public int WorkerCount { get; set; } = 2;

    /// <summary>How long an idle worker sleeps before re-checking the store when no notification arrives.</summary>
    public TimeSpan PollFallbackInterval { get; set; } = TimeSpan.FromSeconds(2);

    public WorkerOptions Worker { get; set; } = new();

    internal IPayloadProtector? PayloadProtector { get; private set; }

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
            return new StoreHandle(new PostgresKairosStore(ds), notifier.WaitAsync, async () =>
            {
                await notifier.DisposeAsync().ConfigureAwait(false);
                await ds.DisposeAsync().ConfigureAwait(false);
            });
        };
        return this;
    }

    /// <summary>Any store implementation (tests, simulators). Workers fall back to timed polling.</summary>
    public KairosOptions UseStore(IKairosStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        StoreFactory = _ => new StoreHandle(store, null, null);
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

    internal sealed record StoreHandle(
        IKairosStore Store, Func<TimeSpan, CancellationToken, Task>? WaitForWork, Func<ValueTask>? Dispose);
}
