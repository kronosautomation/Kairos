using Npgsql;
using NpgsqlTypes;

namespace Kairos.Persistence.Postgres;

// Aggregates the server answers without opening a payload: model usage by workflow type and day, and the approvals runs are waiting for. They are projected from each run's history by
// RunProjection (outside the hot path, whole run at a time) into kairos.usage_by_run and kairos.pending_approvals, and read from there. See 0007_aggregates.sql.
public sealed partial class PostgresKairosStore
{
    /// <summary>How many runs a read projects before it answers; the rest is left to the maintenance tick (and reported as <see cref="UsageSummary.Behind"/>).</summary>
    private const int ReadProjectionBudget = 200;

    private static readonly string[] ProjectedTables = ["kairos.usage_by_run", "kairos.pending_approvals"];

    public async ValueTask<int> ProjectAggregatesAsync(TenantScope scope, int maxRuns, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRuns, 1);
        List<(Guid Run, long Projected)> candidates = await InTransaction(scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT run_id, projected_seq FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND last_seq > projected_seq ORDER BY created_at, run_id LIMIT @limit", conn, tx);
            cmd.Parameters.AddWithValue("t", scope.TenantId);
            cmd.Parameters.AddWithValue("n", scope.NamespaceId);
            cmd.Parameters.AddWithValue("limit", maxRuns);
            var found = new List<(Guid, long)>();
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            return found;
        }, cancellationToken).ConfigureAwait(false);

        int projected = 0;
        foreach ((Guid run, long was) in candidates)
        {
            if (await ProjectRunAsync(new RunKey(scope.TenantId, scope.NamespaceId, run), was, cancellationToken).ConfigureAwait(false))
            {
                projected++;
            }
        }

        return projected;
    }

    /// <summary>
    /// Hands new events to the sink run by run: reads the run's events after <c>published_seq</c>, lets the sink accept them, and only then moves <c>published_seq</c> with a compare-and-set
    /// (a publisher that lost the race to another node moves nothing, and the consumer de-duplicates the repeat by id). No hash chain is read or checked here: a notice is metadata and says nothing a reader could not get from the history.
    /// </summary>
    public async ValueTask<int> PublishEventsAsync(TenantScope scope, IEventSink sink, int maxRuns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRuns, 1);
        List<(Guid Run, long Published)> candidates = await InTransaction(scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT run_id, published_seq FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND last_seq > published_seq ORDER BY created_at, run_id LIMIT @limit", conn, tx);
            cmd.Parameters.AddWithValue("t", scope.TenantId);
            cmd.Parameters.AddWithValue("n", scope.NamespaceId);
            cmd.Parameters.AddWithValue("limit", maxRuns);
            var found = new List<(Guid, long)>();
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            return found;
        }, cancellationToken).ConfigureAwait(false);

        int accepted = 0;
        foreach ((Guid runId, long was) in candidates)
        {
            var run = new RunKey(scope.TenantId, scope.NamespaceId, runId);
            List<HistoryEvent> events = await InTransaction(scope, async (conn, tx) =>
            {
                var read = new List<HistoryEvent>();
                await using var cmd = new NpgsqlCommand(
                    "SELECT seq, body, prev_hash, hash FROM kairos.history_events WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND seq > @after ORDER BY seq", conn, tx);
                cmd.Parameters.AddWithValue("t", scope.TenantId);
                cmd.Parameters.AddWithValue("n", scope.NamespaceId);
                cmd.Parameters.AddWithValue("r", runId);
                cmd.Parameters.AddWithValue("after", was);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    read.Add(DecodeRow(scope, runId, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)));
                }

                return read;
            }, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                continue;
            }

            await sink.PublishAsync(events.Select(e => new RunEventNotice($"{runId:D}:{e.Sequence}", run, e.Type, e.Sequence, e.Timestamp)).ToArray(), cancellationToken).ConfigureAwait(false);
            long to = events[^1].Sequence;
            await InTransaction(scope, async (conn, tx) =>
            {
                await using var advance = new NpgsqlCommand(
                    "UPDATE kairos.runs SET published_seq = @new WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND published_seq = @old AND last_seq >= @new", conn, tx);
                advance.Parameters.AddWithValue("t", scope.TenantId);
                advance.Parameters.AddWithValue("n", scope.NamespaceId);
                advance.Parameters.AddWithValue("r", runId);
                advance.Parameters.AddWithValue("old", was);
                advance.Parameters.AddWithValue("new", to);
                return await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            accepted += events.Count;
        }

        return accepted;
    }

    /// <summary>
    /// Replaces one run's rows with what its history says now. Skipped (false) when the run was projected since the caller looked (another projector got there first: the move of
    /// <c>projected_seq</c> is a compare-and-set, and the one that loses is rolled back with everything it wrote), and when its hash chain does not verify: such a run stays "behind" and
    /// an operator sees it, rather than the aggregate quietly counting a history that does not hold.
    /// </summary>
    private Task<bool> ProjectRunAsync(RunKey run, long projectedSeq, CancellationToken ct) =>
        InTransaction(run.Scope, async (conn, tx) =>
        {
            var history = new List<HistoryEvent>();
            await using (var read = new NpgsqlCommand(
                "SELECT seq, body, prev_hash, hash FROM kairos.history_events WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r ORDER BY seq", conn, tx))
            {
                read.Parameters.AddWithValue("t", run.TenantId);
                read.Parameters.AddWithValue("n", run.NamespaceId);
                read.Parameters.AddWithValue("r", run.RunId);
                await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    history.Add(DecodeRow(run.Scope, run.RunId, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)));
                }
            }

            if (history.Count == 0)
            {
                return false;
            }

            try
            {
                HashChain.Verify(history);
            }
            catch (HistoryIntegrityException)
            {
                return false;
            }

            RunProjectionResult p = RunProjection.Of(history);

            // Compare and set first: whoever moves projected_seq owns this projection; the rest is rolled back with the transaction if it does not.
            await using (var advance = new NpgsqlCommand(
                "UPDATE kairos.runs SET projected_seq = @new WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND projected_seq = @old AND last_seq >= @new", conn, tx))
            {
                advance.Parameters.AddWithValue("t", run.TenantId);
                advance.Parameters.AddWithValue("n", run.NamespaceId);
                advance.Parameters.AddWithValue("r", run.RunId);
                advance.Parameters.AddWithValue("old", projectedSeq);
                advance.Parameters.AddWithValue("new", p.LastSequence);
                if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                {
                    return false;
                }
            }

            foreach (string table in ProjectedTables)
            {
                await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r", conn, tx);
                delete.Parameters.AddWithValue("t", run.TenantId);
                delete.Parameters.AddWithValue("n", run.NamespaceId);
                delete.Parameters.AddWithValue("r", run.RunId);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (p.Usage.Count > 0)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO kairos.usage_by_run (tenant_id, namespace_id, run_id, day, model, steps, failed_attempts, input_tokens, output_tokens, cached_input_tokens,
                                                     saved_input_tokens, saved_output_tokens, saved_cached_input_tokens)
                    SELECT @t, @n, @r, u.day, u.model, u.steps, u.failed, u.input, u.output, u.cached, u.sin, u.sout, u.scached
                    FROM unnest(@days, @models, @steps, @failed, @input, @output, @cached, @sin, @sout, @scached) AS u(day, model, steps, failed, input, output, cached, sin, sout, scached)
                    """, conn, tx);
                insert.Parameters.AddWithValue("t", run.TenantId);
                insert.Parameters.AddWithValue("n", run.NamespaceId);
                insert.Parameters.AddWithValue("r", run.RunId);
                insert.Parameters.Add(Arr("days", NpgsqlDbType.Date, p.Usage.Select(u => u.Day).ToArray()));
                insert.Parameters.Add(Arr("models", NpgsqlDbType.Text, p.Usage.Select(u => u.Model).ToArray()));
                insert.Parameters.Add(Arr("steps", NpgsqlDbType.Bigint, p.Usage.Select(u => u.Steps).ToArray()));
                insert.Parameters.Add(Arr("failed", NpgsqlDbType.Bigint, p.Usage.Select(u => u.FailedAttempts).ToArray()));
                insert.Parameters.Add(Arr("input", NpgsqlDbType.Bigint, p.Usage.Select(u => u.InputTokens).ToArray()));
                insert.Parameters.Add(Arr("output", NpgsqlDbType.Bigint, p.Usage.Select(u => u.OutputTokens).ToArray()));
                insert.Parameters.Add(Arr("cached", NpgsqlDbType.Bigint, p.Usage.Select(u => u.CachedInputTokens).ToArray()));
                insert.Parameters.Add(Arr("sin", NpgsqlDbType.Bigint, p.Usage.Select(u => u.SavedInputTokens).ToArray()));
                insert.Parameters.Add(Arr("sout", NpgsqlDbType.Bigint, p.Usage.Select(u => u.SavedOutputTokens).ToArray()));
                insert.Parameters.Add(Arr("scached", NpgsqlDbType.Bigint, p.Usage.Select(u => u.SavedCachedInputTokens).ToArray()));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (p.Pending.Count > 0)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO kairos.pending_approvals (tenant_id, namespace_id, run_id, approval_id, requested_at)
                    SELECT @t, @n, @r, a.id, a.at FROM unnest(@ids, @ats) AS a(id, at)
                    """, conn, tx);
                insert.Parameters.AddWithValue("t", run.TenantId);
                insert.Parameters.AddWithValue("n", run.NamespaceId);
                insert.Parameters.AddWithValue("r", run.RunId);
                insert.Parameters.Add(Arr("ids", NpgsqlDbType.Text, p.Pending.Select(a => a.ApprovalId).ToArray()));
                insert.Parameters.Add(Arr("ats", NpgsqlDbType.TimestampTz, p.Pending.Select(a => a.RequestedAt.UtcDateTime).ToArray()));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return true;
        }, ct);

    private static async Task<int> CountBehindAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND last_seq > projected_seq", conn, tx);
        cmd.Parameters.AddWithValue("t", scope.TenantId);
        cmd.Parameters.AddWithValue("n", scope.NamespaceId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }

    public async ValueTask<UsageSummary> GetUsageSummaryAsync(TenantScope scope, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        await ProjectAggregatesAsync(scope, ReadProjectionBudget, cancellationToken).ConfigureAwait(false);
        return await InTransaction(scope, async (conn, tx) =>
        {
            var buckets = new List<UsageBucket>();
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT r.workflow_type, u.day, u.model, sum(u.steps), sum(u.failed_attempts), sum(u.input_tokens), sum(u.output_tokens), sum(u.cached_input_tokens),
                       sum(u.saved_input_tokens), sum(u.saved_output_tokens), sum(u.saved_cached_input_tokens)
                FROM kairos.usage_by_run u
                JOIN kairos.runs r ON r.tenant_id = u.tenant_id AND r.namespace_id = u.namespace_id AND r.run_id = u.run_id
                WHERE u.tenant_id = @t AND u.namespace_id = @n AND u.day >= @from AND u.day <= @to
                GROUP BY r.workflow_type, u.day, u.model
                ORDER BY r.workflow_type COLLATE "C", u.day, u.model COLLATE "C"
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("t", scope.TenantId);
                cmd.Parameters.AddWithValue("n", scope.NamespaceId);
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = firstDay });
                cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = lastDay });
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    buckets.Add(new UsageBucket(
                        reader.GetString(0), reader.GetFieldValue<DateOnly>(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                        reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10)));
                }
            }

            var runs = new List<WorkflowTypeRuns>();
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT r.workflow_type, count(DISTINCT u.run_id)
                FROM kairos.usage_by_run u
                JOIN kairos.runs r ON r.tenant_id = u.tenant_id AND r.namespace_id = u.namespace_id AND r.run_id = u.run_id
                WHERE u.tenant_id = @t AND u.namespace_id = @n AND u.day >= @from AND u.day <= @to
                GROUP BY r.workflow_type
                ORDER BY r.workflow_type COLLATE "C"
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("t", scope.TenantId);
                cmd.Parameters.AddWithValue("n", scope.NamespaceId);
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = firstDay });
                cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = lastDay });
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    runs.Add(new WorkflowTypeRuns(reader.GetString(0), reader.GetInt64(1)));
                }
            }

            return new UsageSummary(buckets, runs, await CountBehindAsync(conn, tx, scope, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PendingApprovalPage> GetPendingApprovalsAsync(TenantScope scope, int limit, PendingApprovalCursor? after, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await ProjectAggregatesAsync(scope, ReadProjectionBudget, cancellationToken).ConfigureAwait(false);
        return await InTransaction(scope, async (conn, tx) =>
        {
            var items = new List<PendingApprovalItem>();
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT p.run_id, r.workflow_type, p.approval_id, p.requested_at
                FROM kairos.pending_approvals p
                JOIN kairos.runs r ON r.tenant_id = p.tenant_id AND r.namespace_id = p.namespace_id AND r.run_id = p.run_id
                WHERE p.tenant_id = @t AND p.namespace_id = @n
                  AND (@hasafter = false OR p.requested_at > @afterat OR (p.requested_at = @afterat AND p.approval_id COLLATE "C" > @afterid COLLATE "C"))
                ORDER BY p.requested_at, p.approval_id COLLATE "C"
                LIMIT @limit
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("t", scope.TenantId);
                cmd.Parameters.AddWithValue("n", scope.NamespaceId);
                cmd.Parameters.AddWithValue("hasafter", after is not null);
                cmd.Parameters.Add(new NpgsqlParameter("afterat", NpgsqlDbType.TimestampTz) { Value = after?.RequestedAt.UtcDateTime ?? DateTime.UnixEpoch });
                cmd.Parameters.Add(new NpgsqlParameter("afterid", NpgsqlDbType.Text) { Value = after?.ApprovalId ?? "" });
                cmd.Parameters.AddWithValue("limit", limit);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(new PendingApprovalItem(
                        new RunKey(scope.TenantId, scope.NamespaceId, reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
                }
            }

            int total;
            await using (var count = new NpgsqlCommand("SELECT count(*) FROM kairos.pending_approvals WHERE tenant_id = @t AND namespace_id = @n", conn, tx))
            {
                count.Parameters.AddWithValue("t", scope.TenantId);
                count.Parameters.AddWithValue("n", scope.NamespaceId);
                total = (int)(long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            }

            return new PendingApprovalPage(items, total, await CountBehindAsync(conn, tx, scope, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
    }
}
