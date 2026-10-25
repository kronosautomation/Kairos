namespace Kairos.Testing;

/// <summary>
/// Exactly-once checklist for the store (docs/RESEARCH.md R4): the failure cases that the published exactly-once designs
/// (Beldi, Halfmoon) guard against and that are not already pinned by another contract test. A caller that never learned the
/// outcome of a request must be able to repeat it safely, and an operation racing another must leave exactly one effect.
/// </summary>
public abstract partial class StoreContractTests
{
    [Fact]
    public async Task Repeating_a_workflow_task_completion_whose_acknowledgement_was_lost_appends_nothing_twice()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "retry-wt", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        var timer = new TimerStarted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), 1, TimeSpan.FromMinutes(5));

        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(task, [timer], Time.GetUtcNow(), Ct));
        int lengthAfterFirst = (await client.GetHistoryAsync(key, Ct)).Count;
        StoreStats statsAfterFirst = await StatsAsync(store, A);

        // The worker never saw the answer and sends the same completion again.
        Assert.Equal(CompletionOutcome.StaleLease, await store.CompleteWorkflowTaskAsync(task, [timer], Time.GetUtcNow(), Ct));

        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(key, Ct);
        Assert.Equal(lengthAfterFirst, history.Count);
        Assert.Single(history.OfType<TimerStarted>());
        Assert.Equal(statsAfterFirst, await StatsAsync(store, A));
        AssertChainIntact(history);
    }

    [Fact]
    public async Task Starting_a_workflow_id_again_after_the_run_finished_returns_the_finished_run()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult first = await client.StartAsync(A, "once-ever", "wf", [], cancellationToken: Ct);
        await DriveAsync(store, A, Workflows("default", (_, input) => Task.FromResult(input)), Activities());
        var key = new RunKey(A.TenantId, A.NamespaceId, first.RunId);
        Assert.Equal(RunState.Completed, await client.GetStateAsync(key, Ct));

        // A caller whose first request timed out retries after the work has already finished: it must not start a second run.
        StartRunResult retry = await client.StartAsync(A, "once-ever", "wf", [], cancellationToken: Ct);

        Assert.False(retry.Started);
        Assert.Equal(first.RunId, retry.RunId);
        Assert.Single((await client.GetHistoryAsync(key, Ct)).OfType<WorkflowStarted>());
    }

    [Fact]
    public async Task A_late_activity_result_for_a_run_that_was_ended_changes_nothing()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "late", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input));
        await new WorkerRuntime(store, A, wf, Activities(), Time).ProcessWorkflowTaskAsync(Ct);
        ActivityTask running = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromMinutes(1), Ct))!;

        Assert.True(await store.FailRunAsync(key, "operator stop", "ops", Time.GetUtcNow(), Ct)); // while the activity is still running
        IReadOnlyList<HistoryEvent> ended = await client.GetHistoryAsync(key, Ct);

        var result = new ActivityCompleted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), running.ActivityId, B8("too late"));
        Assert.NotEqual(CompletionOutcome.Applied, await store.CompleteActivityTaskAsync(running, result, null, Time.GetUtcNow(), Ct));

        Assert.Equal(ended.Count, (await client.GetHistoryAsync(key, Ct)).Count);
        Assert.Equal(RunState.Failed, await client.GetStateAsync(key, Ct));
        StoreStats stats = await StatsAsync(store, A);
        Assert.Equal((0, 0, 0), (stats.ActivityTasks, stats.WorkflowTasks, stats.Timers));
    }

    [Fact]
    public async Task Many_callers_starting_the_same_workflow_id_at_once_create_exactly_one_run()
    {
        (_, KairosClient client) = await NewAsync();

        StartRunResult[] results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(async () => await client.StartAsync(A, "race-start", "wf", [], cancellationToken: Ct).AsTask())));

        Assert.Single(results, r => r.Started);
        Assert.Single(results.Select(r => r.RunId).Distinct());
        var key = new RunKey(A.TenantId, A.NamespaceId, results[0].RunId);
        Assert.Single((await client.GetHistoryAsync(key, Ct)).OfType<WorkflowStarted>());
    }

    [Fact]
    public async Task Many_deliveries_of_the_same_signal_id_at_once_record_one_signal()
    {
        (_, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "race-signal", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        bool[] accepted = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(async () => await client.SignalAsync(key, "go", B8("x"), signalId: "same-delivery", Ct).AsTask())));

        Assert.All(accepted, Assert.True);
        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(key, Ct);
        Assert.Single(history.OfType<SignalReceived>());
        AssertChainIntact(history);
    }

    [Fact]
    public async Task Racing_completions_of_one_workflow_task_apply_exactly_one()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "race-complete", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct))!;
        var timer = new TimerStarted(A.TenantId, A.NamespaceId, start.RunId, 0, Time.GetUtcNow(), 1, TimeSpan.FromMinutes(5));

        // The same lease used by two threads (a worker that retried while its first attempt was still in flight).
        CompletionOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await store.CompleteWorkflowTaskAsync(task, [timer], Time.GetUtcNow(), Ct).AsTask())));

        Assert.Single(outcomes, o => o == CompletionOutcome.Applied);
        Assert.All(outcomes.Where(o => o != CompletionOutcome.Applied), o => Assert.Equal(CompletionOutcome.StaleLease, o));
        Assert.Single((await client.GetHistoryAsync(key, Ct)).OfType<TimerStarted>());
    }
}
