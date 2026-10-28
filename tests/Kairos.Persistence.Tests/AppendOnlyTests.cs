using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// docs/SECURITY-ARCHITECTURE.md calls the audit log and the history append-only. The grants are what make it so, so the test is the attempt: the data-plane role may add and read, and an update or a delete is refused by the
/// database itself (permission denied), for the audit log and for history events, inside the role's own scope where row level security would otherwise allow it.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class AppendOnlyTests(PostgresFixture fixture)
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));

    private static async Task<string?> AttemptAsync(NpgsqlDataSource source, string sql)
    {
        try
        {
            await using NpgsqlConnection conn = await source.OpenConnectionAsync();
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.namespace_id', @n, true)", conn, tx))
            {
                set.Parameters.AddWithValue("t", A.TenantId.ToString());
                set.Parameters.AddWithValue("n", A.NamespaceId.ToString());
                _ = await set.ExecuteNonQueryAsync();
            }

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            _ = await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
    }

    [Fact]
    public async Task The_audit_log_and_the_history_accept_additions_and_refuse_updates_and_deletes_from_the_data_plane_role()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = database.NewStore();
        var client = new KairosClient(store, TimeProvider.System);
        StartRunResult started = await client.StartAsync(A, "append-only", "t", [1]);
        var run = new RunKey(A.TenantId, A.NamespaceId, started.RunId);
        _ = await store.ExportHistoryAsync(run, "ops", null, "append-only test", true, TimeProvider.System.GetUtcNow(), default);

        Assert.True(await CountAsync(database.DataPlane, "kairos.audit_log") >= 1);
        Assert.True(await CountAsync(database.DataPlane, "kairos.history_events") >= 1);
        foreach (string table in new[] { "kairos.audit_log", "kairos.history_events" })
        {
            Assert.Equal("42501", await AttemptAsync(database.DataPlane, $"UPDATE {table} SET tenant_id = tenant_id"));
            Assert.Equal("42501", await AttemptAsync(database.DataPlane, $"DELETE FROM {table}"));
            Assert.Equal("42501", await AttemptAsync(database.ReadOnly, $"DELETE FROM {table}"));
        }
    }

    private static async Task<long> CountAsync(NpgsqlDataSource source, string table)
    {
        await using NpgsqlConnection conn = await source.OpenConnectionAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.namespace_id', @n, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", A.TenantId.ToString());
            set.Parameters.AddWithValue("n", A.NamespaceId.ToString());
            _ = await set.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
