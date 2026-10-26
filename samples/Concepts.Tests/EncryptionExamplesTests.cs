using Kairos.Samples.Concepts;
using Kairos.Worker;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The data-key-scope region of the payload-encryption page: a smaller scope means more wraps, which is its price.</summary>
public sealed class EncryptionExamplesTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private sealed class CountingKeys : IKeyProvider
    {
        public int Wraps { get; private set; }

        public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            Wraps++;
            return ValueTask.FromResult(new WrappedKey("test-key", dataKey.ToArray()));
        }

        public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(wrapped.Ciphertext.ToArray());
    }

    [Fact]
    public async Task A_smaller_key_scope_costs_a_wrap_per_key_and_the_default_costs_one()
    {
        var keys = new CountingKeys();
        (EnvelopePayloadProtector tightest, EnvelopePayloadProtector shorter) = EncryptionExamples.ProtectorsWithSmallerKeyScope(keys);
        for (int i = 0; i < 6; i++)
        {
            await tightest.ProtectAsync(Scope, new byte[] { 1 }, new byte[] { 2 }, CancellationToken.None);
        }

        Assert.Equal(6, keys.Wraps);

        int before = keys.Wraps;
        for (int i = 0; i < 120; i++)
        {
            await shorter.ProtectAsync(Scope, new byte[] { 1 }, new byte[] { 2 }, CancellationToken.None);
        }

        Assert.Equal(3, keys.Wraps - before); // 50 + 50 + 20

        var normal = new EnvelopePayloadProtector(keys, TimeProvider.System);
        before = keys.Wraps;
        for (int i = 0; i < 120; i++)
        {
            await normal.ProtectAsync(Scope, new byte[] { 1 }, new byte[] { 2 }, CancellationToken.None);
        }

        Assert.Equal(1, keys.Wraps - before);
    }
}
