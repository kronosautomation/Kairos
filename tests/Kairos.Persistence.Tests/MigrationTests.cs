using Kairos.Cli;
using Kairos.Persistence.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// Migrations are serialized by a database lock and applied as one transaction: instances that start together (a rolling deploy, replicas with <c>MigrateOnStart</c>, a pipeline step
/// run twice) end with one schema and no failure, a stuck migration is reported instead of waited out, and the migration credential is never the application's.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class MigrationTests(PostgresFixture fixture)
{
    private static int MigrationCount => typeof(PostgresProvisioning).Assembly.GetManifestResourceNames().Count(n => n.EndsWith(".sql", StringComparison.Ordinal));

    private static readonly string[] VariableNames = ["ADMIN", "MIG", "DP", "CP", "RO", "CONN"];

    private static readonly string?[] NotTheMigrator = [PostgresProvisioning.DataPlaneRole, null];

    private static async Task<long> ScalarAsync(string connection, string sql)
    {
        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(connection);
        await using NpgsqlCommand cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Many_runs_started_at_once_on_an_empty_database_apply_each_migration_exactly_once()
    {
        string database = await fixture.NewEmptyDatabaseAsync();
        try
        {
            string migratorConnection = fixture.ConnectionStringFor(database, PostgresProvisioning.MigratorRole);
            await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(migratorConnection);

            using var start = new ManualResetEventSlim(false);
            Task<IReadOnlyList<string>>[] runs = [.. Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return PostgresProvisioning.MigrateAsync(migrator);
            }))];
            start.Set();
            IReadOnlyList<string>[] results = await Task.WhenAll(runs); // none of them throws

            Assert.Equal(MigrationCount, results.Sum(r => r.Count));
            Assert.Single(results, r => r.Count == MigrationCount); // the first through the lock did all of it; the others found nothing to do
            Assert.Equal(MigrationCount, await ScalarAsync(migratorConnection, "SELECT count(*) FROM kairos.schema_migrations"));
            Assert.Empty(await PostgresProvisioning.MigrateAsync(migrator));
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task A_run_that_cannot_get_the_lock_in_time_reports_it_and_changes_nothing()
    {
        string database = await fixture.NewEmptyDatabaseAsync();
        try
        {
            string migratorConnection = fixture.ConnectionStringFor(database, PostgresProvisioning.MigratorRole);
            await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(migratorConnection);

            // Another session holds the migration lock, as a stuck migration would.
            await using NpgsqlConnection holder = await migrator.OpenConnectionAsync();
            await using NpgsqlTransaction held = await holder.BeginTransactionAsync();
            await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", holder, held))
            {
                take.Parameters.AddWithValue(PostgresProvisioning.MigrationLockKey);
                await take.ExecuteNonQueryAsync();
            }

            TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
                () => PostgresProvisioning.MigrateAsync(migrator, TimeSpan.FromSeconds(1)));
            Assert.Contains("migration lock", ex.Message, StringComparison.Ordinal);

            await held.RollbackAsync();
            Assert.Equal(0, await ScalarAsync(fixture.ConnectionStringFor(database, null), "SELECT count(*) FROM pg_namespace WHERE nspname = 'kairos'"));
            Assert.Equal(MigrationCount, (await PostgresProvisioning.MigrateAsync(migrator)).Count); // and once the lock is free the same call works
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    private static IHost BuildHost(string application, string? migration, Action<KairosOptions>? more = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.UsePostgres(application);
            if (migration is not null)
            {
                o.MigrateOnStart(migration);
            }

            o.AddWorkflow("echo", (_, input) => Task.FromResult(input));
            more?.Invoke(o);
        });
        return builder.Build();
    }

    [Fact]
    public async Task Several_instances_with_MigrateOnStart_start_together_on_an_empty_database_and_run_work()
    {
        string database = await fixture.NewEmptyDatabaseAsync();
        IHost[] hosts = [];
        try
        {
            string application = fixture.ConnectionStringFor(database, PostgresProvisioning.DataPlaneRole);
            string migration = fixture.ConnectionStringFor(database, PostgresProvisioning.MigratorRole);
            hosts = [.. Enumerable.Range(0, 4).Select(_ => BuildHost(application, migration))];

            await Task.WhenAll(hosts.Select(h => h.StartAsync())); // none fails

            Assert.Equal(MigrationCount, await ScalarAsync(migration, "SELECT count(*) FROM kairos.schema_migrations"));

            // Every instance serves the same database: a run started on one is finished by whichever instance gets its task.
            KairosClient client = hosts[0].Services.GetRequiredService<KairosClient>();
            StartRunResult started = await client.StartAsync(KairosOptions.DefaultScope, "echo-1", "echo", [7]);
            var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, started.RunId);
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            RunState? state = null;
            while (DateTime.UtcNow < deadline && state is not RunState.Completed)
            {
                await Task.Delay(100);
                state = await client.GetStateAsync(run);
            }

            Assert.Equal(RunState.Completed, state);
        }
        finally
        {
            foreach (IHost host in hosts)
            {
                await host.StopAsync();
                host.Dispose();
            }

            NpgsqlConnection.ClearAllPools();
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task MigrateOnStart_refuses_the_same_login_and_a_migration_connection_that_is_not_the_migrator()
    {
        string database = await fixture.NewEmptyDatabaseAsync();
        try
        {
            string dataPlane = fixture.ConnectionStringFor(database, PostgresProvisioning.DataPlaneRole);
            string migrator = fixture.ConnectionStringFor(database, PostgresProvisioning.MigratorRole);

            using (IHost sameLogin = BuildHost(dataPlane, dataPlane))
            {
                InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sameLogin.StartAsync());
                Assert.Contains("second credential", ex.Message, StringComparison.Ordinal);
            }

            using (IHost applicationIsMigrator = BuildHost(migrator, dataPlane))
            {
                InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => applicationIsMigrator.StartAsync());
                Assert.Contains(PostgresProvisioning.MigratorRole, ex.Message, StringComparison.Ordinal); // the "migration" connection is the data-plane login, which is no migrator
            }

            // A different login that is not the migrator is no migrator either: the grants a migration makes depend on who owns the tables.
            using (IHost wrongRole = BuildHost(dataPlane, fixture.ConnectionStringFor(database, PostgresProvisioning.ReadOnlyRole)))
            {
                InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wrongRole.StartAsync());
                Assert.Contains(PostgresProvisioning.MigratorRole, ex.Message, StringComparison.Ordinal);
            }

            // Nothing was migrated by any of the refused starts.
            Assert.Equal(0, await ScalarAsync(fixture.ConnectionStringFor(database, null), "SELECT count(*) FROM pg_namespace WHERE nspname = 'kairos'"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task A_superuser_application_login_under_a_different_name_is_not_taken_for_the_migrator()
    {
        string database = await fixture.NewEmptyDatabaseAsync();
        try
        {
            // The application connecting as the administrator is a poor choice, but it is not the migrator login: MigrateOnStart accepts it, and only the privileged-role check (opted out of here) objects.
            string admin = fixture.ConnectionStringFor(database, null);
            string migrator = fixture.ConnectionStringFor(database, PostgresProvisioning.MigratorRole);
            using IHost host = BuildHost(admin, migrator, o => o.AllowPrivilegedDatabaseRole());
            await host.StartAsync();
            await host.StopAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await fixture.DropAsync(database);
        }
    }

    private static async Task<(int Exit, string Out, string Err)> CliAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await Kairos.Cli.Cli.RunAsync(args, stdout, stderr, default);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task The_cli_provisions_and_migrates_and_refuses_the_wrong_credentials()
    {
        string database = await fixture.NewEmptyDatabaseAsync();
        string suffix = Guid.NewGuid().ToString("N");
        string[] variables = [.. VariableNames.Select(n => $"KAIROS_TEST_{n}_{suffix}")];
        try
        {
            Environment.SetEnvironmentVariable(variables[0], fixture.ConnectionStringFor(database, null));
            Environment.SetEnvironmentVariable(variables[1], "migrator-secret-should-never-print");

            // A missing password is a usage error and says which variable; no secret is echoed.
            (int missing, string outMissing, string errMissing) = await CliAsync(
                "db", "provision", "--admin-connection-env", variables[0], "--migrator-password-env", variables[1],
                "--dataplane-password-env", variables[2], "--controlplane-password-env", variables[3], "--readonly-password-env", variables[4]);
            Assert.Equal(ExitCode.Usage, missing);
            Assert.Contains(variables[2], errMissing, StringComparison.Ordinal);
            Assert.DoesNotContain("migrator-secret", outMissing + errMissing, StringComparison.Ordinal);

            foreach (string v in variables[2..5])
            {
                Environment.SetEnvironmentVariable(v, "other-secret-should-never-print");
            }

            (int provisioned, string outProvision, string errProvision) = await CliAsync(
                "db", "provision", "--admin-connection-env", variables[0], "--migrator-password-env", variables[1],
                "--dataplane-password-env", variables[2], "--controlplane-password-env", variables[3], "--readonly-password-env", variables[4]);
            Assert.True(provisioned == ExitCode.Passed, errProvision);
            Assert.DoesNotContain("secret", outProvision + errProvision, StringComparison.Ordinal);

            // The application's credential and an administrator's are refused; only the migrator migrates.
            foreach (string? role in NotTheMigrator)
            {
                Environment.SetEnvironmentVariable(variables[5], fixture.ConnectionStringFor(database, role));
                (int refused, _, string refusal) = await CliAsync("db", "migrate", "--connection-env", variables[5]);
                Assert.Equal(ExitCode.Usage, refused);
                Assert.Contains(PostgresProvisioning.MigratorRole, refusal, StringComparison.Ordinal);
            }

            Environment.SetEnvironmentVariable(variables[5], fixture.ConnectionStringFor(database, PostgresProvisioning.MigratorRole));
            (int migrated, string outMigrate, string errMigrate) = await CliAsync("db", "migrate", "--connection-env", variables[5]);
            Assert.True(migrated == ExitCode.Passed, errMigrate);
            Assert.Contains($"Applied {MigrationCount} migration(s)", outMigrate, StringComparison.Ordinal);

            (int again, string outAgain, _) = await CliAsync("db", "migrate", "--connection-env", variables[5]);
            Assert.Equal(ExitCode.Passed, again);
            Assert.Contains("nothing to apply", outAgain, StringComparison.Ordinal);

            (int badOption, _, _) = await CliAsync("db", "migrate", "--lock-timeout", "0");
            Assert.Equal(ExitCode.Usage, badOption);
        }
        finally
        {
            foreach (string v in variables)
            {
                Environment.SetEnvironmentVariable(v, null);
            }

            NpgsqlConnection.ClearAllPools();
            await fixture.DropAsync(database);
        }
    }
}
