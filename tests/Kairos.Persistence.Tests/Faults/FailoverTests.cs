using System.Diagnostics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Kairos.Persistence.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kairos.Persistence.Tests.Faults;

/// <summary>
/// Failover drill (docs/TEST-STRATEGY.md, Layer 5): a primary with a synchronous streaming replica, a workload running through a
/// proxy, then the primary is killed, the replica promoted and the proxy flipped to it, the way a virtual IP or DNS name moves.
/// Synchronous replication means every transaction the primary acknowledged is on the replica, so the drill demands more than
/// "the system recovers": every history read before the kill must still be there afterwards, byte for byte, as the start of
/// the new history (no lost events), and every run must finish exactly once (no duplicated events or effects).
/// </summary>
[Trait("Category", "Nightly")] // minutes of container start-up; the per-commit run keeps the faster Toxiproxy cases
public sealed class FailoverTests : IAsyncLifetime
{
    private const string ReplicationPassword = "replica-test-only";
    private const string Image = "postgres:17";
    private const int ProxyContainerPort = 8666;
    private const uint ReadableByAll = 0b100_100_100; // octal 0444, as the resource mapping expects
    private const uint ExecutableByAll = 0b111_101_101; // octal 0755

    private INetwork? _network;
    private PostgreSqlContainer? _primary;
    private IContainer? _replica;
    private IContainer? _toxiproxy;
    private ToxiproxyClient? Toxi { get; set; }

