namespace Kairos.Testing.Simulation;

/// <summary>A generated workflow, described as data so it can be compiled to a <see cref="WorkflowFunc"/> and replayed forever.</summary>
public sealed record SimProgram(string Type, IReadOnlyList<SimStep> Steps, int PatchAt)
{
    public override string ToString() => $"{Type}[{string.Join(", ", Steps)}] patchAt={PatchAt}";

    public IEnumerable<string> SignalNames => Steps.OfType<SignalStep>().Select(s => s.Name);
}

public abstract record SimStep;

/// <param name="FailAttempts">The first N attempts throw.</param>
/// <param name="FailAfterEffect">Throw after the external side effect happened (the nastiest case for exactly-once).</param>
public sealed record ActStep(int FailAttempts, bool FailAfterEffect, int MaxAttempts, bool Catch) : SimStep;

public sealed record TimerStep(int Seconds) : SimStep;

public sealed record SignalStep(string Name) : SimStep;

public sealed record ParStep(ActStep A, ActStep B) : SimStep;

public static class SimPrograms
{
    public static SimProgram Generate(SimRandom rng, string type)
    {
        int count = rng.Between(1, 7);
        var steps = new List<SimStep>(count);
        for (int i = 0; i < count; i++)
        {
            int roll = rng.Next(100);
            steps.Add(roll switch
            {
                < 45 => RandomAct(rng),
                < 60 => new TimerStep(rng.Between(1, 7200)),
                < 75 => new SignalStep($"sig{i}"),
                _ => new ParStep(RandomAct(rng), RandomAct(rng)),
            });
        }

        return new SimProgram(type, steps, rng.Chance(0.5) ? rng.Next(count + 1) : -1);
    }

    public static ActStep RandomAct(SimRandom rng) =>
        new(rng.Chance(0.7) ? 0 : rng.Between(1, 4), rng.Chance(0.5), rng.Between(1, 4), rng.Chance(0.5));

    /// <summary>
    /// The interpreter. With <paramref name="patchedBuild"/> the program gains one extra activity at <see cref="SimProgram.PatchAt"/>,
    /// gated by the patch API, exactly as a developer would ship a change to code that already has runs in flight.
    /// </summary>
    public static WorkflowFunc Compile(SimProgram program, bool patchedBuild) => async (ctx, _) =>
    {
        var output = new List<byte>();
        for (int i = 0; i <= program.Steps.Count; i++)
        {
            if (patchedBuild && i == program.PatchAt && ctx.IsPatched("extra-step"))
            {
                output.Add(await RunAct(ctx, new ActStep(0, false, 2, false)));
            }

            if (i == program.Steps.Count)
            {
                break;
            }

            switch (program.Steps[i])
            {
                case ActStep a:
                    output.Add(await RunAct(ctx, a));
                    break;
                case TimerStep t:
                    await ctx.DelayAsync(TimeSpan.FromSeconds(t.Seconds));
                    output.Add(1);
                    break;
                case SignalStep s:
                    byte[] payload = await ctx.WaitForSignalAsync(s.Name);
                    output.Add(payload.Length == 0 ? (byte)2 : payload[0]);
                    break;
                case ParStep p:
                    output.Add(await RunPar(ctx, p));
                    break;
            }
        }

        output.Add(ctx.NewGuid().ToByteArray()[0]);
        return [.. output];
    };

    private static RetryPolicy Policy(ActStep a) => RetryPolicy.Default with
    {
        MaximumAttempts = a.MaxAttempts,
        InitialInterval = TimeSpan.FromSeconds(1),
        MaximumInterval = TimeSpan.FromSeconds(8),
    };

    private static byte[] Input(ActStep a) => [(byte)a.FailAttempts, a.FailAfterEffect ? (byte)1 : (byte)0];

    private static async Task<byte> RunAct(IWorkflowContext ctx, ActStep a)
    {
        try
        {
            return (await ctx.ExecuteActivityAsync("act", Input(a), Policy(a)))[0];
        }
        catch (ActivityFailureException) when (a.Catch)
        {
            return 0xFF;
        }
    }

    private static async Task<byte> RunPar(IWorkflowContext ctx, ParStep p)
    {
        Task<byte[]> first = ctx.ExecuteActivityAsync("act", Input(p.A), Policy(p.A));
        Task<byte[]> second = ctx.ExecuteActivityAsync("act", Input(p.B), Policy(p.B));
        try
        {
            byte[][] results = await Task.WhenAll(first, second);
            return (byte)(results[0][0] ^ results[1][0]);
        }
        catch (ActivityFailureException) when (p.A.Catch && p.B.Catch)
        {
            return 0xFE;
        }
    }
}

/// <summary>
/// An external system with idempotency-key deduplication, as a payment API or tool server would have. Records every call so
/// I3 can be checked: each key takes effect at most once, only scheduled activities produce keys, and every completed
/// activity's key took effect.
/// </summary>
public sealed class FakeExternalSystem
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _effects = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Effects => _effects;

    public IReadOnlyDictionary<string, int> Calls => _calls;

    public void Apply(string idempotencyKey)
    {
        lock (_calls) // real-Postgres tests call this from many threads
        {
            _calls[idempotencyKey] = _calls.GetValueOrDefault(idempotencyKey) + 1;
            _effects.Add(idempotencyKey); // set semantics: a second call with the same key has no further effect
        }
    }

    public ActivityFunc Activity() => (ctx, input, _) =>
    {
        bool shouldFail = ctx.Attempt <= input[0];
        bool failAfterEffect = input[1] != 0;
        if (shouldFail && !failAfterEffect)
        {
            throw new InvalidOperationException("transient failure before effect");
        }

        Apply(ctx.IdempotencyKey);
        if (shouldFail)
        {
            throw new InvalidOperationException("transient failure after effect");
        }

        return Task.FromResult(new[] { (byte)ctx.ActivityId });
    };
}
