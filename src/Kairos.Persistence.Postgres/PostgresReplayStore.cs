using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// The shared <see cref="IReplayStore"/>: one row per accepted credential in <c>kairos.sign_in_replays</c>, under row level security like every other table, so that every server instance of a deployment refuses a credential
/// that any of them accepted. Adding is one atomic statement (the primary key decides which of several concurrent presentations wins), an expired row is replaced in place, and each call forgets a bounded number of
/// expired rows of its own scope. The database is the only thing the instances share, so no other agreement between them is needed.
/// </summary>
public sealed class PostgresReplayStore(NpgsqlDataSource dataSource) : IReplayStore
{
    private const string SetScopeSql = "SELECT set_config('app.tenant_id', @scope_t, true), set_config('app.namespace_id', @scope_n, true)";

    private const string AddSql = """
        INSERT INTO kairos.sign_in_replays (tenant_id, namespace_id, token_hash, expires_at)
        VALUES (@t, @n, @h, @exp)
        ON CONFLICT (tenant_id, namespace_id, token_hash) DO UPDATE SET expires_at = EXCLUDED.expires_at
            WHERE kairos.sign_in_replays.expires_at <= @now
        RETURNING 1
        """;

    private const string ForgetSql = """
        DELETE FROM kairos.sign_in_replays
        WHERE ctid IN (SELECT ctid FROM kairos.sign_in_replays WHERE tenant_id = @t AND namespace_id = @n AND expires_at <= @now LIMIT 100)
        """;

    public async ValueTask<ReplayDecision> TryAddAsync(TenantScope scope, string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var set = new NpgsqlCommand(SetScopeSql, connection, transaction))
        {
            set.Parameters.AddWithValue("scope_t", scope.TenantId.ToString("D"));
            set.Parameters.AddWithValue("scope_n", scope.NamespaceId.ToString("D"));
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        bool added;
        await using (var add = new NpgsqlCommand(AddSql, connection, transaction))
        {
            add.Parameters.AddWithValue("t", scope.TenantId);
            add.Parameters.AddWithValue("n", scope.NamespaceId);
            add.Parameters.AddWithValue("h", SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            add.Parameters.AddWithValue("exp", expiresAt.UtcDateTime);
            add.Parameters.AddWithValue("now", now.UtcDateTime);
            added = await add.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        if (added)
        {
            await using var forget = new NpgsqlCommand(ForgetSql, connection, transaction);
            forget.Parameters.AddWithValue("t", scope.TenantId);
            forget.Parameters.AddWithValue("n", scope.NamespaceId);
            forget.Parameters.AddWithValue("now", now.UtcDateTime);
            await forget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return added ? ReplayDecision.Added : ReplayDecision.Duplicate;
    }
}
