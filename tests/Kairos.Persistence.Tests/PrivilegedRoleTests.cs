using Kairos.Persistence.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The application's database login must be the data-plane kind. A superuser, a login that bypasses row level security and the login that owns the tables each turn the tenant wall off
/// without an error, so <c>AddKairos</c> refuses them unless the code says <c>AllowPrivilegedDatabaseRole()</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class PrivilegedRoleTests(PostgresFixture fixture)
{
    private static IHost BuildHost(string application, bool allow, List<string>? warnings = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (warnings is not null)
        {
            builder.Logging.AddProvider(new CollectingLogger(warnings));
        }

        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.UsePostgres(application);
            if (allow)
            {
                o.AllowPrivilegedDatabaseRole();
            }
        });
        return builder.Build();
    }

    [Fact]
    public async Task The_data_plane_login_starts_without_the_opt_out_and_is_reported_unprivileged()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(db.DataPlaneConnectionString, allow: false);
        await host.StartAsync();

        KairosDatabaseRole role = host.Services.GetRequiredService<KairosDatabaseRole>();
        Assert.False(role.Inspection!.Privileged);
        Assert.False(role.PrivilegedAllowed);
        await host.StopAsync();
    }

    public static TheoryData<string?> PrivilegedLogins => new() { null, PostgresProvisioning.MigratorRole };

    [Theory]
    [MemberData(nameof(PrivilegedLogins))]
    public async Task A_superuser_or_table_owner_login_is_refused_by_default(string? login)
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(fixture.ConnectionStringFor(db.Name, login), allow: false);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("AllowPrivilegedDatabaseRole", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_login_that_only_bypasses_row_level_security_is_refused()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await PostgresFixture.Execute(db.Superuser, "CREATE ROLE kairos_bypass LOGIN PASSWORD 'bypass-test-only' BYPASSRLS");
        try
        {
            var conn = new NpgsqlConnectionStringBuilder(fixture.ConnectionStringFor(db.Name, null)) { Username = "kairos_bypass", Password = "bypass-test-only" };
            using IHost host = BuildHost(conn.ConnectionString, allow: false);
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
            Assert.Contains("bypassrls=True", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource admin = NpgsqlDataSource.Create(fixture.ConnectionStringFor(db.Name, null)); // roles belong to the cluster, not the database
            await PostgresFixture.Execute(admin, "DROP ROLE IF EXISTS kairos_bypass");
        }
    }

    [Fact]
    public async Task With_the_opt_out_a_privileged_login_starts_and_the_warning_is_logged_and_reported()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var warnings = new List<string>();
        using IHost host = BuildHost(fixture.ConnectionStringFor(db.Name, null), allow: true, warnings);
        await host.StartAsync();

        KairosDatabaseRole role = host.Services.GetRequiredService<KairosDatabaseRole>();
        Assert.True(role.PrivilegedAllowed);
        Assert.True(role.Inspection!.Superuser);
        Assert.Contains(warnings, w => w.Contains("AllowPrivilegedDatabaseRole", StringComparison.Ordinal));
        await host.StopAsync();
    }

    private sealed class CollectingLogger(List<string> warnings) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (warnings)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
