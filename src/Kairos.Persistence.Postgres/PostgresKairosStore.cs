using Npgsql;
using NpgsqlTypes;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// <see cref="IKairosStore"/> on PostgreSQL. Connect as <c>kairos_dataplane</c>: every statement runs in a transaction that first
/// sets <c>app.tenant_id</c>/<c>app.namespace_id</c> (<c>set_config(..., true)</c> = <c>SET LOCAL</c>), so row level security,
/// not just the WHERE clauses below, confines it to one tenant and namespace.
/// The hot path (completing a workflow task) is a single statement in a single round trip; the rest use short explicit
/// transactions. Deadlocks and serialization failures are retried.
/// </summary>
public sealed class PostgresKairosStore(NpgsqlDataSource dataSource) : IKairosStore
{
    public const string NotifyChannel = "kairos_work";

    private const int MaxAttempts = 4;
    private const string SetScopeSql = "SELECT set_config('app.tenant_id', @scope_t, true), set_config('app.namespace_id', @scope_n, true)";
    private static readonly byte[] NoHash = [];

    // ---- starting and signalling ------------------------------------------------------------------------------

    public async ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = new WorkflowStarted(
            request.Run.TenantId, request.Run.NamespaceId, request.Run.RunId, 0, request.Now, request.WorkflowType, request.BuildId, request.Input);
        HistoryEvent first = StoreRules.Seal(started, 1, HashChain.GenesisHash);
        byte[] body = HistoryEventCodec.Encode(first);

        const string Sql = """
            WITH r AS (
                INSERT INTO kairos.runs (tenant_id, namespace_id, run_id, workflow_id, workflow_type, build_id, state, last_seq, last_hash, created_at)
                VALUES (@t, @n, @r, @wf, @wt, @b, 0, 1, @h, @now)
                ON CONFLICT (tenant_id, namespace_id, workflow_id) DO NOTHING
                RETURNING run_id),
            e AS (
                INSERT INTO kairos.history_events (tenant_id, namespace_id, run_id, seq, body, prev_hash, hash)
                SELECT @t, @n, @r, 1, @body, @prev, @h WHERE EXISTS (SELECT 1 FROM r) RETURNING 1),
            w AS (
                INSERT INTO kairos.workflow_tasks (tenant_id, namespace_id, run_id, build_id, visible_at)
                SELECT @t, @n, @r, @b, @now WHERE EXISTS (SELECT 1 FROM r) RETURNING 1)
            SELECT run_id, true FROM r
            UNION ALL
            SELECT run_id, false FROM kairos.runs
            WHERE tenant_id = @t AND namespace_id = @n AND workflow_id = @wf AND NOT EXISTS (SELECT 1 FROM r)
            """;

