using Kairos.Testing.Simulation;

namespace Kairos.Core.Tests;

/// <summary>
/// Layer 4. For each reference workflow: count the store operations of a fault-free run, then kill the worker at every single
/// operation boundary (separately: before the store applied it, and after it applied but before the worker heard back),
/// restart, and require the same final result, a verified history, and exactly-once external effects (I1-I4).
/// </summary>
public sealed class CrashAtEveryStepTests
{
    private static ActStep Act(int failAttempts = 0, bool afterEffect = false, int max = 4, bool catches = false) =>
        new(failAttempts, afterEffect, max, catches);

    public static TheoryData<string> ProgramNames() => [.. Programs.Keys];

    private static readonly Dictionary<string, SimProgram> Programs = new()
    {
        ["single-activity"] = new("p0", [Act()], -1),
        ["retry-after-effect"] = new("p0", [Act(failAttempts: 2, afterEffect: true)], -1),
        ["exhausted-uncaught"] = new("p0", [Act(failAttempts: 3, max: 2)], -1),
        ["exhausted-caught-then-more"] = new("p0", [Act(failAttempts: 3, max: 2, catches: true), Act()], -1),
        ["timer"] = new("p0", [new TimerStep(90), Act()], -1),
        ["signal"] = new("p0", [new SignalStep("sig0"), Act()], -1),
        ["parallel"] = new("p0", [new ParStep(Act(), Act(failAttempts: 1, afterEffect: true))], -1),
        ["mixed"] = new("p0", [Act(), new TimerStep(30), new SignalStep("sig2"), new ParStep(Act(), Act()), Act()], -1),
    };

    private static byte[]? WithoutGuidByte(byte[]? result) => result?[..^1];

    [Theory]
    [MemberData(nameof(ProgramNames))]
    public async Task Surviving_a_crash_at_any_store_operation_changes_nothing(string name)
    {
        SimProgram program = Programs[name];
        const ulong seed = 7;
        var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 1 };

        var baselineWorld = new SimWorld(seed, options, [program]);
        SimResult baseline = await baselineWorld.RunToCompletionAsync();
        long operations = baselineWorld.Store.OperationCount;
        Assert.True(operations > 5, "Reference run did too little to be a meaningful crash matrix.");

        int crashes = 0;
        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                var world = new SimWorld(seed, options, [program]);
                long at = k;
                FaultKind crashKind = kind;
                world.Store.ForcedFault = index => index == at ? crashKind : FaultKind.None;

                SimResult result;
                try
                {
                    result = await world.RunToCompletionAsync();
                }
                catch (InvariantViolationException ex)
                {
                    throw new Xunit.Sdk.XunitException($"{name}: crash {kind} at store operation {k}: {ex.Message}");
                }

                crashes += (int)result.Stats.Crashes;
                // The last byte derives from the run id (ctx.NewGuid); a lost start request legitimately gets a fresh run id.
                Assert.Equal(WithoutGuidByte(baseline.Results[0]), WithoutGuidByte(result.Results[0]));
            }
        }

        Assert.True(crashes > 0, "No crash was actually injected.");
    }
}
