using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>What the store tells listeners: an opaque tag and a count, and nothing a bystander connection could use.</summary>
public sealed class WorkNoticeFormatTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData('w', 1)]
    [InlineData('a', 12)]
    [InlineData('*', 0)]
    public void A_notice_survives_its_own_payload(char queue, int count)
    {
        string payload = WorkNotice.Format(Key, Scope, queue, count);

        Assert.True(WorkNotice.TryParse(payload, out WorkNotice notice));
        Assert.Equal((WorkNotice.TagOf(Key, Scope, queue), count), (notice.Tag, notice.Count));
        Matches(payload);
    }

    private static void Matches(string payload) => Assert.Matches(new Regex("^[0-9a-f]{32}:[0-9]+$"), payload);

    [Fact]
    public void The_tag_is_stable_and_depends_on_tenant_namespace_queue_and_key()
    {
        string tag = WorkNotice.TagOf(Key, Scope, 'w');

        Assert.Equal(tag, WorkNotice.TagOf(Key, Scope, 'w'));
        Assert.NotEqual(tag, WorkNotice.TagOf(Key, Scope, 'a'));
        Assert.NotEqual(tag, WorkNotice.TagOf(Key, Scope with { TenantId = Guid.NewGuid() }, 'w'));
        Assert.NotEqual(tag, WorkNotice.TagOf(Key, Scope with { NamespaceId = Guid.NewGuid() }, 'w'));
        Assert.NotEqual(tag, WorkNotice.TagOf(OtherKey, Scope, 'w')); // without the key a tag means nothing
        Assert.Equal(32, tag.Length);
    }

    [Fact]
    public void A_key_that_is_too_short_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WorkNotice.TagOf(new byte[31], Scope, 'w'));
        Assert.Throws<ArgumentException>(() => new PostgresKairosStore(null!, new PostgresStoreOptions { NotificationKey = new byte[16] }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("0123456789abcdef0123456789abcdeF:1")] // upper case
    [InlineData("0123456789abcdef0123456789abcde:1")] // 31 digits
    [InlineData("0123456789abcdef0123456789abcdef:")]
    [InlineData("0123456789abcdef0123456789abcdef:-1")]
    [InlineData("0123456789abcdef0123456789abcdef:99999999999")]
    [InlineData("0123456789abcdef0123456789abcdef:1:build")]
    [InlineData("11111111111111111111111111111111:22222222222222222222222222222222:w:1")] // the old, identifying format
    public void Anything_else_is_not_a_notice(string? payload) => Assert.False(WorkNotice.TryParse(payload, out _));
}

