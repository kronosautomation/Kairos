using System.Reflection;
using Npgsql;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// One-time setup (roles, as an administrator) and schema migration (as <c>kairos_migrator</c>, from CI/CD). Running services
/// connect as <c>kairos_dataplane</c> and never execute DDL.
/// </summary>
public static class PostgresProvisioning
{
    public const string MigratorRole = "kairos_migrator";
    public const string DataPlaneRole = "kairos_dataplane";
    public const string ControlPlaneRole = "kairos_controlplane";
    public const string ReadOnlyRole = "kairos_readonly";

    /// <summary>Creates the four roles (idempotent). Needs CREATEROLE. Passwords are supplied by the caller, never stored in code.</summary>
    public static async Task CreateRolesAsync(
        NpgsqlDataSource admin, string migratorPassword, string dataPlanePassword, string controlPlanePassword, string readOnlyPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        (string Role, string Password)[] roles =
        [
            (MigratorRole, migratorPassword), (DataPlaneRole, dataPlanePassword),
            (ControlPlaneRole, controlPlanePassword), (ReadOnlyRole, readOnlyPassword),
        ];

        foreach ((string role, string password) in roles)
        {
            await using NpgsqlCommand exists = admin.CreateCommand("SELECT 1 FROM pg_roles WHERE rolname = $1");
            exists.Parameters.AddWithValue(role);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            // CREATE ROLE cannot take bind parameters; let the server quote the identifier and the password literal.
            await using NpgsqlCommand build = admin.CreateCommand(
                "SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD %L', $1::text, $2::text)");
            build.Parameters.AddWithValue(role);
            build.Parameters.AddWithValue(password);
            string statement = (string)(await build.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await using NpgsqlCommand create = admin.CreateCommand(statement);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Grants the migrator the ability to create the schema in the current database.</summary>
    public static async Task PrepareDatabaseAsync(NpgsqlDataSource admin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        await using NpgsqlCommand cmd = admin.CreateCommand(
            $"GRANT CREATE ON DATABASE {Quote(await CurrentDatabaseAsync(admin, cancellationToken).ConfigureAwait(false))} TO {MigratorRole}");
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The key of the advisory lock every migration run takes first. Instances that start together (a rolling deploy, several replicas with <c>MigrateOnStart</c>) queue on it,
    /// and the later ones find nothing left to apply.
    /// </summary>
    public const long MigrationLockKey = 0x4B41495F4D494752; // "KAI_MIGR"

    /// <summary>How long a migration run waits for another run to finish before it gives up, unless the caller says otherwise.</summary>
    public static readonly TimeSpan DefaultMigrationLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Applies pending embedded migrations, in order, and returns the names it applied (empty when the schema was current). Run as <c>kairos_migrator</c>.
    /// The whole run is one transaction that begins by taking a transaction-scoped advisory lock (<see cref="MigrationLockKey"/>): concurrent runs serialize, a run that fails
    /// leaves the schema as it found it, and the lock goes with the transaction, so a crashed migrator or a connection pooler cannot leave it held.
    /// </summary>
    /// <param name="migrator">A data source for the migrator login.</param>
    /// <param name="lockTimeout">How long to wait for the lock; <see cref="DefaultMigrationLockTimeout"/> when null. Waiting longer than this means another run is stuck, and that is reported instead of waited out.</param>
    /// <param name="cancellationToken">Cancels the wait and the run.</param>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource migrator, TimeSpan? lockTimeout, CancellationToken cancellationToken = default) =>
        RunMigrationsAsync(migrator, lockTimeout, cancellationToken);

    /// <inheritdoc cref="MigrateAsync(NpgsqlDataSource, TimeSpan?, CancellationToken)"/>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource migrator, CancellationToken cancellationToken = default) =>
        RunMigrationsAsync(migrator, null, cancellationToken);

    private static async Task<IReadOnlyList<string>> RunMigrationsAsync(NpgsqlDataSource migrator, TimeSpan? lockTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrator);
        TimeSpan timeout = lockTimeout ?? DefaultMigrationLockTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Assembly assembly = typeof(PostgresProvisioning).Assembly;
        string[] names = [.. assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        await using NpgsqlConnection conn = await migrator.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // SET LOCAL takes no bind parameters; the value is a number we format ourselves.
            await using (var wait = new NpgsqlCommand(
                FormattableString.Invariant($"SET LOCAL lock_timeout = {(long)Math.Ceiling(timeout.TotalMilliseconds)}"), conn, tx))
            {
                await wait.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
            {
                take.Parameters.AddWithValue(MigrationLockKey);
                await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new TimeoutException(
                $"Another Kairos migration has held the migration lock for more than {timeout}. Nothing was changed. Find the session that holds advisory lock {MigrationLockKey} (pg_locks) before running again.", ex);
        }

        await using (var reset = new NpgsqlCommand("SET LOCAL lock_timeout = 0", conn, tx))
        {
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); // the migrations themselves wait for their own locks as long as they must
        }

        await using (var schema = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS kairos", conn, tx))
        {
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var create = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS kairos.schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn, tx))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var applied = new List<string>();
        foreach (string name in names)
        {
            await using (var check = new NpgsqlCommand("SELECT 1 FROM kairos.schema_migrations WHERE name = $1", conn, tx))
            {
                check.Parameters.AddWithValue(name);
                if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            string sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await using (var apply = new NpgsqlCommand(sql, conn, tx))
            {
                await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var record = new NpgsqlCommand("INSERT INTO kairos.schema_migrations (name) VALUES ($1)", conn, tx))
            {
                record.Parameters.AddWithValue(name);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            applied.Add(name);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    private static async Task<string> CurrentDatabaseAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand("SELECT current_database()");
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
