using Npgsql;

namespace Kairos.Persistence.Postgres;

public sealed partial class PostgresKairosStore
{
    public async ValueTask NoteWorkerPayloadProtectionAsync(TenantScope scope, bool payloadsProtected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Only the latest time of each kind is kept: GREATEST, so a late report from a slow node never moves the time backwards.
        string column = payloadsProtected ? "protected_seen_at" : "readable_seen_at";
        string sql = $"""
            INSERT INTO kairos.worker_posture (tenant_id, namespace_id, {column}) VALUES (@t, @n, @now)
            ON CONFLICT (tenant_id, namespace_id) DO UPDATE SET {column} = GREATEST(kairos.worker_posture.{column}, EXCLUDED.{column})
            """;
        _ = await InTransaction(scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("t", scope.TenantId);
            cmd.Parameters.AddWithValue("n", scope.NamespaceId);
            cmd.Parameters.Add(Ts("now", now));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask NoteWorkerUnverifiedApproversAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _ = await InTransaction(scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO kairos.worker_posture (tenant_id, namespace_id, unverified_approvers_seen_at) VALUES (@t, @n, @now)
                ON CONFLICT (tenant_id, namespace_id) DO UPDATE SET unverified_approvers_seen_at = GREATEST(kairos.worker_posture.unverified_approvers_seen_at, EXCLUDED.unverified_approvers_seen_at)
                """, conn, tx);
            cmd.Parameters.AddWithValue("t", scope.TenantId);
            cmd.Parameters.AddWithValue("n", scope.NamespaceId);
            cmd.Parameters.Add(Ts("now", now));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkerPosture> GetWorkerPostureAsync(TenantScope scope, CancellationToken cancellationToken) =>
        await InTransaction(scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT readable_seen_at, protected_seen_at, unverified_approvers_seen_at FROM kairos.worker_posture WHERE tenant_id = @t AND namespace_id = @n", conn, tx);
            cmd.Parameters.AddWithValue("t", scope.TenantId);
            cmd.Parameters.AddWithValue("n", scope.NamespaceId);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new WorkerPosture(null, null);
            }

            return new WorkerPosture(
                reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
        }, cancellationToken).ConfigureAwait(false);
}
