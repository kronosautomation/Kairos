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
/// <summary>One workflow-task completion inside a group commit.</summary>
public sealed record WorkflowTaskCompletion(WorkflowTask Task, IReadOnlyList<HistoryEvent> NewEvents);

public sealed class PostgresKairosStore(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null) : IKairosStore
{
    private readonly PostgresStoreOptions _options = options ?? new PostgresStoreOptions();

    /// <summary>
    /// Substitutes the lease/scheduling clock into a statement: the database clock in production, so skewed app servers cannot
    /// steal live leases; the caller's clock (the <c>@now</c> parameter) for simulations and tests with virtual time.
    /// </summary>
    private string Clk(string sql) =>
        sql.Replace("@clock", _options.LeaseClock == LeaseClock.Database ? "clock_timestamp()" : "@now", StringComparison.Ordinal)
           .Replace("{{timer_due}}", _options.LeaseClock == LeaseClock.Database ? "clock_timestamp() + make_interval(secs => x.dur)" : "x.due", StringComparison.Ordinal);

    public const string NotifyChannel = "kairos_work";

    private const int MaxAttempts = 4;
    private const string SetScopeSql = "SELECT set_config('app.tenant_id', @scope_t, true), set_config('app.namespace_id', @scope_n, true)";
    private static readonly byte[] NoHash = [];

    // ---- starting and signalling ------------------------------------------------------------------------------

    public async ValueTask<StartRunResult> StartRunAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = new WorkflowStarted(
            request.Run.TenantId, request.Run.NamespaceId, request.Run.RunId, 0, request.Now, request.WorkflowType, request.BuildId, request.Input)
        {
            TraceContext = request.TraceContext,
        };
        HistoryEvent first = StoreRules.Seal(started, 1, HashChain.GenesisHash);
        byte[] body = HistoryEventCodec.Encode(first);

        const string Sql = """
            WITH r AS (
                INSERT INTO kairos.runs (tenant_id, namespace_id, run_id, workflow_id, workflow_type, build_id, state, last_seq, last_hash, created_at)
                VALUES (@t, @n, @r, @wf, @wt, @b, 0, 1, @h, @now)
                ON CONFLICT (tenant_id, namespace_id, workflow_id) WHERE is_current DO NOTHING
                RETURNING run_id),
            e AS (
                INSERT INTO kairos.history_events (tenant_id, namespace_id, run_id, seq, body, prev_hash, hash)
                SELECT @t, @n, @r, 1, @body, @prev, @h WHERE EXISTS (SELECT 1 FROM r) RETURNING 1),
            w AS (
                INSERT INTO kairos.workflow_tasks (tenant_id, namespace_id, run_id, build_id, visible_at)
                SELECT @t, @n, @r, @b, @clock WHERE EXISTS (SELECT 1 FROM r) RETURNING 1)
            SELECT run_id, true FROM r
            UNION ALL
            SELECT run_id, false FROM kairos.runs
            WHERE tenant_id = @t AND namespace_id = @n AND workflow_id = @wf AND is_current AND NOT EXISTS (SELECT 1 FROM r)
            """;

