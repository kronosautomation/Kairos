using Kairos.Persistence.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kairos.Samples.Operations.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "operations-postgres";
}

/// <summary>One PostgreSQL container with the four Kairos roles created the way <c>kairos db provision</c> creates them. Each test makes its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Password = "operations-test-only";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _admin;
    private int _counter;

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

    /// <summary>A database with the roles available and the migrator allowed to create the schema, migrated when asked.</summary>
    public async Task<string> NewDatabaseAsync(bool migrate)
    {
        string name = $"ops_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        await using (NpgsqlCommand create = _admin!.CreateCommand($"CREATE DATABASE {name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using NpgsqlDataSource asAdmin = NpgsqlDataSource.Create(Connection(name, null));
        await PostgresProvisioning.PrepareDatabaseAsync(asAdmin);
        if (migrate)
        {
            await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(Connection(name, PostgresProvisioning.MigratorRole));
            await PostgresProvisioning.MigrateAsync(migrator);
        }

        return name;
    }

    public string Connection(string database, string? role)
    {
        var b = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database };
        if (role is not null)
        {
            b.Username = role;
            b.Password = Password;
        }

        return b.ConnectionString;
    }

    public async Task<long> ScalarAsync(string database, string sql)
    {
        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(Connection(database, null));
        await using NpgsqlCommand cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task DropAsync(string database)
    {
        NpgsqlConnection.ClearAllPools();
        await using NpgsqlCommand drop = _admin!.CreateCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}
