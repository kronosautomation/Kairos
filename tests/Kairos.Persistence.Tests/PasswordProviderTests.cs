using System.Text;
using Kairos.Persistence.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// A password that is not in the connection string: a short-lived token from the cloud's identity service, supplied to the one data source Kairos builds. The database here takes a password, so a connection that
/// does not use the provider cannot be made at all; each test therefore proves that a connection <i>did</i> use it, the work-notification connection included.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class PasswordProviderTests(PostgresFixture fixture)
{
    private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

    private static IHost BuildHost(Action<KairosOptions> configure)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.AddWorkflow("hello", (_, input) => Task.FromResult(input));
            configure(o);
        });
        return builder.Build();
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T?>> look)
        where T : class
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await look() is { } found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Not reached in 30 s.");
    }

    [Fact]
    public async Task The_store_and_the_notification_connection_both_get_their_password_from_the_provider()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        int calls = 0;
        using IHost host = BuildHost(o => o.UsePostgres(db.DataPlaneConnectionStringWithoutPassword, builder => builder.UsePeriodicPasswordProvider(
            (settings, _) =>
            {
                Interlocked.Increment(ref calls);
                Assert.Equal("kairos_dataplane", settings.Username); // the provider is told whom it signs in
                return ValueTask.FromResult(TestDatabase.DataPlanePassword);
            },
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1))));
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "w-1", "hello", Hello);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);
        await UntilAsync<string>(async () => await client.GetStateAsync(run) is RunState.Completed ? "done" : null); // the store, through the provider

        // The notification connection is a second, long-lived connection of the same data source: it is listening, as the same role, so it signed in with the provider's password too.
        await UntilAsync<string>(async () =>
        {
            await using NpgsqlCommand cmd = db.Superuser.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = @d AND usename = 'kairos_dataplane' AND query LIKE 'LISTEN%'");
            cmd.Parameters.AddWithValue("d", db.Name);
            return (long)(await cmd.ExecuteScalarAsync())! > 0 ? "listening" : null;
        });
        Assert.True(Volatile.Read(ref calls) >= 1);
    }

    [Fact]
    public async Task A_provider_that_gives_the_wrong_password_keeps_every_connection_out()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(o => o.UsePostgres(db.DataPlaneConnectionStringWithoutPassword, builder => builder.UsePeriodicPasswordProvider(
            (_, _) => ValueTask.FromResult("not-the-password"), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1))));
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        await Assert.ThrowsAnyAsync<PostgresException>(async () => await client.StartAsync(KairosOptions.DefaultScope, "w-2", "hello", Hello));
        await Task.Delay(500);
        await using NpgsqlCommand cmd = db.Superuser.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @d AND usename = 'kairos_dataplane'");
        cmd.Parameters.AddWithValue("d", db.Name);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!); // not even the notification connection got in
    }

    [Fact]
    public async Task Without_a_provider_and_without_a_password_the_connection_string_alone_does_not_connect()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(o => o.UsePostgres(db.DataPlaneConnectionStringWithoutPassword));
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.StartAsync(KairosOptions.DefaultScope, "w-3", "hello", Hello));
    }

    private sealed class FixedToken(string token, Action<DatabaseEndpoint>? seen = null) : IDatabaseTokenSource
    {
        public ValueTask<string> GetPasswordAsync(DatabaseEndpoint endpoint, CancellationToken cancellationToken)
        {
            seen?.Invoke(endpoint);
            return ValueTask.FromResult(token);
        }
    }

    [Fact]
    public async Task UseTokenPassword_gives_the_store_and_the_notification_connection_the_token_and_names_the_role()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        DatabaseEndpoint? endpoint = null;
        using IHost host = BuildHost(o => o.UsePostgres(db.DataPlaneConnectionStringWithoutPassword, b => b.UseTokenPassword(new FixedToken(TestDatabase.DataPlanePassword, e => endpoint = e))));
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "w-4", "hello", Hello);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);
        await UntilAsync<string>(async () => await client.GetStateAsync(run) is RunState.Completed ? "done" : null);
        await UntilAsync<string>(async () =>
        {
            await using NpgsqlCommand cmd = db.Superuser.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @d AND usename = 'kairos_dataplane' AND query LIKE 'LISTEN%'");
            cmd.Parameters.AddWithValue("d", db.Name);
            return (long)(await cmd.ExecuteScalarAsync())! > 0 ? "listening" : null;
        });
        Assert.Equal("kairos_dataplane", endpoint!.Value.Username);
        Assert.False(string.IsNullOrEmpty(endpoint.Value.Host));
        Assert.True(endpoint.Value.Port > 0);
    }

    [Fact]
    public async Task UseTokenPassword_refuses_a_connection_string_that_also_has_a_password()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(o => o.UsePostgres(db.DataPlaneConnectionString, b => b.UseTokenPassword(new FixedToken("x"))));

        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.StartAsync());
        Assert.DoesNotContain(TestDatabase.DataPlanePassword, e.ToString(), StringComparison.Ordinal);
        Assert.Contains("password", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_token_source_that_gives_nothing_keeps_every_connection_out_without_a_token_in_the_message()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(o => o.UsePostgres(db.DataPlaneConnectionStringWithoutPassword, b => b.UseTokenPassword(new FixedToken(""))));

        Exception e = await Record.ExceptionAsync(async () =>
        {
            await host.StartAsync();
            var client = host.Services.GetRequiredService<KairosClient>();
            await client.StartAsync(KairosOptions.DefaultScope, "w-5", "hello", Hello);
        });
        Assert.NotNull(e);
    }
}