    public async Task InitializeAsync()
    {
        string image = Environment.GetEnvironmentVariable("KAIROS_PG_IMAGE") is { Length: > 0 } i ? i : Image;
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _primary = new PostgreSqlBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases("pg-primary")
            .WithEnvironment("REPLICATION_PASSWORD", ReplicationPassword)
            // Synchronous replication is switched on below, once the replica is attached: the image also starts a temporary
            // server with these arguments while it runs the init script, and a synchronous commit would wait there forever.
            .WithCommand("-c", "wal_level=replica", "-c", "max_wal_senders=10", "-c", "hot_standby=on")
            .WithResourceMapping(Script("primary-init.sh"), "/docker-entrypoint-initdb.d/10-replication.sh", ExecutableByAll)
            .Build();
        await _primary.StartAsync();

        _replica = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases("pg-replica")
            .WithCreateParameterModifier(p => p.User = "postgres")
            .WithEnvironment("PGDATA", "/var/lib/postgresql/data")
            .WithEnvironment("REPLICATION_PASSWORD", ReplicationPassword)
            .WithEntrypoint("bash", "/scripts/replica-entrypoint.sh")
            .WithResourceMapping(Script("replica-entrypoint.sh"), "/scripts/replica-entrypoint.sh", ReadableByAll)
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "localhost"))
            .Build();
        await _replica.StartAsync();

        // Attach the replica, then make it the synchronous standby: from here on a commit is acknowledged only once the replica has it.
        await using (NpgsqlDataSource probe = NpgsqlDataSource.Create(_primary.GetConnectionString()))
        {
            await WaitUntilAsync(async () =>
            {
                await using NpgsqlCommand cmd = probe.CreateCommand("SELECT count(*) FROM pg_stat_replication WHERE state = 'streaming'");
                return (long)(await cmd.ExecuteScalarAsync())! == 1;
            }, TimeSpan.FromSeconds(90), "the replica to start streaming");
            await PostgresFixture.Execute(probe, "ALTER SYSTEM SET synchronous_standby_names = '*'");
            await PostgresFixture.Execute(probe, "SELECT pg_reload_conf()");
            await WaitUntilAsync(async () =>
            {
                await using NpgsqlCommand cmd = probe.CreateCommand("SELECT count(*) FROM pg_stat_replication WHERE sync_state = 'sync'");
                return (long)(await cmd.ExecuteScalarAsync())! == 1;
            }, TimeSpan.FromSeconds(30), "the replica to become the synchronous standby");
        }

        _toxiproxy = new ContainerBuilder(FaultFixture.ToxiproxyImage)
            .WithNetwork(_network)
            .WithCommand("-host=0.0.0.0")
            .WithPortBinding(8474, true)
            .WithPortBinding(ProxyContainerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build();
        await _toxiproxy.StartAsync();
        Toxi = new ToxiproxyClient(new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(8474)}"));
        await Toxi.CreateProxyAsync(FaultFixture.Proxy, $"0.0.0.0:{ProxyContainerPort}", "pg-primary:5432");
    }

    public async Task DisposeAsync()
    {
        Toxi?.Dispose();
        foreach (IAsyncDisposable? c in new IAsyncDisposable?[] { _toxiproxy, _replica, _primary, _network })
        {
            if (c is not null)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Killing_the_primary_and_promoting_the_replica_loses_no_event_and_duplicates_none()
    {
        NpgsqlConnection.ClearAllPools();
        string direct = DirectConnection(_primary!.Hostname, _primary.GetMappedPublicPort(5432), "kairos_failover");
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(_primary.GetConnectionString());
        await PostgresProvisioning.CreateRolesAsync(
            admin, PostgresFixture.PasswordFor(PostgresProvisioning.MigratorRole), PostgresFixture.PasswordFor(PostgresProvisioning.DataPlaneRole),
            PostgresFixture.PasswordFor(PostgresProvisioning.ControlPlaneRole), PostgresFixture.PasswordFor(PostgresProvisioning.ReadOnlyRole));
        await PostgresFixture.Execute(admin, "CREATE DATABASE kairos_failover");
        await using (NpgsqlDataSource dbAdmin = NpgsqlDataSource.Create(direct))
        {
            await PostgresProvisioning.PrepareDatabaseAsync(dbAdmin);
        }

        await using (NpgsqlDataSource migrator = NpgsqlDataSource.Create(WithRole(direct, PostgresProvisioning.MigratorRole)))
        {
            await PostgresProvisioning.MigrateAsync(migrator);
        }

        string viaProxy = new NpgsqlConnectionStringBuilder(WithRole(direct, PostgresProvisioning.DataPlaneRole))
        {
            Host = _toxiproxy!.Hostname,
            Port = _toxiproxy.GetMappedPublicPort(ProxyContainerPort),
            Timeout = 5,
            CommandTimeout = 5,
            KeepAlive = 1,
            MaxPoolSize = 20,
        }.ConnectionString;

        // The verifier lists both nodes and accepts only the primary, so it follows the promotion without help.
        string bothNodes = new NpgsqlConnectionStringBuilder(WithRole(direct, PostgresProvisioning.DataPlaneRole))
        {
            Host = $"{_primary.Hostname}:{_primary.GetMappedPublicPort(5432)},{_replica!.Hostname}:{_replica.GetMappedPublicPort(5432)}",
            TargetSessionAttributes = "primary",
        }.ConnectionString;
        await using NpgsqlDataSource verifierSource = NpgsqlDataSource.Create(bothNodes);
        await using var scenario = new FaultScenario(new PostgresKairosStore(verifierSource), viaProxy);
        await scenario.StartRunsAsync(60);
        await scenario.StartHostAsync();

        // Let the workload get going, then remember what the primary had acknowledged.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var before = new Dictionary<RunKey, IReadOnlyList<HistoryEvent>>();
        foreach (RunKey run in scenario.Runs)
        {
            before[run] = await scenario.ReadHistoryAsync(run);
        }

        Assert.Contains(before.Values, h => h[^1] is not WorkflowCompleted); // the kill really lands in the middle of the work
        Assert.Contains(before.Values, h => h.Count > 1);

        // Rows become visible on the primary before the synchronous standby acknowledges them, so what the verifier just read is not
        // yet proven durable. Wait until the replica has replayed everything the primary had written when the reads were done;
        // only then does "the history I saw" mean "a history the cluster acknowledged".
        string replicaAdmin = new NpgsqlConnectionStringBuilder(_primary.GetConnectionString())
        {
            Host = _replica!.Hostname,
            Port = _replica.GetMappedPublicPort(5432),
        }.ConnectionString;
        await using NpgsqlDataSource replica = NpgsqlDataSource.Create(replicaAdmin);
        string seenUpTo;
        await using (NpgsqlCommand lsn = admin.CreateCommand("SELECT pg_current_wal_insert_lsn()::text"))
        {
            seenUpTo = (string)(await lsn.ExecuteScalarAsync())!;
        }

        await WaitUntilAsync(async () =>
        {
            await using NpgsqlCommand cmd = replica.CreateCommand("SELECT pg_last_wal_replay_lsn() >= @lsn::pg_lsn");
            cmd.Parameters.AddWithValue("lsn", seenUpTo);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }, TimeSpan.FromSeconds(30), "the replica to catch up with what the verifier read");

        // The primary dies in the middle of the work; the network flip and the promotion follow.
        await Toxi!.SetEnabledAsync(FaultFixture.Proxy, false);
        await KillAsync(_primary);
        await ExecAsync(_replica, "pg_ctl", "promote", "-D", "/var/lib/postgresql/data");
        await WaitUntilAsync(async () =>
        {
            await using NpgsqlCommand cmd = replica.CreateCommand("SELECT NOT pg_is_in_recovery()");
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }, TimeSpan.FromSeconds(60), "the replica to leave recovery");

        // The workload reaches the promoted node through the flipped proxy.
        await Toxi.SetUpstreamAsync(FaultFixture.Proxy, "pg-replica:5432");
        await Toxi.SetEnabledAsync(FaultFixture.Proxy, true);

        await scenario.WaitForAllAsync();
        await scenario.AssertTwoStepRunsAreSoundAsync();

        // No lost events: everything read before the kill is still the start of that run's history, hash for hash.
        foreach ((RunKey run, IReadOnlyList<HistoryEvent> was) in before)
        {
            IReadOnlyList<HistoryEvent> now = await scenario.ReadHistoryAsync(run);
            Assert.True(now.Count >= was.Count, $"Run {run.RunId} lost events in the failover.");
            for (int i = 0; i < was.Count; i++)
            {
                Assert.True(
                    was[i].Hash.AsSpan().SequenceEqual(now[i].Hash),
                    $"Run {run.RunId}, event {i + 1} of {was.Count} read before the kill: was {was[i].GetType().Name} at {was[i].Timestamp:O}, " +
                    $"is {now[i].GetType().Name} at {now[i].Timestamp:O}; history now has {now.Count} events.");
            }
        }
    }

    private static byte[] Script(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Faults", "scripts", name);
        return Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)); // bash needs LF
    }

    private static string DirectConnection(string host, int port, string database) =>
        new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = database, Username = "postgres", Password = "postgres", MaxPoolSize = 40 }.ConnectionString;

    private static string WithRole(string connection, string role) =>
        new NpgsqlConnectionStringBuilder(connection) { Username = role, Password = PostgresFixture.PasswordFor(role) }.ConnectionString;

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan limit, string what)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidCastException)
            {
                // not ready yet
            }

            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(500);
        }
    }

    private static async Task ExecAsync(IContainer container, params string[] command)
    {
        ExecResult result = await container.ExecAsync(command);
        Assert.True(result.ExitCode == 0, $"{string.Join(' ', command)} failed: {result.Stderr}");
    }

    /// <summary>A hard kill (SIGKILL through the Docker CLI) when available; otherwise a stop. Either way the node is gone for good.</summary>
    private static async Task KillAsync(PostgreSqlContainer container)
    {
        try
        {
            using Process? kill = Process.Start(new ProcessStartInfo("docker", $"kill {container.Id}") { RedirectStandardOutput = true, RedirectStandardError = true });
            if (kill is not null)
            {
                await kill.WaitForExitAsync();
                if (kill.ExitCode == 0)
                {
                    return;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no docker CLI on this machine: fall through to the container API
        }

        await container.StopAsync();
    }
}
