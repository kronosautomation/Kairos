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

    /// <summary>A database with the Kairos roles in the cluster but no schema: what a customer has after `kairos db provision` and before the first migration.</summary>
    internal async Task<string> NewEmptyDatabaseAsync()
    {
        string name = $"kairos_e{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        await Execute(_admin!, $"CREATE DATABASE {name}");
        await using NpgsqlDataSource asAdmin = NpgsqlDataSource.Create(ConnectionString(name, null, null));
        await PostgresProvisioning.PrepareDatabaseAsync(asAdmin);
        return name;
    }

    internal string ConnectionStringFor(string database, string? role) =>
        ConnectionString(database, role, role is null ? null : PasswordFor(role));

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

    /// <summary>Virtual-time store: leases follow the caller's clock so tests and simulations control expiry.</summary>
    public PostgresKairosStore NewStore(StoreLimits? limits = null) =>
        new(DataPlane, new PostgresStoreOptions { LeaseClock = LeaseClock.Caller, Limits = limits ?? new StoreLimits() });

    /// <summary>Production behaviour: leases follow the database clock.</summary>
    public PostgresKairosStore NewDatabaseClockStore() => new(DataPlane, new PostgresStoreOptions { LeaseClock = LeaseClock.Database });

    public string DataPlaneConnectionString =>
        fixture.ConnectionString(Name, PostgresProvisioning.DataPlaneRole, PostgresFixture.PasswordFor(PostgresProvisioning.DataPlaneRole));

    /// <summary>The data-plane connection string without its password, and the password: for tests of a password that comes from somewhere else (a token provider).</summary>
    public string DataPlaneConnectionStringWithoutPassword => fixture.ConnectionString(Name, PostgresProvisioning.DataPlaneRole, null);

    public static string DataPlanePassword => PostgresFixture.PasswordFor(PostgresProvisioning.DataPlaneRole);

    /// <summary>For the command-line tool that must be run as the migrator login (a direct history export).</summary>
    public string MigratorConnectionString =>
        fixture.ConnectionString(Name, PostgresProvisioning.MigratorRole, PostgresFixture.PasswordFor(PostgresProvisioning.MigratorRole));

    /// <summary>For tools that connect as the reporting role (read-only, bound by row-level security like everything else).</summary>
    public string ReadOnlyConnectionString =>
        fixture.ConnectionString(Name, PostgresProvisioning.ReadOnlyRole, PostgresFixture.PasswordFor(PostgresProvisioning.ReadOnlyRole));

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