        return await Retry(async () =>
        {
            await using NpgsqlBatch batch = Batch(request.Run.Scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = Clk(Sql);
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
                "SELECT run_id FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND workflow_id = @wf AND is_current", conn);
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

    public ValueTask<bool> SignalRunAsync(RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
        SignalRunTracedAsync(run, signalName, signalId, payload, now, "", cancellationToken);

    public async ValueTask<bool> SignalRunTracedAsync(
        RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, string traceContext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalId);
        ArgumentNullException.ThrowIfNull(traceContext);
        return await InTransaction(run.Scope, async (conn, tx) =>
        {
            RunHead? head = await LockRunAsync(conn, tx, run, cancellationToken).ConfigureAwait(false);
            if (head is not { State: 0 })
            {
                return false;
            }

            await using (var claim = new NpgsqlCommand(
                "INSERT INTO kairos.signal_ids (tenant_id, namespace_id, run_id, signal_id) VALUES (@t, @n, @r, @id) ON CONFLICT DO NOTHING", conn, tx))
            {
                claim.Parameters.AddWithValue("t", run.TenantId);
                claim.Parameters.AddWithValue("n", run.NamespaceId);
                claim.Parameters.AddWithValue("r", run.RunId);
                claim.Parameters.AddWithValue("id", signalId);
                if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    return true; // already delivered: nothing more to do
                }
            }

            var template = new SignalReceived(run.TenantId, run.NamespaceId, run.RunId, 0, now, signalName, payload) { SignalId = signalId, TraceContext = traceContext };
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
                  AND NOT leased AND NOT stuck AND visible_at <= @clock
                ORDER BY ord LIMIT 1
                FOR UPDATE SKIP LOCKED),
            u AS (
                UPDATE kairos.workflow_tasks wt SET leased = true, lease_expires_at = @clock + make_interval(secs => @leasesecs)
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
            cmd.CommandText = Clk(Sql);
            Scope(cmd, scope);
            cmd.Parameters.Add(new NpgsqlParameter("builds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = buildIds.ToArray() });
            cmd.Parameters.Add(Ts("now", now));
            cmd.Parameters.AddWithValue("leasesecs", lease.TotalSeconds);
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
        WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await CompleteWorkflowTasksAsync([new WorkflowTaskCompletion(task, newEvents)], now, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>
    /// Group commit: completes several workflow tasks of one tenant scope in a single round trip and a single transaction commit
    /// (one statement per task, each independently guarded, so one stale lease never rejects its neighbours).
    /// </summary>
    public async ValueTask<CompletionOutcome[]> CompleteWorkflowTasksAsync(
        IReadOnlyList<WorkflowTaskCompletion> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [];
        }

        TenantScope scope = items[0].Task.Run.Scope;
        var prepared = new PreparedCompletion[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Task.Run.Scope != scope)
            {
                throw new ArgumentException("A group commit must stay within one tenant scope.", nameof(items));
            }

            prepared[i] = Prepare(items[i].Task, items[i].NewEvents, now, _options.Limits.MaxHistoryEvents);
        }

        var outcomes = new CompletionOutcome[prepared.Length];
        var regular = new List<int>(prepared.Length);
        for (int i = 0; i < prepared.Length; i++)
        {
            if (prepared[i].Continuation is { } continuation)
            {
                outcomes[i] = await CompleteContinuationAsync(prepared[i], continuation, now, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                regular.Add(i);
            }
        }

        if (regular.Count == 0)
        {
            return outcomes;
        }

        PreparedCompletion[] group = [.. regular.Select(i => prepared[i])];
        long[] applied = await Retry(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            bool notify = false;
            foreach (PreparedCompletion p in group)
            {
                batch.BatchCommands.Add(p.ToCommand(Clk(CompleteWorkflowSql), scope, now));
                notify |= p.HasActivities;
            }

            if (notify)
            {
                batch.BatchCommands.Add(Notify(scope));
            }

            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false); // set_config
            var counts = new long[group.Length];
            for (int i = 0; i < counts.Length; i++)
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                counts[i] = reader.GetInt64(0);
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            }

            return counts;
        }).ConfigureAwait(false);

        for (int k = 0; k < group.Length; k++)
        {
            outcomes[regular[k]] = applied[k] == 1
                ? CompletionOutcome.Applied
                : await ResolveUnappliedAsync(group[k].Task, now, cancellationToken).ConfigureAwait(false);
        }

        return outcomes;
    }

    /// <summary>
    /// Continue-as-new, the rare path: in one transaction close the run (state 3), clear its work, and create the next run of the same
    /// workflow id with an empty history that starts with the carried input. The same fencing guards as the single-statement path apply.
    /// </summary>
    private async Task<CompletionOutcome> CompleteContinuationAsync(PreparedCompletion p, WorkflowContinued continuation, DateTimeOffset now, CancellationToken ct)
    {
        RunKey run = p.Task.Run;
        bool applied = await InTransaction(run.Scope, async (conn, tx) =>
        {
            RunHead? head = await LockRunAsync(conn, tx, run, ct).ConfigureAwait(false);
            if (head is not { State: 0 } || head.LastSeq != p.Head.Sequence || !head.LastHash.AsSpan().SequenceEqual(p.Head.Hash))
            {
                return false;
            }

            await using (var owns = new NpgsqlCommand(
                """
                SELECT r.wf_lease_token = @token AND w.leased
                FROM kairos.runs r JOIN kairos.workflow_tasks w
                  ON w.tenant_id = r.tenant_id AND w.namespace_id = r.namespace_id AND w.run_id = r.run_id
                WHERE r.tenant_id = @t AND r.namespace_id = @n AND r.run_id = @r
                """, conn, tx))
            {
                owns.Parameters.AddWithValue("t", run.TenantId);
                owns.Parameters.AddWithValue("n", run.NamespaceId);
                owns.Parameters.AddWithValue("r", run.RunId);
                owns.Parameters.AddWithValue("token", p.Task.LeaseToken);
                if (await owns.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
                {
                    return false;
                }
            }

            await AppendAsync(conn, tx, run, head, p.Events, ct).ConfigureAwait(false);
            foreach (string sql in ContinueStatements)
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("t", run.TenantId);
                cmd.Parameters.AddWithValue("n", run.NamespaceId);
                cmd.Parameters.AddWithValue("r", run.RunId);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // The next run: same workflow id, type and build; history restarts at sequence 1.
            var next = new RunKey(run.TenantId, run.NamespaceId, continuation.NewRunId);
            var started = new WorkflowStarted(next.TenantId, next.NamespaceId, next.RunId, 0, now, "", "", continuation.Input) { PreviousRunId = run.RunId };
            string workflowType, buildId;
            await using (var meta = new NpgsqlCommand(
                """
                SELECT workflow_type, build_id,
                       (SELECT body FROM kairos.history_events h WHERE h.tenant_id = @t AND h.namespace_id = @n AND h.run_id = @r AND h.seq = 1)
                FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r
                """, conn, tx))
            {
                meta.Parameters.AddWithValue("t", run.TenantId);
                meta.Parameters.AddWithValue("n", run.NamespaceId);
                meta.Parameters.AddWithValue("r", run.RunId);
                await using NpgsqlDataReader reader = await meta.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                workflowType = reader.GetString(0);
                buildId = reader.GetString(1);
                if (!reader.IsDBNull(2) && HistoryEventCodec.Decode(reader.GetFieldValue<byte[]>(2), NoHash, NoHash) is WorkflowStarted origin)
                {
                    started = started with { TraceContext = origin.TraceContext }; // one trace across the whole chain
                }
            }

            HistoryEvent first = StoreRules.Seal(started with { WorkflowType = workflowType, BuildId = buildId }, 1, HashChain.GenesisHash);
            await using (var insertRun = new NpgsqlCommand(
                Clk("""
                INSERT INTO kairos.runs (tenant_id, namespace_id, run_id, workflow_id, workflow_type, build_id, state, last_seq, last_hash, created_at, is_current)
                SELECT tenant_id, namespace_id, @new, workflow_id, workflow_type, build_id, 0, 1, @h, @now, true
                FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r
                """), conn, tx))
            {
                insertRun.Parameters.AddWithValue("t", run.TenantId);
                insertRun.Parameters.AddWithValue("n", run.NamespaceId);
                insertRun.Parameters.AddWithValue("r", run.RunId);
                insertRun.Parameters.AddWithValue("new", next.RunId);
                insertRun.Parameters.AddWithValue("h", first.Hash);
                insertRun.Parameters.Add(Ts("now", now));
                await insertRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var insertEvent = new NpgsqlCommand(
                "INSERT INTO kairos.history_events (tenant_id, namespace_id, run_id, seq, body, prev_hash, hash) VALUES (@t, @n, @new, 1, @body, @prev, @h)", conn, tx))
            {
                insertEvent.Parameters.AddWithValue("t", run.TenantId);
                insertEvent.Parameters.AddWithValue("n", run.NamespaceId);
                insertEvent.Parameters.AddWithValue("new", next.RunId);
                insertEvent.Parameters.AddWithValue("body", HistoryEventCodec.Encode(first));
                insertEvent.Parameters.AddWithValue("prev", first.PrevHash);
                insertEvent.Parameters.AddWithValue("h", first.Hash);
                await insertEvent.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await EnsureWorkflowTaskAsync(conn, tx, next, now, ct).ConfigureAwait(false);
            await NotifyAsync(conn, tx, run.Scope, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

        return applied ? CompletionOutcome.Applied : await ResolveUnappliedAsync(p.Task, now, ct).ConfigureAwait(false);
    }

    private static readonly string[] ContinueStatements =
    [
        "UPDATE kairos.runs SET state = 3, is_current = false WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
        "DELETE FROM kairos.workflow_tasks WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
        "DELETE FROM kairos.activity_tasks WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
        "DELETE FROM kairos.timers WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
    ];

    /// <summary>Seals the boundary + command events as one chain extension from the snapshot the worker replayed.</summary>
    private static PreparedCompletion Prepare(WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now, int maxHistoryEvents)
    {
        ArgumentNullException.ThrowIfNull(task);
        StoreRules.ValidateWorkflowTaskEvents(task.Run, newEvents);
        RunKey run = task.Run;
        HistoryEvent head = task.History[^1];

        // Bounded history (I9): a task that would push the run past the cap, and does not itself close or continue, ends the run.
        bool closes = newEvents.Count > 0 && StoreRules.IsClosing(newEvents[^1]);
        if (!closes && head.Sequence + 1 + newEvents.Count + 1 > maxHistoryEvents)
        {
            newEvents = [new RunTerminated(run.TenantId, run.NamespaceId, run.RunId, 0, now, StoreRules.HistoryLimitReason(maxHistoryEvents))];
        }

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

        short state = 0;
        var actIds = new List<int>();
        var actBodies = new List<byte[]>();
        var timerIds = new List<int>();
        var timerDue = new List<DateTime>();
        var timerSecs = new List<double>();
        foreach (HistoryEvent e in sealedEvents)
        {
            switch (e)
            {
                case WorkflowCompleted:
                    state = 1;
                    break;
                case WorkflowFailed or RunTerminated:
                    state = 2;
                    break;
                case WorkflowContinued:
                    state = 3;
                    break;
                case ActivityScheduled s:
                    actIds.Add(s.ActivityId);
                    actBodies.Add(HistoryEventCodec.Encode(s));
                    break;
                case TimerStarted t:
                    timerIds.Add(t.TimerId);
                    timerDue.Add((t.Timestamp + t.Duration).UtcDateTime);
                    timerSecs.Add(t.Duration.TotalSeconds);
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
            timerSecs.Clear();
        }

        return new PreparedCompletion(task, head, sealedEvents, state, actIds.ToArray(), actBodies.ToArray(), timerIds.ToArray(), timerDue.ToArray(), timerSecs.ToArray());
    }

    private sealed record PreparedCompletion(
        WorkflowTask Task, HistoryEvent Head, List<HistoryEvent> Events, short State,
        int[] ActIds, byte[][] ActBodies, int[] TimerIds, DateTime[] TimerDue, double[] TimerSecs)
    {
        public bool HasActivities => ActIds.Length > 0;

        public WorkflowContinued? Continuation => Events.OfType<WorkflowContinued>().FirstOrDefault();

        public NpgsqlBatchCommand ToCommand(string sql, TenantScope scope, DateTimeOffset now)
        {
            var cmd = new NpgsqlBatchCommand(sql);
            RunKey run = Task.Run;
            Scope(cmd, scope);
            cmd.Parameters.AddWithValue("r", run.RunId);
            cmd.Parameters.AddWithValue("token", Task.LeaseToken);
            cmd.Parameters.AddWithValue("headseq", Head.Sequence);
            cmd.Parameters.AddWithValue("headhash", Head.Hash);
            cmd.Parameters.AddWithValue("lastseq", Events[^1].Sequence);
            cmd.Parameters.AddWithValue("lasthash", Events[^1].Hash);
            cmd.Parameters.AddWithValue("state", State);
            cmd.Parameters.Add(Ts("now", now));
            cmd.Parameters.Add(Arr("seqs", NpgsqlDbType.Bigint, Events.Select(e => e.Sequence).ToArray()));
            cmd.Parameters.Add(Arr("bodies", NpgsqlDbType.Bytea, Events.Select(HistoryEventCodec.Encode).ToArray()));
            cmd.Parameters.Add(Arr("prevs", NpgsqlDbType.Bytea, Events.Select(e => e.PrevHash).ToArray()));
            cmd.Parameters.Add(Arr("hashes", NpgsqlDbType.Bytea, Events.Select(e => e.Hash).ToArray()));
            cmd.Parameters.Add(Arr("actids", NpgsqlDbType.Integer, ActIds));
            cmd.Parameters.Add(Arr("actbodies", NpgsqlDbType.Bytea, ActBodies));
            cmd.Parameters.Add(Arr("timerids", NpgsqlDbType.Integer, TimerIds));
            cmd.Parameters.Add(Arr("timerdue", NpgsqlDbType.TimestampTz, TimerDue));
            cmd.Parameters.Add(Arr("timersecs", NpgsqlDbType.Double, TimerSecs));
            return cmd;
        }
    }

    private const string CompleteWorkflowSql = """
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
                SELECT @t, @n, @r, a.id, a.body, @clock FROM unnest(@actids, @actbodies) AS a(id, body)
                WHERE EXISTS (SELECT 1 FROM guard) RETURNING 1),
            tm AS (
                INSERT INTO kairos.timers (tenant_id, namespace_id, run_id, timer_id, due_at)
                SELECT @t, @n, @r, x.id, {{timer_due}} FROM unnest(@timerids, @timerdue, @timersecs) AS x(id, due, dur)
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

    /// <summary>Rare path: work out why a completion was not applied, and release the task for a re-run when only history moved.</summary>
    private async Task<CompletionOutcome> ResolveUnappliedAsync(WorkflowTask task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RunKey run = task.Run;
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
                Clk("UPDATE kairos.workflow_tasks SET leased = false, visible_at = @clock WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r"), conn, tx);
            release.Parameters.AddWithValue("t", run.TenantId);
            release.Parameters.AddWithValue("n", run.NamespaceId);
            release.Parameters.AddWithValue("r", run.RunId);
            release.Parameters.Add(Ts("now", now));
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return CompletionOutcome.SequenceConflict;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseWorkflowTaskAsync(
        WorkflowTask task, DateTimeOffset retryAt, DateTimeOffset now, bool countAttempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await InTransaction(task.Run.Scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                Clk("""
                UPDATE kairos.workflow_tasks w SET leased = false, visible_at = @clock + make_interval(secs => @delay), attempt = attempt + @inc
                WHERE w.tenant_id = @t AND w.namespace_id = @n AND w.run_id = @r AND w.leased
                  AND EXISTS (SELECT 1 FROM kairos.runs r WHERE r.tenant_id = @t AND r.namespace_id = @n AND r.run_id = @r AND r.wf_lease_token = @token)
                """), conn, tx);
            cmd.Parameters.AddWithValue("t", task.Run.TenantId);
            cmd.Parameters.AddWithValue("n", task.Run.NamespaceId);
            cmd.Parameters.AddWithValue("r", task.Run.RunId);
            cmd.Parameters.AddWithValue("token", task.LeaseToken);
            cmd.Parameters.AddWithValue("delay", Math.Max(0, (retryAt - now).TotalSeconds));
            cmd.Parameters.AddWithValue("inc", countAttempt ? 1 : 0);
            cmd.Parameters.Add(Ts("now", now));
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
                  AND NOT leased AND visible_at <= @clock
                ORDER BY ord LIMIT 1
                FOR UPDATE SKIP LOCKED)
            UPDATE kairos.activity_tasks a SET leased = true, lease_expires_at = @clock + make_interval(secs => @leasesecs), lease_token = a.lease_token + 1
            FROM c WHERE a.tenant_id = @t AND a.namespace_id = @n AND a.run_id = c.run_id AND a.activity_id = c.activity_id
            RETURNING a.run_id, a.scheduled_body, a.attempt, a.lease_token
            """;

        return await Retry<ActivityTask?>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = Clk(Sql);
            Scope(cmd, scope);
            cmd.Parameters.Add(Ts("now", now));
            cmd.Parameters.AddWithValue("leasesecs", lease.TotalSeconds);
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
                scheduled.Input, scheduled.RetryPolicy, reader.GetInt32(2), reader.GetInt64(3), scheduled.TraceContext);
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
                    Clk("""
                    UPDATE kairos.activity_tasks SET leased = false, visible_at = @clock + make_interval(secs => @delay), attempt = attempt + 1
                    WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a
                    """), conn, tx);
                requeue.Parameters.AddWithValue("t", run.TenantId);
                requeue.Parameters.AddWithValue("n", run.NamespaceId);
                requeue.Parameters.AddWithValue("r", run.RunId);
                requeue.Parameters.AddWithValue("a", task.ActivityId);
                requeue.Parameters.AddWithValue("delay", Math.Max(0, (at - now).TotalSeconds));
                requeue.Parameters.Add(Ts("now", now));
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

    public async ValueTask<CompletionOutcome> HeartbeatActivityTaskAsync(ActivityTask task, DateTimeOffset now, TimeSpan extendBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await InTransaction(task.Run.Scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                Clk("""
                UPDATE kairos.activity_tasks
                SET lease_expires_at = greatest(lease_expires_at, @clock + make_interval(secs => @extend))
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a AND leased AND lease_token = @token
                """), conn, tx);
            cmd.Parameters.AddWithValue("t", task.Run.TenantId);
            cmd.Parameters.AddWithValue("n", task.Run.NamespaceId);
            cmd.Parameters.AddWithValue("r", task.Run.RunId);
            cmd.Parameters.AddWithValue("a", task.ActivityId);
            cmd.Parameters.AddWithValue("token", task.LeaseToken);
            cmd.Parameters.AddWithValue("extend", extendBy.TotalSeconds);
            cmd.Parameters.Add(Ts("now", now));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? CompletionOutcome.Applied
                : CompletionOutcome.StaleLease;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseActivityTaskAsync(ActivityTask task, DateTimeOffset retryAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await InTransaction(task.Run.Scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                Clk("""
                UPDATE kairos.activity_tasks SET leased = false, visible_at = @clock + make_interval(secs => @delay)
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a AND leased AND lease_token = @token
                """), conn, tx);
            cmd.Parameters.AddWithValue("t", task.Run.TenantId);
            cmd.Parameters.AddWithValue("n", task.Run.NamespaceId);
            cmd.Parameters.AddWithValue("r", task.Run.RunId);
            cmd.Parameters.AddWithValue("a", task.ActivityId);
            cmd.Parameters.AddWithValue("token", task.LeaseToken);
            cmd.Parameters.AddWithValue("delay", Math.Max(0, (retryAt - now).TotalSeconds));
            cmd.Parameters.Add(Ts("now", now));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upper bound on batches a single maintenance call processes, so one tick never monopolises a worker.</summary>
    private const int MaintenanceMaxBatches = 20;

    /// <summary>Runs handled per maintenance transaction. Small transactions hold few locks for a short time.</summary>
    private const int MaintenanceBatchRuns = 50;

    public async ValueTask<int> ReapExpiredLeasesAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int total = 0;
        for (int batch = 0; batch < MaintenanceMaxBatches; batch++)
        {
            int handled = await InTransaction(scope, (conn, tx) => ReapActivityBatchAsync(conn, tx, scope, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            total += handled;
            if (handled == 0)
            {
                break;
            }
        }

        for (int batch = 0; batch < MaintenanceMaxBatches; batch++)
        {
            int handled = await InTransaction(scope, (conn, tx) => ReapWorkflowBatchAsync(conn, tx, scope, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            total += handled;
            if (handled < MaintenanceBatchRuns * 4)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// One transaction: take a bounded set of expired attempts ordered by run, lock their runs with SKIP LOCKED (a busy run is simply
    /// left for the next tick), then record and requeue or fail each one. Competing reapers split the work instead of queueing.
    /// </summary>
    private async Task<int> ReapActivityBatchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = new List<(Guid RunId, int ActivityId)>();
        await using (var find = new NpgsqlCommand(
            Clk("""
            SELECT run_id, activity_id FROM kairos.activity_tasks
            WHERE tenant_id = @t AND namespace_id = @n AND leased AND lease_expires_at <= @clock
            ORDER BY run_id, activity_id LIMIT @limit
            """), conn, tx))
        {
            find.Parameters.AddWithValue("t", scope.TenantId);
            find.Parameters.AddWithValue("n", scope.NamespaceId);
            find.Parameters.AddWithValue("limit", MaintenanceBatchRuns);
            find.Parameters.Add(Ts("now", now));
            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add((reader.GetGuid(0), reader.GetInt32(1)));
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        Dictionary<Guid, RunHead> heads = await LockRunsSkipLockedAsync(conn, tx, scope, candidates.Select(c => c.RunId).Distinct().ToArray(), ct).ConfigureAwait(false);
        int handled = 0;
        foreach ((Guid runId, int activityId) in candidates)
        {
            if (!heads.TryGetValue(runId, out RunHead? head))
            {
                continue; // another worker is busy with this run; it will be handled next tick
            }

            var run = new RunKey(scope.TenantId, scope.NamespaceId, runId);
            byte[]? body = null;
            int attempt = 0;
            await using (var row = new NpgsqlCommand(
                Clk("""
                SELECT scheduled_body, attempt FROM kairos.activity_tasks
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a AND leased AND lease_expires_at <= @clock
                FOR UPDATE
                """), conn, tx))
            {
                row.Parameters.AddWithValue("t", scope.TenantId);
                row.Parameters.AddWithValue("n", scope.NamespaceId);
                row.Parameters.AddWithValue("r", runId);
                row.Parameters.AddWithValue("a", activityId);
                row.Parameters.Add(Ts("now", now));
                await using NpgsqlDataReader reader = await row.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    body = reader.GetFieldValue<byte[]>(0);
                    attempt = reader.GetInt32(1);
                }
            }

            if (body is null)
            {
                continue; // renewed or completed in the meantime
            }

            handled++;
            if (head.State != 0)
            {
                await DeleteActivityTaskAsync(conn, tx, run, activityId, ct).ConfigureAwait(false);
                continue;
            }

            var scheduled = (ActivityScheduled)HistoryEventCodec.Decode(body, NoHash, NoHash);
            bool retry = scheduled.RetryPolicy.ShouldRetry(attempt, StoreRules.LeaseExpiredError);
            HistoryEvent failed = await AppendAsync(
                conn, tx, run, head,
                [new ActivityFailed(scope.TenantId, scope.NamespaceId, runId, 0, now, activityId, attempt,
                    StoreRules.LeaseExpiredError, StoreRules.LeaseExpiredMessage, retry)],
                ct).ConfigureAwait(false);
            heads[runId] = head with { LastSeq = failed.Sequence, LastHash = failed.Hash };

            if (retry)
            {
                await using var requeue = new NpgsqlCommand(
                    Clk("""
                    UPDATE kairos.activity_tasks SET leased = false, attempt = attempt + 1, visible_at = @clock + make_interval(secs => @delay)
                    WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND activity_id = @a
                    """), conn, tx);
                requeue.Parameters.AddWithValue("t", scope.TenantId);
                requeue.Parameters.AddWithValue("n", scope.NamespaceId);
                requeue.Parameters.AddWithValue("r", runId);
                requeue.Parameters.AddWithValue("a", activityId);
                requeue.Parameters.AddWithValue("delay", scheduled.RetryPolicy.GetBackoff(attempt).TotalSeconds);
                requeue.Parameters.Add(Ts("now", now));
                await requeue.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await DeleteActivityTaskAsync(conn, tx, run, activityId, ct).ConfigureAwait(false);
                await EnsureWorkflowTaskAsync(conn, tx, run, now, ct).ConfigureAwait(false);
            }
        }

        if (handled > 0)
        {
            await NotifyAsync(conn, tx, scope, ct).ConfigureAwait(false);
        }

        return handled;
    }

    /// <summary>Workflow tasks: count the expiry, back off, and park (never drop) after too many. SKIP LOCKED, bounded.</summary>
    private async Task<int> ReapWorkflowBatchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, DateTimeOffset now, CancellationToken ct)
    {
        await using var wf = new NpgsqlCommand(
            Clk("""
            UPDATE kairos.workflow_tasks w
            SET leased = false,
                attempt = w.attempt + 1,
                stuck = (w.attempt + 1 > @max),
                visible_at = CASE WHEN w.attempt + 1 > @max THEN 'infinity'::timestamptz
                                  ELSE @clock + make_interval(secs => least(60, power(2, w.attempt - 1))) END
            WHERE (w.tenant_id, w.namespace_id, w.run_id) IN (
                SELECT tenant_id, namespace_id, run_id FROM kairos.workflow_tasks
                WHERE tenant_id = @t AND namespace_id = @n AND leased AND lease_expires_at <= @clock
                ORDER BY run_id LIMIT @limit
                FOR UPDATE SKIP LOCKED)
            """), conn, tx);
        wf.Parameters.AddWithValue("t", scope.TenantId);
        wf.Parameters.AddWithValue("n", scope.NamespaceId);
        wf.Parameters.AddWithValue("max", StoreRules.MaxWorkflowTaskAttempts);
        wf.Parameters.AddWithValue("limit", MaintenanceBatchRuns * 4);
        wf.Parameters.Add(Ts("now", now));
        int handled = await wf.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (handled > 0)
        {
            await NotifyAsync(conn, tx, scope, ct).ConfigureAwait(false);
        }

        return handled;
    }

    /// <summary>Locks the given runs in run_id order, skipping any that another transaction holds. Returns heads of the locked ones.</summary>
    private static async Task<Dictionary<Guid, RunHead>> LockRunsSkipLockedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, Guid[] runIds, CancellationToken ct)
    {
        var heads = new Dictionary<Guid, RunHead>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_id, state, last_seq, last_hash FROM kairos.runs
            WHERE tenant_id = @t AND namespace_id = @n AND run_id = ANY(@ids)
            ORDER BY run_id FOR UPDATE SKIP LOCKED
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", scope.TenantId);
        cmd.Parameters.AddWithValue("n", scope.NamespaceId);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = runIds });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            heads[reader.GetGuid(0)] = new RunHead(reader.GetInt16(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3));
        }

        return heads;
    }

    // ---- operator actions on parked work --------------------------------------------------------------------------

    public async ValueTask<IReadOnlyList<RunSummary>> ListRunsAsync(TenantScope scope, RunFilter filter, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return await Retry<IReadOnlyList<RunSummary>>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = """
                SELECT run_id, workflow_id, workflow_type, build_id, state, created_at
                FROM kairos.runs
                WHERE tenant_id = @t AND namespace_id = @n
                  AND (@wt::text IS NULL OR workflow_type = @wt)
                  AND (@b::text IS NULL OR build_id = @b)
                  AND (@f = 0 OR (@f = 1 AND state = 0) OR (@f = 2 AND state <> 0))
                  AND (state = 0 OR @since::timestamptz IS NULL OR created_at >= @since)
                  AND (@at::timestamptz IS NULL OR (created_at, run_id) < (@at, @after))
                ORDER BY created_at DESC, run_id DESC
                LIMIT @lim
                """;
            Scope(cmd, scope);
            cmd.Parameters.Add(new NpgsqlParameter("wt", NpgsqlDbType.Text) { Value = (object?)filter.WorkflowType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Text) { Value = (object?)filter.BuildId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Integer) { Value = (int)filter.State });
            cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = filter.StartedSince is { } since ? since.UtcDateTime : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = filter.After is { } after ? after.StartedAt.UtcDateTime : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = filter.After is { } cursor ? cursor.RunId : Guid.Empty });
            cmd.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = limit });
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var runs = new List<RunSummary>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runs.Add(new RunSummary(
                    new RunKey(scope.TenantId, scope.NamespaceId, reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    (RunState)reader.GetInt16(4), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc))));
            }

            return runs;
        }).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken)
    {
        return await Retry<IReadOnlyList<RunGroup>>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = """
                SELECT workflow_type, build_id,
                       count(*) FILTER (WHERE state = 0),
                       count(*) FILTER (WHERE state <> 0 AND (@since::timestamptz IS NULL OR created_at >= @since))
                FROM kairos.runs
                WHERE tenant_id = @t AND namespace_id = @n
                GROUP BY workflow_type, build_id
                ORDER BY workflow_type, build_id
                """;
            Scope(cmd, scope);
            cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = closedSince is { } since ? since.UtcDateTime : DBNull.Value });
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var groups = new List<RunGroup>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups.Add(new RunGroup(reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2), (int)reader.GetInt64(3)));
            }

            return groups;
        }).ConfigureAwait(false);
    }

    public async ValueTask<WorkBacklog?> GetBacklogAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await Retry<WorkBacklog?>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = """
                SELECT (SELECT count(*) FROM kairos.workflow_tasks WHERE tenant_id = @t AND namespace_id = @n AND NOT leased AND NOT stuck AND visible_at <= @now),
                       (SELECT count(*) FROM kairos.activity_tasks WHERE tenant_id = @t AND namespace_id = @n AND NOT leased AND visible_at <= @now),
                       (SELECT count(*) FROM kairos.timers WHERE tenant_id = @t AND namespace_id = @n AND due_at <= @now)
                """;
            Scope(cmd, scope);
            cmd.Parameters.Add(Ts("now", now));
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new WorkBacklog((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2));
        }).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StuckWorkflowTask>> GetStuckWorkflowTasksAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        return await Retry<IReadOnlyList<StuckWorkflowTask>>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = """
                SELECT w.run_id, r.workflow_type, r.build_id, w.attempt - 1
                FROM kairos.workflow_tasks w
                JOIN kairos.runs r ON r.tenant_id = w.tenant_id AND r.namespace_id = w.namespace_id AND r.run_id = w.run_id
                WHERE w.tenant_id = @t AND w.namespace_id = @n AND w.stuck
                ORDER BY w.run_id
                """;
            Scope(cmd, scope);
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var stuck = new List<StuckWorkflowTask>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stuck.Add(new StuckWorkflowTask(new RunKey(scope.TenantId, scope.NamespaceId, reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            }

            return stuck;
        }).ConfigureAwait(false);
    }

    public async ValueTask<bool> ResumeWorkflowTaskAsync(RunKey run, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await InTransaction(run.Scope, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                Clk("""
                UPDATE kairos.workflow_tasks SET stuck = false, leased = false, attempt = 1, visible_at = @clock
                WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r AND stuck
                """), conn, tx);
            cmd.Parameters.AddWithValue("t", run.TenantId);
            cmd.Parameters.AddWithValue("n", run.NamespaceId);
            cmd.Parameters.AddWithValue("r", run.RunId);
            cmd.Parameters.Add(Ts("now", now));
            if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }

            await AuditAsync(conn, tx, run, "ResumeWorkflowTask", actor, now, "unparked after repeated lease expiry", cancellationToken).ConfigureAwait(false);
            await NotifyAsync(conn, tx, run.Scope, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static readonly string[] FailRunStatements =
    [
        "UPDATE kairos.runs SET state = 2 WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
        "DELETE FROM kairos.workflow_tasks WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
        "DELETE FROM kairos.activity_tasks WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
        "DELETE FROM kairos.timers WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r",
    ];

    public async ValueTask<bool> FailRunAsync(RunKey run, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await InTransaction(run.Scope, async (conn, tx) =>
        {
            RunHead? head = await LockRunAsync(conn, tx, run, cancellationToken).ConfigureAwait(false);
            if (head is not { State: 0 })
            {
                return false;
            }

            await AppendAsync(conn, tx, run, head, [new RunTerminated(run.TenantId, run.NamespaceId, run.RunId, 0, now, reason)], cancellationToken).ConfigureAwait(false);
            foreach (string sql in FailRunStatements)
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("t", run.TenantId);
                cmd.Parameters.AddWithValue("n", run.NamespaceId);
                cmd.Parameters.AddWithValue("r", run.RunId);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AuditAsync(conn, tx, run, "FailRun", actor, now, reason, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> GetAuditLogAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        return await Retry<IReadOnlyList<AuditEntry>>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = "SELECT run_id, action, actor, at, detail FROM kairos.audit_log WHERE tenant_id = @t AND namespace_id = @n ORDER BY id";
            Scope(cmd, scope);
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var entries = new List<AuditEntry>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new AuditEntry(
                    scope.TenantId, scope.NamespaceId, reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero), reader.GetString(4)));
            }

            return entries;
        }).ConfigureAwait(false);
    }

    private static async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, string action, string actor, DateTimeOffset at, string detail, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO kairos.audit_log (tenant_id, namespace_id, run_id, at, action, actor, detail) VALUES (@t, @n, @r, @at, @action, @actor, @detail)", conn, tx);
        cmd.Parameters.AddWithValue("t", run.TenantId);
        cmd.Parameters.AddWithValue("n", run.NamespaceId);
        cmd.Parameters.AddWithValue("r", run.RunId);
        cmd.Parameters.Add(Ts("at", at));
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("detail", detail);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---- timers -----------------------------------------------------------------------------------------------

    public async ValueTask<int> FireDueTimersAsync(TenantScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int total = 0;
        for (int batch = 0; batch < MaintenanceMaxBatches; batch++)
        {
            int fired = await InTransaction(scope, (conn, tx) => FireTimerBatchAsync(conn, tx, scope, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            total += fired;
            if (fired == 0)
            {
                break;
            }
        }

        return total;
    }

    private async Task<int> FireTimerBatchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var runIds = new List<Guid>();
        await using (var find = new NpgsqlCommand(
            Clk("""
            SELECT DISTINCT run_id FROM kairos.timers
            WHERE tenant_id = @t AND namespace_id = @n AND due_at <= @clock
            ORDER BY run_id LIMIT @limit
            """), conn, tx))
        {
            find.Parameters.AddWithValue("t", scope.TenantId);
            find.Parameters.AddWithValue("n", scope.NamespaceId);
            find.Parameters.AddWithValue("limit", MaintenanceBatchRuns);
            find.Parameters.Add(Ts("now", now));
            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                runIds.Add(reader.GetGuid(0));
            }
        }

        if (runIds.Count == 0)
        {
            return 0;
        }

        // Lock the runs first (skipping busy ones), then take only the timers of runs we hold: lock order is always run, then children.
        Dictionary<Guid, RunHead> heads = await LockRunsSkipLockedAsync(conn, tx, scope, [.. runIds], ct).ConfigureAwait(false);
        if (heads.Count == 0)
        {
            return 0;
        }

        var due = new List<(DateTime At, Guid RunId, int TimerId)>();
        await using (var take = new NpgsqlCommand(
            Clk("""
            DELETE FROM kairos.timers
            WHERE tenant_id = @t AND namespace_id = @n AND run_id = ANY(@ids) AND due_at <= @clock
            RETURNING due_at, run_id, timer_id
            """), conn, tx))
        {
            take.Parameters.AddWithValue("t", scope.TenantId);
            take.Parameters.AddWithValue("n", scope.NamespaceId);
            take.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = heads.Keys.ToArray() });
            take.Parameters.Add(Ts("now", now));
            await using NpgsqlDataReader reader = await take.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

        foreach ((DateTime _, Guid runId, int timerId) in due)
        {
            var run = new RunKey(scope.TenantId, scope.NamespaceId, runId);
            RunHead head = heads[runId];
            if (head.State != 0)
            {
                continue;
            }

            HistoryEvent fired = await AppendAsync(
                conn, tx, run, head, [new TimerFired(scope.TenantId, scope.NamespaceId, runId, 0, now, timerId)], ct).ConfigureAwait(false);
            heads[runId] = head with { LastSeq = fired.Sequence, LastHash = fired.Hash };
            await EnsureWorkflowTaskAsync(conn, tx, run, now, ct).ConfigureAwait(false);
        }

        if (due.Count > 0)
        {
            await NotifyAsync(conn, tx, scope, ct).ConfigureAwait(false);
        }

        return due.Count;
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

    public async ValueTask<RunKey?> GetCurrentRunAsync(TenantScope scope, string workflowId, CancellationToken cancellationToken)
    {
        return await Retry<RunKey?>(async () =>
        {
            await using NpgsqlBatch batch = Batch(scope);
            NpgsqlBatchCommand cmd = batch.CreateBatchCommand();
            cmd.CommandText = "SELECT run_id FROM kairos.runs WHERE tenant_id = @t AND namespace_id = @n AND workflow_id = @wf AND is_current";
            Scope(cmd, scope);
            cmd.Parameters.AddWithValue("wf", workflowId);
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new RunKey(scope.TenantId, scope.NamespaceId, reader.GetGuid(0)) : null;
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
                       (SELECT count(*) FROM kairos.timers),
                       (SELECT count(*) FROM kairos.workflow_tasks WHERE stuck)
                """;
            batch.BatchCommands.Add(cmd);
            await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            batch.Connection = conn;
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new StoreStats((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2), (int)reader.GetInt64(3), (int)reader.GetInt64(4), (int)reader.GetInt64(5));
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
                    UNION ALL SELECT CASE WHEN leased THEN lease_expires_at ELSE visible_at END FROM kairos.workflow_tasks WHERE NOT stuck
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
        NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, RunHead head, List<HistoryEvent> templates, CancellationToken ct)
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

    private async Task EnsureWorkflowTaskAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunKey run, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            Clk("""
            INSERT INTO kairos.workflow_tasks (tenant_id, namespace_id, run_id, build_id, visible_at)
            SELECT tenant_id, namespace_id, run_id, build_id, @clock FROM kairos.runs
            WHERE tenant_id = @t AND namespace_id = @n AND run_id = @r
            ON CONFLICT DO NOTHING
            """), conn, tx);
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

    private static long s_deadlockRetries;
    private static long s_retriesExhausted;

    /// <summary>Deadlocks / serialization failures that were retried successfully, process-wide (also exported as a metric).</summary>
    public static long DeadlockRetries => Interlocked.Read(ref s_deadlockRetries);

    /// <summary>Operations that failed because retries ran out. Must stay zero; an alert condition.</summary>
    public static long RetriesExhausted => Interlocked.Read(ref s_retriesExhausted);

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
                Interlocked.Increment(ref s_deadlockRetries);
                KairosMetrics.StoreRetries.Add(1);
                await Task.Delay(TimeSpan.FromMilliseconds(5 * attempt)).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                Interlocked.Increment(ref s_retriesExhausted);
                throw;
            }
        }
    }
}


public enum LeaseClock
{
    /// <summary>Lease and scheduling times come from the database clock. Default; immune to skewed app servers.</summary>
    Database,

    /// <summary>They come from the caller's <c>now</c>. For simulations and tests that run on virtual time.</summary>
    Caller,
}

public sealed class PostgresStoreOptions
{
    public LeaseClock LeaseClock { get; init; } = LeaseClock.Database;

    public StoreLimits Limits { get; init; } = new();
}
