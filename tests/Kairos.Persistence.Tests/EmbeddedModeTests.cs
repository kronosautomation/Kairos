using System.Text;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Kairos.Testing.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Persistence.Tests;

/// <summary><c>services.AddKairos(o =&gt; o.UsePostgres(...))</c> with in-process workers, real clock, real Postgres.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class EmbeddedModeTests(PostgresFixture fixture)
{
    private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

    private static void Configure(KairosOptions o) => o
        .AddActivity("shout", (_, input, _) => Task.FromResult(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input).ToUpperInvariant())))
        .AddWorkflow("greet", async (ctx, input) =>
        {
            byte[] shouted = await ctx.ExecuteActivityAsync("shout", input);
            await ctx.DelayAsync(TimeSpan.FromMilliseconds(300));
            byte[] approval = await ctx.WaitForSignalAsync("approve");
            return [.. shouted, .. approval];
        });

    private static IHost BuildHost(Action<KairosOptions> configure)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            configure(o);
        });
        return builder.Build();
    }

    private static async Task<RunState> WaitForCloseAsync(KairosClient client, RunKey run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            RunState? state = await client.GetStateAsync(run);
            if (state is RunState.Completed or RunState.Failed)
            {
                return state.Value;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Run did not close within 30 s.");
    }

    [Fact]
    public async Task AddKairos_with_postgres_runs_workflows_with_activities_timers_and_signals()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        using IHost host = BuildHost(o => Configure(o.UsePostgres(db.DataPlaneConnectionString)));
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "greeting-1", "greet", Hello);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);

        await Task.Delay(700); // past the timer, now waiting for the signal
        Assert.Equal(RunState.Running, await client.GetStateAsync(run));
        await client.SignalAsync(run, "approve", Encoding.UTF8.GetBytes("!"));

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(client, run));
        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(run);
        HashChain.Verify(history);
        Assert.Equal("HELLO!", Encoding.UTF8.GetString(((WorkflowCompleted)history[^1]).Result));

        await host.StopAsync();
    }

    [Fact]
    public async Task Work_started_before_a_restart_finishes_after_it()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        RunKey run;

        using (IHost first = BuildHost(o => Configure(o.UsePostgres(db.DataPlaneConnectionString))))
        {
            await first.StartAsync();
            var client = first.Services.GetRequiredService<KairosClient>();
            StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "restart-1", "greet", Hello);
            run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);
            await Task.Delay(700);
            await first.StopAsync(); // the process goes away mid-run
        }

        using IHost second = BuildHost(o => Configure(o.UsePostgres(db.DataPlaneConnectionString)));
        await second.StartAsync();
        var client2 = second.Services.GetRequiredService<KairosClient>();
        Assert.Equal(RunState.Running, await client2.GetStateAsync(run));
        await client2.SignalAsync(run, "approve", Encoding.UTF8.GetBytes("?"));

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(client2, run));
        Assert.Equal("HELLO?", Encoding.UTF8.GetString(((WorkflowCompleted)(await client2.GetHistoryAsync(run))[^1]).Result));
        await second.StopAsync();
    }

    [Fact]
    public async Task Workers_still_make_progress_when_notifications_never_arrive()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        // UseStore wires no LISTEN/NOTIFY at all: only the timed fallback poll drives the workers.
        using IHost host = BuildHost(o => Configure(o.UseStore(db.NewStore())));
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "no-notify", "greet", Hello);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);
        await Task.Delay(800);
        await client.SignalAsync(run, "approve", []);

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(client, run));
        await host.StopAsync();
    }

    [Fact]
    public async Task Notifier_wakes_a_waiting_worker_immediately_and_times_out_otherwise()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        await using PostgresWorkNotifier notifier = PostgresWorkNotifier.Start(db.DataPlane);
        await notifier.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); // consumes the initial "just connected" wake-up

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await notifier.WaitAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        Assert.True(watch.ElapsedMilliseconds >= 300, "With no work, the wait should last for the fallback interval.");

        Task wait = notifier.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(200);
        Assert.False(wait.IsCompleted);
        watch.Restart();
        await new KairosClient(db.NewStore(), TimeProvider.System).StartAsync(KairosOptions.DefaultScope, "wake", "wf", []);
        await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(watch.ElapsedMilliseconds < 5000, "A start should wake the waiter long before the 30 s fallback.");
    }

    [Fact]
    public async Task Many_parallel_workers_never_run_the_same_task_twice()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var external = new FakeExternalSystem();
        var time = TimeProvider.System;
        var client = new KairosClient(store, time);
        TenantScope scope = KairosOptions.DefaultScope;

        WorkflowRegistry wf = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, _) =>
        {
            var results = new List<byte>();
            for (int i = 0; i < 3; i++)
            {
                results.Add((await ctx.ExecuteActivityAsync("act", new byte[] { 0, 0 }))[0]);
            }

            Task<byte[]> a = ctx.ExecuteActivityAsync("act", new byte[] { 0, 0 });
            Task<byte[]> b = ctx.ExecuteActivityAsync("act", new byte[] { 0, 0 });
            await Task.WhenAll(a, b);
            return [.. results];
        });
        ActivityRegistry act = new ActivityRegistry().Register("act", external.Activity());

        const int Runs = 60;
        var keys = new List<RunKey>();
        for (int i = 0; i < Runs; i++)
        {
            StartRunResult s = await client.StartAsync(scope, $"c-{i}", "wf", []);
            keys.Add(new RunKey(scope.TenantId, scope.NamespaceId, s.RunId));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task[] workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var worker = new WorkerRuntime(store, scope, wf, act, time);
            while (!cts.IsCancellationRequested)
            {
                bool did = await worker.ProcessWorkflowTaskAsync(cts.Token);
                did |= await worker.ProcessActivityTaskAsync(cts.Token);
                if (!did)
                {
                    StoreStats stats = await store.GetStatsAsync(scope);
                    if (stats.OpenRuns == 0)
                    {
                        return;
                    }

                    await Task.Delay(5);
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);

        foreach (RunKey key in keys)
        {
            Assert.Equal(RunState.Completed, await client.GetStateAsync(key));
            HashChain.Verify(await client.GetHistoryAsync(key));
        }

        Assert.Equal(Runs * 5, external.Calls.Count);
        Assert.All(external.Calls, kv => Assert.Equal(1, kv.Value)); // each activity attempt was claimed exactly once
    }
}
