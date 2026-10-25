using Kairos.Testing.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos.Persistence.Tests.Faults;

/// <summary>
/// The workload every network-fault test runs: an embedded Kairos host whose only path to Postgres is the proxy, many two-step
/// workflows, and a verdict computed from the database itself. Whatever the network does, afterwards every run must have
/// completed exactly once with a sound history (I1, I2, I3, I6).
/// </summary>
internal sealed class FaultScenario : IAsyncDisposable
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private static readonly byte[] NoFailure = [0, 0];

    private readonly string _connectionString;
    private readonly KairosClient _verifier;
    private IHost? _host;

    /// <param name="verifierStore">A store over a connection that bypasses the proxy: what the database really holds.</param>
    /// <param name="connectionString">What the workers under test use, normally through the proxy.</param>
    public FaultScenario(IKairosStore verifierStore, string connectionString)
    {
        _connectionString = connectionString;
        _verifier = new KairosClient(verifierStore, TimeProvider.System);
    }

    public FakeExternalSystem External { get; } = new();

    public List<RunKey> Runs { get; } = [];

    /// <summary>Starts the host. Leases are short so a worker that loses its connection is replaced within seconds.</summary>
    public async Task StartHostAsync()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.WorkerCount = 3;
            o.Worker = new WorkerOptions
            {
                WorkflowTaskLease = TimeSpan.FromSeconds(6),
                ActivityTaskLease = TimeSpan.FromSeconds(6),
                FailedTaskRetryDelay = TimeSpan.FromSeconds(1),
            };
            o.UsePostgres(_connectionString)
                .AddActivity("work", External.Activity())
                .AddWorkflow("two-step", async (ctx, _) =>
                {
                    await ctx.ExecuteActivityAsync("work", NoFailure);
                    await ctx.DelayAsync(TimeSpan.FromMilliseconds(100));
                    await ctx.ExecuteActivityAsync("work", NoFailure);
                    return [1];
                })
                .AddWorkflow("collect", async (ctx, input) =>
                {
                    var received = new List<byte>();
                    for (int i = 0; i < input[0]; i++)
                    {
                        received.AddRange(await ctx.WaitForSignalAsync("s"));
                    }

                    return [.. received];
                });
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public KairosClient ClientViaProxy => _host!.Services.GetRequiredService<KairosClient>();

    /// <summary>Starts the runs straight through the database (before any fault is injected).</summary>
    public async Task StartRunsAsync(int count, string workflowType = "two-step", byte[]? input = null)
    {
        for (int i = 0; i < count; i++)
        {
            StartRunResult s = await _verifier.StartAsync(KairosOptions.DefaultScope, $"{workflowType}-{Runs.Count}", workflowType, input ?? []);
            Runs.Add(new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, s.RunId));
        }
    }

    /// <summary>Waits, reading the database directly, until every run has closed or the deadline passes.</summary>
    public async Task WaitForAllAsync()
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        var open = new HashSet<RunKey>(Runs);
        while (open.Count > 0)
        {
            foreach (RunKey run in open.ToArray())
            {
                if (await ReadAsync(() => _verifier.GetStateAsync(run).AsTask()) is RunState.Completed or RunState.Failed)
                {
                    open.Remove(run);
                }
            }

            if (open.Count > 0)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{open.Count} of {Runs.Count} runs were still open after {Deadline.TotalSeconds:0} s (I1: no lost work).");
                await Task.Delay(200);
            }
        }
    }

    /// <summary>A run's history as the database holds it, read without the proxy.</summary>
    public Task<IReadOnlyList<HistoryEvent>> ReadHistoryAsync(RunKey run) => ReadAsync(() => _verifier.GetHistoryAsync(run).AsTask());

    /// <summary>
    /// A read that rides out a database that is changing underneath it: pooled connections to a node that just died fail once, and
    /// the next attempt opens a fresh one (to the promoted node after a failover).
    /// </summary>
    private static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await read();
            }
            catch (Exception ex) when (attempt < 40 && ex is NpgsqlException or TimeoutException or IOException)
            {
                await Task.Delay(250);
            }
        }
    }

    /// <summary>I2, I3, I6 for every two-step run, read from the database.</summary>
    public async Task AssertTwoStepRunsAreSoundAsync()
    {
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (RunKey run in Runs)
        {
            Assert.Equal(RunState.Completed, await ReadAsync(() => _verifier.GetStateAsync(run).AsTask()));
            IReadOnlyList<HistoryEvent> history = await ReadHistoryAsync(run);
            HashChain.Verify(history); // I6, and gap-free chaining
            for (int i = 0; i < history.Count; i++)
            {
                Assert.Equal(i + 1, history[i].Sequence); // I2: sequence 1..n without gaps or repeats
            }

            Assert.Single(history.OfType<WorkflowStarted>());
            Assert.Single(history.OfType<WorkflowCompleted>());
            Assert.IsType<WorkflowCompleted>(history[^1]);
            int[] completed = [.. history.OfType<ActivityCompleted>().Select(a => a.ActivityId).Order()];
            Assert.Equal(completed.Distinct().Count(), completed.Length); // no activity result recorded twice
            Assert.Equal(2, completed.Length);
            foreach (int id in completed)
            {
                expectedKeys.Add($"{run.RunId:N}:{id}");
            }
        }

        // I3: the external system saw exactly the keys of the recorded activities; a retry reused its key, so no extra effect exists.
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), External.Effects.Order(StringComparer.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _host.StopAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or NpgsqlException or TimeoutException)
            {
                // the proxy may still be misbehaving; the test has already made its assertions
            }

            _host.Dispose();
        }
    }
}
