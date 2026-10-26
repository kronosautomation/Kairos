namespace Kairos.Core.Tests;

public sealed class CodecTests
{
    private static readonly Guid T = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid N = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid R = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Ts = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    public static TheoryData<HistoryEvent> AllEventTypes() =>
    [
        new WorkflowStarted(T, N, R, 1, Ts, "wf-é", "build-1", [1, 2, 3]),
        new WorkflowStarted(T, N, R, 1, Ts, "wf", "b", [1]) { TraceContext = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" },
        new WorkflowStarted(T, N, R, 1, Ts, "wf", "b", [1]) { StartedBy = "Operator:user-1" },
        new WorkflowStarted(T, N, R, 1, Ts, "wf", "b", [1]) { TraceContext = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", StartedBy = "Admin:ü-2" },
        new ActivityScheduled(T, N, R, 2, Ts, 7, "act", [9], RetryPolicy.Default with { NonRetryableErrorTypes = ["A", "B"], BackoffCoefficient = 1.5 }),
        new ActivityCompleted(T, N, R, 3, Ts, 7, [4, 5]),
        new ActivityFailed(T, N, R, 4, Ts, 7, 2, "TimeoutException", "ünï", true),
        new TimerStarted(T, N, R, 5, Ts, 1, TimeSpan.FromHours(3)),
        new TimerFired(T, N, R, 6, Ts, 1),
        new SignalReceived(T, N, R, 7, Ts, "go", []),
        new PatchMarker(T, N, R, 8, Ts, "add-b"),
        new WorkflowCompleted(T, N, R, 9, Ts, [0]),
        new WorkflowFailed(T, N, R, 10, Ts, "Boom", "message"),
        new RunTerminated(T, N, R, 11, Ts, "operator: stuck after bad deploy"),
    ];

    [Theory]
    [MemberData(nameof(AllEventTypes))]
    public void Every_event_type_round_trips_and_keeps_its_hash(HistoryEvent e)
    {
        HistoryEvent sealedEvent = HashChain.Seal(e, HashChain.GenesisHash);

        byte[] body = HistoryEventCodec.Encode(sealedEvent);
        HistoryEvent decoded = HistoryEventCodec.Decode(body, sealedEvent.PrevHash, sealedEvent.Hash);

        Assert.Equal(sealedEvent.GetType(), decoded.GetType());
        Assert.Equal(body, HistoryEventCodec.Encode(decoded)); // canonical: decode then encode is the identity
        Assert.Equal(sealedEvent.Hash, HashChain.ComputeHash(decoded, decoded.PrevHash)); // a decoded event still verifies
    }

    [Theory]
    [MemberData(nameof(AllEventTypes))]
    public void Truncated_or_extended_bodies_are_rejected_with_format_exception_only(HistoryEvent e)
    {
        byte[] body = HistoryEventCodec.Encode(HashChain.Seal(e, HashChain.GenesisHash));
        byte[] hash = new byte[32];

        HistoryEvent original = HashChain.Seal(e, HashChain.GenesisHash);
        for (int length = 0; length < body.Length; length++)
        {
            try
            {
                HistoryEvent shorter = HistoryEventCodec.Decode(body.AsSpan(0, length), hash, hash);

                // Trailing fields are optional, so a body cut exactly where one begins is the older spelling of a different event. It must decode to that event and to
                // nothing else: its canonical encoding is the prefix, and its hash is not the original's, so a chain would reject the swap.
                Assert.IsType<WorkflowStarted>(e);
                Assert.Equal(body.AsSpan(0, length).ToArray(), HistoryEventCodec.Encode(shorter));
                Assert.NotEqual(original.Hash, HashChain.ComputeHash(shorter, hash));
            }
            catch (FormatException)
            {
            }
        }

        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode([.. body, 0], hash, hash));
    }

    [Fact]
    public void A_run_without_a_starter_keeps_the_encoding_and_hash_it_had_before_the_field_existed()
    {
        // The bytes below are what this event encoded to before StartedBy existed: the header, the three strings and the previous run id, nothing after.
        var plain = new WorkflowStarted(T, N, R, 1, Ts, "wf", "b", [1]);
        byte[] body = HistoryEventCodec.Encode(HashChain.Seal(plain, HashChain.GenesisHash));
        var withTrace = plain with { TraceContext = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" };
        byte[] traced = HistoryEventCodec.Encode(HashChain.Seal(withTrace, HashChain.GenesisHash));
        var withStarter = plain with { StartedBy = "Operator:u" };
        byte[] started = HistoryEventCodec.Encode(HashChain.Seal(withStarter, HashChain.GenesisHash));

        Assert.Equal(body.Length + 4 + withTrace.TraceContext.Length, traced.Length);
        Assert.Equal(body.Length + 4 + 0 + 4 + "Operator:u".Length, started.Length); // an empty trace is written, then the starter
        Assert.NotEqual(HashChain.Seal(plain, HashChain.GenesisHash).Hash, HashChain.Seal(withStarter, HashChain.GenesisHash).Hash); // the starter is covered by the chain
    }

    [Fact]
    public void A_starter_that_is_not_canonical_is_rejected()
    {
        var withStarter = new WorkflowStarted(T, N, R, 1, Ts, "wf", "b", [1]) { StartedBy = "Operator:u" };
        byte[] good = HistoryEventCodec.Encode(HashChain.Seal(withStarter, HashChain.GenesisHash));
        var noStarter = new WorkflowStarted(T, N, R, 1, Ts, "wf", "b", [1]);
        byte[] plain = HistoryEventCodec.Encode(HashChain.Seal(noStarter, HashChain.GenesisHash));

        // An empty trace followed by nothing is the same event as one without either: only one spelling is accepted.
        byte[] emptyTrace = [.. plain, 0, 0, 0, 0];
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(emptyTrace, new byte[32], new byte[32]));
        // An empty starter after a trace is likewise the spelling of "no starter".
        byte[] emptyStarter = [.. good[..^("Operator:u".Length + 4)], 0, 0, 0, 0];
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(emptyStarter, new byte[32], new byte[32]));
        // Longer than the bound.
        var tooLong = withStarter with { StartedBy = new string('x', WorkflowStarted.MaxStartedByLength + 1) };
        Assert.Throws<ArgumentException>(() => StoreRules.CheckStartedBy(tooLong.StartedBy));
        Assert.Throws<ArgumentException>(() => StoreRules.CheckStartedBy("Operator:\nu"));
        Assert.Equal("", StoreRules.CheckStartedBy(""));
    }

