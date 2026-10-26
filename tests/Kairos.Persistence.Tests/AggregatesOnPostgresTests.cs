using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The aggregates against real storage: the tables the server answers from hold nothing for the wrong scope or the wrong role, projecting is bounded and resumable, concurrent projectors do not
/// count twice, and a history that does not verify is not counted.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class AggregatesOnPostgresTests(PostgresFixture fixture)
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));
    private static readonly string[] Tables = ["usage_by_run", "pending_approvals"];

    private static readonly WorkflowRegistry Workflows = new WorkflowRegistry().Register("agent", KairosClient.DefaultBuildId, async (ctx, input) =>
    {
        _ = ctx.IsPatched(RunProjection.ApprovalRequestedMarker + System.Text.Encoding.UTF8.GetString(input));
        _ = await ctx.ExecuteActivityAsync("model", input);
        _ = await ctx.WaitForSignalAsync("decide");
        return input;
    });

    private static readonly ActivityRegistry Activities = new ActivityRegistry().Register("model", (context, input, _) =>
    {
        context.ReportUsage(new UsageRecord(100, 10, 20, "model-x"));
        return Task.FromResult(input);
    });

    private static async Task<List<RunKey>> StartAgentsAsync(PostgresKairosStore store, VirtualTimeProvider time, TenantScope scope, int count, string prefix)
    {
        var client = new KairosClient(store, time);
        var runs = new List<RunKey>();
        for (int i = 0; i < count; i++)
        {
            StartRunResult r = await client.StartAsync(scope, $"{prefix}-{i}", "agent", System.Text.Encoding.UTF8.GetBytes($"{prefix}-ap-{i}"));
            runs.Add(new RunKey(scope.TenantId, scope.NamespaceId, r.RunId));
        }

        await Driver.DriveAsync(new WorkerRuntime(store, scope, Workflows, Activities, time), maxSteps: 5000);
        return runs;
    }

    private static async Task<long> Behind(TestDatabase db, TenantScope scope)
    {
        await using NpgsqlCommand cmd = db.Superuser.CreateCommand(
            $"SELECT count(*) FROM kairos.runs WHERE tenant_id = '{scope.TenantId}' AND namespace_id = '{scope.NamespaceId}' AND last_seq > projected_seq");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> SqlState(NpgsqlDataSource ds, string sql, TenantScope? scope)
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

    private static async Task<long> CountAsync(NpgsqlDataSource ds, string table, TenantScope? scope)
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

    [Fact]
    public async Task The_tables_are_invisible_without_a_scope_and_to_the_wrong_scope_and_the_reporting_role_cannot_write()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        await StartAgentsAsync(store, time, A, 2, "a");
        await StartAgentsAsync(store, time, B, 1, "b");
        await store.ProjectAggregatesAsync(A, 100, default);
        await store.ProjectAggregatesAsync(B, 100, default);

        foreach (string table in Tables)
        {
            Assert.Equal(0, await CountAsync(db.DataPlane, table, scope: null)); // no scope set: no rows, not an error
            Assert.True(await CountAsync(db.Superuser, table, scope: null) >= 3);
        }

        Assert.Equal(2, await CountAsync(db.DataPlane, "pending_approvals", A));
        Assert.Equal(1, await CountAsync(db.DataPlane, "pending_approvals", B));
        Assert.Equal(1, await CountAsync(db.DataPlane, "usage_by_run", B));
        Assert.Equal(2, await CountAsync(db.ReadOnly, "pending_approvals", A));

        // A scope cannot write a row for another, and the reporting role cannot write at all.
        Assert.Equal("42501", await SqlState(db.DataPlane,
            $"INSERT INTO kairos.pending_approvals (tenant_id, namespace_id, run_id, approval_id, requested_at) VALUES ('{B.TenantId}', '{B.NamespaceId}', '{Guid.NewGuid()}', 'forged', now())", A));
        Assert.Equal("42501", await SqlState(db.ReadOnly, "DELETE FROM kairos.pending_approvals", A));
        Assert.Equal("42501", await SqlState(db.ReadOnly, "UPDATE kairos.usage_by_run SET steps = 0", A));
        Assert.Equal(2, await CountAsync(db.DataPlane, "pending_approvals", A));
    }

    [Fact]
    public async Task Projecting_is_bounded_resumes_where_it_stopped_and_has_nothing_left_to_do_after()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        await StartAgentsAsync(store, time, A, 5, "a");
        Assert.Equal(5, await Behind(db, A));

        Assert.Equal(2, await store.ProjectAggregatesAsync(A, 2, default));
        Assert.Equal(3, await Behind(db, A));
        Assert.Equal(2, await store.ProjectAggregatesAsync(A, 2, default));
        Assert.Equal(1, await store.ProjectAggregatesAsync(A, 2, default));
        Assert.Equal(0, await store.ProjectAggregatesAsync(A, 2, default));
        Assert.Equal(0, await Behind(db, A));

        // New events make the run "behind" again, and only that run is read.
        var client = new KairosClient(store, time);
        RunKey first = (await store.GetCurrentRunAsync(A, "a-0", default))!.Value;
        await client.SignalAsync(first, "decide", []);
        await Driver.DriveAsync(new WorkerRuntime(store, A, Workflows, Activities, time));
        Assert.Equal(1, await Behind(db, A));
        Assert.Equal(1, await store.ProjectAggregatesAsync(A, 100, default));
        Assert.Equal(4, (await store.GetPendingApprovalsAsync(A, 100, null, default)).Total); // the closed run no longer waits
    }

    [Fact]
    public async Task Projectors_running_at_the_same_time_neither_fail_nor_count_twice()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        await StartAgentsAsync(store, time, A, 12, "a");

        int projected = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                Interlocked.Add(ref projected, await store.ProjectAggregatesAsync(A, 5, default));
                await store.GetPendingApprovalsAsync(A, 100, null, default);
            }
        })));
        Assert.True(projected <= 12, $"{projected} projections for 12 runs: a run was projected twice"); // the loser of a race is rolled back, not counted

        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        UsageSummary answered = await store.GetUsageSummaryAsync(A, today, today, default);
        UsageSummary scanned = await RunAggregates.ScanUsageAsync(store, A, today, today, default);
        Assert.Equal(scanned.Buckets, answered.Buckets);
        Assert.Equal(scanned.Runs, answered.Runs);
        Assert.Equal(12, answered.Buckets.Single().Steps);
        Assert.Equal(12, (await store.GetPendingApprovalsAsync(A, 100, null, default)).Total);
        Assert.Equal(0, await Behind(db, A));
    }

    [Fact]
    public async Task A_history_that_does_not_verify_is_not_counted_and_is_reported_as_behind()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        List<RunKey> runs = await StartAgentsAsync(store, time, A, 3, "a");
        await using (NpgsqlCommand tamper = db.Superuser.CreateCommand(
            $"UPDATE kairos.history_events SET body = set_byte(body, length(body) - 1, get_byte(body, length(body) - 1) # 255) WHERE run_id = '{runs[1].RunId}' AND seq = 1"))
        {
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        PendingApprovalPage page = await store.GetPendingApprovalsAsync(A, 100, null, default);

        Assert.Equal(2, page.Total); // the run whose history does not hold is not counted
        Assert.Equal(1, page.Behind); // and says so, rather than looking complete
        Assert.DoesNotContain(page.Items, i => i.Run == runs[1]);
    }

    [Fact]
    public async Task Pending_approvals_page_in_the_database_exactly_as_the_scan_pages()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        for (int i = 0; i < 4; i++)
        {
            await StartAgentsAsync(store, time, A, 2, "round" + i); // two runs per round share a timestamp, so ties are broken by id
            time.Advance(TimeSpan.FromMinutes(1));
        }

        PendingApprovalCursor? after = null;
        var fromDatabase = new List<PendingApprovalItem>();
        while (true)
        {
            PendingApprovalPage page = await store.GetPendingApprovalsAsync(A, 3, after, default);
            fromDatabase.AddRange(page.Items);
            if (page.Items.Count < 3)
            {
                break;
            }

            after = page.Items[^1].Cursor;
        }

        PendingApprovalPage scanned = await RunAggregates.ScanPendingAsync(store, A, 100, null, default);
        Assert.Equal(scanned.Items, fromDatabase);
        Assert.Equal(8, fromDatabase.Count);
    }
}
