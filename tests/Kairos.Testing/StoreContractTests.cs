using System.Text;

namespace Kairos.Testing;

/// <summary>
/// The behaviour every <see cref="IKairosStore"/> must have. Subclass and supply a fresh, empty store; the in-memory
/// reference and PostgreSQL run exactly the same tests. Covers task fencing, tenant isolation and the other store guarantees.
/// </summary>
public abstract partial class StoreContractTests
{
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    protected static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    private static readonly string[] DefaultBuild = [KairosClient.DefaultBuildId];

    protected VirtualTimeProvider Time { get; } = new();

    protected Task<IKairosStore> CreateStoreAsync() => CreateStoreAsync(new StoreLimits());

    /// <summary>A store with no data in it and the given history cap. Called once per test.</summary>
    protected abstract Task<IKairosStore> CreateStoreAsync(StoreLimits limits);

    /// <summary>Resource counters for the scope (the stuck-task checks).</summary>
    protected abstract Task<StoreStats> StatsAsync(IKairosStore store, TenantScope scope);

    protected static byte[] B8(string s) => Encoding.UTF8.GetBytes(s);

    protected static string Str(byte[] b) => Encoding.UTF8.GetString(b);

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<(IKairosStore Store, KairosClient Client)> NewAsync()
    {
        IKairosStore store = await CreateStoreAsync();
        return (store, new KairosClient(store, Time));
    }

    private static WorkflowRegistry Workflows(string build, WorkflowFunc f) => new WorkflowRegistry().Register("wf", build, f);

    private static ActivityRegistry Activities() =>
        new ActivityRegistry().Register("echo", (_, input, _) => Task.FromResult(input));

    private async Task DriveAsync(IKairosStore store, TenantScope scope, WorkflowRegistry wf, ActivityRegistry act, int maxSteps = 200)
    {
        var worker = new WorkerRuntime(store, scope, wf, act, Time);
        for (int i = 0; i < maxSteps; i++)
        {
            bool did = await worker.ProcessWorkflowTaskAsync(Ct);
            did |= await worker.ProcessActivityTaskAsync(Ct);
            did |= await worker.FireTimersAsync(Ct) > 0;
            if (!did)
            {
                return;
            }
        }
    }

