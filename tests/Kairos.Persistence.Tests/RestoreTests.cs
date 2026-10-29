using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Containers;
using Kairos.Cloud.SelfHosted;
using Kairos.Persistence.Postgres;
using Kairos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The restore test behind the backup page of the documentation. A database is copied with <c>pg_dump</c> while runs are in flight, the runs go on, and the older copy is restored with <c>pg_restore</c>
/// into a new database, with a new application instance on it. (Both tools run inside the PostgreSQL container, through Testcontainers' exec, with the dump kept in the container; the restore goes into
/// a second database of the same cluster, so the Kairos roles already exist, as they do after <c>kairos db provision</c> on a new cluster.) What the page promises is asserted here: runs resume from the
/// snapshot's point, an activity that had run after it runs again with the same idempotency key, a timer that came due fires, and the hash chains still verify.
/// </summary>
[Trait("Category", "Nightly")]
[Trait("Category", "Integration")]
public sealed class RestoreTests : IAsyncLifetime
{
    private const string Password = "restore-test-only";
    private const string Live = "kairos_live";
    private const string Restored = "kairos_restored";
    private const string DumpFile = "/tmp/kairos-snapshot.dump";

    private static readonly IReadOnlyDictionary<string, byte[]> KeyRing = new Dictionary<string, byte[]> { ["ring-1"] = RandomNumberGenerator.GetBytes(32) };

    private static readonly string[] ActivityNames = ["before", "charge", "tick"];

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _admin;

    public async Task InitializeAsync()
    {
        string image = Environment.GetEnvironmentVariable("KAIROS_PG_IMAGE") is { Length: > 0 } i ? i : "postgres:17";
        _container = new PostgreSqlBuilder(image).Build();
        await _container.StartAsync();
        _admin = NpgsqlDataSource.Create(_container.GetConnectionString());
        await PostgresProvisioning.CreateRolesAsync(_admin, Password, Password, Password, Password);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_admin is not null)
        {
            await _admin.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private string Connection(string database, string? role)
    {
        var b = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database };
        if (role is not null)
        {
            b.Username = role;
            b.Password = Password;
        }

        return b.ConnectionString;
    }

