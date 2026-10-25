using System.Diagnostics;
using Kairos.Persistence.Postgres;

namespace Kairos.Persistence.Tests.Faults;

/// <summary>
/// Layer 5 network faults (docs/TEST-STRATEGY.md): latency, slicing, resets, half-open connections and a dead database between the
/// workers and Postgres, using Toxiproxy. Each test injects the fault while a workload is running, removes it, and then requires
/// that every run completed exactly once with a sound history and no duplicated external effect.
/// </summary>
[Collection(FaultTestGroup.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "FaultInjection")]
public sealed class FaultInjectionTests(FaultFixture fixture)
{
    private const int Runs = 16;

    private async Task RunAsync(Func<ToxiproxyClient, Task> inject)
    {
        await using FaultDatabase db = await fixture.NewDatabaseAsync();
        await using var scenario = new FaultScenario(db.DirectStore(), db.ProxiedConnectionString);
        await scenario.StartRunsAsync(Runs);
        await scenario.StartHostAsync();

        await inject(fixture.Toxi);
        await fixture.Toxi.ResetAsync();

        await scenario.WaitForAllAsync();
        await scenario.AssertTwoStepRunsAreSoundAsync();
    }

    [Fact]
    public async Task Control_the_toxics_really_disturb_the_connection()
    {
        await using FaultDatabase db = await fixture.NewDatabaseAsync();
        await using var viaProxy = Npgsql.NpgsqlDataSource.Create(db.ProxiedConnectionString);
        async Task<long> QueryAsync()
        {
            await using Npgsql.NpgsqlCommand cmd = viaProxy.CreateCommand("SELECT 1");
            return (long)(int)(await cmd.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await QueryAsync()); // healthy

        await fixture.Toxi.AddToxicAsync(FaultFixture.Proxy, "reset", "reset_peer", "downstream", new { timeout = 0 });
        Npgsql.NpgsqlConnection.ClearAllPools();
        await Assert.ThrowsAnyAsync<Exception>(QueryAsync);
        await fixture.Toxi.ResetAsync();
        Npgsql.NpgsqlConnection.ClearAllPools();
        Assert.Equal(1, await QueryAsync()); // recovered

        await fixture.Toxi.AddToxicAsync(FaultFixture.Proxy, "blackhole", "timeout", "downstream", new { timeout = 0 });
        Npgsql.NpgsqlConnection.ClearAllPools();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(QueryAsync);
        Assert.InRange(watch.Elapsed.TotalSeconds, 3, 20); // silence is detected by the timeouts, not waited on forever
        await fixture.Toxi.ResetAsync();
    }

    [Fact]
    public Task Latency_in_both_directions_slows_the_work_but_never_corrupts_it() => RunAsync(async toxi =>
    {
        await toxi.AddToxicAsync(FaultFixture.Proxy, "lag-up", "latency", "upstream", new { latency = 150, jitter = 100 });
        await toxi.AddToxicAsync(FaultFixture.Proxy, "lag-down", "latency", "downstream", new { latency = 150, jitter = 100 });
        await Task.Delay(TimeSpan.FromSeconds(4));
    });

    [Fact]
    public Task Tiny_packets_on_a_slow_link_are_reassembled_correctly() => RunAsync(async toxi =>
    {
        await toxi.AddToxicAsync(FaultFixture.Proxy, "slice", "slicer", "downstream", new { average_size = 24, size_variation = 8, delay = 500 });
        await toxi.AddToxicAsync(FaultFixture.Proxy, "slow", "bandwidth", "upstream", new { rate = 20 });
        await Task.Delay(TimeSpan.FromSeconds(4));
    });

    [Fact]
    public Task Connections_reset_over_and_over_while_the_workload_runs() => RunAsync(async toxi =>
    {
        for (int round = 0; round < 4; round++)
        {
            await toxi.AddToxicAsync(FaultFixture.Proxy, "reset", "reset_peer", "downstream", new { timeout = 0 });
            await Task.Delay(TimeSpan.FromSeconds(1));
            await toxi.RemoveToxicAsync(FaultFixture.Proxy, "reset");
            await Task.Delay(TimeSpan.FromMilliseconds(700));
        }
    });

    [Fact]
    public Task Commits_whose_acknowledgement_is_lost_do_not_duplicate_anything() => RunAsync(async toxi =>
    {
        // Requests reach the server and commit, but the answer is reset on its way back: the caller cannot know the outcome and retries.
        await toxi.AddToxicAsync(FaultFixture.Proxy, "lost-ack", "reset_peer", "downstream", new { timeout = 0 }, toxicity: 0.5);
        await Task.Delay(TimeSpan.FromSeconds(6));
    });

    [Fact]
    public Task A_half_open_connection_that_swallows_traffic_is_abandoned_not_waited_on_forever() => RunAsync(async toxi =>
    {
        // timeout=0 keeps the connection open and drops every byte: the failure only shows up as silence.
        await toxi.AddToxicAsync(FaultFixture.Proxy, "blackhole-up", "timeout", "upstream", new { timeout = 0 });
        await toxi.AddToxicAsync(FaultFixture.Proxy, "blackhole-down", "timeout", "downstream", new { timeout = 0 });
        await Task.Delay(TimeSpan.FromSeconds(8));
    });

    [Fact]
    public Task A_database_that_disappears_and_returns_loses_no_work() => RunAsync(async toxi =>
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await toxi.SetEnabledAsync(FaultFixture.Proxy, false);
        await Task.Delay(TimeSpan.FromSeconds(8)); // longer than every lease: tasks in flight are reaped and redone
        await toxi.SetEnabledAsync(FaultFixture.Proxy, true);
    });