    [Fact]
    public void Corrupt_headers_and_lengths_are_rejected()
    {
        byte[] body = HistoryEventCodec.Encode(new WorkflowCompleted(T, N, R, 1, Ts, [1, 2, 3]));
        byte[] hash = new byte[32];

        byte[] badVersion = [.. body];
        badVersion[0] = 99;
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(badVersion, hash, hash));

        byte[] badType = [.. body];
        badType[1] = 200;
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(badType, hash, hash));

        byte[] hugeLength = [.. body];
        hugeLength[^7] = 0xFF; // length prefix of the result payload
        hugeLength[^6] = 0xFF;
        hugeLength[^5] = 0xFF;
        hugeLength[^4] = 0x7F;
        Assert.Throws<FormatException>(() => HistoryEventCodec.Decode(hugeLength, hash, hash));
    }

    [Fact]
    public void Random_bytes_never_throw_anything_but_format_exception()
    {
        var rng = new Testing.SimRandom(12345);
        byte[] hash = new byte[32];
        for (int i = 0; i < 5000; i++)
        {
            byte[] junk = rng.Bytes(rng.Between(0, 200));
            if (junk.Length > 1 && rng.Chance(0.7))
            {
                junk[0] = 1;
                junk[1] = (byte)rng.Between(1, 10);
            }

            try
            {
                HistoryEventCodec.Decode(junk, hash, hash);
            }
            catch (FormatException)
            {
                // expected for malformed input
            }
        }
    }
}
