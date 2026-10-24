using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>I5 at the database layer: row level security and role separation hold even when application code is wrong.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class SecurityTests(PostgresFixture fixture)
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));
    private static readonly string[] DataTables = ["runs", "history_events", "workflow_tasks", "activity_tasks", "timers"];

    private static async Task<long> Count(NpgsqlDataSource ds, string table, TenantScope? scope)
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

        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM kairos.{table}", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> SqlState(NpgsqlDataSource ds, string sql, TenantScope? scope = null)
    {
        try
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
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task SeedAsync(TestDatabase db)
    {
        var time = new VirtualTimeProvider();
        var client = new KairosClient(db.NewStore(), time);
        foreach (TenantScope scope in new[] { A, B })
        {
            await client.StartAsync(scope, "wf-" + scope.TenantId, "wf", [1, 2, 3]);
        }
    }

    [Fact]
    public async Task Tables_are_invisible_without_a_tenant_scope_and_to_the_wrong_scope()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await SeedAsync(db);

        foreach (string table in DataTables.Where(t => t is "runs" or "history_events" or "workflow_tasks"))
        {
            Assert.Equal(2, await Count(db.Superuser, table, null)); // the rows exist
            Assert.Equal(0, await Count(db.DataPlane, table, null)); // forgotten scope: nothing, not everything
            Assert.Equal(1, await Count(db.DataPlane, table, A));
            Assert.Equal(1, await Count(db.DataPlane, table, B));
            Assert.Equal(0, await Count(db.DataPlane, table, new TenantScope(A.TenantId, B.NamespaceId))); // right tenant, wrong namespace
            Assert.Equal(0, await Count(db.DataPlane, table, new TenantScope(B.TenantId, A.NamespaceId)));
            Assert.Equal(0, await Count(db.ReadOnly, table, null));
            Assert.Equal(1, await Count(db.ReadOnly, table, A));
        }
    }

    [Fact]
    public async Task Row_level_security_is_forced_even_for_the_table_owner()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await SeedAsync(db);

        Assert.Equal(0, await Count(db.Migrator, "runs", null)); // owner, but FORCE ROW LEVEL SECURITY
        Assert.Equal(1, await Count(db.Migrator, "runs", A));
    }

    [Fact]
    public async Task Writes_cannot_cross_tenants()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await SeedAsync(db);

        string insertAsB = $"""
            INSERT INTO kairos.timers (tenant_id, namespace_id, run_id, timer_id, due_at)
            VALUES ('{B.TenantId}', '{B.NamespaceId}', gen_random_uuid(), 1, now())
            """;
        Assert.Equal("42501", await SqlState(db.DataPlane, insertAsB, A)); // row-level security policy violation

        await using (NpgsqlConnection conn = await db.DataPlane.OpenConnectionAsync())
        await using (NpgsqlTransaction tx = await conn.BeginTransactionAsync())
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.namespace_id', @n, true)", conn, tx);
            set.Parameters.AddWithValue("t", A.TenantId.ToString());
            set.Parameters.AddWithValue("n", A.NamespaceId.ToString());
            await set.ExecuteNonQueryAsync();

            await using var update = new NpgsqlCommand("UPDATE kairos.runs SET build_id = 'hijacked'", conn, tx);
            Assert.Equal(1, await update.ExecuteNonQueryAsync()); // only A's row
            await using var delete = new NpgsqlCommand($"DELETE FROM kairos.runs WHERE tenant_id = '{B.TenantId}'", conn, tx);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        }

        await using NpgsqlCommand check = db.Superuser.CreateCommand($"SELECT build_id FROM kairos.runs WHERE tenant_id = '{B.TenantId}'");
        Assert.Equal("default", await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Every_kairos_table_enforces_row_level_security_and_carries_tenant_columns()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();

        await using NpgsqlCommand tables = db.Superuser.CreateCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped) AS has_tenant,
                   EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'namespace_id' AND NOT a.attisdropped) AS has_ns
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'kairos' AND c.relkind IN ('r', 'p') AND NOT c.relispartition AND c.relname <> 'schema_migrations'
            """);
        var seen = new List<string>();
        await using NpgsqlDataReader reader = await tables.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string name = reader.GetString(0);
            seen.Add(name);
            Assert.True(reader.GetBoolean(1) && reader.GetBoolean(2), $"kairos.{name} must ENABLE and FORCE row level security.");
            Assert.True(reader.GetBoolean(3), $"kairos.{name} needs tenant_id.");
            if (name != "tenants")
            {
                Assert.True(reader.GetBoolean(4), $"kairos.{name} needs namespace_id.");
            }
        }

        Assert.True(seen.Count >= 7, $"Expected the full schema, saw: {string.Join(", ", seen)}");
    }

    [Fact]
    public async Task Service_roles_cannot_bypass_rls_or_escalate()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();

        await using NpgsqlCommand roles = db.Superuser.CreateCommand(
            "SELECT rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname LIKE 'kairos\\_%'");
        int count = 0;
        await using NpgsqlDataReader reader = await roles.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            count++;
            Assert.False(reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4), $"{reader.GetString(0)} is over-privileged.");
        }

        Assert.Equal(4, count);
    }

    [Fact]
    public async Task Data_plane_role_cannot_run_ddl_rewrite_history_or_read_control_plane_tables()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await SeedAsync(db);

        Assert.Equal("42501", await SqlState(db.DataPlane, "CREATE TABLE kairos.evil (x int)"));
        Assert.Equal("42501", await SqlState(db.DataPlane, "CREATE TABLE public.evil (x int)"));
        Assert.Equal("42501", await SqlState(db.DataPlane, "DROP TABLE kairos.runs"));
        Assert.Equal("42501", await SqlState(db.DataPlane, "ALTER TABLE kairos.runs DISABLE ROW LEVEL SECURITY"));
        Assert.Equal("42501", await SqlState(db.DataPlane, "TRUNCATE kairos.history_events"));
        Assert.Equal("42501", await SqlState(db.DataPlane, "UPDATE kairos.history_events SET body = '\\x00'", A)); // append-only
        Assert.Equal("42501", await SqlState(db.DataPlane, "DELETE FROM kairos.history_events", A));
        Assert.Equal("42501", await SqlState(db.DataPlane, "SELECT * FROM kairos.tenants"));
        Assert.Equal("42501", await SqlState(db.DataPlane, "INSERT INTO kairos.tenants (tenant_id, name) VALUES (gen_random_uuid(), 'x')"));
    }

    [Fact]
    public async Task Control_plane_role_cannot_read_workflow_data()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await SeedAsync(db);

        foreach (string table in DataTables)
        {
            Assert.Equal("42501", await SqlState(db.ControlPlane, $"SELECT * FROM kairos.{table}", A));
        }

        Assert.Null(await SqlState(db.ControlPlane, "INSERT INTO kairos.tenants (tenant_id, name) VALUES (gen_random_uuid(), 'acme')"));
        Assert.Null(await SqlState(db.ControlPlane, "SELECT * FROM kairos.tenants"));
        Assert.Equal("42501", await SqlState(db.ControlPlane, "CREATE TABLE kairos.evil (x int)"));
    }

    [Fact]
    public async Task Read_only_role_cannot_write()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await SeedAsync(db);

        Assert.Equal("42501", await SqlState(db.ReadOnly, "UPDATE kairos.runs SET state = 2", A));
        Assert.Equal("42501", await SqlState(db.ReadOnly, "DELETE FROM kairos.runs", A));
        Assert.Equal("42501", await SqlState(db.ReadOnly, $"INSERT INTO kairos.timers VALUES ('{A.TenantId}', '{A.NamespaceId}', gen_random_uuid(), 1, now())", A));
    }
}
