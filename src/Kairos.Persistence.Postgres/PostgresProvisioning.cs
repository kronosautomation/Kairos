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

    /// <summary>Applies pending embedded migrations, in order, each in its own transaction. Run as <c>kairos_migrator</c>.</summary>
    public static async Task MigrateAsync(NpgsqlDataSource migrator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrator);
        Assembly assembly = typeof(PostgresProvisioning).Assembly;
        string[] names = [.. assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        await using (NpgsqlCommand schema = migrator.CreateCommand("CREATE SCHEMA IF NOT EXISTS kairos"))
        {
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand create = migrator.CreateCommand(
            "CREATE TABLE IF NOT EXISTS kairos.schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string name in names)
        {
            await using NpgsqlConnection conn = await migrator.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> CurrentDatabaseAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand("SELECT current_database()");
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