    private async Task CreateDatabaseAsync(string name)
    {
        await using (NpgsqlCommand create = _admin!.CreateCommand($"CREATE DATABASE {name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using NpgsqlDataSource asAdmin = NpgsqlDataSource.Create(Connection(name, null));
        await PostgresProvisioning.PrepareDatabaseAsync(asAdmin);
    }

    private async Task ExecInContainerAsync(params string[] command)
    {
        ExecResult result = await _container!.ExecAsync(command);
        Assert.True(result.ExitCode == 0, $"{string.Join(' ', command)} exited with {result.ExitCode}: {result.Stderr}");
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<(string Activity, string Key)> Ran { get; } = new();

        public IEnumerable<string> Keys(string activity) => Ran.Where(r => r.Activity == activity).Select(r => r.Key);
    }

    private static IHost Build(string connection, Recorder recorder, IPayloadProtector protector)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.UsePostgres(connection).UsePayloadProtector(protector);
            foreach (string name in ActivityNames)
            {
                o.AddActivity(name, (context, input, _) =>
                {
                    recorder.Ran.Enqueue((name, context.IdempotencyKey));
                    return Task.FromResult(input);
                });
            }

            // Waits for a person's go-ahead, then charges.
            o.AddWorkflow("order", async (ctx, input) =>
            {
                await ctx.ExecuteActivityAsync("before", input);
                await ctx.WaitForSignalAsync("go");
                await ctx.ExecuteActivityAsync("charge", input);
                return input;
            });

            // Sleeps, then ticks.
            o.AddWorkflow("sleeper", async (ctx, input) =>
            {
                await ctx.DelayAsync(TimeSpan.FromSeconds(6));
                await ctx.ExecuteActivityAsync("tick", input);
                return input;
            });
        });
        return builder.Build();
    }

    private static async Task<RunState?> WaitAsync(KairosClient client, RunKey run, Func<RunState?, bool> done, TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        RunState? state = null;
        while (DateTime.UtcNow < deadline)
        {
            state = await client.GetStateAsync(run);
            if (done(state))
            {
                break;
            }

            await Task.Delay(100);
        }

        return state;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for: {what}.");
    }

    private static async Task<RunKey> StartAsync(KairosClient client, string type)
    {
        StartRunResult started = await client.StartAsync(KairosOptions.DefaultScope, $"{type}-1", type, Encoding.UTF8.GetBytes("payload"));
        return new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, started.RunId);
    }

    [Fact]
    public async Task Restoring_an_older_snapshot_sends_runs_back_runs_the_later_activities_again_with_the_same_keys_fires_the_timer_and_keeps_the_chains_valid()
    {
        await CreateDatabaseAsync(Live);
        await using (NpgsqlDataSource migrator = NpgsqlDataSource.Create(Connection(Live, PostgresProvisioning.MigratorRole)))
        {
            await PostgresProvisioning.MigrateAsync(migrator);
        }

        var protector = new EnvelopePayloadProtector(new LocalKeyProvider(KeyRing, "ring-1"));
        var onLive = new Recorder();
        RunKey order;
        RunKey sleeper;
        long orderEventsAtSnapshot;

        // 1. Work that is in flight: the order has done its first step and waits for a go-ahead; the sleeper's timer is running.
        using (IHost live = Build(Connection(Live, PostgresProvisioning.DataPlaneRole), onLive, protector))
        {
            await live.StartAsync();
            KairosClient client = live.Services.GetRequiredService<KairosClient>();
            order = await StartAsync(client, "order");
            sleeper = await StartAsync(client, "sleeper");
            await WaitUntilAsync(async () => (await client.GetHistoryAsync(order)).OfType<ActivityCompleted>().Any(), "the order's first step");
            await WaitUntilAsync(async () => (await client.GetHistoryAsync(sleeper)).OfType<TimerStarted>().Any(), "the sleeper's timer");
            await Task.Delay(TimeSpan.FromSeconds(1)); // both are now idle
            orderEventsAtSnapshot = (await client.GetHistoryAsync(order)).Count;

            // 2. The snapshot: a custom-format dump of the whole database.
            await ExecInContainerAsync("pg_dump", "-U", "postgres", "-d", Live, "-Fc", "-f", DumpFile);

            // 3. The work goes on after the snapshot.
            Assert.True(await client.SignalAsync(order, "go", [1]));
            Assert.Equal(RunState.Completed, await WaitAsync(client, order, s => s is RunState.Completed, TimeSpan.FromSeconds(30)));
            Assert.Equal(RunState.Completed, await WaitAsync(client, sleeper, s => s is RunState.Completed, TimeSpan.FromSeconds(30)));
            await live.StopAsync();
        }

        string chargeKeyOnLive = Assert.Single(onLive.Keys("charge"));
        string tickKeyOnLive = Assert.Single(onLive.Keys("tick"));
        Assert.Single(onLive.Keys("before"));

        // 4. Restore the older snapshot into a new database, as after a disaster.
        await CreateDatabaseAsync(Restored);
        await ExecInContainerAsync("pg_restore", "-U", "postgres", "-d", Restored, "--exit-on-error", DumpFile);

        // 5. A new instance on the restored database.
        var onRestored = new Recorder();
        using (IHost restored = Build(Connection(Restored, PostgresProvisioning.DataPlaneRole), onRestored, protector))
        {
            await restored.StartAsync();
            KairosClient client = restored.Services.GetRequiredService<KairosClient>();

            // The sleeper's timer came due while the database was away, so it fires now and the tick runs again, under the key it had before.
            Assert.Equal(RunState.Completed, await WaitAsync(client, sleeper, s => s is RunState.Completed, TimeSpan.FromSeconds(30)));
            Assert.Equal(tickKeyOnLive, Assert.Single(onRestored.Keys("tick")));

            // The order is back where the snapshot had it: running, waiting for the go-ahead that was sent after the snapshot, and its first step is not run again.
            Assert.Equal(RunState.Running, await client.GetStateAsync(order));
            Assert.Equal(orderEventsAtSnapshot, (await client.GetHistoryAsync(order)).Count);
            Assert.Empty(onRestored.Keys("before"));
            Assert.Empty(onRestored.Keys("charge"));

            // Sending the go-ahead again runs the charge again, with the same idempotency key: a payment service that honours the key charges once.
            Assert.True(await client.SignalAsync(order, "go", [1]));
            Assert.Equal(RunState.Completed, await WaitAsync(client, order, s => s is RunState.Completed, TimeSpan.FromSeconds(30)));
            Assert.Equal(chargeKeyOnLive, Assert.Single(onRestored.Keys("charge")));

            // The hash chains verify end to end after the restore and the work that followed it.
            foreach (RunKey run in new[] { order, sleeper })
            {
                IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(run);
                HashChain.Verify(history);
                Assert.IsType<WorkflowCompleted>(history[^1]);
            }

            // Without the key ring the restored database is ciphertext: another key cannot open it.
            var wrongKeys = new EnvelopePayloadProtector(new LocalKeyProvider(new Dictionary<string, byte[]> { ["ring-1"] = RandomNumberGenerator.GetBytes(32) }, "ring-1"));
            var stranger = new KairosClient(restored.Services.GetRequiredService<IKairosStore>(), TimeProvider.System, payloadProtector: wrongKeys);
            await Assert.ThrowsAnyAsync<Exception>(async () => await stranger.GetOpenedHistoryAsync(order));

            await restored.StopAsync();
        }
    }
}