/// <summary>The notifications of the real store, observed on a real listener and on a bystander.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class WorkNotificationTests(PostgresFixture fixture)
{
    private static readonly TenantScope Scope = new(Guid.Parse("33333333-0000-0000-0000-000000000001"), Guid.Parse("33333333-0000-0000-0000-0000000000a1"));
    private static readonly byte[] Key = Enumerable.Range(40, 32).Select(i => (byte)i).ToArray();
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private static string Tag(char queue) => WorkNotice.TagOf(Key, Scope, queue);

    private static PostgresKairosStore NewStore(TestDatabase db) => new(db.DataPlane, new PostgresStoreOptions { NotificationKey = Key });

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

            return [.. all.Where(n => n.Tag == Tag('w') || n.Tag == Tag('a') || n.Tag == Tag('*'))];
        }

        public ValueTask DisposeAsync() => _notifier.DisposeAsync();
    }

    [Fact]
    public async Task A_start_says_one_workflow_task_and_a_duplicate_start_says_nothing()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        var client = new KairosClient(NewStore(db), TimeProvider.System);

        await client.StartAsync(Scope, "wf-1", "t", [], buildId: "build-7", cancellationToken: default);
        WorkNotice first = Assert.Single(await listener.NextAsync(1));
        await client.StartAsync(Scope, "wf-1", "t", [], buildId: "build-7", cancellationToken: default); // the same workflow id: no new run
        WorkNotice[] second = await listener.NextAsync(0);

        Assert.Equal((Tag('w'), 1), (first.Tag, first.Count));
        Assert.Empty(second);
    }

    [Fact]
    public async Task Scheduling_activities_says_how_many_and_completing_one_says_a_workflow_task_is_ready()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        PostgresKairosStore store = NewStore(db);
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
        Assert.Equal((Tag('a'), 3), (scheduled.Tag, scheduled.Count));

        Assert.True(await worker.ProcessActivityTaskAsync(default));
        WorkNotice completed = Assert.Single(await listener.NextAsync(1));
        Assert.Equal((Tag('w'), 1), (completed.Tag, completed.Count));
    }

    [Fact]
    public async Task A_signal_says_a_task_is_ready_only_when_it_made_one()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        PostgresKairosStore store = NewStore(db);
        var client = new KairosClient(store, TimeProvider.System);
        StartRunResult start = await client.StartAsync(Scope, "sig-1", "wait", [], cancellationToken: default);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await listener.NextAsync(1);

        Assert.True(await client.SignalAsync(run, "poke", [1], "s1", default)); // the first task is still unclaimed: nothing new is ready
        Assert.Empty(await listener.NextAsync(0));

        var workflows = new WorkflowRegistry().Register("wait", "default", async (ctx, _) => await ctx.WaitForSignalAsync("later"));
        var worker = new WorkerRuntime(store, Scope, workflows, new ActivityRegistry(), TimeProvider.System);
        Assert.True(await worker.ProcessWorkflowTaskAsync(default));
        Assert.True(await client.SignalAsync(run, "later", [2], "s2", default));
        WorkNotice notice = Assert.Single(await listener.NextAsync(1));
        Assert.Equal((Tag('w'), 1), (notice.Tag, notice.Count));
    }

    [Fact]
    public async Task A_fired_timer_and_a_released_task_say_that_something_is_ready_without_counting()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using var listener = new Listener(db);
        await listener.ReadyAsync();
        PostgresKairosStore store = NewStore(db);
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
        Assert.Equal((Tag('*'), 0), (fired.Tag, fired.Count));

        WorkflowTask task = (await store.PollWorkflowTaskAsync(Scope, ["default"], DateTimeOffset.UtcNow, Lease, default))!;
        await store.ReleaseWorkflowTaskAsync(task, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, countAttempt: false, default);
        WorkNotice released = Assert.Single(await listener.NextAsync(1));
        Assert.Equal(Tag('*'), released.Tag);
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

    /// <summary>
    /// LISTEN is not covered by row level security: any connection to the database can listen and read every payload. A plain connection listens through a whole workload (starts in two
    /// tenants, activities, signals, timers, releases) and must never see a tenant id, a namespace id, a queue name or a build name, in any format.
    /// </summary>
    [Fact]
    public async Task A_bystander_connection_that_listens_sees_no_tenant_namespace_queue_or_build()
    {
        const string SecretBuild = "secret-build-9f3a-payroll";
        TenantScope other = new(Guid.Parse("44444444-0000-0000-0000-000000000002"), Guid.Parse("44444444-0000-0000-0000-0000000000b2"));
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var payloads = new ConcurrentQueue<string>();
        await using NpgsqlConnection bystander = await db.Superuser.OpenConnectionAsync();
        bystander.Notification += (_, e) => payloads.Enqueue(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN kairos_work", bystander))
        {
            await listen.ExecuteNonQueryAsync();
        }

        using var stop = new CancellationTokenSource();
        Task pump = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await bystander.WaitAsync(stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        PostgresKairosStore store = NewStore(db);
        var client = new KairosClient(store, TimeProvider.System);
        var workflows = new WorkflowRegistry().Register("fan", SecretBuild, async (ctx, input) =>
        {
            Task<byte[]> a = ctx.ExecuteActivityAsync("work", input);
            Task<byte[]> b = ctx.ExecuteActivityAsync("work", input);
            await Task.WhenAll(a, b);
            await ctx.DelayAsync(TimeSpan.FromMilliseconds(100));
            return input;
        });
        var activities = new ActivityRegistry().Register("work", (_, input, _) => Task.FromResult(input));
        foreach (TenantScope scope in new[] { Scope, other })
        {
            var worker = new WorkerRuntime(store, scope, workflows, activities, TimeProvider.System);
            await client.StartAsync(scope, "leak-1", "fan", [1], buildId: SecretBuild, cancellationToken: default);
            Assert.True(await worker.ProcessWorkflowTaskAsync(default));
            Assert.True(await worker.ProcessActivityTaskAsync(default));
            Assert.True(await worker.ProcessActivityTaskAsync(default));
            Assert.True(await worker.ProcessWorkflowTaskAsync(default));
            await Task.Delay(250);
            await store.FireDueTimersAsync(scope, DateTimeOffset.UtcNow, default);
            Assert.True(await worker.ProcessWorkflowTaskAsync(default));
        }

        await Task.Delay(500);
        await stop.CancelAsync();
        await pump;

        string[] seen = [.. payloads];
        Assert.True(seen.Length >= 8, $"the workload should have produced notices; saw {seen.Length}");
        string[] forbidden =
        [
            .. new[] { Scope, other }.SelectMany(s => new[]
            {
                s.TenantId.ToString("N"), s.TenantId.ToString("D"), s.NamespaceId.ToString("N"), s.NamespaceId.ToString("D"),
            }),
            SecretBuild, "secret", "payroll", "default", "workflow", "activity", "build", "tenant", "namespace",
        ];
        foreach (string payload in seen)
        {
            Assert.Matches(new Regex("^[0-9a-f]{32}:[0-9]+$"), payload);
            foreach (string word in forbidden)
            {
                Assert.DoesNotContain(word, payload, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Opaque, but the holder of the key can tell the tenants apart; nobody else can.
        string[] tags = [.. seen.Select(p => p[..32]).Distinct()];
        Assert.Contains(Tag('a'), tags);
        Assert.Contains(WorkNotice.TagOf(Key, other, 'a'), tags);
        Assert.DoesNotContain(WorkNotice.TagOf(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray(), Scope, 'a'), tags);
    }
}
