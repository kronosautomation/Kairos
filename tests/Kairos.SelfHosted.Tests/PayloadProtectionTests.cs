using System.Security.Cryptography;
using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Conformance;
using Kairos.Testing;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

public sealed class EnvelopePayloadProtectorConformanceTests : PayloadProtectorConformance
{
    protected override IPayloadProtector CreateProtector() => new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
}

public sealed class EnvelopePayloadProtectorTests
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));

    private sealed class CountingKeys(IKeyProvider inner) : IKeyProvider
    {
        public int Wraps { get; private set; }

        public int Unwraps { get; private set; }

        public bool Down { get; set; }

        public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            Wraps++;
            return Down ? throw new InvalidOperationException("key service down") : inner.WrapAsync(dataKey, context, ct);
        }

        public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken ct)
        {
            Unwraps++;
            return Down ? throw new InvalidOperationException("key service down") : inner.UnwrapAsync(wrapped, context, ct);
        }
    }

    [Fact]
    public async Task Data_keys_are_reused_so_the_key_provider_is_not_called_for_every_payload()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        var protector = new EnvelopePayloadProtector(keys);

        var sealedPayloads = new List<byte[]>();
        for (int i = 0; i < 500; i++)
        {
            sealedPayloads.Add(await protector.ProtectAsync(A, BitConverter.GetBytes(i), BitConverter.GetBytes(i), CancellationToken.None));
        }

        Assert.Equal(1, keys.Wraps);
        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(BitConverter.GetBytes(i), await protector.UnprotectAsync(A, sealedPayloads[i], BitConverter.GetBytes(i), CancellationToken.None));
        }

        Assert.Equal(0, keys.Unwraps); // the key just wrapped is already known; no provider round trip to read it back
    }

    [Fact]
    public async Task A_new_process_unwraps_once_per_data_key_not_once_per_payload()
    {
        var keys = new CountingKeys(LocalKeyProvider.CreateEphemeral());
        var writer = new EnvelopePayloadProtector(keys);
        var sealedPayloads = new List<byte[]>();
        for (int i = 0; i < 100; i++)
        {
            sealedPayloads.Add(await writer.ProtectAsync(A, BitConverter.GetBytes(i), BitConverter.GetBytes(i), CancellationToken.None));
        }

        var reader = new EnvelopePayloadProtector(keys); // restart: no cached keys
        for (int i = 0; i < 100; i++)
        {
            await reader.UnprotectAsync(A, sealedPayloads[i], BitConverter.GetBytes(i), CancellationToken.None);
        }

        Assert.Equal(1, keys.Unwraps);
    }

    [Fact]
    public async Task Payloads_sealed_before_a_key_rotation_still_open_and_new_ones_use_the_new_key()
    {
        byte[] k1 = RandomNumberGenerator.GetBytes(32);
        var oldRing = new LocalKeyProvider(new Dictionary<string, byte[]> { ["k1"] = k1 }, "k1");
        byte[] before = await new EnvelopePayloadProtector(oldRing).ProtectAsync(A, "old"u8.ToArray(), "c"u8.ToArray(), CancellationToken.None);

        var newRing = new LocalKeyProvider(new Dictionary<string, byte[]> { ["k1"] = k1, ["k2"] = RandomNumberGenerator.GetBytes(32) }, "k2");
        var rotated = new EnvelopePayloadProtector(newRing);
        byte[] after = await rotated.ProtectAsync(A, "new"u8.ToArray(), "c"u8.ToArray(), CancellationToken.None);

        Assert.Equal("old", Encoding.UTF8.GetString(await rotated.UnprotectAsync(A, before, "c"u8.ToArray(), CancellationToken.None)));
        Assert.Equal("new", Encoding.UTF8.GetString(await rotated.UnprotectAsync(A, after, "c"u8.ToArray(), CancellationToken.None)));
        Assert.Contains("k2", Encoding.UTF8.GetString(after), StringComparison.Ordinal); // the key id travels with the payload
    }

    [Fact]
    public async Task A_different_key_ring_cannot_open_the_payload_and_says_nothing_about_why()
    {
        byte[] sealedBytes = await new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral("k1"))
            .ProtectAsync(A, "secret"u8.ToArray(), "c"u8.ToArray(), CancellationToken.None);

        PayloadProtectionException ex = await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral("k1")).UnprotectAsync(A, sealedBytes, "c"u8.ToArray(), CancellationToken.None));

        Assert.DoesNotContain("secret", ex.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>I7 on the in-memory store: with encryption on, the server-side state holds no plaintext and every binding is enforced.</summary>
public sealed class EncryptedWorkflowTests
{
    private const string Canary = "CANARY-4111-1111-1111-1111";
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static string Everything(IReadOnlyList<HistoryEvent> history) =>
        string.Join("\n", history.Select(e => e.ToString() + Convert.ToHexString(HistoryEventCodec.Encode(e)) + Encoding.Latin1.GetString(HistoryEventCodec.Encode(e))));

    private static WorkflowRegistry Registry() => new WorkflowRegistry().Register("wf", "default", async (ctx, input) =>
    {
        byte[] charged = await ctx.ExecuteActivityAsync("charge", input);
        byte[] approval = await ctx.WaitForSignalAsync("approve");
        try
        {
            await ctx.ExecuteActivityAsync("explode", input, RetryPolicy.NoRetry);
        }
        catch (ActivityFailureException)
        {
        }

        return [.. charged, .. approval];
    });

    private static ActivityRegistry Activities() => new ActivityRegistry()
        .Register("charge", (_, i, _) => Task.FromResult(Encoding.UTF8.GetBytes("charged:" + Encoding.UTF8.GetString(i))))
        .Register("explode", (_, i, _) => throw new InvalidOperationException("card " + Encoding.UTF8.GetString(i) + " declined"));

    [Fact]
    public async Task A_full_run_completes_correctly_while_the_store_only_ever_holds_ciphertext()
    {
        var store = new InMemoryKairosStore();
        var time = new VirtualTimeProvider();
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var client = new KairosClient(store, time, payloadProtector: protector);
        var worker = new WorkerRuntime(store, Scope, Registry(), Activities(), time, new WorkerOptions { AutoHeartbeat = false }, protector);

        StartRunResult start = await client.StartAsync(Scope, "pay-1", "wf", Encoding.UTF8.GetBytes(Canary));
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await Driver.DriveAsync(worker);
        await client.SignalAsync(run, "approve", Encoding.UTF8.GetBytes("+" + Canary));
        await Driver.DriveAsync(worker);

        // What the server holds: no trace of the canary anywhere, and the chain still verifies.
        IReadOnlyList<HistoryEvent> stored = await client.GetHistoryAsync(run);
        HashChain.Verify(stored);
        Assert.DoesNotContain(Canary, Everything(stored), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(Canary)), Everything(stored), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RunState.Completed, await client.GetStateAsync(run));

        // What the key holder sees: the real values.
        IReadOnlyList<HistoryEvent> opened = await client.GetOpenedHistoryAsync(run);
        Assert.Equal("charged:" + Canary + "+" + Canary, Encoding.UTF8.GetString(((WorkflowCompleted)opened[^1]).Result));
        Assert.Contains(opened.OfType<ActivityFailed>(), f => f.Message.Contains(Canary, StringComparison.Ordinal)); // error text is protected too
        Assert.DoesNotContain(stored.OfType<ActivityFailed>(), f => f.Message.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_encryption_the_same_scan_does_find_the_canary()
    {
        // Control: proves the scan above can fail.
        var store = new InMemoryKairosStore();
        var time = new VirtualTimeProvider();
        var client = new KairosClient(store, time);
        var worker = new WorkerRuntime(store, Scope, Registry(), Activities(), time, new WorkerOptions { AutoHeartbeat = false });
        StartRunResult start = await client.StartAsync(Scope, "pay-2", "wf", Encoding.UTF8.GetBytes(Canary));
        await Driver.DriveAsync(worker);

        Assert.Contains(Canary, Everything(await client.GetHistoryAsync(new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ciphertext_moved_to_another_field_run_or_tenant_does_not_open()
    {
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var crypto = new PayloadCrypto(protector);
        Guid run1 = Guid.NewGuid(), run2 = Guid.NewGuid();
        var other = new TenantScope(Guid.NewGuid(), Guid.NewGuid());

        ActivityCompleted one = (ActivityCompleted)await crypto.SealEventAsync(new ActivityCompleted(Scope.TenantId, Scope.NamespaceId, run1, 2, DateTimeOffset.UnixEpoch, 1, "result-1"u8.ToArray()), CancellationToken.None);

        // Same run, a different activity's result slot.
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await crypto.OpenHistoryAsync([one with { ActivityId = 2 }], CancellationToken.None));
        // Another run.
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await crypto.OpenHistoryAsync([one with { RunId = run2 }], CancellationToken.None));
        // Another tenant.
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await crypto.OpenHistoryAsync([one with { TenantId = other.TenantId, NamespaceId = other.NamespaceId }], CancellationToken.None));
        // As a different kind of field (the result of the whole workflow).
        await Assert.ThrowsAsync<PayloadProtectionException>(async () =>
            await crypto.OpenHistoryAsync([new WorkflowCompleted(Scope.TenantId, Scope.NamespaceId, run1, 2, DateTimeOffset.UnixEpoch, one.Result)], CancellationToken.None));
        // And the original still opens.
        Assert.Equal("result-1", Encoding.UTF8.GetString(((ActivityCompleted)(await crypto.OpenHistoryAsync([one], CancellationToken.None))[0]).Result));
    }

    [Fact]
    public async Task A_worker_without_the_right_key_does_not_advance_the_run_and_loses_nothing()
    {
        var store = new InMemoryKairosStore();
        var time = new VirtualTimeProvider();
        var rightKeys = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var wrongKeys = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var client = new KairosClient(store, time, payloadProtector: rightKeys);
        StartRunResult start = await client.StartAsync(Scope, "keys", "wf", Encoding.UTF8.GetBytes(Canary));
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);

        var faults = new List<TaskFault>();
        var wrongWorker = new WorkerRuntime(store, Scope, Registry(), Activities(), time, new WorkerOptions { AutoHeartbeat = false }, wrongKeys) { OnFault = faults.Add };
        await wrongWorker.ProcessWorkflowTaskAsync(CancellationToken.None);

        Assert.IsType<PayloadProtectionException>(Assert.Single(faults).Error);
        Assert.Single(await client.GetHistoryAsync(run)); // nothing was appended on top of data it could not read

        // The right worker takes over after the retry delay and the run proceeds.
        time.Advance(TimeSpan.FromSeconds(31));
        var rightWorker = new WorkerRuntime(store, Scope, Registry(), Activities(), time, new WorkerOptions { AutoHeartbeat = false }, rightKeys);
        await Driver.DriveAsync(rightWorker);
        Assert.Contains(await client.GetHistoryAsync(run), e => e is ActivityScheduled);
    }
}

/// <summary>Fuzzing of the parsers that read untrusted input in this package (layer 7).</summary>
public sealed class ParserFuzzTests
{
    [Fact]
    [Trait("Category", "Fuzz")]
    public void Mutated_webhook_bodies_parse_or_fail_with_format_exception_only()
    {
        var run = Guid.NewGuid();
        byte[] valid = WebhookEventSink.Serialize(
        [
            new RunEventNotice($"{run:N}:1", new RunKey(Guid.NewGuid(), Guid.NewGuid(), run), HistoryEventType.WorkflowStarted, 1, DateTimeOffset.UnixEpoch),
            new RunEventNotice($"{run:N}:2", new RunKey(Guid.NewGuid(), Guid.NewGuid(), run), HistoryEventType.ActivityCompleted, 2, DateTimeOffset.UnixEpoch.AddDays(3)),
        ]);
        Assert.Equal(2, WebhookEventSink.Parse(valid).Count);

        var rng = new SimRandom(99);
        int parsed = 0, refused = 0;
        for (int i = 0; i < 20_000; i++)
        {
            byte[] b = [.. valid];
            switch (rng.Next(4))
            {
                case 0:
                    b[rng.Next(b.Length)] = (byte)rng.Next(256);
                    break;
                case 1:
                    b = b[..rng.Next(b.Length)];
                    break;
                case 2:
                    b = [.. b[..rng.Next(b.Length)], .. rng.Bytes(rng.Between(1, 10)), .. b[rng.Next(b.Length)..]];
                    break;
                default:
                    b = rng.Bytes(rng.Between(0, 100));
                    break;
            }

            try
            {
                WebhookEventSink.Parse(b);
                parsed++;
            }
            catch (FormatException)
            {
                refused++;
            }
        }

        Assert.True(parsed > 10 && refused > 1000, $"parsed {parsed}, refused {refused}");
    }
}