    private static void AssertChainIntact(IReadOnlyList<HistoryEvent> history)
    {
        HashChain.Verify(history);
        Assert.Equal(Enumerable.Range(1, history.Count).Select(i => (long)i), history.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Workflow_runs_to_completion_with_an_intact_chain()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "order-1", "wf", B8("in"), cancellationToken: Ct);
        Assert.True(start.Started);

        await DriveAsync(store, A, Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input)), Activities());

        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        Assert.Equal(RunState.Completed, await client.GetStateAsync(key, Ct));
        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(key, Ct);
        AssertChainIntact(history);
        Assert.Equal("in", Str(((WorkflowCompleted)history[^1]).Result));
    }

    [Fact]
    public async Task Runs_are_listed_newest_first_in_pages_and_filtered_by_type_build_and_state()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        try
        {
            await store.ListRunsAsync(A, new RunFilter(), 1, Ct);
        }
        catch (NotSupportedException)
        {
            return; // a store that cannot list says so (and the replay check built on it refuses to run)
        }

        var started = new List<Guid>();
        foreach ((string id, string type, string build) in new[] { ("r1", "wf", "default"), ("r2", "wf", "b2"), ("r3", "other", "default"), ("r4", "wf", "default") })
        {
            started.Add((await client.StartAsync(A, id, type, [], build, Ct)).RunId);
            Time.Advance(TimeSpan.FromMinutes(1));
        }

        await client.StartAsync(B, "foreign", "wf", [], cancellationToken: Ct);

        // Newest first, paged by cursor, with nothing repeated or missed.
        var seen = new List<RunSummary>();
        RunCursor? cursor = null;
        while (true)
        {
            IReadOnlyList<RunSummary> page = await store.ListRunsAsync(A, new RunFilter { After = cursor }, 3, Ct);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page);
            cursor = page[^1].Cursor;
        }

        Assert.Equal(Enumerable.Reverse(started), seen.Select(r => r.Run.RunId));
        Assert.All(seen, r => Assert.Equal(A, r.Run.Scope));
        Assert.Equal(["wf", "wf", "other", "wf"], Enumerable.Reverse(seen).Select(r => r.WorkflowType));
        Assert.Equal(["r4", "r3", "r2", "r1"], seen.Select(r => r.WorkflowId));

        Assert.Equal(2, (await store.ListRunsAsync(A, new RunFilter { WorkflowType = "wf", BuildId = "default" }, 10, Ct)).Count);
        Assert.Single(await store.ListRunsAsync(A, new RunFilter { BuildId = "b2" }, 10, Ct));
        Assert.Equal(4, (await store.ListRunsAsync(A, new RunFilter { State = RunStateFilter.Open }, 10, Ct)).Count);
        Assert.Empty(await store.ListRunsAsync(A, new RunFilter { State = RunStateFilter.Closed }, 10, Ct));
        Assert.Single(await store.ListRunsAsync(A, new RunFilter(), 1, Ct));

        // Finish the default-build runs of "wf" and "other"; the b2 run stays open.
        WorkflowFunc echo = (_, input) => Task.FromResult(input);
        await DriveAsync(store, A, new WorkflowRegistry().Register("wf", "default", echo).Register("other", "default", echo), Activities());
        Assert.Equal(RunState.Completed, (await store.ListRunsAsync(A, new RunFilter { WorkflowType = "other" }, 10, Ct)).Single().State);
        Assert.Equal(3, (await store.ListRunsAsync(A, new RunFilter { State = RunStateFilter.Closed }, 10, Ct)).Count);

        // StartedSince trims closed runs only; the open one is listed whenever it started.
        DateTimeOffset cutoff = Time.GetUtcNow();
        IReadOnlyList<RunSummary> recent = await store.ListRunsAsync(A, new RunFilter { StartedSince = cutoff }, 10, Ct);
        Assert.Equal(["r2"], recent.Select(r => r.WorkflowId));

        Assert.Equal(
            [("other", "default", 0, 1), ("wf", "b2", 1, 0), ("wf", "default", 0, 2)],
            (await store.GetRunGroupsAsync(A, null, Ct)).Select(g => (g.WorkflowType, g.BuildId, g.Open, g.Closed)));
        Assert.Equal(
            [("other", "default", 0, 0), ("wf", "b2", 1, 0), ("wf", "default", 0, 0)],
            (await store.GetRunGroupsAsync(A, cutoff, Ct)).Select(g => (g.WorkflowType, g.BuildId, g.Open, g.Closed)));
        Assert.Equal([("wf", "default", 1, 0)], (await store.GetRunGroupsAsync(B, null, Ct)).Select(g => (g.WorkflowType, g.BuildId, g.Open, g.Closed)));
    }

    [Fact]
    public async Task An_export_returns_the_history_and_records_who_and_why_in_the_same_step()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "x-1", "wf", B8("in"), cancellationToken: Ct);
        await DriveAsync(store, A, Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input)), Activities());
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        try
        {
            Assert.Null(await store.ExportHistoryAsync(new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid()), "ops@example.com", null, "why", false, Time.GetUtcNow(), Ct));
        }
        catch (NotSupportedException)
        {
            return;
        }

        Assert.Empty(await store.GetAuditLogAsync(A, Ct)); // a run that does not exist is not an export

        IReadOnlyList<HistoryEvent> exported = (await store.ExportHistoryAsync(run, "ops@example.com", null, "investigating ticket 4411", false, Time.GetUtcNow(), Ct))!;
        await store.ExportHistoryAsync(run, "ceo@example.com", null, "again", true, Time.GetUtcNow(), Ct);
        await store.ExportHistoryAsync(run, "ignored", "operator:alice", "verified", false, Time.GetUtcNow(), Ct);

        Assert.Equal((await store.GetHistoryAsync(run, Ct)).Select(e => Convert.ToHexString(e.Hash)), exported.Select(e => Convert.ToHexString(e.Hash)));
        AssertChainIntact(exported);
        IReadOnlyList<AuditEntry> audit = await store.GetAuditLogAsync(A, Ct);
        Assert.Equal(3, audit.Count);
        string head = Convert.ToHexStringLower(exported[^1].Hash);
        Assert.Equal(("ExportHistory", $"investigating ticket 4411 (head {head})", run.RunId), (audit[0].Action, audit[0].Detail, audit[0].RunId));
        Assert.Equal($"metadata only: again (head {head})", audit[1].Detail);

        // Who did it comes from the store, not from the caller: the name that was asked for is kept apart, as a claim, and changes nothing else.
        Assert.False(string.IsNullOrEmpty(audit[0].SessionUser));
        Assert.Equal("db:" + audit[0].SessionUser, audit[0].Actor);
        Assert.Equal("ops@example.com", audit[0].ClaimedActor);
        Assert.Equal("ceo@example.com", audit[1].ClaimedActor);
        Assert.Equal((audit[0].Actor, audit[0].SessionUser, audit[0].ClientAddress), (audit[1].Actor, audit[1].SessionUser, audit[1].ClientAddress));
        Assert.DoesNotContain("ceo", audit[1].Actor, StringComparison.Ordinal);
        Assert.Null(audit[0].VerifiedPrincipal);

        // A verified principal is recorded as the actor, and a claim cannot displace it.
        Assert.Equal(("operator:alice", "operator:alice", "ignored"), (audit[2].Actor, audit[2].VerifiedPrincipal, audit[2].ClaimedActor));
        Assert.Equal(audit[0].SessionUser, audit[2].SessionUser);
        Assert.Empty(await store.GetAuditLogAsync(B, Ct));
        Assert.Null(await store.ExportHistoryAsync(new RunKey(B.TenantId, B.NamespaceId, run.RunId), "mallory", null, "curious", false, Time.GetUtcNow(), Ct)); // another tenant cannot export it
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ExportHistoryAsync(run, "ops", null, " ", false, Time.GetUtcNow(), Ct)); // no reason, no export
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ExportHistoryAsync(run, "", null, "why", false, Time.GetUtcNow(), Ct));
        Assert.Equal(3, (await store.GetAuditLogAsync(A, Ct)).Count);
    }

    [Fact]
    public async Task The_backlog_breaks_ready_workflow_tasks_down_by_build()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        if (await store.GetBacklogAsync(A, Time.GetUtcNow(), Ct) is null)
        {
            return;
        }

        await client.StartAsync(A, "by-1", "wf", [], buildId: "build-x", cancellationToken: Ct);
        await client.StartAsync(A, "by-2", "wf", [], buildId: "build-x", cancellationToken: Ct);
        await client.StartAsync(A, "by-3", "wf", [], buildId: "build-y:with:colons", cancellationToken: Ct);

        WorkBacklog backlog = (await store.GetBacklogAsync(A, Time.GetUtcNow(), Ct))!;

        Assert.Equal(3, backlog.WorkflowTasks);
        Assert.Equal(new Dictionary<string, int> { ["build-x"] = 2, ["build-y:with:colons"] = 1 }, backlog.WorkflowTasksByBuild.ToDictionary(p => p.Key, p => p.Value));
        Assert.Empty((await store.GetBacklogAsync(B, Time.GetUtcNow(), Ct))!.WorkflowTasksByBuild);
    }

    [Fact]
    public async Task The_backlog_counts_ready_work_of_this_scope_only()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        WorkBacklog? empty = await store.GetBacklogAsync(A, Time.GetUtcNow(), Ct);
        if (empty is null)
        {
            return; // a store that cannot say is allowed to say nothing
        }

        Assert.Equal(new WorkBacklog(0, 0, 0), empty);
        await client.StartAsync(A, "b-1", "wf", [], cancellationToken: Ct);
        await client.StartAsync(A, "b-2", "wf", [], cancellationToken: Ct);

        Assert.Equal(new WorkBacklog(2, 0, 0), await store.GetBacklogAsync(A, Time.GetUtcNow(), Ct));
        Assert.Equal(2, (await store.GetBacklogAsync(A, Time.GetUtcNow(), Ct))!.WorkflowTasksByBuild.Values.Sum()); // and by build, when the store can say
        Assert.Equal(new WorkBacklog(0, 0, 0), await store.GetBacklogAsync(B, Time.GetUtcNow(), Ct));

        WorkflowTask? leased = await store.PollWorkflowTaskAsync(A, ["default"], Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct);
        Assert.NotNull(leased);
        Assert.Equal(1, (await store.GetBacklogAsync(A, Time.GetUtcNow(), Ct))!.WorkflowTasks); // a leased task is not ready
    }

    [Fact]
    public async Task Trace_contexts_are_recorded_in_history_and_reach_the_activity_task()
    {
        const string Start = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        const string Scheduler = "00-4bf92f3577b34da6a3ce929d0e0e4736-b7ad6b7169203331-01";
        const string Sender = "00-0af7651916cd43dd8448eb211c80319c-b9c7c989f97918e1-01";
        (IKairosStore store, _) = await NewAsync();
        var run = new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid());
        await store.StartRunAsync(new StartRunRequest(run, "t-1", "wf", "default", [], Time.GetUtcNow(), Start), Ct);
        Assert.True(await store.SignalRunTracedAsync(run, "go", "sig-1", [], Time.GetUtcNow(), Sender, Ct));

        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, ["default"], Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct))!;
        Assert.Equal(Start, ((WorkflowStarted)task.History[0]).TraceContext);
        Assert.Equal(Sender, ((SignalReceived)task.History[1]).TraceContext);
        var scheduled = new ActivityScheduled(run.TenantId, run.NamespaceId, run.RunId, 0, Time.GetUtcNow(), 1, "work", [], RetryPolicy.Default) { TraceContext = Scheduler };
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(task, [scheduled], Time.GetUtcNow(), Ct));

        ActivityTask? activity = await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(Scheduler, activity!.TraceContext);
        IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(run, Ct);
        AssertChainIntact(history); // the context is part of what the chain covers
        Assert.Equal(Scheduler, history.OfType<ActivityScheduled>().Single().TraceContext);
    }

    [Fact]
    public async Task A_continued_run_keeps_the_trace_of_the_run_it_continues()
    {
        const string Start = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        (IKairosStore store, _) = await NewAsync();
        var run = new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid());
        await store.StartRunAsync(new StartRunRequest(run, "t-2", "wf", "default", [], Time.GetUtcNow(), Start), Ct);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, ["default"], Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct))!;
        Guid next = Guid.NewGuid();
        await store.CompleteWorkflowTaskAsync(task, [new WorkflowContinued(run.TenantId, run.NamespaceId, run.RunId, 0, Time.GetUtcNow(), next, [])], Time.GetUtcNow(), Ct);

        IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(new RunKey(run.TenantId, run.NamespaceId, next), Ct);
        Assert.Equal(Start, ((WorkflowStarted)history[0]).TraceContext);
    }

    [Fact]
    public async Task A_run_records_who_started_it_and_a_continued_run_keeps_it()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult started = await client.StartAsync(new Principal(A, Role.Operator, "user-1", null), "by-1", "wf", [], cancellationToken: Ct);
        StartRunResult anonymous = await client.StartAsync(A, "by-2", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, started.RunId);

        IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(run, Ct);
        Assert.Equal("Operator:user-1", ((WorkflowStarted)history[0]).StartedBy);
        AssertChainIntact(history); // the starter is part of what the chain covers
        Assert.Equal("", ((WorkflowStarted)(await store.GetHistoryAsync(new RunKey(A.TenantId, A.NamespaceId, anonymous.RunId), Ct))[0]).StartedBy);

        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, ["default"], Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct))!;
        Assert.Equal(run.RunId, task.Run.RunId); // the first started is polled first; the continuation below is of this one
        Guid next = Guid.NewGuid();
        await store.CompleteWorkflowTaskAsync(task, [new WorkflowContinued(run.TenantId, run.NamespaceId, run.RunId, 0, Time.GetUtcNow(), next, [])], Time.GetUtcNow(), Ct);
        Assert.Equal("Operator:user-1", ((WorkflowStarted)(await store.GetHistoryAsync(new RunKey(run.TenantId, run.NamespaceId, next), Ct))[0]).StartedBy);

        await Assert.ThrowsAsync<ArgumentException>(async () => await client.StartAsync(new Principal(A, Role.Operator, new string('x', 400), null), "by-3", "wf", [], cancellationToken: Ct));
    }

    [Fact]
    public async Task Starting_an_existing_workflow_id_is_deduplicated()
    {
        (_, KairosClient client) = await NewAsync();
        StartRunResult first = await client.StartAsync(A, "same", "wf", [], cancellationToken: Ct);
        StartRunResult second = await client.StartAsync(A, "same", "wf", [], cancellationToken: Ct);

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(first.RunId, second.RunId);
    }

    [Fact]
    public async Task Signal_wakes_a_blocked_run_and_closed_runs_reject_signals()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "w", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, _) => await ctx.WaitForSignalAsync("go"));

        await DriveAsync(store, A, wf, Activities());
        Assert.Equal(RunState.Running, await client.GetStateAsync(key, Ct));

        Assert.True(await client.SignalAsync(key, "go", B8("payload"), Ct));
        await DriveAsync(store, A, wf, Activities());

        Assert.Equal(RunState.Completed, await client.GetStateAsync(key, Ct));
        Assert.False(await client.SignalAsync(key, "go", B8("late"), Ct));
        AssertChainIntact(await client.GetHistoryAsync(key, Ct));
    }

    [Fact]
    public async Task Timers_fire_only_when_due()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "t", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, _) =>
        {
            await ctx.DelayAsync(TimeSpan.FromMinutes(10));
            return B8("done");
        });

        await DriveAsync(store, A, wf, Activities());
        Time.Advance(TimeSpan.FromMinutes(9));
        await DriveAsync(store, A, wf, Activities());
        Assert.Equal(RunState.Running, await client.GetStateAsync(key, Ct));

        Time.Advance(TimeSpan.FromMinutes(2));
        await DriveAsync(store, A, wf, Activities());
        Assert.Equal(RunState.Completed, await client.GetStateAsync(key, Ct));
    }

    [Fact]
    public async Task Failed_activity_attempts_are_retried_after_backoff_and_share_an_idempotency_key()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "r", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        var keys = new List<string>();
        ActivityRegistry act = new ActivityRegistry().Register("flaky", (ctx, _, _) =>
        {
            keys.Add(ctx.IdempotencyKey);
            return ctx.Attempt < 3 ? throw new InvalidOperationException("transient") : Task.FromResult(B8("ok"));
        });
        WorkflowRegistry wf = Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("flaky", input));

        await DriveAsync(store, A, wf, act);
        Assert.Single(keys); // first attempt failed, retry not yet due

        Time.Advance(TimeSpan.FromSeconds(1));
        await DriveAsync(store, A, wf, act);
        Assert.Equal(2, keys.Count);

        Time.Advance(TimeSpan.FromSeconds(2));
        await DriveAsync(store, A, wf, act);

        Assert.Equal(RunState.Completed, await client.GetStateAsync(key, Ct));
        Assert.Equal(3, keys.Count);
        Assert.Single(keys.Distinct());
        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(key, Ct);
        Assert.Equal([1, 2], history.OfType<ActivityFailed>().Select(f => f.Attempt));
        AssertChainIntact(history);
    }

    [Fact]
    public async Task Expired_workflow_lease_is_reclaimed_and_the_old_holder_is_fenced_off()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        await client.StartAsync(A, "l", "wf", [], cancellationToken: Ct);

        WorkflowTask first = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Null(await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct)); // still leased

        Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct)); // expired but not yet reaped
        Assert.Equal(1, await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct));
        Assert.Null(await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct)); // backing off
        Time.Advance(TimeSpan.FromSeconds(2));
        WorkflowTask second = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(first.Run, second.Run);
        Assert.True(second.LeaseToken > first.LeaseToken);
        Assert.Equal(first.Attempt + 1, second.Attempt); // L2: the expiry counted as an attempt

        var done = new WorkflowCompleted(A.TenantId, A.NamespaceId, first.Run.RunId, 0, Time.GetUtcNow(), []);
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteWorkflowTaskAsync(first, [done], Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(second, [done], Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteWorkflowTaskAsync(second, [done], Time.GetUtcNow(), Ct)); // no double apply

        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(first.Run, Ct);
        Assert.Equal(3, history.Count); // started, task boundary, completed
        AssertChainIntact(history);
    }

    [Fact]
    public async Task Completion_after_history_advanced_is_a_sequence_conflict_and_the_task_is_rerun()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "c", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.True(await client.SignalAsync(key, "late", B8("x"), Ct)); // arrives while the worker is busy

        var timer = new TimerStarted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), 1, TimeSpan.FromSeconds(5));
        Assert.Equal(CompletionOutcome.SequenceConflict, await store.CompleteWorkflowTaskAsync(task, [timer], Time.GetUtcNow(), Ct));

        WorkflowTask again = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(2, again.History.Count);
        Assert.IsType<SignalReceived>(again.History[1]);
    }

    [Fact]
    public async Task Stale_activity_completion_is_rejected_and_effect_is_recorded_once()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "a", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input));
        var worker = new WorkerRuntime(store, A, wf, Activities(), Time);
        await worker.ProcessWorkflowTaskAsync(Ct);

        ActivityTask slow = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct))!;
        Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct));
        Time.Advance(TimeSpan.FromSeconds(2)); // retry back-off
        ActivityTask retry = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal(slow.IdempotencyKey, retry.IdempotencyKey);
        Assert.True(retry.LeaseToken > slow.LeaseToken);
        Assert.Equal(slow.Attempt + 1, retry.Attempt);

        var result = new ActivityCompleted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), slow.ActivityId, B8("r"));
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteActivityTaskAsync(slow, result, null, Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteActivityTaskAsync(retry, result, null, Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteActivityTaskAsync(retry, result, null, Time.GetUtcNow(), Ct));

        Assert.Single((await client.GetHistoryAsync(key, Ct)).OfType<ActivityCompleted>());
    }

    [Fact]
    public async Task Workflow_workers_cannot_forge_results_signals_or_timer_firings()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "f", "wf", [], cancellationToken: Ct);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;

        HistoryEvent[] forgeries =
        [
            new ActivityCompleted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), 1, B8("fake")),
            new SignalReceived(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), "go", []),
            new TimerFired(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), 1),
            new WorkflowStarted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), "wf", "default", []),
        ];
        foreach (HistoryEvent forged in forgeries)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await store.CompleteWorkflowTaskAsync(task, [forged], Time.GetUtcNow(), Ct));
        }
    }

    [Fact]
    public async Task Runs_are_pinned_to_their_build()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        await client.StartAsync(A, "p", "wf", [], buildId: "v1", cancellationToken: Ct);

        Assert.Null(await store.PollWorkflowTaskAsync(A, ["v2"], Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct));
        WorkflowTask? task = await store.PollWorkflowTaskAsync(A, ["v2", "v1"], Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct);
        Assert.Equal("v1", task?.BuildId);
    }

    // ---- I5: tenant isolation, fails closed -------------------------------------------------------------------

    [Fact]
    public async Task Tenant_B_cannot_see_poll_signal_or_complete_tenant_A_work()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "secret-flow", "wf", B8("PLAINTEXT-CANARY"), cancellationToken: Ct);
        var aKey = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        var asB = new RunKey(B.TenantId, B.NamespaceId, start.RunId);
        var mixedTenant = new RunKey(B.TenantId, A.NamespaceId, start.RunId);
        var mixedNamespace = new RunKey(A.TenantId, B.NamespaceId, start.RunId);

        // Polls see nothing.
        Assert.Null(await store.PollWorkflowTaskAsync(B, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct));
        Assert.Null(await store.PollActivityTaskAsync(B, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct));

        // Reads under any wrong scope are empty / unknown, never an error that reveals existence.
        foreach (RunKey wrong in new[] { asB, mixedTenant, mixedNamespace })
        {
            Assert.Empty(await store.GetHistoryAsync(wrong, Ct));
            Assert.Null(await store.GetRunStateAsync(wrong, Ct));
            Assert.False(await store.SignalRunAsync(wrong, "x", "s1", [], Time.GetUtcNow(), Ct));
        }

        // A task claimed by A cannot be completed by presenting it under B.
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        WorkflowTask hijack = task with { Run = asB };
        var done = new WorkflowCompleted(B.TenantId, B.NamespaceId, start.RunId, 0, Time.GetUtcNow(), []);
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteWorkflowTaskAsync(hijack, [done], Time.GetUtcNow(), Ct));

        // Timers of A are not fired by B's timer service.
        Assert.Equal(0, await store.FireDueTimersAsync(B, Time.GetUtcNow().AddYears(1), Ct));

        // Same business id in another tenant is a different workflow, not a dedup hit.
        StartRunResult other = await client.StartAsync(B, "secret-flow", "wf", [], cancellationToken: Ct);
        Assert.True(other.Started);
        Assert.NotEqual(start.RunId, other.RunId);

        // Nothing of A's changed.
        Assert.Single(await client.GetHistoryAsync(aKey, Ct), e => e is WorkflowStarted);
        Assert.Equal(RunState.Running, await client.GetStateAsync(aKey, Ct));
    }

    [Fact]
    public async Task Events_for_another_tenant_are_refused_when_starting_or_appending()
    {
        (IKairosStore store, _) = await NewAsync();
        var runId = Guid.NewGuid();
        var key = new RunKey(A.TenantId, A.NamespaceId, runId);
        await store.StartRunAsync(new StartRunRequest(key, "x", "wf", "default", [], Time.GetUtcNow()), Ct);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;

        var foreign = new WorkflowCompleted(B.TenantId, B.NamespaceId, runId, 0, Time.GetUtcNow(), []);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CompleteWorkflowTaskAsync(task, [foreign], Time.GetUtcNow(), Ct));
    }

    // ---- L1: lease renewal (heartbeat) ---------------------------------------------------------------------------

    private async Task<(IKairosStore Store, KairosClient Client, RunKey Run, ActivityTask Task)> ClaimedActivityAsync(
        RetryPolicy? policy = null, TimeSpan? lease = null)
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "hb-" + Guid.NewGuid(), "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input, policy));
        await new WorkerRuntime(store, A, wf, Activities(), Time).ProcessWorkflowTaskAsync(Ct);
        ActivityTask task = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), lease ?? TimeSpan.FromSeconds(10), Ct))!;
        return (store, client, run, task);
    }

    [Fact]
    public async Task Heartbeat_extends_the_lease_so_a_long_attempt_is_not_reaped()
    {
        (IKairosStore store, KairosClient client, RunKey run, ActivityTask task) = await ClaimedActivityAsync();

        for (int i = 0; i < 5; i++) // five times the lease in total, renewed every 8 s
        {
            Time.Advance(TimeSpan.FromSeconds(8));
            Assert.Equal(CompletionOutcome.Applied, await store.HeartbeatActivityTaskAsync(task, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct));
            Assert.Equal(0, await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct));
        }

        var done = new ActivityCompleted(A.TenantId, A.NamespaceId, run.RunId, 0, Time.GetUtcNow(), task.ActivityId, B8("late but fine"));
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteActivityTaskAsync(task, done, null, Time.GetUtcNow(), Ct));
        Assert.DoesNotContain(await client.GetHistoryAsync(run, Ct), e => e is ActivityFailed);
    }

    [Fact]
    public async Task Heartbeat_from_a_holder_that_lost_the_lease_is_refused()
    {
        (IKairosStore store, _, _, ActivityTask task) = await ClaimedActivityAsync();

        Time.Advance(TimeSpan.FromSeconds(11));
        await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);

        Assert.Equal(CompletionOutcome.StaleLease, await store.HeartbeatActivityTaskAsync(task, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Heartbeat_never_shortens_a_lease()
    {
        (IKairosStore store, _, _, ActivityTask task) = await ClaimedActivityAsync(lease: TimeSpan.FromSeconds(60));

        Assert.Equal(CompletionOutcome.Applied, await store.HeartbeatActivityTaskAsync(task, Time.GetUtcNow(), TimeSpan.FromSeconds(1), Ct));
        Time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct));
    }

    // ---- L2: expiry counts as an attempt, backs off, and cannot loop forever -----------------------------------------

    [Fact]
    public async Task Expired_attempt_is_recorded_counted_and_delayed_by_the_retry_policy()
    {
        (IKairosStore store, KairosClient client, RunKey run, ActivityTask first) =
            await ClaimedActivityAsync(RetryPolicy.Default with { MaximumAttempts = 3, InitialInterval = TimeSpan.FromSeconds(5) });

        Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct));

        ActivityFailed failure = Assert.Single((await client.GetHistoryAsync(run, Ct)).OfType<ActivityFailed>());
        Assert.Equal((StoreRules.LeaseExpiredError, 1, true), (failure.ErrorType, failure.Attempt, failure.WillRetry));

        Assert.Null(await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct)); // backing off
        Time.Advance(TimeSpan.FromSeconds(6));
        ActivityTask second = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal(2, second.Attempt);
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public async Task A_poison_activity_that_keeps_killing_its_worker_ends_in_a_recorded_failure()
    {
        (IKairosStore store, KairosClient client, RunKey run, ActivityTask task) =
            await ClaimedActivityAsync(RetryPolicy.Default with { MaximumAttempts = 3, InitialInterval = TimeSpan.FromSeconds(1) });

        int attempts = 1;
        while (true)
        {
            Time.Advance(TimeSpan.FromSeconds(11)); // the worker died; nobody completes or renews
            await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);
            Time.Advance(TimeSpan.FromSeconds(10)); // past any back-off
            ActivityTask? next = await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct);
            if (next is null)
            {
                break;
            }

            attempts++;
            Assert.True(attempts <= 3, "The task was handed out beyond MaximumAttempts.");
            task = next;
        }

        Assert.Equal(3, attempts);
        ActivityFailed[] failures = [.. (await client.GetHistoryAsync(run, Ct)).OfType<ActivityFailed>()];
        Assert.Equal([1, 2, 3], failures.Select(f => f.Attempt));
        Assert.Equal([true, true, false], failures.Select(f => f.WillRetry));
        Assert.All(failures, f => Assert.Equal(StoreRules.LeaseExpiredError, f.ErrorType));

        // The workflow is told: its task is runnable and sees the terminal failure.
        WorkflowTask wf = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Contains(wf.History, e => e is ActivityFailed { WillRetry: false });
        HashChain.Verify(await client.GetHistoryAsync(run, Ct));
    }

    [Fact]
    public async Task A_workflow_task_that_keeps_expiring_backs_off_then_is_parked_not_dropped()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "poison-wf", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        int claims = 0;
        TimeSpan lastGap = TimeSpan.Zero;
        for (int i = 0; i < 30; i++)
        {
            DateTimeOffset before = Time.GetUtcNow();
            WorkflowTask? task = await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct);
            if (task is null)
            {
                // Not offered right now: either backing off or parked. Let time pass and look again.
                Time.Advance(TimeSpan.FromSeconds(70));
                await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);
                if ((await StatsAsync(store, A)).StuckWorkflowTasks == 1)
                {
                    break;
                }

                continue;
            }

            claims++;
            lastGap = Time.GetUtcNow() - before;
            Time.Advance(TimeSpan.FromSeconds(31)); // the worker died holding it
            await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);
        }

        Assert.Equal(StoreRules.MaxWorkflowTaskAttempts, claims);
        Assert.Equal(1, (await StatsAsync(store, A)).StuckWorkflowTasks);
        Time.Advance(TimeSpan.FromDays(30));
        Assert.Null(await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct)); // parked for good
        Assert.Equal(RunState.Running, await client.GetStateAsync(run, Ct)); // never dropped: still open, visible as stuck
        Assert.True(lastGap >= TimeSpan.Zero);
    }

    // ---- L4: graceful shutdown releases leases ----------------------------------------------------------------------

    [Fact]
    public async Task Released_activity_is_claimable_at_once_and_the_release_is_not_an_attempt()
    {
        (IKairosStore store, _, _, ActivityTask task) = await ClaimedActivityAsync();

        await store.ReleaseActivityTaskAsync(task, Time.GetUtcNow(), Time.GetUtcNow(), Ct);
        ActivityTask again = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct))!;

        Assert.Equal(task.Attempt, again.Attempt);
        Assert.True(again.LeaseToken > task.LeaseToken);

        // The old holder can neither renew nor release the new holder's attempt.
        Assert.Equal(CompletionOutcome.StaleLease, await store.HeartbeatActivityTaskAsync(task, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct));
        await store.ReleaseActivityTaskAsync(task, Time.GetUtcNow(), Time.GetUtcNow(), Ct);
        Assert.Null(await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Released_workflow_task_is_claimable_at_once_without_counting_an_attempt()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        await client.StartAsync(A, "rel", "wf", [], cancellationToken: Ct);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;

        await store.ReleaseWorkflowTaskAsync(task, Time.GetUtcNow(), Time.GetUtcNow(), countAttempt: false, Ct);
        WorkflowTask again = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(task.Attempt, again.Attempt);

        await store.ReleaseWorkflowTaskAsync(again, Time.GetUtcNow(), Time.GetUtcNow(), countAttempt: true, Ct);
        WorkflowTask third = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(task.Attempt + 1, third.Attempt);
    }

    [Fact]
    public async Task Worker_shutdown_mid_activity_hands_the_attempt_to_a_peer_immediately()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "drain", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        var started = new TaskCompletionSource();
        ActivityRegistry slow = new ActivityRegistry().Register("echo", async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        WorkflowRegistry wf = Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input));
        var options = new WorkerOptions { AutoHeartbeat = false };
        var draining = new WorkerRuntime(store, A, wf, slow, Time, options);
        await draining.ProcessWorkflowTaskAsync(Ct);

        using var shutdown = new CancellationTokenSource();
        Task<bool> running = draining.ProcessActivityTaskAsync(shutdown.Token).AsTask();
        await started.Task;
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        // No clock advance: the peer gets it right away, as the same attempt, and nothing was recorded as a failure.
        ActivityTask? peer = await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(peer);
        Assert.Equal(1, peer.Attempt);
        Assert.DoesNotContain(await client.GetHistoryAsync(run, Ct), e => e is ActivityFailed);
    }

    [Fact]
    public async Task An_activity_that_loses_its_lease_is_cancelled_and_records_nothing()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "lost", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        bool sawCancellation = false;
        ActivityRegistry act = new ActivityRegistry().Register("echo", async (ctx, input, ct) =>
        {
            entered.SetResult();
            await gate.Task;
            await ctx.HeartbeatAsync(); // learns the lease is gone
            sawCancellation = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            return input;
        });
        WorkflowRegistry wf = Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input));
        var worker = new WorkerRuntime(store, A, wf, act, Time, new WorkerOptions { AutoHeartbeat = false });
        await worker.ProcessWorkflowTaskAsync(Ct);

        Task<bool> running = worker.ProcessActivityTaskAsync(Ct).AsTask();
        await entered.Task;
        Time.Advance(TimeSpan.FromMinutes(2)); // the lease (1 min) expires while the activity is "stuck in a GC pause"
        await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);
        gate.SetResult();

        Assert.True(await running);
        Assert.True(sawCancellation);
        Assert.DoesNotContain(await client.GetHistoryAsync(run, Ct), e => e is ActivityCompleted);
        Assert.Single((await client.GetHistoryAsync(run, Ct)).OfType<ActivityFailed>(), f => f.ErrorType == StoreRules.LeaseExpiredError);
    }

    // ---- F1: parked workflow tasks are visible and recoverable ---------------------------------------------------------

    /// <summary>Starts a run and lets its workflow task expire until the store parks it.</summary>
    private async Task<(IKairosStore Store, KairosClient Client, RunKey Run)> ParkedRunAsync()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "parked-" + Guid.NewGuid(), "wf", [], cancellationToken: Ct);
        for (int i = 0; i < StoreRules.MaxWorkflowTaskAttempts + 2; i++)
        {
            await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct);
            Time.Advance(TimeSpan.FromSeconds(31));
            await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);
            Time.Advance(TimeSpan.FromSeconds(70)); // past any back-off
        }

        Assert.Equal(1, (await StatsAsync(store, A)).StuckWorkflowTasks);
        return (store, client, new RunKey(A.TenantId, A.NamespaceId, start.RunId));
    }

    [Fact]
    public async Task Parked_tasks_are_listed_and_a_resume_lets_the_run_finish()
    {
        (IKairosStore store, KairosClient client, RunKey run) = await ParkedRunAsync();

        StuckWorkflowTask stuck = Assert.Single(await store.GetStuckWorkflowTasksAsync(A, Ct));
        Assert.Equal((run, "wf", "default", StoreRules.MaxWorkflowTaskAttempts), (stuck.Run, stuck.WorkflowType, stuck.BuildId, stuck.Attempts));
        Assert.Empty(await store.GetStuckWorkflowTasksAsync(B, Ct)); // another tenant sees nothing

        Assert.False(await store.ResumeWorkflowTaskAsync(new RunKey(B.TenantId, B.NamespaceId, run.RunId), "mallory", Time.GetUtcNow(), Ct));
        Assert.True(await store.ResumeWorkflowTaskAsync(run, "ops@example.com", Time.GetUtcNow(), Ct));
        Assert.False(await store.ResumeWorkflowTaskAsync(run, "ops@example.com", Time.GetUtcNow(), Ct)); // nothing left to resume
        Assert.Empty(await store.GetStuckWorkflowTasksAsync(A, Ct));

        await DriveAsync(store, A, Workflows("default", (_, _) => Task.FromResult(B8("recovered"))), Activities());

        Assert.Equal(RunState.Completed, await client.GetStateAsync(run, Ct));
        AuditEntry audit = Assert.Single(await store.GetAuditLogAsync(A, Ct));
        Assert.Equal(("ResumeWorkflowTask", "ops@example.com", run.RunId), (audit.Action, audit.ClaimedActor, audit.RunId));
        Assert.Equal("db:" + audit.SessionUser, audit.Actor); // the supplied name is a claim, never the actor
        Assert.Empty(await store.GetAuditLogAsync(B, Ct));
    }

    [Fact]
    public async Task A_parked_run_can_be_failed_by_an_operator_leaving_no_work_behind()
    {
        (IKairosStore store, KairosClient client, RunKey run) = await ParkedRunAsync();

        Assert.False(await store.FailRunAsync(new RunKey(B.TenantId, B.NamespaceId, run.RunId), "x", "mallory", Time.GetUtcNow(), Ct));
        Assert.True(await store.FailRunAsync(run, "bad build 1.2.3; replay cannot succeed", "ops@example.com", Time.GetUtcNow(), Ct));
        Assert.False(await store.FailRunAsync(run, "again", "ops@example.com", Time.GetUtcNow(), Ct));

        Assert.Equal(RunState.Failed, await client.GetStateAsync(run, Ct));
        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(run, Ct);
        Assert.Equal("bad build 1.2.3; replay cannot succeed", Assert.IsType<RunTerminated>(history[^1]).Reason);
        AssertChainIntact(history);
        ReplayResult replay = WorkflowReplayer.Replay(async (ctx, _) => await ctx.WaitForSignalAsync("never"), history);
        Assert.True(replay.IsClosed);

        StoreStats stats = await StatsAsync(store, A); // I9: nothing left behind
        Assert.Equal((0, 0, 0, 0, 0), (stats.OpenRuns, stats.WorkflowTasks, stats.ActivityTasks, stats.Timers, stats.StuckWorkflowTasks));
        Assert.Null(await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow().AddDays(1), TimeSpan.FromSeconds(30), Ct));
        Assert.False(await client.SignalAsync(run, "late", [], Ct));
        Assert.Equal(["FailRun"], (await store.GetAuditLogAsync(A, Ct)).Select(a => a.Action));
    }

    [Fact]
    public async Task Failing_a_run_removes_its_activity_tasks_and_timers_too()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "busy", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, input) =>
        {
            Task<byte[]> work = ctx.ExecuteActivityAsync("echo", input);
            await ctx.DelayAsync(TimeSpan.FromHours(1));
            return await work;
        });
        await new WorkerRuntime(store, A, wf, Activities(), Time).ProcessWorkflowTaskAsync(Ct);
        StoreStats before = await StatsAsync(store, A);
        Assert.Equal((1, 1), (before.ActivityTasks, before.Timers));

        Assert.True(await store.FailRunAsync(run, "operator stop", "ops", Time.GetUtcNow(), Ct));

        StoreStats after = await StatsAsync(store, A);
        Assert.Equal((0, 0, 0, 0), (after.ActivityTasks, after.Timers, after.WorkflowTasks, after.OpenRuns));
        Assert.Null(await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(0, await store.FireDueTimersAsync(A, Time.GetUtcNow().AddDays(1), Ct));
    }

    // ---- continue-as-new and the history cap ---------------------------------------------------------------------------

    private static WorkflowFunc Summer(int handoverAt) => ReferenceWorkflows.Summer(handoverAt);

    private static byte[] SumInput(int first, int last) => ReferenceWorkflows.SumInput(first, last);

    [Fact]
    public async Task Continue_as_new_hands_over_to_a_fresh_run_and_keeps_every_history_short()
    {
        const int Cap = 24;
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "summer", "wf", SumInput(1, 40), cancellationToken: Ct);
        await DriveAsync(store, A, Workflows("default", Summer(Cap - 6)), Activities(), maxSteps: 2000);

        // Follow the chain from the first run.
        var chain = new List<IReadOnlyList<HistoryEvent>>();
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        while (true)
        {
            IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(key, Ct);
            AssertChainIntact(history);
            Assert.True(history.Count <= Cap, $"Run {chain.Count} grew to {history.Count} events (cap {Cap}).");
            chain.Add(history);
            if (history[^1] is not WorkflowContinued next)
            {
                break;
            }

            Assert.Equal(RunState.ContinuedAsNew, await client.GetStateAsync(key, Ct));
            key = new RunKey(A.TenantId, A.NamespaceId, next.NewRunId);
            Assert.Equal(chain[^1][0].RunId, ((WorkflowStarted)(await client.GetHistoryAsync(key, Ct))[0]).PreviousRunId); // linked both ways
        }

        Assert.True(chain.Count >= 4, $"Expected the work to span several runs, got {chain.Count}.");
        Assert.Equal(RunState.Completed, await client.GetStateAsync(key, Ct));
        Assert.Equal(40 * 41 / 2, BitConverter.ToInt32(((WorkflowCompleted)chain[^1][^1]).Result, 0)); // state carried through every hand-over

        // The business id now means the latest run; starting it again is a duplicate, not a new chain.
        Assert.Equal(key, await store.GetCurrentRunAsync(A, "summer", Ct));
        StartRunResult again = await client.StartAsync(A, "summer", "wf", SumInput(1, 1), cancellationToken: Ct);
        Assert.False(again.Started);
        Assert.Equal(key.RunId, again.RunId);

        // Old runs replay cleanly as closed histories.
        ReplayResult replay = WorkflowReplayer.Replay(Summer(Cap - 6), chain[0]);
        Assert.True(replay.IsClosed);
        Assert.Empty(replay.NewCommands);
        StoreStats stats = await StatsAsync(store, A);
        Assert.Equal((0, 0, 0, 0), (stats.OpenRuns, stats.WorkflowTasks, stats.ActivityTasks, stats.Timers));
    }

    [Fact]
    public async Task Continuing_discards_the_old_runs_work_and_signals_reach_only_the_new_run()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "handover", "wf", [], cancellationToken: Ct);
        var first = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowFunc wf = async (ctx, input) =>
        {
            if (input.Length == 0)
            {
                _ = ctx.ExecuteActivityAsync("echo", ReadOnlyMemory<byte>.Empty); // outstanding when we hand over
                _ = ctx.DelayAsync(TimeSpan.FromHours(1));
                throw ctx.ContinueAsNew(B8("carried"));
            }

            return B8(Str(input) + "+" + Str(await ctx.WaitForSignalAsync("go")));
        };
        await DriveAsync(store, A, Workflows("default", wf), Activities());

        RunKey current = (await store.GetCurrentRunAsync(A, "handover", Ct))!.Value;
        Assert.NotEqual(first.RunId, current.RunId);
        Assert.False(await client.SignalAsync(first, "go", B8("old"), Ct)); // the old run is closed
        Assert.True(await client.SignalAsync(current, "go", B8("new"), Ct));
        await DriveAsync(store, A, Workflows("default", wf), Activities());

        Assert.Equal(RunState.ContinuedAsNew, await client.GetStateAsync(first, Ct));
        Assert.Equal("carried+new", Str(((WorkflowCompleted)(await client.GetHistoryAsync(current, Ct))[^1]).Result));
        StoreStats stats = await StatsAsync(store, A);
        Assert.Equal((0, 0, 0), (stats.ActivityTasks, stats.Timers, stats.WorkflowTasks)); // the old run left nothing behind
    }

    [Fact]
    public async Task A_run_that_never_continues_is_stopped_at_the_history_cap()
    {
        const int Cap = 30;
        IKairosStore store = await CreateStoreAsync(new StoreLimits { MaxHistoryEvents = Cap });
        var client = new KairosClient(store, Time);
        StartRunResult start = await client.StartAsync(A, "runaway", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowFunc forever = async (ctx, _) =>
        {
            while (true)
            {
                await ctx.ExecuteActivityAsync("echo", ReadOnlyMemory<byte>.Empty);
            }
        };

        await DriveAsync(store, A, Workflows("default", forever), Activities(), maxSteps: 2000);

        Assert.Equal(RunState.Failed, await client.GetStateAsync(run, Ct));
        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(run, Ct);
        Assert.Contains("HistoryLimitExceeded", Assert.IsType<RunTerminated>(history[^1]).Reason, StringComparison.Ordinal);
        Assert.True(history.Count <= Cap + 2, $"History grew to {history.Count} events with a cap of {Cap} (plus the results in flight).");
        AssertChainIntact(history);
        StoreStats stats = await StatsAsync(store, A);
        Assert.Equal((0, 0, 0, 0), (stats.OpenRuns, stats.WorkflowTasks, stats.ActivityTasks, stats.Timers)); // I9: nothing left running
    }

    // ---- signal ids ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redelivered_signal_id_is_ignored_and_distinct_ids_each_deliver()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "sig-dedup", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        Assert.True(await store.SignalRunAsync(run, "approve", "id-1", B8("first"), Time.GetUtcNow(), Ct));
        Assert.True(await store.SignalRunAsync(run, "approve", "id-1", B8("first"), Time.GetUtcNow(), Ct)); // redelivery: accepted, not applied twice
        Assert.True(await store.SignalRunAsync(run, "approve", "id-2", B8("second"), Time.GetUtcNow(), Ct));

        SignalReceived[] signals = [.. (await client.GetHistoryAsync(run, Ct)).OfType<SignalReceived>()];
        Assert.Equal(["id-1", "id-2"], signals.Select(x => x.SignalId));
        Assert.Equal(["first", "second"], signals.Select(x => Str(x.Payload)));
        AssertChainIntact(await client.GetHistoryAsync(run, Ct));
    }

    [Fact]
    public async Task Signal_ids_are_per_run_and_an_empty_id_is_refused()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult one = await client.StartAsync(A, "sig-a", "wf", [], cancellationToken: Ct);
        StartRunResult two = await client.StartAsync(A, "sig-b", "wf", [], cancellationToken: Ct);
        var runOne = new RunKey(A.TenantId, A.NamespaceId, one.RunId);
        var runTwo = new RunKey(A.TenantId, A.NamespaceId, two.RunId);

        Assert.True(await store.SignalRunAsync(runOne, "go", "same-id", [], Time.GetUtcNow(), Ct));
        Assert.True(await store.SignalRunAsync(runTwo, "go", "same-id", [], Time.GetUtcNow(), Ct));

        Assert.Single((await client.GetHistoryAsync(runOne, Ct)).OfType<SignalReceived>());
        Assert.Single((await client.GetHistoryAsync(runTwo, Ct)).OfType<SignalReceived>());
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.SignalRunAsync(runOne, "go", "", [], Time.GetUtcNow(), Ct));
    }

    [Fact]
    public async Task A_signal_to_a_closed_run_is_refused_even_with_a_known_id()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "sig-closed", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        Assert.True(await store.SignalRunAsync(run, "go", "id-1", [], Time.GetUtcNow(), Ct));
        await store.FailRunAsync(run, "done", "ops", Time.GetUtcNow(), Ct);

        Assert.False(await store.SignalRunAsync(run, "go", "id-1", [], Time.GetUtcNow(), Ct));
        Assert.False(await store.SignalRunAsync(run, "go", "id-2", [], Time.GetUtcNow(), Ct));
    }
}
