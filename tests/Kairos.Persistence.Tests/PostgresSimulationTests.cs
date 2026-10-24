using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Kairos.Testing.Simulation;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The deterministic simulator and the crash matrix, pointed at real PostgreSQL. Because every random choice comes from the seed
/// and both stores obey one contract, the same seed must produce bit-identical histories on the in-memory reference and on
/// Postgres: any difference is a bug in one of them.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PostgresSimulationTests(PostgresFixture fixture)
{
    private sealed class PostgresDiagnostics(PostgresKairosStore store) : IStoreDiagnostics
    {
        public async ValueTask<StoreStats> GetStatsAsync(TenantScope scope) => await store.GetStatsAsync(scope);

        public async ValueTask<DateTimeOffset?> NextWakeupAsync(TenantScope scope, DateTimeOffset now) => await store.NextWakeupAsync(scope, now);
    }

    private async Task<(TestDatabase Db, StoreUnderTest Backend)> NewBackendAsync()
    {
        TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        return (db, new StoreUnderTest(store, new PostgresDiagnostics(store)));
    }

    private static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;

    [Fact]
    [Trait("Category", "FaultInjection")]
    public async Task Chaos_on_postgres_holds_all_invariants()
    {
        int seeds = EnvInt("KAIROS_SIM_PG_SEEDS", 20);
        int start = EnvInt("KAIROS_SIM_START_SEED", 1);
        var failures = new List<string>();
        var total = new SimStats();

        for (int seed = start; seed < start + seeds; seed++)
        {
            (TestDatabase db, StoreUnderTest backend) = await NewBackendAsync();
            await using (db)
            {
                try
                {
                    total.Add((await SimWorld.RunSeedAsync((ulong)seed, backend: backend)).Stats);
                }
                catch (InvariantViolationException ex)
                {
                    failures.Add(ex.Message);
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failing seeds on Postgres:\n{string.Join('\n', failures.Take(10))}");
        Assert.True(total.Crashes > 0 && total.StaleRejections > 0, $"Chaos on Postgres exercised too little: {total}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Same_seed_gives_identical_histories_on_postgres_and_the_in_memory_reference()
    {
        for (ulong seed = 1; seed <= 15; seed++)
        {
            SimResult reference = await SimWorld.RunSeedAsync(seed);

            (TestDatabase db, StoreUnderTest backend) = await NewBackendAsync();
            await using (db)
            {
                SimResult actual = await SimWorld.RunSeedAsync(seed, backend: backend);
                Assert.Equal(reference.Fingerprint, actual.Fingerprint);
                Assert.Equal(reference.Stats.ToString(), actual.Stats.ToString());
            }
        }
    }

    private static ActStep Act(int failAttempts = 0, bool afterEffect = false, int max = 4, bool catches = false) =>
        new(failAttempts, afterEffect, max, catches);

    public static TheoryData<string> CrashPrograms() => ["retry-after-effect", "timer-then-act", "signal-and-parallel", "poison-then-act"];

    private static SimProgram CrashProgram(string name) => name switch
    {
        "retry-after-effect" => new SimProgram("p0", [Act(failAttempts: 2, afterEffect: true)], -1),
        "timer-then-act" => new SimProgram("p0", [new TimerStep(60), Act()], -1),
        "poison-then-act" => new SimProgram("p0", [new ActStep(0, false, 2, true, Poison: true), Act()], -1),
        _ => new SimProgram("p0", [new SignalStep("sig0"), new ParStep(Act(), Act(failAttempts: 1, afterEffect: true))], -1),
    };

    [Theory]
    [MemberData(nameof(CrashPrograms))]
    [Trait("Category", "CrashMatrix")]
    public async Task Crash_at_every_store_operation_on_postgres_changes_nothing(string name)
    {
        SimProgram program = CrashProgram(name);
        var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 1 };
        const ulong Seed = 11;

        (TestDatabase baselineDb, StoreUnderTest baselineBackend) = await NewBackendAsync();
        SimResult baseline;
        long operations;
        await using (baselineDb)
        {
            var world = new SimWorld(Seed, options, [program], backend: baselineBackend);
            baseline = await world.RunToCompletionAsync();
            operations = world.Store.OperationCount;
        }

        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                (TestDatabase db, StoreUnderTest backend) = await NewBackendAsync();
                await using (db)
                {
                    var world = new SimWorld(Seed, options, [program], backend: backend);
                    long at = k;
                    FaultKind crash = kind;
                    world.Store.ForcedFault = index => index == at ? crash : FaultKind.None;
                    SimResult result;
                    try
                    {
                        result = await world.RunToCompletionAsync();
                    }
                    catch (InvariantViolationException ex)
                    {
                        throw new Xunit.Sdk.XunitException($"{name}: {kind} crash at store operation {k}: {ex.Message}");
                    }

                    Assert.Equal(baseline.Results[0]?[..^1], result.Results[0]?[..^1]);
                }
            }
        }
    }
}