        return await Retry(async () =>
        {
            await using NpgsqlBatch batch = Batch(request.Run.Scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = Sql;
            Scope(cmd, request.Run.Scope);
            cmd.Parameters.AddWithValue("r", request.Run.RunId);
            cmd.Parameters.AddWithValue("wf", request.WorkflowId);
            cmd.Parameters.AddWithValue("wt", request.WorkflowType);
            cmd.Parameters.AddWithValue("b", request.BuildId);
            cmd.Parameters.AddWithValue("h", first.Hash);
            cmd.Parameters.AddWithValue("prev", first.PrevHash);
            cmd.Parameters.AddWithValue("body", body);
            cmd.Parameters.Add(Ts("now", request.Now));
            batch.BatchCommands.Add(cmd);
            batch.BatchCommands.Add(Notify(request.Run.Scope));

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false); // skip set_config
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new StartRunResult(reader.GetGuid(0), reader.GetBoolean(1));
            }

            // A concurrent starter of the same workflow id committed after our snapshot: look it up.
            await reader.CloseAsync().ConfigureAwait(false);
            await using NpgsqlCommand find = new(
                "SELECT run_id FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND workflow_id = @wf", conn);
            find.Parameters.AddWithValue("t", request.Run.TenantId);
            find.Parameters.AddWithValue("n", request.Run.NamespaceId);
            find.Parameters.AddWithValue("wf", request.WorkflowId);
            return await InScope(conn, request.Run.Scope, async tx =>
            {
                find.Transaction = tx;
                object? id = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return id is Guid g ? new StartRunResult(g, false) : throw new InvalidOperationException("Run vanished after conflict.");
            }, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask<bool> SignalRunAsync(RunKey run, string signalName, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await InTransaction(run.Scope, async (conn, tx) =>
        {
            RunHead? head = await LockRunAsync(conn, tx, run, cancellationToken).ConfigureAwait(false);
            if (head is not { State: 0 })
            {
                return false;
            }

            var template = new SignalReceived(run.TenantId, run.NamespaceId, run.RunId, 0, now, signalName, payload);
            await AppendAsync(conn, tx, run, head, [template], cancellationToken).ConfigureAwait(false);
            await EnsureWorkflowTaskAsync(conn, tx, run, now, cancellationToken).ConfigureAwait(false);
            await NotifyAsync(conn, tx, run.Scope, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- workflow tasks ---------------------------------------------------------------------------------------

    public async ValueTask<WorkflowTask?> PollWorkflowTaskAsync(
        TenantScope scope, IReadOnlyCollection<string> buildIds, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildIds);
        const string Sql = """
            WITH c AS (
                SELECT run_id FROM kairos.workflow_tasks
                WHERE tenant_id = @t AND namespace_id = @n AND build_id = ANY(@builds)
                  AND ((NOT leased AND visible_at <= @now) OR (leased AND lease_expires_at <= @now))
                ORDER BY ord LIMIT 1
                FOR UPDATE SKIP LOCKED),
            u AS (
                UPDATE kairos.workflow_tasks wt SET leased = true, lease_expires_at = @expires
                FROM c WHERE wt.tenant_id = @t AND wt.namespace_id = @n AND wt.run_id = c.run_id
                RETURNING wt.run_id, wt.attempt),
            r AS (
                UPDATE kairos.runs SET wf_lease_token = wf_lease_token + 1
                FROM u WHERE runs.tenant_id = @t AND runs.namespace_id = @n AND runs.run_id = u.run_id
                RETURNING runs.run_id, runs.wf_lease_token, runs.workflow_type, runs.build_id)
            SELECT r.run_id, r.wf_lease_token, r.workflow_type, r.build_id, u.attempt, h.seq, h.body, h.prev_hash, h.hash
            FROM r JOIN u ON u.run_id = r.run_id
            JOIN kairos.history_events h ON h.tenant_id = @t AND h.namespace_id = @n AND h.run_id = r.run_id
            ORDER BY h.seq
            """;

        return await Retry<WorkflowTask?>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = Sql;
            Scope(cmd, scope);
            cmd.Parameters.Add(new NpgsqlParameter("builds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = buildIds.ToArray() });
            cmd.Parameters.Add(Ts("now", now));
            cmd.Parameters.Add(Ts("expires", now + lease));
            batch.BatchCommands.Add(cmd);

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);

            Guid runId = default;
            long token = 0;
            string type = "", build = "";
            int attempt = 0;
            var history = new List<HistoryEvent>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (history.Count == 0)
                {
                    runId = reader.GetGuid(0);
                    token = reader.GetInt64(1);
                    type = reader.GetString(2);
                    build = reader.GetString(3);
                    attempt = reader.GetInt32(4);
                }

                history.Add(DecodeRow(scope, runId, reader.GetInt64(5), reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7), reader.GetFieldValue<byte[]>(8)));
            }

            return history.Count == 0
                ? null
                : new WorkflowTask(new RunKey(scope.TenantId, scope.NamespaceId, runId), type, build, token, attempt, history);
        }).ConfigureAwait(false);
    }

    public async ValueTask<CompletionOutcome> CompleteWorkflowTaskAsync(
        WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        StoreRules.ValidateWorkflowTaskEvents(task.Run, newEvents);
        RunKey run = task.Run;
        HistoryEvent head = task.History[^1];

        // Seal the boundary and the command events as one chain extension from the snapshot the worker replayed.
        var sealedEvents = new List<HistoryEvent>(newEvents.Count + 1);
        long seq = head.Sequence;
        byte[] prev = head.Hash;
        var boundary = new WorkflowTaskCompleted(run.TenantId, run.NamespaceId, run.RunId, 0, now);
        foreach (HistoryEvent template in Prepend(boundary, newEvents))
        {
            HistoryEvent s = StoreRules.Seal(template, ++seq, prev);
            sealedEvents.Add(s);
            prev = s.Hash;
        }

        int state = 0;
        var actIds = new List<int>();
        var actBodies = new List<byte[]>();
        var timerIds = new List<int>();
        var timerDue = new List<DateTime>();
        foreach (HistoryEvent e in sealedEvents)
        {
            switch (e)
            {
                case WorkflowCompleted:
                    state = 1;
                    break;
                case WorkflowFailed:
                    state = 2;
                    break;
                case ActivityScheduled s:
                    actIds.Add(s.ActivityId);
                    actBodies.Add(HistoryEventCodec.Encode(s));
                    break;
                case TimerStarted t:
                    timerIds.Add(t.TimerId);
                    timerDue.Add((t.Timestamp + t.Duration).UtcDateTime);
                    break;
            }
        }

        if (state != 0)
        {
            // A run that closes in this task leaves no follow-up work behind.
            actIds.Clear();
            actBodies.Clear();
            timerIds.Clear();
            timerDue.Clear();
        }

        const string Sql = """
            WITH guard AS (
                UPDATE kairos.runs SET last_seq = @lastseq, last_hash = @lasthash, state = @state
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r
                  AND wf_lease_token = @token AND last_seq = @headseq AND last_hash = @headhash AND state = 0
                  AND EXISTS (SELECT 1 FROM kairos.workflow_tasks w
                              WHERE w.tenant_id = @t AND w.namespace_id = @n AND w.run_id = @r AND w.leased)
                RETURNING 1),
            ev AS (
                INSERT INTO kairos.history_events (tenant_id, namespace_id, run_id, seq, body, prev_hash, hash)
                SELECT @t, @n, @r, s.seq, s.body, s.prev, s.hash
                FROM unnest(@seqs, @bodies, @prevs, @hashes) AS s(seq, body, prev, hash)
                WHERE EXISTS (SELECT 1 FROM guard) RETURNING 1),
            act AS (
                INSERT INTO kairos.activity_tasks (tenant_id, namespace_id, run_id, activity_id, scheduled_body, visible_at)
                SELECT @t, @n, @r, a.id, a.body, @now FROM unnest(@actids, @actbodies) AS a(id, body)
                WHERE EXISTS (SELECT 1 FROM guard) RETURNING 1),
            tm AS (
                INSERT INTO kairos.timers (tenant_id, namespace_id, run_id, timer_id, due_at)
                SELECT @t, @n, @r, x.id, x.due FROM unnest(@timerids, @timerdue) AS x(id, due)
                WHERE EXISTS (SELECT 1 FROM guard) RETURNING 1),
            wt AS (
                DELETE FROM kairos.workflow_tasks
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND EXISTS (SELECT 1 FROM guard) RETURNING 1),
            ca AS (
                DELETE FROM kairos.activity_tasks
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND @state <> 0 AND EXISTS (SELECT 1 FROM guard) RETURNING 1),
            ct AS (
                DELETE FROM kairos.timers
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND @state <> 0 AND EXISTS (SELECT 1 FROM guard) RETURNING 1)
            SELECT count(*) FROM guard
            """;

        long applied = await Retry(async () =>
        {
            await using NpgsqlBatch batch = Batch(run.Scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = Sql;
            Scope(cmd, run.Scope);
            cmd.Parameters.AddWithValue("r", run.RunId);
            cmd.Parameters.AddWithValue("token", task.LeaseToken);
            cmd.Parameters.AddWithValue("headseq", head.Sequence);
            cmd.Parameters.AddWithValue("headhash", head.Hash);
            cmd.Parameters.AddWithValue("lastseq", sealedEvents[^1].Sequence);
            cmd.Parameters.AddWithValue("lasthash", sealedEvents[^1].Hash);
            cmd.Parameters.AddWithValue("state", (short)state);
            cmd.Parameters.Add(Ts("now", now));
            cmd.Parameters.Add(Arr("seqs", NpgsqlDbType.Bigint, sealedEvents.Select(e => e.Sequence).ToArray()));
            cmd.Parameters.Add(Arr("bodies", NpgsqlDbType.Bytea, sealedEvents.Select(HistoryEventCodec.Encode).ToArray()));
            cmd.Parameters.Add(Arr("prevs", NpgsqlDbType.Bytea, sealedEvents.Select(e => e.PrevHash).ToArray()));
            cmd.Parameters.Add(Arr("hashes", NpgsqlDbType.Bytea, sealedEvents.Select(e => e.Hash).ToArray()));
            cmd.Parameters.Add(Arr("actids", NpgsqlDbType.Integer, actIds.ToArray()));
            cmd.Parameters.Add(Arr("actbodies", NpgsqlDbType.Bytea, actBodies.ToArray()));
            cmd.Parameters.Add(Arr("timerids", NpgsqlDbType.Integer, timerIds.ToArray()));
            cmd.Parameters.Add(Arr("timerdue", NpgsqlDbType.TimestampTz, timerDue.ToArray()));
            batch.BatchCommands.Add(cmd);
            if (actIds.Count > 0)
            {
                batch.BatchCommands.Add(Notify(run.Scope));
            }

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return reader.GetInt64(0);
        }).ConfigureAwait(false);

        if (applied == 1)
        {
            return CompletionOutcome.Applied;
        }

        // Rare path: work out why, and release the task for a re-run when only history moved.
        return await InTransaction(run.Scope, async (conn, tx) =>
        {
            await using var q = new NpgsqlCommand(
                """
                SELECT r.wf_lease_token, r.state, w.leased
                FROM kairos.runs r JOIN kairos.workflow_tasks w
                  ON w.tenant_id = r.tenant_id AND w.namespace_id = r.namespace_id AND w.run_id = r.run_id
                WHERE r.tenant_id = @t AND r.namespace_id = @n AND r.run_id = @r
                FOR UPDATE OF w
                """, conn, tx);
            q.Parameters.AddWithValue("t", run.TenantId);
            q.Parameters.AddWithValue("n", run.NamespaceId);
            q.Parameters.AddWithValue("r", run.RunId);
            bool ours;
            await using (NpgsqlDataReader reader = await q.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                ours = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                    reader.GetInt64(0) == task.LeaseToken && reader.GetInt16(1) == 0 && reader.GetBoolean(2);
            }

            if (!ours)
            {
                return CompletionOutcome.StaleLease;
            }

            await using var release = new NpgsqlCommand(
                "UPDATE kairos.workflow_tasks SET leased = false, visible_at = @now WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r", conn, tx);
            release.Parameters.AddWithValue("t", run.TenantId);
            release.Parameters.AddWithValue("n", run.NamespaceId);
            release.Parameters.AddWithValue("r", run.RunId);
            release.Parameters.Add(Ts("now", now));
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return CompletionOutcome.SequenceConflict;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseWorkflowTaskAsync(WorkflowTask task, DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await InTransaction(task.Run.Scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE kairos.workflow_tasks w SET leased = false, visible_at = @at, attempt = attempt + 1
                WHERE w.tenant_id = @t AND w.namespace_id = @n AND w.run_id = @r AND w.leased
                  AND EXISTS (SELECT 1 FROM kairos.runs r WHERE r.tenant_id = @t AND r.namespace_id = @n AND r.run_id = @r AND r.wf_lease_token = @token)
                """, conn, tx);
            cmd.Parameters.AddWithValue("t", task.Run.TenantId);
            cmd.Parameters.AddWithValue("n", task.Run.NamespaceId);
            cmd.Parameters.AddWithValue("r", task.Run.RunId);
            cmd.Parameters.AddWithValue("token", task.LeaseToken);
            cmd.Parameters.Add(Ts("at", retryAt));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- activity tasks ---------------------------------------------------------------------------------------

    public async ValueTask<ActivityTask?> PollActivityTaskAsync(TenantScope scope, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string Sql = """
            WITH c AS (
                SELECT run_id, activity_id FROM kairos.activity_tasks
                WHERE tenant_id = @t AND namespace_id = @n
                  AND ((NOT leased AND visible_at <= @now) OR (leased AND lease_expires_at <= @now))
                ORDER BY ord LIMIT 1
                FOR UPDATE SKIP LOCKED)
            UPDATE kairos.activity_tasks a SET leased = true, lease_expires_at = @expires, lease_token = a.lease_token + 1
            FROM c WHERE a.tenant_id = @t AND a.namespace_id = @n AND a.run_id = c.run_id AND a.activity_id = c.activity_id
            RETURNING a.run_id, a.scheduled_body, a.attempt, a.lease_token
            """;

        return await Retry<ActivityTask?>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = Sql;
            Scope(cmd, scope);
            cmd.Parameters.Add(Ts("now", now));
            cmd.Parameters.Add(Ts("expires", now + lease));
            batch.BatchCommands.Add(cmd);

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var scheduled = (ActivityScheduled)HistoryEventCodec.Decode(reader.GetFieldValue<byte[]>(1), NoHash, NoHash);
            if (scheduled.TenantId != scope.TenantId || scheduled.NamespaceId != scope.NamespaceId)
            {
                throw new HistoryIntegrityException("Activity task body belongs to a different scope.");
            }

            return new ActivityTask(
                new RunKey(scope.TenantId, scope.NamespaceId, reader.GetGuid(0)), scheduled.ActivityId, scheduled.ActivityType,
                scheduled.Input, scheduled.RetryPolicy, reader.GetInt32(2), reader.GetInt64(3));
        }).ConfigureAwait(false);
    }

    public async ValueTask<CompletionOutcome> CompleteActivityTaskAsync(
        ActivityTask task, HistoryEvent outcome, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StoreRules.ValidateActivityOutcome(task, outcome, retryAt);
        RunKey run = task.Run;
        return await InTransaction(run.Scope, async (conn, tx) =>
        {
            // Lock order is always run, then children, so completions cannot deadlock against workflow-task completions.
            RunHead? head = await LockRunAsync(conn, tx, run, cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                return CompletionOutcome.StaleLease;
            }

            await using var lockTask = new NpgsqlCommand(
                """
                SELECT leased, lease_token FROM kairos.activity_tasks
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a FOR UPDATE
                """, conn, tx);
            lockTask.Parameters.AddWithValue("t", run.TenantId);
            lockTask.Parameters.AddWithValue("n", run.NamespaceId);
            lockTask.Parameters.AddWithValue("r", run.RunId);
            lockTask.Parameters.AddWithValue("a", task.ActivityId);
            bool current;
            await using (NpgsqlDataReader reader = await lockTask.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                current = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetBoolean(0) && reader.GetInt64(1) == task.LeaseToken;
            }

            if (!current)
            {
                return CompletionOutcome.StaleLease;
            }

            if (head.State != 0)
            {
                await DeleteActivityTaskAsync(conn, tx, run, task.ActivityId, cancellationToken).ConfigureAwait(false);
                return CompletionOutcome.Applied; // workflow already closed: the result is moot
            }

            await AppendAsync(conn, tx, run, head, [outcome], cancellationToken).ConfigureAwait(false);
            if (retryAt is { } at)
            {
                await using var requeue = new NpgsqlCommand(
                    """
                    UPDATE kairos.activity_tasks SET leased = false, visible_at = @at, attempt = attempt + 1
                    WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a
                    """, conn, tx);
                requeue.Parameters.AddWithValue("t", run.TenantId);
                requeue.Parameters.AddWithValue("n", run.NamespaceId);
                requeue.Parameters.AddWithValue("r", run.RunId);
                requeue.Parameters.AddWithValue("a", task.ActivityId);
                requeue.Parameters.Add(Ts("at", at));
                await requeue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteActivityTaskAsync(conn, tx, run, task.ActivityId, cancellationToken).ConfigureAwait(false);
                await EnsureWorkflowTaskAsync(conn, tx, run, now, cancellationToken).ConfigureAwait(false);
                await NotifyAsync(conn, tx, run.Scope, cancellationToken).ConfigureAwait(false);
            }

            return CompletionOutcome.Applied;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- timers -----------------------------------------------------------------------------------------------

    public async ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await InTransaction(scope, async (conn, tx) =>
        {
            var due = new List<(DateTime At, Guid RunId, int TimerId)>();
            await using (var take = new NpgsqlCommand(
                """
                DELETE FROM kairos.timers
                WHERE (tenant_id, namespace_id, run_id, timer_id) IN (
                    SELECT tenant_id, namespace_id, run_id, timer_id FROM kairos.timers
                    WHERE tenant_id = @t AND namespace_id = @n AND due_at <= @now
                    ORDER BY due_at, run_id, timer_id LIMIT 1000
                    FOR UPDATE SKIP LOCKED)
                RETURNING due_at, run_id, timer_id
                """, conn, tx))
            {
                take.Parameters.AddWithValue("t", scope.TenantId);
                take.Parameters.AddWithValue("n", scope.NamespaceId);
                take.Parameters.Add(Ts("now", now));
                await using NpgsqlDataReader reader = await take.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    due.Add((reader.GetDateTime(0), reader.GetGuid(1), reader.GetInt32(2)));
                }
            }

            due.Sort(static (a, b) =>
            {
                int c = a.At.CompareTo(b.At);
                if (c != 0)
                {
                    return c;
                }

                c = a.RunId.CompareTo(b.RunId);
                return c != 0 ? c : a.TimerId.CompareTo(b.TimerId);
            });

            var heads = new Dictionary<Guid, RunHead?>();
            foreach ((DateTime _, Guid runId, int timerId) in due)
            {
                var run = new RunKey(scope.TenantId, scope.NamespaceId, runId);
                if (!heads.TryGetValue(runId, out RunHead? head))
                {
                    head = await LockRunAsync(conn, tx, run, cancellationToken).ConfigureAwait(false);
                }

                if (head is not { State: 0 })
                {
                    heads[runId] = head;
                    continue;
                }

                HistoryEvent fired = await AppendAsync(
                    conn, tx, run, head, [new TimerFired(scope.TenantId, scope.NamespaceId, runId, 0, now, timerId)], cancellationToken).ConfigureAwait(false);
                heads[runId] = head with { LastSeq = fired.Sequence, LastHash = fired.Hash };
                await EnsureWorkflowTaskAsync(conn, tx, run, now, cancellationToken).ConfigureAwait(false);
            }

            if (due.Count > 0)
            {
                await NotifyAsync(conn, tx, scope, cancellationToken).ConfigureAwait(false);
            }

            return due.Count;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- reads ------------------------------------------------------------------------------------------------

    public async ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken)
    {
        return await Retry<IReadOnlyList<HistoryEvent>>(async () =>
        {
            await using NpgsqlBatch batch = Batch(run.Scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = "SELECT seq, body, prev_hash, hash FROM kairos.history_events WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r ORDER BY seq";
            Scope(cmd, run.Scope);
            cmd.Parameters.AddWithValue("r", run.RunId);
            batch.BatchCommands.Add(cmd);

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var history = new List<HistoryEvent>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                history.Add(DecodeRow(run.Scope, run.RunId, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)));
            }

            return history;
        }).ConfigureAwait(false);
    }

    public async ValueTask<RunState?> GetRunStateAsync(RunKey run, CancellationToken cancellationToken)
    {
        return await Retry<RunState?>(async () =>
        {
            await using NpgsqlBatch batch = Batch(run.Scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = "SELECT state FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r";
            Scope(cmd, run.Scope);
            cmd.Parameters.AddWithValue("r", run.RunId);
            batch.BatchCommands.Add(cmd);

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (RunState)reader.GetInt16(0) : null;
        }).ConfigureAwait(false);
    }

    // ---- diagnostics (used by simulators, tests and dashboards; scoped like everything else) -----------------

    public async Task<StoreStats> GetStatsAsync(TenantScope scope, CancellationToken cancellationToken = default)
    {
        return await Retry(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = """
                SELECT (SELECT count(*) FROM kairos.runs),
                       (SELECT count(*) FROM kairos.runs WHERE state = 0),
                       (SELECT count(*) FROM kairos.workflow_tasks),
                       (SELECT count(*) FROM kairos.activity_tasks),
                       (SELECT count(*) FROM kairos.timers)
                """;
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new StoreStats((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2), (int)reader.GetInt64(3), (int)reader.GetInt64(4));
        }).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> NextWakeupAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return await Retry(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = """
                SELECT min(at) FROM (
                    SELECT due_at AS at FROM kairos.timers
                    UNION ALL SELECT CASE WHEN leased THEN lease_expires_at ELSE visible_at END FROM kairos.workflow_tasks
                    UNION ALL SELECT CASE WHEN leased THEN lease_expires_at ELSE visible_at END FROM kairos.activity_tasks) w
                WHERE at > @now
                """;
            cmd.Parameters.Add(Ts("now", now));
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return reader.IsDBNull(0) ? (DateTimeOffset?)null : new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
        }).ConfigureAwait(false);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private sealed record RunHead(short State, long LastSeq, byte[] LastHash);

    private static IEnumerable<HistoryEvent> Prepend(HistoryEvent first, IReadOnlyList<HistoryEvent> rest)
    {
        yield return first;
        for (int i = 0; i < rest.Count; i++)
        {
            yield return rest[i];
        }
    }

    private static HistoryEvent DecodeRow(TenantScope scope, Guid runId, long seq, byte[] body, byte[] prev, byte[] hash)
    {
        HistoryEvent e;
        try
        {
            e = HistoryEventCodec.Decode(body, prev, hash);
        }
        catch (FormatException ex)
        {
            throw new HistoryIntegrityException($"History row {seq} is not a valid event: {ex.Message}", seq);
        }

        if (e.TenantId != scope.TenantId || e.NamespaceId != scope.NamespaceId || e.RunId != runId || e.Sequence != seq)
        {
            throw new HistoryIntegrityException($"History row {seq} does not match the event stored in it.", seq);
        }

        return e;
    }

    private static async Task<RunHead?> LockRunAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT state, last_seq, last_hash FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("t", run.TenantId);
        cmd.Parameters.AddWithValue("n", run.NamespaceId);
        cmd.Parameters.AddWithValue("r", run.RunId);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new RunHead(reader.GetInt16(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2))
            : null;
    }

    /// <summary>Seals and appends events after <paramref name="head"/> in one statement (insert + head update). Returns the last event.</summary>
    private static async Task<HistoryEvent> AppendAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, RunHead head, IReadOnlyList<HistoryEvent> templates, CancellationToken ct)
    {
        var sealedEvents = new List<HistoryEvent>(templates.Count);
        long seq = head.LastSeq;
        byte[] prev = head.LastHash;
        foreach (HistoryEvent template in templates)
        {
            StoreRules.RequireSameRun(run, template);
            HistoryEvent s = StoreRules.Seal(template, ++seq, prev);
            sealedEvents.Add(s);
            prev = s.Hash;
        }

        await using var cmd = new NpgsqlCommand(
            """
            WITH ins AS (
                INSERT INTO kairos.history_events (tenant_id, namespace_id, run_id, seq, body, prev_hash, hash)
                SELECT @t, @n, @r, s.seq, s.body, s.prev, s.hash FROM unnest(@seqs, @bodies, @prevs, @hashes) AS s(seq, body, prev, hash)
                RETURNING 1)
            UPDATE kairos.runs SET last_seq = @lastseq, last_hash = @lasthash
            WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND (SELECT count(*) FROM ins) >= 0
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", run.TenantId);
        cmd.Parameters.AddWithValue("n", run.NamespaceId);
        cmd.Parameters.AddWithValue("r", run.RunId);
        cmd.Parameters.AddWithValue("lastseq", sealedEvents[^1].Sequence);
        cmd.Parameters.AddWithValue("lasthash", sealedEvents[^1].Hash);
        cmd.Parameters.Add(Arr("seqs", NpgsqlDbType.Bigint, sealedEvents.Select(e => e.Sequence).ToArray()));
        cmd.Parameters.Add(Arr("bodies", NpgsqlDbType.Bytea, sealedEvents.Select(HistoryEventCodec.Encode).ToArray()));
        cmd.Parameters.Add(Arr("prevs", NpgsqlDbType.Bytea, sealedEvents.Select(e => e.PrevHash).ToArray()));
        cmd.Parameters.Add(Arr("hashes", NpgsqlDbType.Bytea, sealedEvents.Select(e => e.Hash).ToArray()));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return sealedEvents[^1];
    }

    private static async Task EnsureWorkflowTaskAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO kairos.workflow_tasks (tenant_id, namespace_id, run_id, build_id, visible_at)
            SELECT tenant_id, namespace_id, run_id, build_id, @now FROM kairos.runs
            WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r
            ON CONFLICT DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", run.TenantId);
        cmd.Parameters.AddWithValue("n", run.NamespaceId);
        cmd.Parameters.AddWithValue("r", run.RunId);
        cmd.Parameters.Add(Ts("now", now));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteActivityTaskAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, int activityId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM kairos.activity_tasks WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a", conn, tx);
        cmd.Parameters.AddWithValue("t", run.TenantId);
        cmd.Parameters.AddWithValue("n", run.NamespaceId);
        cmd.Parameters.AddWithValue("r", run.RunId);
        cmd.Parameters.AddWithValue("a", activityId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task NotifyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@ch, @payload)", conn, tx);
        cmd.Parameters.AddWithValue("ch", NotifyChannel);
        cmd.Parameters.AddWithValue("payload", $"{scope.TenantId:N}:{scope.NamespaceId:N}");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NpgsqlBatchCommand Notify(TenantScope scope)
    {
        var cmd = new NpgsqlBatchCommand("SELECT pg_notify(@ch, @payload)");
        cmd.Parameters.AddWithValue("ch", NotifyChannel);
        cmd.Parameters.AddWithValue("payload", $"{scope.TenantId:N}:{scope.NamespaceId:N}");
        return cmd;
    }

    /// <summary>A batch whose first command pins the tenant scope; the batch runs as one implicit transaction in one round trip.</summary>
    private static NpgsqlBatch Batch(TenantScope scope)
    {
        var batch = new NpgsqlBatch();
        var set = new NpgsqlBatchCommand(SetScopeSql);
        set.Parameters.AddWithValue("scope_t", scope.TenantId.ToString());
        set.Parameters.AddWithValue("scope_n", scope.NamespaceId.ToString());
        batch.BatchCommands.Add(set);
        return batch;
    }

    private static void Scope(NpgsqlBatchCommand cmd, TenantScope scope)
    {
        cmd.Parameters.AddWithValue("t", scope.TenantId);
        cmd.Parameters.AddWithValue("n", scope.NamespaceId);
    }

    private static NpgsqlParameter Ts(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value.UtcDateTime };

    private static NpgsqlParameter Arr<T>(string name, NpgsqlDbType element, T[] values) =>
        new(name, NpgsqlDbType.Array | element) { Value = values };

    private static async Task<T> InScope<T>(NpgsqlConnection conn, TenantScope scope, Func<NpgsqlTransaction, Task<T>> body, CancellationToken ct)
    {
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SetScopeAsync(conn, tx, scope, ct).ConfigureAwait(false);
        T result = await body(tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private static async Task SetScopeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(SetScopeSql, conn, tx);
        cmd.Parameters.AddWithValue("scope_t", scope.TenantId.ToString());
        cmd.Parameters.AddWithValue("scope_n", scope.NamespaceId.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> InTransaction<T>(TenantScope scope, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body, CancellationToken ct)
    {
        return await Retry(async () =>
        {
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await SetScopeAsync(conn, tx, scope, ct).ConfigureAwait(false);
            T result = await body(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private static async Task<T> Retry<T>(Func<Task<T>> operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (PostgresException ex) when (attempt < MaxAttempts && ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * attempt)).ConfigureAwait(false);
            }
        }
    }
}
