using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The one exception to row level security on <c>kairos.runs</c> (migration 0010): with the marker set a session may read the runs that still have events to publish, in every scope, and nothing else. Without the
/// marker, with another value, or from another role, it reads nothing across scopes; it never writes through the exception; and only the store's sweep query sets the marker.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class UnpublishedSweepOnPostgresTests(PostgresFixture fixture)
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    private static async Task<long> CountAsync(NpgsqlDataSource source, string setup, string query)
    {
        await using NpgsqlConnection conn = await source.OpenConnectionAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
        if (setup.Length > 0)
        {
            await using var s = new NpgsqlCommand(setup, conn, tx);
            _ = await s.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(query, conn, tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task The_marker_shows_unpublished_runs_across_scopes_and_nothing_else()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = database.NewStore();
        var client = new KairosClient(store, TimeProvider.System);
        _ = await client.StartAsync(A, "a-1", "t", [1]);
        _ = await client.StartAsync(B, "b-1", "t", [1]);
        const string Runs = "SELECT count(*) FROM kairos.runs";
        const string Marker = "SELECT set_config('app.sweep', 'publish-events', true)";

        Assert.Equal(0, await CountAsync(database.DataPlane, "", Runs)); // no scope set: nothing
        Assert.Equal(0, await CountAsync(database.DataPlane, "SELECT set_config('app.sweep', 'yes', true)", Runs));
        Assert.Equal(0, await CountAsync(database.DataPlane, "SELECT set_config('app.sweep', 'reconcile', true)", Runs)); // the other sweep's marker is not this one
        Assert.Equal(2, await CountAsync(database.DataPlane, Marker, Runs));
        Assert.Equal(0, await CountAsync(database.DataPlane, Marker, "SELECT count(*) FROM kairos.history_events")); // no other table opens
        Assert.Equal(0, await CountAsync(database.DataPlane, Marker, "SELECT count(*) FROM kairos.workflow_tasks"));
        Assert.Equal(0, await CountAsync(database.DataPlane, Marker, "WITH u AS (UPDATE kairos.runs SET published_seq = last_seq RETURNING 1) SELECT count(*) FROM u")); // never a write through the exception

        _ = await store.PublishEventsAsync(A, new Kairos.Cloud.SelfHosted.InMemoryEventSink(), 10, default);
        Assert.Equal(1, await CountAsync(database.DataPlane, Marker, Runs)); // published runs leave the exception
    }

    [Fact]
    public async Task The_read_only_role_gets_no_exception_and_the_store_names_scopes_without_their_runs()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = database.NewStore();
        var client = new KairosClient(store, TimeProvider.System);
        _ = await client.StartAsync(A, "a-1", "t", [1]);
        NpgsqlDataSource readOnly = database.ReadOnly;

        Assert.Equal(0, await CountAsync(readOnly, "SELECT set_config('app.sweep', 'publish-events', true)", "SELECT count(*) FROM kairos.runs"));
        Assert.Equal([A], await store.ScopesWithUnpublishedEventsAsync(10, null, default));
    }

    [Fact]
    public void Only_the_stores_sweep_query_sets_the_marker()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Kairos.Persistence.Postgres")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        string[] users = [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && File.ReadAllText(f).Contains("'publish-events'", StringComparison.Ordinal))
            .Select(Path.GetFileName)!];

        Assert.Equal(["PostgresKairosStore.Aggregates.cs"], users);
    }
}
