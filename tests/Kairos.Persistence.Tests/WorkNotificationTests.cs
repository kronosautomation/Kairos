using System.Collections.Concurrent;
using Kairos.Persistence.Postgres;
using Kairos.Testing;

namespace Kairos.Persistence.Tests;

/// <summary>What the store tells listeners: which scope, which queue, how many tasks and which build, and nothing for work that is not new.</summary>
public sealed class WorkNoticeFormatTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Theory]
    [InlineData('w', 1, "default")]
    [InlineData('w', 3, "build:with:colons")]
    [InlineData('a', 12, null)]
    [InlineData('*', 0, null)]
    public void A_notice_survives_its_own_payload(char queue, int count, string? build)
    {
        string payload = WorkNotice.Format(Scope, queue, count, build);

        Assert.True(WorkNotice.TryParse(payload, out WorkNotice notice));
        Assert.Equal((Scope, queue, count, build), (notice.Scope, notice.Queue, notice.Count, notice.Build));
        Assert.True(payload.Length < 400); // far below the 8000 bytes PostgreSQL allows
    }

    [Fact]
    public void An_older_writers_two_part_payload_means_something_is_ready_here()
    {
        Assert.True(WorkNotice.TryParse($"{Scope.TenantId:N}:{Scope.NamespaceId:N}", out WorkNotice notice));
        Assert.Equal((WorkNotice.Unknown, 0), (notice.Queue, notice.Count));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("11111111111111111111111111111111:short")]
    [InlineData("11111111111111111111111111111111:22222222222222222222222222222222:x:1")]
    [InlineData("11111111111111111111111111111111:22222222222222222222222222222222:w:-1")]
    [InlineData("11111111111111111111111111111111:22222222222222222222222222222222:w:99999999999")]
    [InlineData("11111111111111111111111111111111:22222222222222222222222222222222:w")]
    public void Anything_else_is_not_a_notice(string? payload) => Assert.False(WorkNotice.TryParse(payload, out _));
}

