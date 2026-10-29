using Kairos.Persistence.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos;

/// <summary>Runs the schema migration at host start (<see cref="KairosOptions.MigrateOnStart(NpgsqlDataSource, TimeSpan?)"/>). Any failure stops the host from starting.</summary>
internal sealed partial class KairosMigrationService(KairosOptions.StoreHandle handle, KairosOptions options, ILogger<KairosMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        KairosOptions.MigrationSettings settings = options.Migration!;
        NpgsqlDataSource application = handle.DataSource
            ?? throw new InvalidOperationException("MigrateOnStart needs the application's store to be PostgreSQL (UsePostgres).");

        NpgsqlDataSource? owned = null;
        try
        {
            NpgsqlDataSource migrator = settings.DataSource ?? (owned = BuildMigrator(settings.ConnectionString!));
            RefuseSameCredential(application, migrator);

            await RequireRoleAsync(migrator, expectMigrator: true, "the migration connection", cancellationToken).ConfigureAwait(false);
            await RequireRoleAsync(application, expectMigrator: false, "the application's connection", cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> applied = await PostgresProvisioning.MigrateAsync(migrator, settings.LockTimeout, cancellationToken).ConfigureAwait(false);
            LogMigrated(logger, applied.Count, applied);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private NpgsqlDataSource BuildMigrator(string connectionString) =>
        new NpgsqlDataSourceBuilder(connectionString).ApplyDefault(connectionString, options.Tls12ToDatabaseAllowed).Build();

    private static void RefuseSameCredential(NpgsqlDataSource application, NpgsqlDataSource migrator)
    {
        string? applicationLogin = new NpgsqlConnectionStringBuilder(application.ConnectionString).Username;
        string? migratorLogin = new NpgsqlConnectionStringBuilder(migrator.ConnectionString).Username;
        if (string.Equals(applicationLogin, migratorLogin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MigrateOnStart needs a second credential: the application's connection and the migration connection log in as the same database user. " +
                $"The application connects as {PostgresProvisioning.DataPlaneRole}, which cannot change the schema; only the migration connection uses {PostgresProvisioning.MigratorRole}.");
        }
    }

    // The migration connection must be the migrator login (the grants the migrations make depend on who owns the tables); the application's must not be.
    private static async Task RequireRoleAsync(NpgsqlDataSource source, bool expectMigrator, string what, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection conn = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT current_user = $1", conn);
        cmd.Parameters.AddWithValue(PostgresProvisioning.MigratorRole);
        bool isMigrator = (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (isMigrator != expectMigrator)
        {
            throw new InvalidOperationException(expectMigrator
                ? $"MigrateOnStart: {what} is not the {PostgresProvisioning.MigratorRole} login. Create the roles with `kairos db provision` and connect as {PostgresProvisioning.MigratorRole}."
                : $"MigrateOnStart: {what} is the {PostgresProvisioning.MigratorRole} login. The application must connect as {PostgresProvisioning.DataPlaneRole}; the migrator can change the schema.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kairos schema is current; applied {Count} migration(s) at start: {Names}")]
    private static partial void LogMigrated(ILogger logger, int count, IReadOnlyList<string> names);
}
