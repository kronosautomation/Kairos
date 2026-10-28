using Kairos.Cloud.SelfHosted;
using Kairos.Testing.Simulation;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

/// <summary>
/// Layer 4 with blob offload on: every payload (threshold one byte) goes through the blob store, and the worker, the store or the blob store is killed at every single operation, before it applied and after it applied but
/// before the caller heard back, including the steps between "the blob is stored" and "the event that refers to it is committed". Then: the same result as without a crash, a verified history that opens completely (so no
/// reference points at nothing), at most one effect per idempotency key (the simulation's own checks), and the blob store holds ciphertext only: every blob is exactly something the protector returned, never a plaintext it was given.
/// Blobs no event refers to are allowed (a crash between the two leaves one) and are the only kind of garbage.
/// </summary>
public sealed class BlobOffloadCrashMatrixTests
{
    private sealed class WatchingProtector(IPayloadProtector inner) : IPayloadProtector
    {
        public List<byte[]> Plaintexts { get; } = [];

        public List<byte[]> Ciphertexts { get; } = [];

        public async ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] sealedBytes = await inner.ProtectAsync(scope, plaintext, context, cancellationToken);
            Plaintexts.Add(plaintext.ToArray());
            Ciphertexts.Add(sealedBytes);
            return sealedBytes;
        }

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) =>
            inner.UnprotectAsync(scope, protectedPayload, context, cancellationToken);
    }

    private static ActStep Act(int failAttempts = 0, bool afterEffect = false, int max = 4, bool catches = false) => new(failAttempts, afterEffect, max, catches);

    public static TheoryData<string> ProgramNames() => [.. Programs.Keys];

    private static readonly Dictionary<string, SimProgram> Programs = new()
    {
        ["single-activity"] = new("p0", [Act()], -1),
        ["retry-after-effect"] = new("p0", [Act(failAttempts: 2, afterEffect: true)], -1),
        ["exhausted-caught-then-more"] = new("p0", [Act(failAttempts: 3, max: 2, catches: true), Act()], -1),
        ["signal"] = new("p0", [new SignalStep("sig0"), Act()], -1),
        ["parallel"] = new("p0", [new ParStep(Act(), Act(failAttempts: 1, afterEffect: true))], -1),
        ["mixed"] = new("p0", [Act(), new TimerStep(30), new SignalStep("sig2"), new ParStep(Act(), Act()), Act()], -1),
    };

    private static (SimWorld World, RecordingBlobStore Blobs, WatchingProtector Protector) NewWorld(SimProgram program)
    {
        var blobs = new RecordingBlobStore();
        var protector = new WatchingProtector(new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral()));
        var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 1, Protector = protector, Offload = new BlobOffloadOptions(blobs, 1) };
        return (new SimWorld(7, options, [program]), blobs, protector);
    }

    private static byte[]? WithoutGuidByte(byte[]? result) => result?[..^1];

    private static void AssertCiphertextOnly(RecordingBlobStore blobs, WatchingProtector protector, string where)
    {
        Assert.NotEmpty(blobs.All);
        foreach ((TenantScope _, string hash, byte[] content) in blobs.All)
        {
            Assert.True(protector.Ciphertexts.Any(c => c.AsSpan().SequenceEqual(content)), $"{where}: blob {hash[..12]} is not something the protector returned");
            Assert.DoesNotContain(protector.Plaintexts, p => p.Length > 0 && p.AsSpan().SequenceEqual(content));
        }
    }

    [Theory]
    [MemberData(nameof(ProgramNames))]
    public async Task Surviving_a_crash_at_any_store_or_blob_operation_changes_nothing_and_leaves_no_reference_to_nothing(string name)
    {
        SimProgram program = Programs[name];
        (SimWorld baselineWorld, RecordingBlobStore baselineBlobs, WatchingProtector baselineProtector) = NewWorld(program);
        SimResult baseline = await baselineWorld.RunToCompletionAsync();
        long operations = baselineWorld.Store.OperationCount;
        Assert.True(operations > 8, "The reference run did too little to be a meaningful crash matrix.");
        AssertCiphertextOnly(baselineBlobs, baselineProtector, name + " baseline");

        int crashes = 0;
        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                (SimWorld world, RecordingBlobStore blobs, WatchingProtector protector) = NewWorld(program);
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
                    throw new Xunit.Sdk.XunitException($"{name}: crash {kind} at operation {k}: {ex.Message}");
                }

                crashes += (int)result.Stats.Crashes;
                Assert.Equal(WithoutGuidByte(baseline.Results[0]), WithoutGuidByte(result.Results[0]));
                AssertCiphertextOnly(blobs, protector, $"{name}: crash {kind} at operation {k}");
            }
        }

        Assert.True(crashes > 0, "No crash was actually injected.");
    }

    [Fact]
    public async Task The_matrix_really_reaches_the_blob_store()
    {
        (SimWorld world, RecordingBlobStore blobs, _) = NewWorld(Programs["mixed"]);
        _ = await world.RunToCompletionAsync();

        // Each blob call is an operation of the same counter as the store's, so a fault at index k can land on a put or a get.
        Assert.True(blobs.All.Count >= 4);
        Assert.True(world.Store.OperationCount > blobs.All.Count * 2);
    }
}
