using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

/// <summary>"Every unwrap is logged by the party that performs it": the protector tells its observer about each call it makes to the key provider, and fails closed when the record cannot be made.</summary>
public sealed class KeyUseObserverTests
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b1"));
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("run:1/field:input");

    private sealed class Recorder : IKeyUseObserver
    {
        public List<KeyUseEvent> Events { get; } = [];

        public bool Throw { get; set; }

        public void KeyUsed(KeyUseEvent use)
        {
            if (Throw)
            {
                throw new InvalidOperationException("the log is down");
            }

            Events.Add(use);
        }
    }

    private sealed class Keys(IKeyProvider inner) : IKeyProvider
    {
        public bool Refuse { get; set; }

        public int Unwraps { get; private set; }

        public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken ct) =>
            Refuse ? throw new InvalidOperationException("denied") : inner.WrapAsync(dataKey, context, ct);

        public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            Unwraps++;
            return Refuse ? throw new KeyUnwrapException() : inner.UnwrapAsync(wrapped, context, ct);
        }
    }

    private static (EnvelopePayloadProtector Protector, Recorder Log, Keys Keys) New(Recorder? log = null)
    {
        log ??= new Recorder();
        var keys = new Keys(LocalKeyProvider.CreateEphemeral());
        return (new EnvelopePayloadProtector(keys, options: new EnvelopeOptions { KeyUseObserver = log }), log, keys);
    }

    [Fact]
    public async Task One_wrap_is_recorded_for_the_data_key_not_one_per_payload()
    {
        (EnvelopePayloadProtector protector, Recorder log, _) = New();

        for (int i = 0; i < 5; i++)
        {
            _ = await protector.ProtectAsync(A, new byte[] { 1 }, Context, default);
        }

        KeyUseEvent only = Assert.Single(log.Events);
        Assert.Equal((KeyUseKind.Wrap, A, true), (only.Kind, only.Scope, only.Succeeded));
        Assert.NotEmpty(only.KeyId);
    }

    [Fact]
    public async Task An_unwrap_is_recorded_with_the_scope_and_the_key_when_another_process_opens_the_payload()
    {
        (EnvelopePayloadProtector sealer, _, Keys keys) = New();
        byte[] sealedPayload = await sealer.ProtectAsync(A, new byte[] { 1, 2, 3 }, Context, default);
        var log = new Recorder();
        using var opener = new EnvelopePayloadProtector(keys, options: new EnvelopeOptions { KeyUseObserver = log });

        _ = await opener.UnprotectAsync(A, sealedPayload, Context, default);
        _ = await opener.UnprotectAsync(A, sealedPayload, Context, default); // the unwrapped key is cached: no second call to the provider, so no second record

        KeyUseEvent only = Assert.Single(log.Events);
        Assert.Equal((KeyUseKind.Unwrap, A, true), (only.Kind, only.Scope, only.Succeeded));
        Assert.NotEmpty(only.KeyId);
        Assert.Equal(1, keys.Unwraps);
    }

    [Fact]
    public async Task A_refused_unwrap_is_recorded_as_a_failure()
    {
        (EnvelopePayloadProtector sealer, _, Keys keys) = New();
        byte[] sealedPayload = await sealer.ProtectAsync(B, new byte[] { 9 }, Context, default);
        var log = new Recorder();
        keys.Refuse = true;
        using var opener = new EnvelopePayloadProtector(keys, options: new EnvelopeOptions { KeyUseObserver = log });

        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await opener.UnprotectAsync(B, sealedPayload, Context, default));

        KeyUseEvent only = Assert.Single(log.Events);
        Assert.Equal((KeyUseKind.Unwrap, B, false), (only.Kind, only.Scope, only.Succeeded));
    }

    [Fact]
    public async Task A_refused_wrap_is_recorded_and_the_provider_error_is_the_one_reported()
    {
        (EnvelopePayloadProtector protector, Recorder log, Keys keys) = New();
        keys.Refuse = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await protector.ProtectAsync(A, new byte[] { 1 }, Context, default));

        KeyUseEvent only = Assert.Single(log.Events);
        Assert.Equal((KeyUseKind.Wrap, false), (only.Kind, only.Succeeded));
    }

    [Fact]
    public async Task When_the_record_cannot_be_made_the_key_is_not_used_and_nothing_is_cached()
    {
        (EnvelopePayloadProtector sealer, _, Keys keys) = New();
        byte[] sealedPayload = await sealer.ProtectAsync(A, new byte[] { 1 }, Context, default);
        var log = new Recorder { Throw = true };
        using var opener = new EnvelopePayloadProtector(keys, options: new EnvelopeOptions { KeyUseObserver = log });

        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await opener.UnprotectAsync(A, sealedPayload, Context, default));
        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await opener.ProtectAsync(B, new byte[] { 1 }, Context, default));

        log.Throw = false;
        Assert.Equal(new byte[] { 1 }, await opener.UnprotectAsync(A, sealedPayload, Context, default));
        Assert.Equal(2, keys.Unwraps); // the first, unrecorded unwrap left no cached key behind
        Assert.Contains(log.Events, e => e is { Kind: KeyUseKind.Unwrap, Succeeded: true });
    }

    [Fact]
    public void The_event_carries_no_key_bytes_and_no_payload()
    {
        string[] members = [.. typeof(KeyUseEvent).GetProperties().Select(p => p.Name)];

        Assert.Equal(["Kind", "Scope", "KeyId", "At", "Succeeded"], members.Where(m => m != "EqualityContract"));
    }
}
