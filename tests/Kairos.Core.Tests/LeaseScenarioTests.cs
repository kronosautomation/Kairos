using Kairos.Testing.Simulation;

namespace Kairos.Core.Tests;

/// <summary>
/// Simulator scenarios for the lease review (docs/reviews/2026-10-24-leases.md): L1 long activities must survive by renewing
/// their lease, L2 poison tasks must end in a recorded failure instead of looping. (L3, clock skew, needs a database clock and
/// lives in the Postgres tests; L4, graceful release, is in the store contract tests.)
/// </summary>
public sealed class LeaseScenarioTests
{
    private static ActStep Act(bool slow = false, bool poison = false, int max = 3, bool catches = false) =>
        new(0, false, max, catches, slow, poison);

    private static readonly SimOptions Controlled = new()
    {
        InjectFaults = false,
        Deploy = false,
        Workers = 3,
        ClockJumpsDuringSlowActivities = false, // nobody pauses long enough to excuse a lost lease
    };

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Activities_that_take_three_leases_complete_with_no_lease_expiry_when_they_heartbeat()
    {
        SimProgram program = new("p0", [Act(slow: true), new ParStep(Act(slow: true), Act()), Act(slow: true)], -1);
        var total = new SimStats();

        for (ulong seed = 1; seed <= 40; seed++)
        {
            SimResult result = await new SimWorld(seed, Controlled, [program, program]).RunToCompletionAsync();

            Assert.All(result.Results, r => Assert.NotNull(r)); // I1: every run completed (none failed)
            total.Add(result.Stats);
        }

        Assert.Equal(0, total.LeaseExpirations); // the attempts lasted ~3 leases each, yet no lease ever expired
    }

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Without_heartbeats_the_same_activities_lose_their_leases_but_the_run_still_ends()
    {
        SimProgram program = new("p0", [Act(slow: true), Act(slow: true)], -1);
        var options = new SimOptions
        {
            InjectFaults = false, Deploy = false, Workers = 3, HeartbeatSlowActivities = false, ClockJumpsDuringSlowActivities = false,
        };
        var total = new SimStats();

        for (ulong seed = 1; seed <= 40; seed++)
        {
            // Pre-L2 this was a livelock (I1 violation); now every run reaches a recorded end.
            total.Add((await new SimWorld(seed, options, [program, program]).RunToCompletionAsync()).Stats);
        }

        Assert.True(total.LeaseExpirations > 0, "The negative control never lost a lease, so the scenario proves nothing.");
    }

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Poison_tasks_that_kill_their_worker_end_in_a_recorded_failure_after_maximum_attempts()
    {
        SimProgram uncaught = new("p0", [Act(poison: true, max: 3)], -1);
        SimProgram caught = new("p1", [Act(poison: true, max: 2, catches: true), Act()], -1);
        var total = new SimStats();

        for (ulong seed = 1; seed <= 40; seed++)
        {
            SimResult result = await new SimWorld(seed, Controlled, [uncaught, caught]).RunToCompletionAsync();

            Assert.Null(result.Results[0]);    // failed with the recorded ActivityFailureException
            Assert.NotNull(result.Results[1]); // the workflow caught it and carried on
            total.Add(result.Stats);
        }

        Assert.Equal(40 * (3 + 2), total.LeaseExpirations); // exactly MaximumAttempts expiries per poison activity, never more
    }

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Chaos_with_many_long_and_poison_activities_holds_the_invariants()
    {
        var options = new SimOptions { SlowActivityProbability = 0.35, PoisonActivityProbability = 0.2 };
        var failures = new List<string>();
        var total = new SimStats();

        for (ulong seed = 1; seed <= 300; seed++)
        {
            try
            {
                total.Add((await SimWorld.RunSeedAsync(seed, options)).Stats);
            }
            catch (InvariantViolationException ex)
            {
                failures.Add(ex.Message);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failing seeds:\n{string.Join('\n', failures.Take(10))}");
        Assert.True(total.LeaseExpirations > 0 && total.Crashes > 0, $"Scenario exercised too little: {total}");
    }
}
