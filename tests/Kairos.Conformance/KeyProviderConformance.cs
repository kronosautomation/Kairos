using System.Security.Cryptography;

namespace Kairos.Conformance;

/// <summary>
/// What every <see cref="IKeyProvider"/> must do. Subclass, supply a provider, and run. Failure semantics matter as much as the
/// happy path: anything wrong with the ciphertext, key id or context is refused with <see cref="KeyUnwrapException"/> and no detail.
/// </summary>
public abstract class KeyProviderConformance
{
    private static readonly byte[] ContextA = "tenant-a/ns-1"u8.ToArray();
    private static readonly byte[] ContextB = "tenant-b/ns-1"u8.ToArray();

    protected abstract IKeyProvider CreateProvider();

    /// <summary>A provider holding the old keys plus a newer current key, or null when the adapter has no rotation to test.</summary>
    protected virtual IKeyProvider? CreateRotated(IKeyProvider original) => null;

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(1)]
    public async Task Wrapped_keys_unwrap_to_the_original(int length)
    {
        IKeyProvider provider = CreateProvider();
        byte[] dataKey = RandomNumberGenerator.GetBytes(length);

        WrappedKey wrapped = await provider.WrapAsync(dataKey, ContextA, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(wrapped.KeyId));
        Assert.Equal(dataKey, await provider.UnwrapAsync(wrapped, ContextA, CancellationToken.None));
    }

    [Fact]
    public async Task The_plaintext_key_is_not_visible_in_the_wrapped_form_and_wrapping_is_randomised()
    {
        IKeyProvider provider = CreateProvider();
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);

        WrappedKey first = await provider.WrapAsync(dataKey, ContextA, CancellationToken.None);
        WrappedKey second = await provider.WrapAsync(dataKey, ContextA, CancellationToken.None);

        Assert.DoesNotContain(Convert.ToHexString(dataKey), Convert.ToHexString(first.Ciphertext), StringComparison.Ordinal);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext); // same key twice must not give the same bytes
    }

    [Fact]
    public async Task A_key_wrapped_for_one_context_does_not_unwrap_for_another()
    {
        IKeyProvider provider = CreateProvider();
        WrappedKey wrapped = await provider.WrapAsync(RandomNumberGenerator.GetBytes(32), ContextA, CancellationToken.None);

        await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(wrapped, ContextB, CancellationToken.None));
        await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(wrapped, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Altering_any_byte_of_the_wrapped_key_is_detected()
    {
        IKeyProvider provider = CreateProvider();
        WrappedKey wrapped = await provider.WrapAsync(RandomNumberGenerator.GetBytes(32), ContextA, CancellationToken.None);

        for (int i = 0; i < wrapped.Ciphertext.Length; i++)
        {
            byte[] altered = [.. wrapped.Ciphertext];
            altered[i] ^= 0x01;
            await Assert.ThrowsAsync<KeyUnwrapException>(async () =>
                await provider.UnwrapAsync(wrapped with { Ciphertext = altered }, ContextA, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Unknown_key_ids_and_malformed_ciphertext_are_refused_without_other_exceptions()
    {
        IKeyProvider provider = CreateProvider();
        WrappedKey wrapped = await provider.WrapAsync(RandomNumberGenerator.GetBytes(32), ContextA, CancellationToken.None);

        WrappedKey[] bad =
        [
            wrapped with { KeyId = "no-such-key" },
            wrapped with { KeyId = "" },
            wrapped with { Ciphertext = [] },
            wrapped with { Ciphertext = wrapped.Ciphertext[..^1] },
            wrapped with { Ciphertext = wrapped.Ciphertext[..5] },
            wrapped with { Ciphertext = [.. wrapped.Ciphertext, 0] },
            default,
        ];
        foreach (WrappedKey candidate in bad)
        {
            await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(candidate, ContextA, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Failures_reveal_no_key_material()
    {
        IKeyProvider provider = CreateProvider();
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        WrappedKey wrapped = await provider.WrapAsync(dataKey, ContextA, CancellationToken.None);

        KeyUnwrapException ex = await Assert.ThrowsAsync<KeyUnwrapException>(async () =>
            await provider.UnwrapAsync(wrapped, ContextB, CancellationToken.None));

        string everything = ex.ToString();
        Assert.DoesNotContain(Convert.ToHexString(dataKey), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(dataKey), everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_token_is_honoured()
    {
        IKeyProvider provider = CreateProvider();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.WrapAsync(RandomNumberGenerator.GetBytes(32), ContextA, cts.Token));
    }

    [Fact]
    public async Task Concurrent_use_is_safe()
    {
        IKeyProvider provider = CreateProvider();

        await Parallel.ForAsync(0, 200, async (i, ct) =>
        {
            byte[] dataKey = RandomNumberGenerator.GetBytes(32);
            byte[] context = BitConverter.GetBytes(i);
            WrappedKey wrapped = await provider.WrapAsync(dataKey, context, ct);
            Assert.Equal(dataKey, await provider.UnwrapAsync(wrapped, context, ct));
        });
    }

    [Fact]
    public async Task Keys_wrapped_before_a_rotation_still_unwrap_and_new_ones_use_the_new_key()
    {
        IKeyProvider original = CreateProvider();
        IKeyProvider? rotated = CreateRotated(original);
        if (rotated is null)
        {
            return; // this adapter has no rotation to exercise
        }

        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        WrappedKey before = await original.WrapAsync(dataKey, ContextA, CancellationToken.None);
        WrappedKey after = await rotated.WrapAsync(dataKey, ContextA, CancellationToken.None);

        Assert.NotEqual(before.KeyId, after.KeyId);
        Assert.Equal(dataKey, await rotated.UnwrapAsync(before, ContextA, CancellationToken.None));
        Assert.Equal(dataKey, await rotated.UnwrapAsync(after, ContextA, CancellationToken.None));
    }
}
