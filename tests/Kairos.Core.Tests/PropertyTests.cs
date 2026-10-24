using System.Text;
using CsCheck;
using Kairos.Testing.Simulation;

namespace Kairos.Core.Tests;

/// <summary>
/// Layer 1: property-based tests over randomly generated workflows. A failing case is shrunk to a minimal workflow and printed
/// with its CsCheck seed (rerun with <c>seed:</c> to reproduce). Iterations: KAIROS_PROP_ITER (default 150).
/// </summary>
public sealed class PropertyTests
{
    private static int Iterations(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable("KAIROS_PROP_ITER"), out int n) ? n : fallback;

    // ---- generators ---------------------------------------------------------------------------------------------

    private static readonly Gen<ActStep> GenAct = Gen.Select(
        Gen.Int[0, 3], Gen.Bool, Gen.Int[1, 4], Gen.Bool, Gen.Int[0, 9], Gen.Int[0, 19],
        (fail, afterEffect, max, catches, slow, poison) => new ActStep(fail, afterEffect, max, catches, slow == 0, poison == 0));

    private static readonly Gen<SimStep> GenStep = Gen.OneOf<SimStep>(
        GenAct.Select(a => (SimStep)a),
        Gen.Int[1, 7200].Select(s => (SimStep)new TimerStep(s)),
        Gen.Const<SimStep>(new SignalStep("placeholder")),
        Gen.Select(GenAct, GenAct, (a, b) => (SimStep)new ParStep(a, b)));

    /// <summary>A program with unique signal names and an optional patch point.</summary>
    private static readonly Gen<SimProgram> GenProgram = Gen.Select(GenStep.Array[1, 6], Gen.Int[-1, 6], (steps, patch) =>
    {
        SimStep[] named = [.. steps.Select((s, i) => s is SignalStep ? new SignalStep($"sig{i}") : s)];
        return new SimProgram("p", named, patch > named.Length ? -1 : patch);
    });

    private static IReadOnlyList<SimProgram> Typed(SimProgram[] programs) =>
        [.. programs.Select((p, i) => p with { Type = $"p{i}" })];

    private static string Describe(WorkflowCommand c) => c switch
    {
        ScheduleActivityCommand s => $"act {s.ActivityId} {s.ActivityType} {Convert.ToHexString(s.Input)}",
        StartTimerCommand t => $"timer {t.TimerId} {t.Duration}",
        RecordPatchCommand p => $"patch {p.PatchId}",
        CompleteWorkflowCommand w => $"done {Convert.ToHexString(w.Result)}",
        FailWorkflowCommand f => $"fail {f.ErrorType}",
        ContinueAsNewCommand n => $"continue {n.NewRunId} {Convert.ToHexString(n.Input)}",
        _ => c.GetType().Name,
    };

    private static string Describe(IEnumerable<WorkflowCommand> commands) => string.Join("|", commands.Select(Describe));

