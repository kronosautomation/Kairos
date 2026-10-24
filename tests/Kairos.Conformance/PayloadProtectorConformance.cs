using System.Security.Cryptography;

namespace Kairos.Conformance;

/// <summary>What every <see cref="IPayloadProtector"/> must do: confidentiality, binding to scope and context, and integrity.</summary>
public abstract class PayloadProtectorConformance
{
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    protected static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));
    private static readonly byte[] Ctx = "run-1|start|0"u8.ToArray();

    protected abstract IPayloadProtector CreateProtector();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(2_000_000)]
    public async Task Sealed_payloads_open_to_the_original(int length)
    {
        IPayloadProtector p = CreateProtector();
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);

        byte[] sealedBytes = await p.ProtectAsync(A, plaintext, Ctx, CancellationToken.None);

        Assert.Equal(plaintext, await p.UnprotectAsync(A, sealedBytes, Ctx, CancellationToken.None));
    }

    [Fact]
    public async Task The_sealed_form_hides_the_plaintext_and_is_different_every_time()
    {
        IPayloadProtector p = CreateProtector();
        byte[] plaintext = "SSN 078-05-1120 and the rest of a long enough canary string"u8.ToArray();

        byte[] first = await p.ProtectAsync(A, plaintext, Ctx, CancellationToken.None);
        byte[] second = await p.ProtectAsync(A, plaintext, Ctx, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.False(first.AsSpan().IndexOf(plaintext.AsSpan(0, 12)) >= 0, "Plaintext is visible in the sealed payload.");
        Assert.True(first.Length > plaintext.Length);
    }

    [Fact]
    public async Task A_payload_opens_only_for_its_own_scope_and_context()
    {
        IPayloadProtector p = CreateProtector();
        byte[] sealedBytes = await p.ProtectAsync(A, "secret"u8.ToArray(), Ctx, CancellationToken.None);

        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await p.UnprotectAsync(B, sealedBytes, Ctx, CancellationToken.None));
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await p.UnprotectAsync(new TenantScope(A.TenantId, B.NamespaceId), sealedBytes, Ctx, CancellationToken.None));
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await p.UnprotectAsync(A, sealedBytes, "run-2|start|0"u8.ToArray(), CancellationToken.None));
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await p.UnprotectAsync(A, sealedBytes, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Altering_any_byte_is_detected_and_only_payload_protection_exceptions_escape()
    {
        IPayloadProtector p = CreateProtector();
        byte[] sealedBytes = await p.ProtectAsync(A, "important"u8.ToArray(), Ctx, CancellationToken.None);

        for (int i = 0; i < sealedBytes.Length; i++)
        {
            byte[] altered = [.. sealedBytes];
            altered[i] ^= 0x01;
            await Assert.ThrowsAsync<PayloadProtectionException>(async () => await p.UnprotectAsync(A, altered, Ctx, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Truncated_extended_and_random_input_is_refused_cleanly()
    {
        IPayloadProtector p = CreateProtector();
        byte[] sealedBytes = await p.ProtectAsync(A, "important"u8.ToArray(), Ctx, CancellationToken.None);
        var rng = new Random(42);

        byte[] extended = new byte[sealedBytes.Length + 1];
        sealedBytes.CopyTo(extended, 0);
        var bad = new List<byte[]> { Array.Empty<byte>(), new byte[] { 1 }, sealedBytes[..^1], sealedBytes[..8], extended, new byte[100] };
        for (int i = 0; i < 300; i++)
        {
            byte[] junk = new byte[rng.Next(0, 200)];
            rng.NextBytes(junk);
            if (junk.Length > 0 && rng.Next(2) == 0)
            {
                junk[0] = sealedBytes[0]; // look like a real header sometimes
            }

            bad.Add(junk);
        }

        foreach (byte[] candidate in bad)
        {
            await Assert.ThrowsAsync<PayloadProtectionException>(async () => await p.UnprotectAsync(A, candidate, Ctx, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Concurrent_use_is_safe()
    {
        IPayloadProtector p = CreateProtector();

        await Parallel.ForAsync(0, 300, async (i, ct) =>
        {
            byte[] plaintext = BitConverter.GetBytes(i);
            byte[] context = BitConverter.GetBytes(i * 7);
            byte[] sealedBytes = await p.ProtectAsync(i % 2 == 0 ? A : B, plaintext, context, ct);
            Assert.Equal(plaintext, await p.UnprotectAsync(i % 2 == 0 ? A : B, sealedBytes, context, ct));
        });
    }
}