    [Fact]
    public async Task Signals_retried_after_an_unknown_outcome_are_delivered_exactly_once()
    {
        await using FaultDatabase db = await fixture.NewDatabaseAsync();
        await using var scenario = new FaultScenario(db.DirectStore(), db.ProxiedConnectionString);
        await scenario.StartHostAsync();
        await scenario.StartRunsAsync(1, "collect", [10]);
        RunKey run = scenario.Runs[0];

        await fixture.Toxi.AddToxicAsync(FaultFixture.Proxy, "lost-ack", "reset_peer", "downstream", new { timeout = 0 }, toxicity: 0.5);
        for (byte i = 0; i < 10; i++)
        {
            byte[] payload = [i];
            while (true)
            {
                try
                {
                    if (await scenario.ClientViaProxy.SignalAsync(run, "s", payload, signalId: $"sig-{i}", CancellationToken.None))
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or IOException)
                {
                    // outcome unknown: send again with the same id
                }

                await Task.Delay(50);
            }
        }

        await fixture.Toxi.ResetAsync();
        await scenario.WaitForAllAsync();

        var verifier = new KairosClient(db.DirectStore(), TimeProvider.System);
        IReadOnlyList<HistoryEvent> history = await verifier.GetHistoryAsync(run);
        HashChain.Verify(history);
        string[] ids = [.. history.OfType<SignalReceived>().Select(s => s.SignalId!)];
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"sig-{i}"), ids); // each id once, in the order sent
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (byte)i), ((WorkflowCompleted)history[^1]).Result);
    }

    [Fact]
    public async Task A_cut_LISTEN_connection_falls_back_to_polling_and_listens_again()
    {
        await using FaultDatabase db = await fixture.NewDatabaseAsync();
        await using var notifier = PostgresWorkNotifier.Start(Npgsql.NpgsqlDataSource.Create(db.NotifyConnectionString));
        await notifier.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); // "just connected" wake-up
        var client = new KairosClient(db.DirectStore(), TimeProvider.System);

        // Cut the LISTEN connection. A notification sent now is lost; the waiter must still return, on the fallback interval.
        await fixture.Toxi.SetEnabledAsync(FaultFixture.NotifyProxy, false);
        var lost = Stopwatch.StartNew();
        await client.StartAsync(KairosOptions.DefaultScope, "during-outage", "wf", []);
        await notifier.WaitAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None);
        Assert.InRange(lost.ElapsedMilliseconds, 600, 5000);

        // Restore it: the notifier reconnects (wake-up on connect), then a new start wakes a waiter promptly.
        await fixture.Toxi.SetEnabledAsync(FaultFixture.NotifyProxy, true);
        await notifier.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); // the reconnect wake-up
        Task wait = notifier.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(300);
        Assert.False(wait.IsCompleted);
        var woken = Stopwatch.StartNew();
        await client.StartAsync(KairosOptions.DefaultScope, "after-outage", "wf", []);
        await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(woken.ElapsedMilliseconds < 5000, "A start after the reconnect should wake the waiter long before the fallback.");
    }
}
