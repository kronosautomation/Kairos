using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Samples.GettingStarted;

/// <summary>One-time database setup for the sample: the four Kairos roles, then the schema.</summary>
public static class Database
{
    #region provision
    /// <summary>
    /// Run once per database as an administrator. Creates the Kairos roles and applies the schema as the migrator role. The running
    /// application then connects as <c>kairos_dataplane</c>, which can read and write workflow data but cannot change the schema.
    /// </summary>
    public static async Task ProvisionAsync(string adminConnectionString, string dataPlanePassword)
    {
        string migratorPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        string otherPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(adminConnectionString);
        await PostgresProvisioning.CreateRolesAsync(admin, migratorPassword, dataPlanePassword, otherPassword, otherPassword);
        await PostgresProvisioning.PrepareDatabaseAsync(admin);

        var migratorConnection = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = PostgresProvisioning.MigratorRole,
            Password = migratorPassword,
        };
        await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(migratorConnection.ConnectionString);
        await PostgresProvisioning.MigrateAsync(migrator);
    }
    #endregion
}
