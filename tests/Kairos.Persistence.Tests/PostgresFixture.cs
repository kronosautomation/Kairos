using Kairos.Persistence.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kairos.Persistence.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// One Postgres container for the whole run (image from KAIROS_PG_IMAGE, default postgres:17). Roles and the migrated schema
/// are built once into a template database; every test gets its own copy, so tests are isolated and cheap.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateName = "kairos_template";
    private const string MigratorPassword = "migrator-test-only";
    private const string DataPlanePassword = "dataplane-test-only";
    private const string ControlPlanePassword = "controlplane-test-only";
    private const string ReadOnlyPassword = "readonly-test-only";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _admin;
    private int _counter;

    public async Task InitializeAsync()
    {
        string image = Environment.GetEnvironmentVariable("KAIROS_PG_IMAGE") is { Length: > 0 } i ? i : "postgres:17";
        _container = new PostgreSqlBuilder(image).Build();
        await _container.StartAsync();

        _admin = NpgsqlDataSource.Create(_container.GetConnectionString());
        await PostgresProvisioning.CreateRolesAsync(_admin, MigratorPassword, DataPlanePassword, ControlPlanePassword, ReadOnlyPassword);

        await Execute(_admin, $"CREATE DATABASE {TemplateName}");
        await using NpgsqlDataSource templateAdmin = NpgsqlDataSource.Create(ConnectionString(TemplateName, null, null));
        await PostgresProvisioning.PrepareDatabaseAsync(templateAdmin);
        await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(ConnectionString(TemplateName, PostgresProvisioning.MigratorRole, MigratorPassword));
        await PostgresProvisioning.MigrateAsync(migrator);
        NpgsqlConnection.ClearAllPools(); // a template must have no open connections when it is copied
    }

    public async Task DisposeAsync()
    {
        if (_admin is not null)
        {
            await _admin.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task<TestDatabase> NewDatabaseAsync()
    {
        string name = $"kairos_t{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        await Execute(_admin!, $"CREATE DATABASE {name} TEMPLATE {TemplateName}");
        return new TestDatabase(name, this);
    }

    internal string ConnectionString(string database, string? user, string? password)
    {
        var b = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = database, MaxPoolSize = 40 };
        if (user is not null)
        {
            b.Username = user;
            b.Password = password;
        }

        return b.ConnectionString;
    }

    internal static async Task Execute(NpgsqlDataSource ds, string sql)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    internal async Task DropAsync(string name) =>
        await Execute(_admin!, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");

    internal static string PasswordFor(string role) => role switch
    {
        PostgresProvisioning.MigratorRole => MigratorPassword,
        PostgresProvisioning.DataPlaneRole => DataPlanePassword,
        PostgresProvisioning.ControlPlaneRole => ControlPlanePassword,
        PostgresProvisioning.ReadOnlyRole => ReadOnlyPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

/// <summary>A private, freshly migrated database with a data source per role (and a superuser one for tampering and inspection).</summary>
public sealed class TestDatabase(string name, PostgresFixture fixture) : IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> _sources = [];

    public string Name { get; } = name;

    public NpgsqlDataSource Superuser => For(null);

    public NpgsqlDataSource DataPlane => For(PostgresProvisioning.DataPlaneRole);

    public NpgsqlDataSource ControlPlane => For(PostgresProvisioning.ControlPlaneRole);

    public NpgsqlDataSource ReadOnly => For(PostgresProvisioning.ReadOnlyRole);

    public NpgsqlDataSource Migrator => For(PostgresProvisioning.MigratorRole);

    public PostgresKairosStore NewStore() => new(DataPlane);

    public string DataPlaneConnectionString =>
        fixture.ConnectionString(Name, PostgresProvisioning.DataPlaneRole, PostgresFixture.PasswordFor(PostgresProvisioning.DataPlaneRole));

    private NpgsqlDataSource For(string? role)
    {
        string key = role ?? "";
        if (!_sources.TryGetValue(key, out NpgsqlDataSource? ds))
        {
            ds = NpgsqlDataSource.Create(fixture.ConnectionString(Name, role, role is null ? null : PostgresFixture.PasswordFor(role)));
            _sources.Add(key, ds);
        }

        return ds;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (NpgsqlDataSource ds in _sources.Values)
        {
            await ds.DisposeAsync();
        }

        await fixture.DropAsync(Name);
    }
}