    private static IReadOnlyList<IReadOnlyList<HistoryEvent>> Histories(ulong seed, IReadOnlyList<SimProgram> programs, out bool patched)
    {
        var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 2 };
        var world = new SimWorld(seed, options, programs);
        world.RunToCompletionAsync().GetAwaiter().GetResult();
        patched = false;
        return world.GetHistoriesAsync().GetAwaiter().GetResult();
    }

    private static bool IsCommitPoint(IReadOnlyList<HistoryEvent> history, int length)
    {
        if (length >= history.Count)
        {
            return true;
        }

        static bool CommandSide(HistoryEvent e) =>
            e.Type is HistoryEventType.WorkflowTaskCompleted or HistoryEventType.ActivityScheduled or HistoryEventType.TimerStarted
                or HistoryEventType.PatchMarker;

        return !(CommandSide(history[length - 1]) && (CommandSide(history[length]) || StoreRules.IsClosing(history[length])));
    }

    // ---- I4: replay round trip -----------------------------------------------------------------------------------

    [Fact]
    public void Replaying_any_recorded_history_yields_identical_commands_every_time()
    {
        Gen.Select(GenProgram.Array[1, 3], Gen.ULong).Sample(
            t =>
            {
                IReadOnlyList<SimProgram> programs = Typed(t.Item1);
                var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 2 };
                var world = new SimWorld(t.Item2, options, programs);
                world.RunToCompletionAsync().GetAwaiter().GetResult();
                IReadOnlyList<IReadOnlyList<HistoryEvent>> histories = world.GetHistoriesAsync().GetAwaiter().GetResult();

                for (int i = 0; i < programs.Count; i++)
                {
                    WorkflowFunc code = SimPrograms.Compile(programs[i], patchedBuild: false);
                    IReadOnlyList<HistoryEvent> history = histories[i];

                    // The finished history is a no-op, however often it is replayed.
                    for (int round = 0; round < 3; round++)
                    {
                        ReplayResult full = WorkflowReplayer.Replay(code, history);
                        Assert.True(full.IsClosed);
                        Assert.Empty(full.NewCommands);
                    }

                    // Every point the system can be in yields the same next commands on each replay.
                    for (int length = 1; length <= history.Count; length++)
                    {
                        if (!IsCommitPoint(history, length))
                        {
                            continue;
                        }

                        HistoryEvent[] prefix = [.. history.Take(length)];
                        string first = Describe(WorkflowReplayer.Replay(code, prefix, verifyHashChain: false).NewCommands);
                        string second = Describe(WorkflowReplayer.Replay(code, prefix, verifyHashChain: false).NewCommands);
                        Assert.Equal(first, second);
                    }
                }
            },
            iter: Iterations(60));
    }

    // ---- I6: any tampering with a history is detected ------------------------------------------------------------

    private static HistoryEvent[] SampleHistory(SimProgram[] programs, ulong seed)
    {
        IReadOnlyList<IReadOnlyList<HistoryEvent>> histories = Histories(seed, Typed(programs), out _);
        return [.. histories[0]];
    }

    [Fact]
    public void Any_single_byte_change_in_a_stored_event_is_detected()
    {
        int effective = 0;
        Gen.Select(GenProgram.Array[1, 4], Gen.ULong, Gen.Int[0, 10_000], Gen.Int[0, 10_000], Gen.Int[1, 255]).Sample(
            t =>
            {
                HistoryEvent[] history = SampleHistory(t.Item1, t.Item2);
                int index = t.Item3 % history.Length;
                byte[] body = HistoryEventCodec.Encode(history[index]);
                int position = t.Item4 % body.Length;
                byte[] mutated = [.. body];
                mutated[position] ^= (byte)t.Item5;

                HistoryEvent decoded;
                try
                {
                    decoded = HistoryEventCodec.Decode(mutated, history[index].PrevHash, history[index].Hash);
                }
                catch (FormatException)
                {
                    return; // no longer a decodable event: stores reject it on read, which is also detection
                }

                if (HistoryEventCodec.Encode(decoded).AsSpan().SequenceEqual(body))
                {
                    return; // the flip changed nothing the codec distinguishes (e.g. a boolean byte 1 vs 3)
                }

                HistoryEvent[] tampered = [.. history];
                tampered[index] = decoded;
                Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(tampered));
                Interlocked.Increment(ref effective);
            },
            iter: Iterations(150));
        Assert.True(effective > Iterations(150) / 4, $"Only {effective} mutations decoded to a different event; the property is not exercising tamper detection.");
    }

    [Fact]
    public void Deleting_inserting_or_swapping_events_is_detected()
    {
        int effective = 0;
        Gen.Select(GenProgram.Array[1, 4], Gen.ULong, Gen.Int[0, 3], Gen.Int[0, 10_000], Gen.Int[0, 10_000]).Sample(
            t =>
            {
                HistoryEvent[] history = SampleHistory(t.Item1, t.Item2);
                if (history.Length < 3)
                {
                    return;
                }

                int i = t.Item4 % history.Length;
                int j = t.Item5 % history.Length;
                List<HistoryEvent> mutated = [.. history];
                switch (t.Item3)
                {
                    case 0: // delete (the last event excluded: a shorter chain is valid until compared with the stored head)
                        i %= history.Length - 1;
                        mutated.RemoveAt(i);
                        break;
                    case 1: // insert a copy of an existing event somewhere else
                        if (i == j || j == i + 1)
                        {
                            return;
                        }

                        mutated.Insert(j, history[i]);
                        break;
                    case 2: // swap two different events
                        if (i == j)
                        {
                            return;
                        }

                        (mutated[i], mutated[j]) = (mutated[j], mutated[i]);
                        break;
                    default: // drop the first event
                        mutated.RemoveAt(0);
                        break;
                }

                Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(mutated));
                Interlocked.Increment(ref effective);
            },
            iter: Iterations(150));
        Assert.True(effective > Iterations(150) / 4, $"Only {effective} structural mutations ran.");
    }

    // ---- serialization round trip ---------------------------------------------------------------------------------

    private static readonly Gen<Guid> GenGuid = Gen.Guid;
    private static readonly Gen<byte[]> GenBytes = Gen.Byte.Array[0, 40];
    private static readonly Gen<string> GenText = Gen.String[0, 30];

    private static readonly Gen<RetryPolicy> GenPolicy = Gen.Select(
        Gen.Int[1, 9], Gen.Int[1, 10_000], Gen.Double[1, 4], Gen.Int[1, 100_000], GenText.Array[0, 3],
        (attempts, initial, coefficient, max, errors) => new RetryPolicy
        {
            MaximumAttempts = attempts,
            InitialInterval = TimeSpan.FromMilliseconds(initial),
            BackoffCoefficient = coefficient,
            MaximumInterval = TimeSpan.FromMilliseconds(max),
            NonRetryableErrorTypes = errors,
        });

    private static readonly Gen<HistoryEvent> GenEvent = Gen.Select(
        GenGuid, GenGuid, GenGuid, Gen.Long[1, 1_000_000], Gen.Int[0, 1_000_000_000], Gen.Int[0, 13],
        (t, n, r, seq, secs, kind) => (t, n, r, seq, ts: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(secs), kind))
        .SelectMany(h => Gen.Select(GenText, GenText, GenBytes, GenPolicy, Gen.Int[0, 100], Gen.Bool, GenGuid, (text, text2, bytes, policy, num, flag, guid) =>
        {
            (Guid t, Guid n, Guid r, long seq, DateTimeOffset ts, int kind) = h;
            return kind switch
            {
                0 => new WorkflowStarted(t, n, r, seq, ts, text, text2, bytes) { PreviousRunId = guid },
                1 => new ActivityScheduled(t, n, r, seq, ts, num, text, bytes, policy),
                2 => (HistoryEvent)new ActivityCompleted(t, n, r, seq, ts, num, bytes),
                3 => new ActivityFailed(t, n, r, seq, ts, num, num + 1, text, text2, flag),
                4 => new TimerStarted(t, n, r, seq, ts, num, TimeSpan.FromMilliseconds(num * 1000L)),
                5 => new TimerFired(t, n, r, seq, ts, num),
                6 => new SignalReceived(t, n, r, seq, ts, text, bytes),
                7 => new WorkflowCompleted(t, n, r, seq, ts, bytes),
                8 => new WorkflowFailed(t, n, r, seq, ts, text, text2),
                9 => new PatchMarker(t, n, r, seq, ts, text),
                10 => new WorkflowTaskCompleted(t, n, r, seq, ts),
                11 => new RunTerminated(t, n, r, seq, ts, text),
                _ => new WorkflowContinued(t, n, r, seq, ts, guid, bytes),
            };
        }));

    [Fact]
    public void Every_event_type_round_trips_through_the_codec_and_keeps_its_hash()
    {
        GenEvent.Sample(
            e =>
            {
                HistoryEvent sealedEvent = HashChain.Seal(e, HashChain.GenesisHash);
                byte[] body = HistoryEventCodec.Encode(sealedEvent);
                HistoryEvent decoded = HistoryEventCodec.Decode(body, sealedEvent.PrevHash, sealedEvent.Hash);

                Assert.Equal(sealedEvent.GetType(), decoded.GetType());
                Assert.Equal(body, HistoryEventCodec.Encode(decoded));
                Assert.Equal(sealedEvent.Hash, HashChain.ComputeHash(decoded, decoded.PrevHash));
                _ = Encoding.UTF8; // strings above include arbitrary Unicode via Gen.String; encoding must be lossless
            },
            iter: Iterations(400));
    }

    // ---- I1-I4, I9 under chaos, with shrinking ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Simulation")]
    public void Any_generated_set_of_workflows_survives_chaos_with_all_invariants_intact()
    {
        Gen.Select(GenProgram.Array[1, 4], Gen.ULong).Sample(
            t =>
            {
                try
                {
                    SimWorld.RunChaosAsync(t.Item2, new SimOptions { MaxSteps = 150, Deploy = true }, Typed(t.Item1)).GetAwaiter().GetResult();
                }
                catch (InvariantViolationException ex)
                {
                    throw new Xunit.Sdk.XunitException(ex.Message);
                }
            },
            iter: Iterations(60));
    }
}
