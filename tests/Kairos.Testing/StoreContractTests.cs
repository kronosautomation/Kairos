using System.Text;

namespace Kairos.Testing;

/// <summary>
/// The behaviour every <see cref="IKairosStore"/> must have. Subclass and supply a fresh, empty store; the in-memory
/// reference and PostgreSQL run exactly the same tests. Covers I2, I3 (task fencing), I5 (tenant isolation), I8.
/// </summary>
public abstract class StoreContractTests
{
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    protected static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    private static readonly string[] DefaultBuild = [KairosClient.DefaultBuildId];

    protected VirtualTimeProvider Time { get; } = new();

    /// <summary>A store with no data in it. Called once per test.</summary>
    protected abstract Task<IKairosStore> CreateStoreAsync();

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
        WorkflowTask second = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(first.Run, second.Run);
        Assert.True(second.LeaseToken > first.LeaseToken);

        var done = new WorkflowCompleted(A.TenantId, A.NamespaceId, first.Run.RunId, 0, Time.GetUtcNow(), []);
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteWorkflowTaskAsync(first, [done], Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(second, [done], Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteWorkflowTaskAsync(second, [done], Time.GetUtcNow(), Ct)); // no double apply

        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(first.Run, Ct);
        Assert.Equal(2, history.Count);
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
        ActivityTask retry = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal(slow.IdempotencyKey, retry.IdempotencyKey);
        Assert.True(retry.LeaseToken > slow.LeaseToken);

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
            Assert.False(await store.SignalRunAsync(wrong, "x", [], Time.GetUtcNow(), Ct));
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
}
