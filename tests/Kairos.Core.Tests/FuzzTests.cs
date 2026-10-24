using System.Diagnostics;
using Kairos.Testing;

namespace Kairos.Core.Tests;

/// <summary>
/// Layer 7 fuzzing, the per-commit slice (docs/TEST-STRATEGY.md). Everything that reads untrusted bytes must fail in exactly one
/// defined way and never hang, over-allocate or crash. Mutational fuzzing starts from the golden corpus, so inputs are close to valid.
/// Coverage-guided SharpFuzz runs of the same entry points belong in the nightly pipeline. Iterations: KAIROS_FUZZ_ITER.
/// </summary>
public sealed class FuzzTests
{
    private static int Iterations(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable("KAIROS_FUZZ_ITER"), out int n) ? n : fallback;

    private static List<CorpusEntry> LoadCorpus()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Corpus");
        return [.. Directory.EnumerateFiles(root, "*.khist", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => GoldenCorpus.Parse(f, File.ReadAllText(f)))];
    }

    private static byte[] Mutate(SimRandom rng, byte[] original, IReadOnlyList<byte[]> others)
    {
        byte[] b = [.. original];
        switch (rng.Next(7))
        {
            case 0: // flip a few bits
                for (int i = rng.Between(1, 3); i > 0 && b.Length > 0; i--)
                {
                    b[rng.Next(b.Length)] ^= (byte)(1 << rng.Next(8));
                }

                return b;
            case 1: // truncate
                return b.Length == 0 ? b : b[..rng.Next(b.Length)];
            case 2: // append noise
                return [.. b, .. rng.Bytes(rng.Between(1, 40))];
            case 3: // overwrite a window with extreme values (length fields, counts, ticks)
                if (b.Length >= 4)
                {
                    int at = rng.Next(b.Length - 3);
                    byte fill = rng.Pick(new byte[] { 0x00, 0xFF, 0x7F, 0x80 });
                    for (int k = 0; k < 4; k++)
                    {
                        b[at + k] = fill;
                    }
                }

                return b;
            case 4: // splice the head of this event onto the tail of another
                byte[] other = rng.Pick(others);
                return [.. b[..rng.Next(b.Length + 1)], .. other[rng.Next(other.Length + 1)..]];
            case 5: // random type byte
                if (b.Length > 1)
                {
                    b[1] = (byte)rng.Next(256);
                }

                return b;
            default: // pure noise of similar size
                return rng.Bytes(rng.Between(0, original.Length + 10));
        }
    }

    [Fact]
    [Trait("Category", "Fuzz")]
    public void Mutated_event_bytes_decode_or_fail_with_format_exception_only_and_quickly()
    {
        List<CorpusEntry> corpus = LoadCorpus();
        byte[][] bodies = [.. corpus.SelectMany(c => c.History).Select(HistoryEventCodec.Encode)];
        var rng = new SimRandom(20261024);
        byte[] hash = new byte[32];
        int decoded = 0, rejected = 0;
        long slowest = 0;

        for (int i = 0; i < Iterations(60_000); i++)
        {
            byte[] input = Mutate(rng, rng.Pick(bodies), bodies);
            long start = Stopwatch.GetTimestamp();
            try
            {
                HistoryEvent e = HistoryEventCodec.Decode(input, hash, hash);
                decoded++;
                // Whatever decodes must be a stable event: encoding it again and decoding that gives the same bytes.
                byte[] again = HistoryEventCodec.Encode(e);
                Assert.Equal(again, HistoryEventCodec.Encode(HistoryEventCodec.Decode(again, hash, hash)));
            }
            catch (FormatException)
            {
                rejected++;
            }

            slowest = Math.Max(slowest, Stopwatch.GetTimestamp() - start);
        }

        Assert.True(decoded > 100 && rejected > 100, $"The fuzzer did not reach both outcomes (decoded {decoded}, rejected {rejected}).");
        Assert.True(Stopwatch.GetElapsedTime(0, slowest).TotalMilliseconds < 200, "A single decode took far too long.");
    }

    /// <summary>Re-chains edited events so they pass integrity checks: the semantic checks in replay are what is under test.</summary>
    private static List<HistoryEvent> Reseal(List<HistoryEvent> edited)
    {
        var sealedEvents = new List<HistoryEvent>(edited.Count);
        byte[] prev = HashChain.GenesisHash.ToArray();
        for (int i = 0; i < edited.Count; i++)
        {
            HistoryEvent s = StoreRules.Seal(edited[i], i + 1, prev);
            sealedEvents.Add(s);
            prev = s.Hash;
        }

        return sealedEvents;
    }

    [Fact]
    [Trait("Category", "Fuzz")]
    public void Semantically_corrupt_but_validly_chained_histories_are_rejected_in_a_defined_way()
    {
        List<CorpusEntry> corpus = LoadCorpus();
        var rng = new SimRandom(777);
        int divergences = 0, replayed = 0;

        for (int i = 0; i < Iterations(8_000); i++)
        {
            CorpusEntry entry = rng.Pick(corpus);
            List<HistoryEvent> events = [.. entry.History];
            for (int edits = rng.Between(1, 3); edits > 0 && events.Count > 1; edits--)
            {
                int at = rng.Next(events.Count);
                switch (rng.Next(6))
                {
                    case 0:
                        events.RemoveAt(at);
                        break;
                    case 1:
                        events.Insert(rng.Next(events.Count + 1), events[at]);
                        break;
                    case 2:
                        int other = rng.Next(events.Count);
                        (events[at], events[other]) = (events[other], events[at]);
                        break;
                    case 3:
                        events[at] = Retarget(events[at], rng);
                        break;
                    case 4:
                        events.RemoveRange(at, events.Count - at); // truncated history (a prefix)
                        break;
                    default:
                        events.Add(rng.Pick(entry.History)); // junk after the end
                        break;
                }
            }

            if (events.Count == 0)
            {
                continue;
            }

            List<HistoryEvent> chained = Reseal(events);
            try
            {
                WorkflowReplayer.Replay(GoldenCorpus.Resolve(entry.ReplayWith), chained, verifyHashChain: true);
                replayed++;
            }
            catch (Exception ex) when (ex is NonDeterminismException or HistoryIntegrityException or ArgumentException)
            {
                divergences++; // a defined refusal
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Replay of an edited '{entry.File}' threw {ex.GetType().Name} instead of a defined refusal: {ex.Message}\n{ex.StackTrace}");
            }
        }

        Assert.True(divergences > 500, $"Only {divergences} of the edited histories were refused ({replayed} replayed); the fuzzer is too tame.");
    }

    private static HistoryEvent Retarget(HistoryEvent e, SimRandom rng) => e switch
    {
        ActivityScheduled s => s with { ActivityId = rng.Between(0, 5) },
        ActivityCompleted c => c with { ActivityId = rng.Between(0, 5) },
        ActivityFailed f => f with { ActivityId = rng.Between(0, 5), Attempt = rng.Between(0, 5) },
        TimerStarted t => t with { TimerId = rng.Between(0, 5), Duration = TimeSpan.FromSeconds(rng.Between(0, 100)) },
        TimerFired t => t with { TimerId = rng.Between(0, 5) },
        SignalReceived s => s with { SignalName = "sig" + rng.Between(0, 5) },
        WorkflowCompleted c => c with { Result = rng.Bytes(rng.Between(0, 4)) },
        WorkflowContinued c => c with { NewRunId = rng.NextGuid() },
        _ => e,
    };
}
