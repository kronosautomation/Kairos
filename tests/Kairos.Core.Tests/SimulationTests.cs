using Kairos.Testing;
using Kairos.Testing.Simulation;

namespace Kairos.Core.Tests;

/// <summary>
/// Layer 2 (deterministic simulation). Every failure names its seed; to reproduce, run that single seed:
/// <c>KAIROS_SIM_START_SEED=&lt;seed&gt; KAIROS_SIM_SEEDS=1 dotnet test --filter Category=Simulation</c>.
/// Per commit: 1,000 seeds. Nightly: KAIROS_SIM_SEEDS=0 (unlimited) bounded by KAIROS_SIM_DURATION_MINUTES.
/// </summary>
public sealed class SimulationTests
{
    private static ulong EnvU64(string name, ulong fallback) =>
        ulong.TryParse(Environment.GetEnvironmentVariable(name), out ulong v) ? v : fallback;

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Seeded_chaos_holds_invariants_I1_to_I4_and_I9()
    {
        ulong start = EnvU64("KAIROS_SIM_START_SEED", 1);
        ulong count = EnvU64("KAIROS_SIM_SEEDS", 1000); // 0 = unlimited (bounded by the duration)
        ulong minutes = EnvU64("KAIROS_SIM_DURATION_MINUTES", 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var failures = new List<string>();
        var total = new SimStats();

        for (ulong seed = start; count == 0 || seed < start + count; seed++)
        {
            if (minutes > 0 && clock.Elapsed.TotalMinutes >= minutes)
            {
                break;
            }

            try
            {
                SimResult result = await SimWorld.RunSeedAsync(seed);
                total.Add(result.Stats);
            }
            catch (InvariantViolationException ex)
            {
                failures.Add(ex.Message);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failing seeds:\n{string.Join('\n', failures.Take(10))}");

        // Guard against a vacuous pass: the chaos must actually have happened.
        Assert.True(total.Crashes > 0 && total.StaleRejections > 0 && total.SequenceConflicts > 0 && total.DuplicateMessages > 0 &&
            total.DelayedMessages > 0 && total.ClockJumps > 0, $"Simulation exercised too little: {total}");
    }

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Same_seed_reproduces_the_run_bit_for_bit()
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3, 42, 977, 123456789 })
        {
            SimResult a = await SimWorld.RunSeedAsync(seed);
            SimResult b = await SimWorld.RunSeedAsync(seed);

            Assert.Equal(a.Fingerprint, b.Fingerprint);
            Assert.Equal(a.Stats.ToString(), b.Stats.ToString());
        }
    }

    [Fact]
    public async Task Different_seeds_explore_different_executions()
    {
        var fingerprints = new HashSet<string>();
        for (ulong seed = 1; seed <= 50; seed++)
        {
            fingerprints.Add((await SimWorld.RunSeedAsync(seed)).Fingerprint);
        }

        Assert.True(fingerprints.Count > 40, $"Only {fingerprints.Count} distinct executions in 50 seeds.");
    }

    [Fact]
    public async Task Regression_corpus_of_previously_failing_seeds_stays_green()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sim-regression-seeds.txt");
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            string trimmed = line.Split('#')[0].Trim();
            if (ulong.TryParse(trimmed, out ulong seed))
            {
                await SimWorld.RunSeedAsync(seed);
            }
        }
    }

    [Fact]
    public async Task Simulation_catches_a_store_that_loses_signals()
    {
        // Meta-test: a simulator is only worth anything if it can fail. A store that acknowledges but drops signals strands
        // runs, which invariant I1 (no lost work) must report.
        await AssertCaughtAsync("I1", inner => new LossySignalStore(inner));
    }

    [Fact]
    public async Task Simulation_catches_a_store_that_corrupts_history()
    {
        await AssertCaughtAsync(null, inner => new TamperingStore(inner));
    }

    private static async Task AssertCaughtAsync(string? invariant, Func<IKairosStore, IKairosStore> decorate)
    {
        for (ulong seed = 1; seed <= 100; seed++)
        {
            try
            {
                await SimWorld.RunSeedAsync(seed, decorate: decorate);
            }
            catch (InvariantViolationException ex) when (invariant is null || ex.Invariant == invariant)
            {
                return;
            }
        }

        Assert.Fail("A broken store went undetected by 100 seeds.");
    }

    private sealed class LossySignalStore(IKairosStore inner) : ForwardingStore(inner)
    {
        public override ValueTask<bool> SignalRunAsync(RunKey run, string signalName, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true); // "ok" -- and nothing happened
    }

    private sealed class TamperingStore(IKairosStore inner) : ForwardingStore(inner)
    {
        public override async ValueTask<WorkflowTask?> PollWorkflowTaskAsync(
            TenantScope scope, IReadOnlyCollection<string> buildIds, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
        {
            WorkflowTask? task = await base.PollWorkflowTaskAsync(scope, buildIds, now, lease, cancellationToken);
            if (task is null)
            {
                return null;
            }

            HistoryEvent[] history = [.. task.History];
            for (int i = 0; i < history.Length; i++)
            {
                if (history[i] is ActivityCompleted c && c.Result.Length > 0)
                {
                    history[i] = c with { Result = [(byte)(c.Result[0] ^ 1)] };
                }
            }

            return task with { History = history };
        }
    }
}
