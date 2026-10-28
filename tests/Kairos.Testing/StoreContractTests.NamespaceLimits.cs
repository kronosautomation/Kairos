namespace Kairos.Testing;

/// <summary>Noisy-neighbour limits: one namespace cannot hold more open runs or pending timers than its limit, and another namespace is not touched by it.</summary>
public abstract partial class StoreContractTests
{
    [Fact]
    public async Task A_namespace_at_its_limit_of_open_runs_refuses_one_more_and_takes_it_again_when_a_run_finishes()
    {
        IKairosStore store = await CreateStoreAsync(new StoreLimits { MaxOpenRunsPerNamespace = 3, NamespaceCountCacheTtl = TimeSpan.Zero });
        var client = new KairosClient(store, Time);
        for (int i = 0; i < 3; i++)
        {
            _ = await client.StartAsync(A, $"open-{i}", "wf", [], cancellationToken: Ct);
        }

        NamespaceLimitExceededException refused = await Assert.ThrowsAsync<NamespaceLimitExceededException>(async () => await client.StartAsync(A, "open-3", "wf", [], cancellationToken: Ct));
        Assert.Equal((NamespaceLimit.OpenRuns, 3), (refused.Limit, refused.Max));
        Assert.Equal(3, (await StatsAsync(store, A)).Runs); // nothing was stored for the refused start

        StartRunResult again = await client.StartAsync(A, "open-1", "wf", [], cancellationToken: Ct); // an id that already has a run is that run, not a new one
        Assert.False(again.Started);

        await DriveAsync(store, A, Workflows("default", (_, _) => Task.FromResult(Array.Empty<byte>())), Activities());
        StartRunResult afterRoom = await client.StartAsync(A, "open-3", "wf", [], cancellationToken: Ct);
        Assert.True(afterRoom.Started);
    }

    [Fact]
    public async Task One_namespace_at_its_limit_does_not_stop_another_tenant_or_namespace()
    {
        IKairosStore store = await CreateStoreAsync(new StoreLimits { MaxOpenRunsPerNamespace = 2, NamespaceCountCacheTtl = TimeSpan.Zero });
        var client = new KairosClient(store, Time);
        _ = await client.StartAsync(A, "a-1", "wf", [], cancellationToken: Ct);
        _ = await client.StartAsync(A, "a-2", "wf", [], cancellationToken: Ct);
        _ = await Assert.ThrowsAsync<NamespaceLimitExceededException>(async () => await client.StartAsync(A, "a-3", "wf", [], cancellationToken: Ct));

        Assert.True((await client.StartAsync(B, "b-1", "wf", [], cancellationToken: Ct)).Started);
        var sameTenantOtherNamespace = new TenantScope(A.TenantId, Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a2"));
        Assert.True((await client.StartAsync(sameTenantOtherNamespace, "a2-1", "wf", [], cancellationToken: Ct)).Started);
    }

    [Fact]
    public async Task A_workflow_task_that_would_start_more_timers_than_the_namespace_may_have_waiting_ends_the_run()
    {
        IKairosStore store = await CreateStoreAsync(new StoreLimits { MaxPendingTimersPerNamespace = 2, NamespaceCountCacheTtl = TimeSpan.Zero });
        var client = new KairosClient(store, Time);
        StartRunResult first = await client.StartAsync(A, "timers-ok", "wf", [], cancellationToken: Ct);
        StartRunResult second = await client.StartAsync(A, "timers-over", "wf", [], cancellationToken: Ct);
        WorkflowTask okTask = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(first.RunId, okTask.Run.RunId);
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(
            okTask, [Timer(first.RunId, 1), Timer(first.RunId, 2)], Time.GetUtcNow(), Ct)); // exactly the limit

        WorkflowTask overTask = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        Assert.Equal(second.RunId, overTask.Run.RunId);
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(overTask, [Timer(second.RunId, 1)], Time.GetUtcNow(), Ct));

        var overRun = new RunKey(A.TenantId, A.NamespaceId, second.RunId);
        Assert.Equal(RunState.Failed, await client.GetStateAsync(overRun, Ct));
        Assert.Contains("TimerLimitExceeded", Assert.IsType<RunTerminated>((await client.GetHistoryAsync(overRun, Ct))[^1]).Reason, StringComparison.Ordinal);
        Assert.Equal(RunState.Running, await client.GetStateAsync(new RunKey(A.TenantId, A.NamespaceId, first.RunId), Ct));
        Assert.Equal(2, (await StatsAsync(store, A)).Timers);
    }

    [Fact]
    public async Task Timers_in_another_namespace_do_not_count_against_this_one()
    {
        IKairosStore store = await CreateStoreAsync(new StoreLimits { MaxPendingTimersPerNamespace = 1, NamespaceCountCacheTtl = TimeSpan.Zero });
        var client = new KairosClient(store, Time);
        StartRunResult a = await client.StartAsync(A, "ta", "wf", [], cancellationToken: Ct);
        StartRunResult b = await client.StartAsync(B, "tb", "wf", [], cancellationToken: Ct);
        WorkflowTask taskA = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        WorkflowTask taskB = (await store.PollWorkflowTaskAsync(B, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;

        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(taskA, [Timer(a.RunId, 1)], Time.GetUtcNow(), Ct));
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(taskB, [Timer(b.RunId, 1, B)], Time.GetUtcNow(), Ct));

        Assert.Equal(RunState.Running, await client.GetStateAsync(new RunKey(B.TenantId, B.NamespaceId, b.RunId), Ct));
    }

    private TimerStarted Timer(Guid run, int id, TenantScope? scope = null)
    {
        TenantScope s = scope ?? A;
        return new TimerStarted(s.TenantId, s.NamespaceId, run, 0, Time.GetUtcNow(), id, TimeSpan.FromMinutes(5));
    }
}
