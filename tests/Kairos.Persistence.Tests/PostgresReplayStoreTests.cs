using Kairos.Conformance;
using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>The shared replay store on PostgreSQL passes the same suite as the in-memory one, and "another instance" is a second connection pool on the same database: another node.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresReplayStoreConformanceTests(PostgresFixture fixture) : ReplayStoreConformance, IAsyncLifetime
{
    private TestDatabase? _database;
    private readonly List<NpgsqlDataSource> _others = [];

    public async Task InitializeAsync() => _database = await fixture.NewDatabaseAsync();

    public async Task DisposeAsync()
    {
        foreach (NpgsqlDataSource ds in _others)
        {
            await ds.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    protected override IReplayStore CreateStore() => new PostgresReplayStore(_database!.DataPlane);

    protected override IReplayStore? CreateOtherInstance(IReplayStore first)
    {
        NpgsqlDataSource other = NpgsqlDataSource.Create(_database!.DataPlaneConnectionString);
        _others.Add(other);
        return new PostgresReplayStore(other);
    }
}

/// <summary>Row level security on <c>kairos.sign_in_replays</c>: one scope's accepted credentials are invisible to, and untouchable by, another scope, and with no scope set nothing is visible.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresReplayStoreIsolationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    private static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql, TenantScope? scope)
    {
        await using NpgsqlConnection conn = await ds.OpenConnectionAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
        if (scope is { } s)
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.namespace_id', @n, true)", conn, tx);
            set.Parameters.AddWithValue("t", s.TenantId.ToString());
            set.Parameters.AddWithValue("n", s.NamespaceId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task One_scopes_accepted_credentials_are_invisible_to_another_scope_and_to_no_scope()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var store = new PostgresReplayStore(db.DataPlane);
        await store.TryAddAsync(A, "credential-1", Now.AddMinutes(5), Now, default);

        Assert.Equal(1, await ScalarAsync(db.DataPlane, "SELECT count(*) FROM kairos.sign_in_replays", A));
        Assert.Equal(0, await ScalarAsync(db.DataPlane, "SELECT count(*) FROM kairos.sign_in_replays", B));
        Assert.Equal(0, await ScalarAsync(db.DataPlane, "SELECT count(*) FROM kairos.sign_in_replays", null)); // an unset scope matches nothing
        Assert.Equal(1, await ScalarAsync(db.Superuser, "SELECT count(*) FROM kairos.sign_in_replays", null));
    }

    [Fact]
    public async Task Another_scope_can_neither_write_into_nor_delete_nor_change_a_scopes_entries()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await new PostgresReplayStore(db.DataPlane).TryAddAsync(A, "credential-1", Now.AddMinutes(5), Now, default);

        NpgsqlException forged = await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await ScalarAsync(db.DataPlane,
            $"INSERT INTO kairos.sign_in_replays (tenant_id, namespace_id, token_hash, expires_at) VALUES ('{A.TenantId}', '{A.NamespaceId}', '\\x00'::bytea, now()) RETURNING 1", B));
        Assert.Equal("42501", (forged as PostgresException)?.SqlState); // row-level security refuses a row of a scope that is not the current one
        Assert.Equal(0, await ScalarAsync(db.DataPlane, "WITH d AS (DELETE FROM kairos.sign_in_replays RETURNING 1) SELECT count(*) FROM d", B));
        Assert.Equal(0, await ScalarAsync(db.DataPlane, "WITH u AS (UPDATE kairos.sign_in_replays SET expires_at = now() RETURNING 1) SELECT count(*) FROM u", B));
        Assert.Equal(1, await ScalarAsync(db.Superuser, "SELECT count(*) FROM kairos.sign_in_replays", null)); // untouched
    }

    [Fact]
    public async Task The_table_holds_a_hash_of_the_key_and_never_the_key()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await new PostgresReplayStore(db.DataPlane).TryAddAsync(A, "a-secret-looking-credential-key", Now.AddMinutes(5), Now, default);

        Assert.Equal(32, await ScalarAsync(db.Superuser, "SELECT octet_length(token_hash) FROM kairos.sign_in_replays", null));
        Assert.Equal(0, await ScalarAsync(db.Superuser, "SELECT count(*) FROM kairos.sign_in_replays WHERE encode(token_hash, 'escape') LIKE '%credential%'", null));
    }

    [Fact]
    public async Task Expired_entries_of_the_scope_are_forgotten_when_a_new_one_is_added_and_other_scopes_are_left_alone()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var store = new PostgresReplayStore(db.DataPlane);
        for (int i = 0; i < 5; i++)
        {
            await store.TryAddAsync(A, "old-" + i, Now.AddMinutes(1), Now, default);
        }

        await store.TryAddAsync(B, "other-scope-old", Now.AddMinutes(1), Now, default);
        await store.TryAddAsync(A, "new", Now.AddMinutes(20), Now.AddMinutes(10), default);

        Assert.Equal(1, await ScalarAsync(db.Superuser, $"SELECT count(*) FROM kairos.sign_in_replays WHERE tenant_id = '{A.TenantId}'", null));
        Assert.Equal(1, await ScalarAsync(db.Superuser, $"SELECT count(*) FROM kairos.sign_in_replays WHERE tenant_id = '{B.TenantId}'", null)); // forgotten when B next adds one
    }
}
