using System.Diagnostics;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;
using Xunit.Abstractions;

namespace Kairos.Persistence.Tests;

/// <summary>
/// F2: every worker runs timer firing and lease reaping on every tick. Run eight of them at once over a backlog of expired leases
/// and due timers: each expiry and each timer must be handled exactly once, nobody may exhaust their deadlock retries, and
/// workflow-task completions must keep flowing while the maintenance runs.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class MaintenanceConcurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const int Backlog = 1000;
    private static readonly TenantScope Scope = new(Guid.Parse("eeeeeeee-0000-0000-0000-000000000005"), Guid.Parse("eeeeeeee-0000-0000-0000-0000000000e5"));

    private static async Task<List<(int Value, long Count)>> CountByRunAsync(TestDatabase db, HistoryEventType type)
    {
        await using NpgsqlCommand cmd = db.Superuser.CreateCommand(
            $"SELECT count(*), n FROM (SELECT run_id, count(*) AS n FROM kairos.history_events WHERE get_byte(body, 1) = {(int)type} GROUP BY run_id) g GROUP BY n");
        var rows = new List<(int, long)>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(((int)reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    [Fact]
    public async Task Eight_concurrent_maintenance_workers_handle_each_expiry_and_each_timer_exactly_once()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore(); // virtual time: the test decides when leases expire and timers fall due
        var time = new VirtualTimeProvider();
        var client = new KairosClient(store, time);
        var workflows = new WorkflowRegistry()
            .Register("lease", KairosClient.DefaultBuildId, async (ctx, input) => await ctx.ExecuteActivityAsync("work", input))
            .Register("timer", KairosClient.DefaultBuildId, async (ctx, _) =>
            {
                await ctx.DelayAsync(TimeSpan.FromSeconds(60));
                return [];
            });
        var activities = new ActivityRegistry().Register("work", (_, i, _) => Task.FromResult(i));
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 16 };

        // Setup: 1,000 runs whose activity attempt is claimed and then abandoned, and 1,000 runs waiting on a 60 s timer.
        await Parallel.ForEachAsync(Enumerable.Range(0, Backlog), parallel, async (i, ct) =>
        {
            await client.StartAsync(Scope, $"lease-{i}", "lease", [1], cancellationToken: ct);
            await client.StartAsync(Scope, $"timer-{i}", "timer", [1], cancellationToken: ct);
        });
        var setupWorker = new WorkerRuntime(store, Scope, workflows, activities, time, new WorkerOptions { AutoHeartbeat = false });
        await Parallel.ForEachAsync(Enumerable.Range(0, 16), parallel, async (_, ct) =>
        {
            while (await setupWorker.ProcessWorkflowTaskAsync(ct))
            {
            }
        });
        await Parallel.ForEachAsync(Enumerable.Range(0, 16), parallel, async (_, ct) =>
        {
            while (await store.PollActivityTaskAsync(Scope, time.GetUtcNow(), TimeSpan.FromSeconds(30), ct) is not null)
            {
            }
        });
        StoreStats ready = await store.GetStatsAsync(Scope);
        Assert.Equal((Backlog, Backlog), (ready.ActivityTasks, ready.Timers));

        time.Advance(TimeSpan.FromMinutes(5)); // every lease has expired and every timer is due
        long retriesBefore = PostgresKairosStore.DeadlockRetries;
        long exhaustedBefore = PostgresKairosStore.RetriesExhausted;

        // Meanwhile completions keep arriving: measure how long they wait behind the maintenance.
        var completionMillis = new List<double>();
        using var stop = new CancellationTokenSource();
        Task probe = Task.Run(async () =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                StartRunResult s = await client.StartAsync(Scope, $"probe-{n++}", "lease", [1]);
                WorkflowTask? task = await store.PollWorkflowTaskAsync(Scope, [KairosClient.DefaultBuildId], time.GetUtcNow(), TimeSpan.FromSeconds(30), CancellationToken.None);
                if (task is null)
                {
                    continue;
                }

                RunKey r = task.Run;
                HistoryEvent[] events = [new ActivityScheduled(r.TenantId, r.NamespaceId, r.RunId, 0, time.GetUtcNow(), 1, "work", [1], RetryPolicy.Default)];
                long t0 = Stopwatch.GetTimestamp();
                await store.CompleteWorkflowTaskAsync(task, events, time.GetUtcNow(), CancellationToken.None);
                lock (completionMillis)
                {
                    completionMillis.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
            }
        });

        var watch = Stopwatch.StartNew();
        Task[] maintainers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            int quiet = 0;
            while (quiet < 2)
            {
                int did = await store.FireDueTimersAsync(Scope, time.GetUtcNow(), CancellationToken.None);
                did += await store.ReapExpiredLeasesAsync(Scope, time.GetUtcNow(), CancellationToken.None);
                quiet = did == 0 ? quiet + 1 : 0;
            }
        })).ToArray();
        await Task.WhenAll(maintainers);
        watch.Stop();
        await stop.CancelAsync();
        await probe;

        // Exactly one LeaseExpired record per abandoned attempt and one TimerFired per timer: no loss, no double handling.
        Assert.Equal([(Backlog, 1L)], await CountByRunAsync(db, HistoryEventType.TimerFired));
        List<(int Value, long Count)> expiries = await CountByRunAsync(db, HistoryEventType.ActivityFailed);
        Assert.Equal([(Backlog, 1L)], expiries);

        Assert.Equal(0, PostgresKairosStore.RetriesExhausted - exhaustedBefore);
        completionMillis.Sort();
        double p99 = completionMillis.Count == 0 ? 0 : completionMillis[Math.Min(completionMillis.Count - 1, (int)(completionMillis.Count * 0.99))];
        output.WriteLine(
            $"maintenance of {Backlog} expiries + {Backlog} timers by 8 workers: {watch.Elapsed.TotalSeconds:0.0}s; " +
            $"deadlock retries {PostgresKairosStore.DeadlockRetries - retriesBefore}; " +
            $"{completionMillis.Count} concurrent completions, p99 {p99:0.0} ms");
        Assert.True(completionMillis.Count > 0, "No completion ran while maintenance was in progress, so the latency figure means nothing.");
        Assert.True(p99 < 10_000, $"Completions waited {p99:0} ms (p99) behind maintenance.");
    }
}
