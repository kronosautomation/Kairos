using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Kairos.Persistence.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kairos.Persistence.Tests.Faults;

[CollectionDefinition(Name)]
public sealed class FaultTestGroup : ICollectionFixture<FaultFixture>
{
    public const string Name = "postgres-faults";
}

/// <summary>
/// Postgres behind Toxiproxy on a private network. Tests talk to the database through the proxy (<see cref="ProxyPort"/> for the
/// store, <see cref="NotifyProxyPort"/> for the LISTEN connection) and verify through a direct connection, so the proxy can
/// delay, cut, reset or blackhole the traffic under test without hiding what was really stored.
/// </summary>
public sealed class FaultFixture : IAsyncLifetime
{
    internal const string ToxiproxyImage = "ghcr.io/shopify/toxiproxy:latest@sha256:5d3dae09bc185dcc5ff6cf43abf6062a4a720aff0c4306a471e773acdcd107aa";
    internal const string Proxy = "pg";
    internal const string NotifyProxy = "pgnotify";
    private const string TemplateName = "kairos_fault_template";
    private const int ProxyContainerPort = 8666;
    private const int NotifyContainerPort = 8667;

    private INetwork? _network;
    private PostgreSqlContainer? _pg;
    private IContainer? _toxiproxy;
    private NpgsqlDataSource? _admin;
    private int _counter;

    internal ToxiproxyClient Toxi { get; private set; } = null!;

    public int ProxyPort => _toxiproxy!.GetMappedPublicPort(ProxyContainerPort);

    public int NotifyProxyPort => _toxiproxy!.GetMappedPublicPort(NotifyContainerPort);

    public async Task InitializeAsync()
    {
        string image = Environment.GetEnvironmentVariable("KAIROS_PG_IMAGE") is { Length: > 0 } i ? i : "postgres:17";
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _pg = new PostgreSqlBuilder(image).WithNetwork(_network).WithNetworkAliases("pg").Build();
        _toxiproxy = new ContainerBuilder(ToxiproxyImage)
            .WithNetwork(_network)
            .WithNetworkAliases("toxiproxy")
            .WithCommand("-host=0.0.0.0")
            .WithPortBinding(8474, true)
            .WithPortBinding(ProxyContainerPort, true)
            .WithPortBinding(NotifyContainerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build();
        await Task.WhenAll(_pg.StartAsync(), _toxiproxy.StartAsync());

        Toxi = new ToxiproxyClient(new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(8474)}"));
        await Toxi.CreateProxyAsync(Proxy, $"0.0.0.0:{ProxyContainerPort}", "pg:5432");
        await Toxi.CreateProxyAsync(NotifyProxy, $"0.0.0.0:{NotifyContainerPort}", "pg:5432");

        _admin = NpgsqlDataSource.Create(_pg.GetConnectionString());
        await PostgresProvisioning.CreateRolesAsync(
            _admin, PostgresFixture.PasswordFor(PostgresProvisioning.MigratorRole), PostgresFixture.PasswordFor(PostgresProvisioning.DataPlaneRole),
            PostgresFixture.PasswordFor(PostgresProvisioning.ControlPlaneRole), PostgresFixture.PasswordFor(PostgresProvisioning.ReadOnlyRole));
        await PostgresFixture.Execute(_admin, $"CREATE DATABASE {TemplateName}");
        await using NpgsqlDataSource templateAdmin = NpgsqlDataSource.Create(Direct(TemplateName, null));
        await PostgresProvisioning.PrepareDatabaseAsync(templateAdmin);
        await using NpgsqlDataSource migrator = NpgsqlDataSource.Create(Direct(TemplateName, PostgresProvisioning.MigratorRole));
        await PostgresProvisioning.MigrateAsync(migrator);
        NpgsqlConnection.ClearAllPools();
    }

    public async Task DisposeAsync()
    {
        Toxi?.Dispose();
        if (_admin is not null)
        {
            await _admin.DisposeAsync();
        }

        if (_toxiproxy is not null)
        {
            await _toxiproxy.DisposeAsync();
        }

        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }

    /// <summary>A private migrated database. The proxies are reset (no toxics, enabled) first.</summary>
    public async Task<FaultDatabase> NewDatabaseAsync()
    {
        await Toxi.ResetAsync();
        string name = $"kairos_f{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        await PostgresFixture.Execute(_admin!, $"CREATE DATABASE {name} TEMPLATE {TemplateName}");
        return new FaultDatabase(name, this);
    }

    internal Task DropAsync(string name) => PostgresFixture.Execute(_admin!, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");

    internal string Direct(string database, string? role)
    {
        var b = new NpgsqlConnectionStringBuilder(_pg!.GetConnectionString()) { Database = database, MaxPoolSize = 40 };
        if (role is not null)
        {
            b.Username = role;
            b.Password = PostgresFixture.PasswordFor(role);
        }

        return b.ConnectionString;
    }

    /// <summary>Data-plane connection through a proxy, with short timeouts so a blackholed connection fails fast.</summary>
    internal string ViaProxy(string database, int port)
    {
        var b = new NpgsqlConnectionStringBuilder(Direct(database, PostgresProvisioning.DataPlaneRole))
        {
            Host = _toxiproxy!.Hostname,
            Port = port,
            Timeout = 5,
            CommandTimeout = 5,
            KeepAlive = 1,
            MaxPoolSize = 20,
        };
        return b.ConnectionString;
    }
}

public sealed class FaultDatabase(string name, FaultFixture fixture) : IAsyncDisposable
{
    private NpgsqlDataSource? _direct;

    public string Name { get; } = name;

    /// <summary>Connection string for the store, through the proxy.</summary>
    public string ProxiedConnectionString => fixture.ViaProxy(Name, fixture.ProxyPort);

    /// <summary>Connection string for the LISTEN connection, through its own proxy.</summary>
    public string NotifyConnectionString => fixture.ViaProxy(Name, fixture.NotifyProxyPort);

    /// <summary>The database as it really is, bypassing the proxy.</summary>
    public NpgsqlDataSource Direct => _direct ??= NpgsqlDataSource.Create(fixture.Direct(Name, PostgresProvisioning.DataPlaneRole));

    public PostgresKairosStore DirectStore() => new(Direct);

    public async ValueTask DisposeAsync()
    {
        if (_direct is not null)
        {
            await _direct.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await fixture.DropAsync(Name);
    }
}
