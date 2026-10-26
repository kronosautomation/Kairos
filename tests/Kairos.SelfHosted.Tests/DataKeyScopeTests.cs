using Kairos.Cloud.SelfHosted;
using Kairos.Testing;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

/// <summary>
/// What one data key covers. The statement the documentation makes is tested here: an unwrapped data key opens every value sealed with it in that scope, until the data key is
/// rotated (an hour by default), and a customer who wants a smaller unit can have one.
/// </summary>
public sealed class DataKeyScopeTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly TenantScope OtherScope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private sealed class CountingKeys(IKeyProvider inner) : IKeyProvider
    {
        public int Wraps { get; private set; }

        public int Unwraps { get; private set; }

        public async ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            Wraps++;
            return await inner.WrapAsync(dataKey, context, ct);
        }

        public async ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            Unwraps++;
            return await inner.UnwrapAsync(wrapped, context, ct);
        }
    }

    private static byte[] Context(int n) => System.Text.Encoding.UTF8.GetBytes("field-" + n);

    private static async Task<byte[][]> SealAsync(EnvelopePayloadProtector protector, int count)
    {
        var sealedBytes = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            sealedBytes[i] = await protector.ProtectAsync(Scope, System.Text.Encoding.UTF8.GetBytes("value-" + i), Context(i), CancellationToken.None);
        }

        return sealedBytes;
    }

    [Fact]
    public async Task By_default_one_unwrap_opens_every_value_sealed_in_the_same_hour_in_that_scope()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        var time = new VirtualTimeProvider();
        byte[][] sealedBytes = await SealAsync(new EnvelopePayloadProtector(keys, time), 5);
        Assert.Equal(1, keys.Wraps); // one data key for all five
        time.Advance(TimeSpan.FromMinutes(30));

        // A reader asks for ONE value. The key that comes back opens all of them: it was the same key.
        var reader = new EnvelopePayloadProtector(keys, time);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("value-" + i, System.Text.Encoding.UTF8.GetString(await reader.UnprotectAsync(Scope, sealedBytes[i], Context(i), CancellationToken.None)));
        }

        Assert.Equal(1, keys.Unwraps); // asked once, opened five
    }

    [Fact]
    public async Task A_value_sealed_after_the_key_rotated_is_not_covered_by_the_old_key()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        var time = new VirtualTimeProvider();
        var writer = new EnvelopePayloadProtector(keys, time);
        byte[] before = (await SealAsync(writer, 1))[0];
        time.Advance(TimeSpan.FromMinutes(61));
        byte[] after = await writer.ProtectAsync(Scope, "later"u8.ToArray(), Context(0), CancellationToken.None);
        Assert.Equal(2, keys.Wraps); // an hour on, a new data key

        var reader = new EnvelopePayloadProtector(keys, time);
        await reader.UnprotectAsync(Scope, before, Context(0), CancellationToken.None);
        await reader.UnprotectAsync(Scope, after, Context(0), CancellationToken.None);

        Assert.Equal(2, keys.Unwraps); // the two values needed two different keys
    }

    [Fact]
    public async Task One_key_per_value_makes_every_unwrap_open_exactly_one_value()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        byte[][] sealedBytes = await SealAsync(new EnvelopePayloadProtector(keys, TimeProvider.System, EnvelopeOptions.OneKeyPerValue), 5);
        Assert.Equal(5, keys.Wraps); // the price: a wrap per value

        var reader = new EnvelopePayloadProtector(keys, TimeProvider.System);
        for (int i = 0; i < 5; i++)
        {
            await reader.UnprotectAsync(Scope, sealedBytes[i], Context(i), CancellationToken.None);
        }

        Assert.Equal(5, keys.Unwraps); // each value needed a key of its own
    }

    [Fact]
    public async Task A_use_limit_rotates_the_key_by_count_as_a_time_limit_does_by_the_clock()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        await SealAsync(new EnvelopePayloadProtector(keys, TimeProvider.System, EnvelopeOptions.ShortLived(TimeSpan.FromHours(1), maxUses: 3)), 10);

        Assert.Equal(4, keys.Wraps); // 3 + 3 + 3 + 1
    }

    [Fact]
    public async Task A_key_is_never_shared_across_scopes_however_it_is_configured()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        var protector = new EnvelopePayloadProtector(keys, TimeProvider.System);
        byte[] a = await protector.ProtectAsync(Scope, "a"u8.ToArray(), Context(0), CancellationToken.None);
        await protector.ProtectAsync(OtherScope, "b"u8.ToArray(), Context(0), CancellationToken.None);

        Assert.Equal(2, keys.Wraps);
        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await new EnvelopePayloadProtector(keys, TimeProvider.System).UnprotectAsync(OtherScope, a, Context(0), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(10, 0)]
    [InlineData(10, -5)]
    public void Options_that_would_make_no_sense_are_refused_when_the_protector_is_made(int maxUses, int lifetimeSeconds)
    {
        var options = new EnvelopeOptions { MaxUsesPerDataKey = maxUses, DataKeyLifetime = TimeSpan.FromSeconds(lifetimeSeconds) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral(), TimeProvider.System, options));
    }

    [Fact]
    public void A_cache_that_holds_nothing_or_a_key_that_never_lives_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral(), TimeProvider.System, new EnvelopeOptions { MaxCachedKeys = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral(), TimeProvider.System, new EnvelopeOptions { UnwrappedKeyTtl = TimeSpan.Zero }));
    }
}
