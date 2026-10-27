using System.Diagnostics;
using Kairos.Persistence.Postgres;
using Kairos.Testing;

namespace Kairos.Persistence.Tests;

/// <summary>Waiting for a run to change, on a real database with the real notifier: woken by the database's notice, and without taking anything a worker is waiting for.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class RunUpdateWaitTests(PostgresFixture fixture)
{
    private static readonly TenantScope Scope = new(Guid.Parse("44444444-0000-0000-0000-000000000001"), Guid.Parse("44444444-0000-0000-0000-0000000000a1"));

    [Fact]
    public async Task A_write_that_makes_work_wakes_a_waiter_at_once_and_the_worker_waiting_beside_it_is_woken_too()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using PostgresWorkNotifier notifier = PostgresWorkNotifier.Start(db.DataPlane);
        PostgresKairosStore store = db.NewDatabaseClockStore();
        store.AttachNotifier(notifier);
        var client = new KairosClient(store, TimeProvider.System);
        var release = new TaskCompletionSource();
        var workflows = new WorkflowRegistry().Register("wf", "default", async (ctx, input) => await ctx.ExecuteActivityAsync("slow", input));
        var activities = new ActivityRegistry().Register("slow", async (_, input, _) =>
        {
            await release.Task;
            return input;
        });
        var worker = new WorkerRuntime(store, Scope, workflows, activities, TimeProvider.System, new WorkerOptions { AutoHeartbeat = false });
        StartRunResult start = await client.StartAsync(Scope, "run-update", "wf", [1], cancellationToken: default);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await worker.ProcessWorkflowTaskAsync(default); // schedules the activity
        Task activity = Task.Run(async () => await worker.ProcessActivityTaskAsync(default)); // takes it and waits for release
        await Task.Delay(700); // the listener is connected and everything so far has been heard
        await notifier.WaitAsync(TimeSpan.FromMilliseconds(50), default);
        long length = (await store.GetHistoryTailAsync(run, 1, default)).LastSequence;

        Task<bool> clientWait = store.WaitForHistoryAsync(run, length, TimeSpan.FromSeconds(20), default).AsTask();
        Task workerWait = notifier.WaitAsync(TimeSpan.FromSeconds(20), default);
        await Task.Delay(100);
        Assert.False(clientWait.IsCompleted);
        var clock = Stopwatch.StartNew();
        release.SetResult(); // the activity completes: the database holds a new event and a new workflow task, and says so

        Assert.True(await clientWait.WaitAsync(TimeSpan.FromSeconds(10)));
        TimeSpan clientWoke = clock.Elapsed;
        await workerWait.WaitAsync(TimeSpan.FromSeconds(10));
        await activity;
        Assert.True(clientWoke < PostgresKairosStore.WaitFallback - TimeSpan.FromMilliseconds(250), $"the waiter should be woken by the notice, not by the look it makes every second ({clientWoke.TotalMilliseconds:N0} ms)");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "and the worker beside it is woken too");
    }

    [Fact]
    public async Task Waiting_for_run_updates_leaves_a_workers_wake_up_for_the_worker()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using PostgresWorkNotifier notifier = PostgresWorkNotifier.Start(db.DataPlane);
        PostgresKairosStore store = db.NewDatabaseClockStore();
        store.AttachNotifier(notifier);
        var client = new KairosClient(store, TimeProvider.System);
        await Task.Delay(500);

        // Fifty waiters for runs that never change are looking while a run is started: the worker's wake-up for the new work must still come.
        var absent = new RunKey(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid());
        Task[] waiters = [.. Enumerable.Range(0, 50).Select(_ => store.WaitForHistoryAsync(absent, 0, TimeSpan.FromSeconds(5), default).AsTask())];
        Task workerWait = notifier.WaitAsync(TimeSpan.FromSeconds(20), default);
        await Task.Delay(100);
        var clock = Stopwatch.StartNew();
        await client.StartAsync(Scope, "new-work", "wf", [], cancellationToken: default);

        await workerWait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "the worker is woken by the notice of new work");
        Assert.All(await Task.WhenAll(waiters.Select(async w => { await w; return true; })), done => Assert.True(done));
    }
}
