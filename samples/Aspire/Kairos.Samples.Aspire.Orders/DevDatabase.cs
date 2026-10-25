using System.Security.Cryptography;
using System.Text;
using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Samples.Aspire.Orders;

/// <summary>
/// Development only. The PostgreSQL container Aspire starts has one administrator account; Kairos wants its own roles (the running app may read and write run data but never
/// change the schema). This creates them and applies the schema, then returns the connection string of the <c>kairos_dataplane</c> role. The role passwords are derived from the
/// administrator password, so nothing is stored and a restart finds the same roles. Real deployments provision roles and migrate from their pipeline.
/// </summary>
internal static class DevDatabase
{
    public static async Task<string> PrepareAsync(string adminConnectionString)
    {
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);
        string Password(string role) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"kairos-dev:{role}:{admin.Password}")))[..32];

        await using NpgsqlDataSource adminSource = NpgsqlDataSource.Create(adminConnectionString);
        await PostgresProvisioning.CreateRolesAsync(
            adminSource, Password(PostgresProvisioning.MigratorRole), Password(PostgresProvisioning.DataPlaneRole),
            Password(PostgresProvisioning.ControlPlaneRole), Password(PostgresProvisioning.ReadOnlyRole));
        await PostgresProvisioning.PrepareDatabaseAsync(adminSource);

        var migratorConnection = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = PostgresProvisioning.MigratorRole,
            Password = Password(PostgresProvisioning.MigratorRole),
        };
        await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(migratorConnection.ConnectionString);
        await PostgresProvisioning.MigrateAsync(migrator);

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = PostgresProvisioning.DataPlaneRole,
            Password = Password(PostgresProvisioning.DataPlaneRole),
        }.ConnectionString;
    }
}