/// <summary>The notifications of the real store, observed on a real listener.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class WorkNotificationTests(PostgresFixture fixture)
{
    private static readonly TenantScope Scope = new(Guid.Parse("33333333-0000-0000-0000-000000000001"), Guid.Parse("33333333-0000-0000-0000-0000000000a1"));

    private sealed class Listener : IAsyncDisposable
    {
        private readonly PostgresWorkNotifier _notifier;
        private int _listening;

        public Listener(TestDatabase db)
        {
            _notifier = PostgresWorkNotifier.Start(db.DataPlane);
            _notifier.Notified += notice => Notices.Enqueue(notice);
            _notifier.Listening += () => Interlocked.Increment(ref _listening);
        }

        public ConcurrentQueue<WorkNotice> Notices { get; } = new();

        public int ListeningCount => Volatile.Read(ref _listening);

        public async Task ReadyAsync()
        {
            for (int i = 0; i < 100 && ListeningCount == 0; i++)
            {
                await Task.Delay(50);
            }

            Assert.True(ListeningCount > 0, "the listener never started listening");
        }

        /// <summary>Notices that arrive within the time (it cannot prove an absence, only that none came in a while).</summary>
        public async Task<WorkNotice[]> NextAsync(int atLeast, int settleMs = 400)
        {
            for (int i = 0; i < 100 && Notices.Count < atLeast; i++)
            {
                await Task.Delay(50);
            }

            await Task.Delay(settleMs);
            var all = new List<WorkNotice>();
            while (Notices.TryDequeue(out WorkNotice n))
            {
                all.Add(n);
            }

            return [.. all.Where(n => n.Scope == Scope)];
        }

        public ValueTask DisposeAsync() => _notifier.DisposeAsync();
    }

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_start_says_one_workflow_task_of_its_build_and_a_duplicate_start_says_nothing()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        var client = new KairosClient(db.NewStore(), TimeProvider.System);

        await client.StartAsync(Scope, "wf-1", "t", [], buildId: "build-7", cancellationToken: default);
        WorkNotice first = Assert.Single(await listener.NextAsync(1));
        await client.StartAsync(Scope, "wf-1", "t", [], buildId: "build-7", cancellationToken: default); // the same workflow id: no new run
        WorkNotice[] second = await listener.NextAsync(0);

        Assert.Equal((WorkNotice.Workflow, 1, "build-7"), (first.Queue, first.Count, first.Build));
        Assert.Empty(second);
    }

    [Fact]
    public async Task Scheduling_activities_says_how_many_and_completing_one_says_a_workflow_task_is_ready()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        PostgresKairosStore store = db.NewStore();
        var client = new KairosClient(store, TimeProvider.System);
        var workflows = new WorkflowRegistry().Register("fan", "default", async (ctx, input) =>
        {
            Task<byte[]> a = ctx.ExecuteActivityAsync("work", input);
            Task<byte[]> b = ctx.ExecuteActivityAsync("work", input);
            Task<byte[]> c = ctx.ExecuteActivityAsync("work", input);
            await Task.WhenAll(a, b, c);
            return input;
        });
        var activities = new ActivityRegistry().Register("work", (_, input, _) => Task.FromResult(input));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, TimeProvider.System);
        await client.StartAsync(Scope, "fan-1", "fan", [1], cancellationToken: default);
        await listener.NextAsync(1);

        Assert.True(await worker.ProcessWorkflowTaskAsync(default));
        WorkNotice scheduled = Assert.Single(await listener.NextAsync(1));
        Assert.Equal((WorkNotice.Activity, 3), (scheduled.Queue, scheduled.Count));

        Assert.True(await worker.ProcessActivityTaskAsync(default));
        WorkNotice completed = Assert.Single(await listener.NextAsync(1));
        Assert.Equal((WorkNotice.Workflow, 1, "default"), (completed.Queue, completed.Count, completed.Build));
    }

    [Fact]
    public async Task A_signal_says_a_task_is_ready_only_when_it_made_one()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        PostgresKairosStore store = db.NewStore();
        var client = new KairosClient(store, TimeProvider.System);
        StartRunResult start = await client.StartAsync(Scope, "sig-1", "wait", [], cancellationToken: default);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await listener.NextAsync(1);

        // The run's first task is still waiting to be claimed: a signal readies nothing new.
        Assert.True(await client.SignalAsync(run, "poke", [1], "s1", default));
        Assert.Empty(await listener.NextAsync(0));

        // Once the task has been run and the workflow is waiting, a signal makes a task.
        var workflows = new WorkflowRegistry().Register("wait", "default", async (ctx, _) => await ctx.WaitForSignalAsync("later"));
        var worker = new WorkerRuntime(store, Scope, workflows, new ActivityRegistry(), TimeProvider.System);
        Assert.True(await worker.ProcessWorkflowTaskAsync(default));
        Assert.True(await client.SignalAsync(run, "later", [2], "s2", default));
        WorkNotice notice = Assert.Single(await listener.NextAsync(1));
        Assert.Equal((WorkNotice.Workflow, 1), (notice.Queue, notice.Count));
    }

    [Fact]
    public async Task A_fired_timer_and_a_released_task_say_that_something_is_ready_without_counting()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        PostgresKairosStore store = db.NewStore();
        var client = new KairosClient(store, TimeProvider.System);
        var workflows = new WorkflowRegistry().Register("sleep", "default", async (ctx, input) =>
        {
            await ctx.DelayAsync(TimeSpan.FromMilliseconds(200));
            return input;
        });
        var worker = new WorkerRuntime(store, Scope, workflows, new ActivityRegistry(), TimeProvider.System);
        await client.StartAsync(Scope, "sleep-1", "sleep", [], cancellationToken: default);
        Assert.True(await worker.ProcessWorkflowTaskAsync(default));
        await listener.NextAsync(1);
        await Task.Delay(300);

        Assert.Equal(1, await store.FireDueTimersAsync(Scope, DateTimeOffset.UtcNow, default));
        WorkNotice fired = Assert.Single(await listener.NextAsync(1));
        Assert.Equal(WorkNotice.Unknown, fired.Queue);

        WorkflowTask task = (await store.PollWorkflowTaskAsync(Scope, ["default"], DateTimeOffset.UtcNow, Lease, default))!;
        await store.ReleaseWorkflowTaskAsync(task, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, countAttempt: false, default);
        WorkNotice released = Assert.Single(await listener.NextAsync(1));
        Assert.Equal(WorkNotice.Unknown, released.Queue);
    }

    [Fact]
    public async Task A_listener_whose_connection_is_cut_starts_listening_again_and_says_so()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        int before = listener.ListeningCount;

        await PostgresFixture.Execute(db.Superuser, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query LIKE 'LISTEN kairos_work%' AND pid <> pg_backend_pid()");

        for (int i = 0; i < 100 && listener.ListeningCount == before; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(listener.ListeningCount > before, "the notifier must announce that it listens again, so a listener can look at what it missed");
    }
}
