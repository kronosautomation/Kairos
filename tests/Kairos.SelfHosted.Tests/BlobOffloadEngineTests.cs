using System.Security.Cryptography;
using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

/// <summary>
/// Payloads over the threshold go to the blob store, sealed, by content hash, with a sealed reference in the history; the hash chain covers the reference; reads verify length and SHA-256; a missing or replaced blob
/// fails clearly. Whole engine in memory (store, client, workers, protector); the blob store is a recording one so each test can look inside it and break it.
/// </summary>
public sealed class BlobOffloadEngineTests
{
    private const int Threshold = 1024;
    private const string Canary = "CANARY-PAN-4111-1111-1111-1111";
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    /// <summary>A blob store that remembers everything asked of it and can be damaged.</summary>
    private sealed class Blobs : IBlobOffload
    {
        public Dictionary<(TenantScope Scope, string Hash), byte[]> Stored { get; } = [];

        public List<TenantScope> ScopesAsked { get; } = [];

        public int Puts { get; private set; }

        public bool VerifyOnRead { get; set; } = true;

        public ValueTask<BlobRef> PutAsync(TenantScope scope, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Puts++;
            ScopesAsked.Add(scope);
            var reference = new BlobRef(Convert.ToHexStringLower(SHA256.HashData(content.Span)), content.Length);
            Stored[(scope, reference.Sha256Hex)] = content.ToArray();
            return ValueTask.FromResult(reference);
        }

        public ValueTask<byte[]?> GetAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken)
        {
            ScopesAsked.Add(scope);
            if (!Stored.TryGetValue((scope, reference.Sha256Hex), out byte[]? bytes))
            {
                return ValueTask.FromResult<byte[]?>(null);
            }

            if (VerifyOnRead && (bytes.Length != reference.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Sha256Hex))
            {
                throw new InvalidDataException("the stored bytes no longer match the reference");
            }

            return ValueTask.FromResult<byte[]?>(bytes);
        }

        public ValueTask<bool> DeleteAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken) => ValueTask.FromResult(Stored.Remove((scope, reference.Sha256Hex)));
    }

    private sealed class World
    {
        public World(Blobs? blobs = null, bool offload = true, int threshold = Threshold, IPayloadProtector? protector = null)
        {
            Blobs = blobs ?? new Blobs();
            Protector = protector ?? new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
            Offload = offload ? new BlobOffloadOptions(Blobs, threshold) : null;
            Client = new KairosClient(Store, Time, payloadProtector: Protector, blobOffload: Offload);
            Workflows = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, input) =>
            {
                byte[] first = await ctx.ExecuteActivityAsync("echo", input);
                byte[] signal = await ctx.WaitForSignalAsync("more");
                return [.. first, .. signal];
            });
            Activities = new ActivityRegistry().Register("echo", (_, i, _) => Task.FromResult(i));
            Worker = NewWorker(Offload);
        }

        public Blobs Blobs { get; }

        public IPayloadProtector Protector { get; }

        public BlobOffloadOptions? Offload { get; }

        public InMemoryKairosStore Store { get; } = new();

        public VirtualTimeProvider Time { get; } = new();

        public KairosClient Client { get; }

        public WorkflowRegistry Workflows { get; }

        public ActivityRegistry Activities { get; }

        public WorkerRuntime Worker { get; }

        public List<TaskFault> Faults { get; } = [];

        public WorkerRuntime NewWorker(BlobOffloadOptions? offload)
        {
            var worker = new WorkerRuntime(Store, A, Workflows, Activities, Time, new WorkerOptions(), Protector, offload);
            worker.OnFault = Faults.Add;
            return worker;
        }

        public async Task DriveAsync(WorkerRuntime? worker = null, int rounds = 40)
        {
            worker ??= Worker;
            for (int i = 0; i < rounds; i++)
            {
                bool any = await worker.ProcessWorkflowTaskAsync(default);
                any |= await worker.ProcessActivityTaskAsync(default);
                Time.Advance(TimeSpan.FromSeconds(31)); // past a failed-task retry delay
                if (!any && i > 15)
                {
                    break;
                }
            }
        }

        public async Task<RunKey> StartAsync(byte[] input, TenantScope? scope = null)
        {
            TenantScope s = scope ?? A;
            StartRunResult r = await Client.StartAsync(s, "w-" + Guid.NewGuid().ToString("N")[..6], "wf", input);
            return new RunKey(s.TenantId, s.NamespaceId, r.RunId);
        }

        public async Task<IReadOnlyList<HistoryEvent>> HistoryAsync(RunKey run) => await Store.GetHistoryAsync(run, default);
    }

    private static byte[] Big(int length, string seed = Canary)
    {
        byte[] bytes = new byte[length];
        byte[] s = Encoding.UTF8.GetBytes(seed);
        for (int i = 0; i < length; i++)
        {
            bytes[i] = s[i % s.Length];
        }

        return bytes;
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>Every byte the server side holds for a run, as the pieces a reader could search: the payload fields of its events.</summary>
    private static IEnumerable<byte[]> StoredPayloads(IReadOnlyList<HistoryEvent> history)
    {
        foreach (HistoryEvent e in history)
        {
            switch (e)
            {
                case WorkflowStarted s: yield return s.Input; break;
                case ActivityScheduled s: yield return s.Input; break;
                case ActivityCompleted c: yield return c.Result; break;
                case SignalReceived s: yield return s.Payload; break;
                case WorkflowCompleted c: yield return c.Result; break;
            }
        }
    }

    // ---- what is offloaded ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_with_large_payloads_in_every_field_completes_with_the_right_result_and_the_blob_store_holds_only_ciphertext()
    {
        var w = new World();
        byte[] input = Big(5000);
        RunKey run = await w.StartAsync(input);
        await w.DriveAsync();
        byte[] more = Big(3000, "SIGNAL-" + Canary);
        Assert.True(await w.Client.SignalAsync(run, "more", more));
        await w.DriveAsync();

        IReadOnlyList<HistoryEvent> history = await w.Client.GetOpenedHistoryAsync(run);
        byte[] result = history.OfType<WorkflowCompleted>().Single().Result;
        Assert.Equal([.. input, .. more], result);

        Assert.True(w.Blobs.Stored.Count >= 5); // start, activity input, activity result, signal, result
        byte[] plaintextMarker = Encoding.UTF8.GetBytes("CANARY-PAN");
        Assert.All(w.Blobs.Stored.Values, blob => Assert.False(Contains(blob, plaintextMarker), "a blob holds readable plaintext"));
        Assert.All(w.Blobs.Stored.Keys, k => Assert.Equal(A, k.Scope));
    }

    [Fact]
    public async Task The_history_keeps_a_small_sealed_reference_and_neither_the_payload_nor_the_blobs_hash_is_readable_in_it()
    {
        var w = new World();
        RunKey run = await w.StartAsync(Big(200_000));
        await w.DriveAsync();

        IReadOnlyList<HistoryEvent> stored = await w.HistoryAsync(run);
        foreach (byte[] payload in StoredPayloads(stored).Where(p => p.Length > 0))
        {
            Assert.True(payload.Length < 512, $"a stored field of {payload.Length} bytes is not a reference");
            Assert.All(w.Blobs.Stored.Keys, k => Assert.False(Contains(payload, Convert.FromHexString(k.Hash)), "the blob's hash is readable in the history"));
        }

        Assert.NotEmpty(w.Blobs.Stored);
    }

    [Fact]
    public async Task The_threshold_is_exact_a_payload_of_that_length_stays_in_the_history_and_one_byte_more_goes_out()
    {
        var w = new World(threshold: 2000);
        _ = await w.StartAsync(Big(2000));
        Assert.Equal(0, w.Blobs.Puts);

        _ = await w.StartAsync(Big(2001));
        Assert.Equal(1, w.Blobs.Puts);
    }

    [Fact]
    public async Task Without_offload_nothing_changes_for_large_payloads_and_with_it_small_ones_never_touch_the_blob_store()
    {
        var plain = new World(offload: false);
        RunKey run = await plain.StartAsync(Big(50_000));
        await plain.DriveAsync();
        Assert.Equal(0, plain.Blobs.Puts);
        Assert.Contains(StoredPayloads(await plain.HistoryAsync(run)), p => p.Length > 50_000); // sealed, in the history, as before

        var small = new World();
        RunKey r = await small.StartAsync(Big(100));
        await small.DriveAsync();
        Assert.Equal(0, small.Blobs.Puts);
        Assert.Empty(small.Blobs.ScopesAsked);
        Assert.Equal(Big(100), (await small.HistoryAsync(r)).OfType<ActivityCompleted>().Count() == 1 ? Big(100) : []);
    }

    [Fact]
    public async Task Only_the_runs_own_scope_is_ever_asked_of_the_blob_store()
    {
        var w = new World();
        _ = await w.StartAsync(Big(4000));
        var otherWorker = new WorkerRuntime(w.Store, B, w.Workflows, w.Activities, w.Time, new WorkerOptions(), w.Protector, w.Offload);
        _ = await w.StartAsync(Big(4000), B);
        await w.DriveAsync();
        await w.DriveAsync(otherWorker);

        Assert.Equal([A, B], w.Blobs.ScopesAsked.Distinct().OrderBy(s => s.TenantId));
        Assert.All(w.Blobs.Stored.Keys, k => Assert.Contains(k.Scope, new[] { A, B }));
        // A's blobs are not B's: the same hash asked in the other scope is absent.
        (TenantScope scope, string hash) = w.Blobs.Stored.Keys.First(k => k.Scope == A);
        Assert.Null(await w.Blobs.GetAsync(B, new BlobRef(hash, w.Blobs.Stored[(scope, hash)].Length), default));
    }

    [Fact]
    public async Task Cross_tenant_a_reference_and_a_blob_of_one_scope_open_nothing_in_another()
    {
        var w = new World();
        var crypto = new PayloadCrypto(w.Protector, w.Offload);
        Guid run = Guid.NewGuid();
        byte[] secret = Big(4000, "TENANT-A-SECRET");
        byte[] sealedByA = await crypto.SealAsync(A, run, "start", 0, secret, default);
        BlobRef blobOfA = w.Blobs.Stored.Keys.Where(k => k.Scope == A).Select(k => new BlobRef(k.Hash, w.Blobs.Stored[k].Length)).Single();

        // Tenant B's reader is handed A's sealed reference (a hostile server can copy bytes anywhere): B's keys do not open it.
        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await crypto.OpenAsync(B, run, "start", 0, sealedByA, default));

        // B seals a reference of its own to A's blob hash: the blob store is asked in B's scope and has nothing there.
        byte[] forged = await w.Protector.ProtectAsync(B, ReferenceFor(blobOfA), PayloadCrypto.Context(run, "start", 0), default);
        await Assert.ThrowsAsync<BlobMissingException>(async () => await crypto.OpenAsync(B, run, "start", 0, forged, default));

        // And the ciphertext, copied by hand into B's scope, is sealed under A's keys: B cannot open it either.
        _ = await w.Blobs.PutAsync(B, w.Blobs.Stored[(A, blobOfA.Sha256Hex)], default);
        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await crypto.OpenAsync(B, run, "start", 0, forged, default));
        Assert.Equal(secret, await crypto.OpenAsync(A, run, "start", 0, sealedByA, default)); // control: A still reads its own
    }

    // ---- reads verify, and breakage is loud ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_blob_that_is_gone_fails_the_task_with_a_named_error_and_the_run_does_not_move_on_or_complete()
    {
        var w = new World();
        RunKey run = await w.StartAsync(Big(4000));
        await w.DriveAsync();
        int before = (await w.HistoryAsync(run)).Count;
        w.Blobs.Stored.Clear(); // the blob store lost everything
        Assert.True(await w.Client.SignalAsync(run, "more", [1]));
        w.Faults.Clear();

        await w.DriveAsync();

        Assert.NotEmpty(w.Faults);
        Assert.All(w.Faults, f => Assert.IsType<BlobMissingException>(f.Error));
        Assert.DoesNotContain(await w.HistoryAsync(run), e => e is WorkflowCompleted);
        Assert.Equal(before + 1, (await w.HistoryAsync(run)).Count); // only the signal; nothing was decided on a payload nobody could read
        Assert.True(w.Faults.Count >= 3, "the task is tried again and fails loudly each time (the log and the fault hook), as for any payload that cannot be opened");
        Assert.Contains(await w.HistoryAsync(run), e => e is SignalReceived); // the signal is recorded; nothing was decided from the payload nobody could read
    }

    [Fact]
    public async Task A_blob_that_was_replaced_is_detected_whatever_the_blob_store_says()
    {
        foreach (bool storeVerifies in new[] { true, false })
        {
            var blobs = new Blobs { VerifyOnRead = storeVerifies };
            var w = new World(blobs);
            RunKey run = await w.StartAsync(Big(4000));
            await w.DriveAsync();
            Assert.True(await w.Client.SignalAsync(run, "more", [1]));
            (TenantScope, string) victim = blobs.Stored.Keys.First();
            byte[] other = (byte[])blobs.Stored[victim].Clone();
            other[^1] ^= 1; // one flipped bit, same length
            blobs.Stored[victim] = other;
            w.Faults.Clear();

            await w.DriveAsync();

            Assert.NotEmpty(w.Faults);
            Assert.All(w.Faults, f => Assert.IsType<BlobMismatchException>(f.Error));
            Assert.DoesNotContain(await w.HistoryAsync(run), e => e is WorkflowCompleted);
        }
    }

    [Fact]
    public async Task A_blob_of_another_length_is_detected()
    {
        var blobs = new Blobs { VerifyOnRead = false };
        var w = new World(blobs);
        RunKey run = await w.StartAsync(Big(4000));
        await w.DriveAsync();
        Assert.True(await w.Client.SignalAsync(run, "more", [1]));
        (TenantScope, string) victim = blobs.Stored.Keys.First();
        blobs.Stored[victim] = [.. blobs.Stored[victim], 0];
        w.Faults.Clear();

        await w.DriveAsync();

        Assert.All(w.Faults, f => Assert.IsType<BlobMismatchException>(f.Error));
        Assert.NotEmpty(w.Faults);
    }

    [Fact]
    public async Task An_activity_whose_input_blob_is_missing_fails_for_good_with_the_reason_and_the_run_then_stops_loudly_at_replay()
    {
        var w = new World();
        RunKey run = await w.StartAsync(Big(4000));
        // Run only the workflow task: the activity is scheduled with a blob input. Then the blob goes missing before an activity worker polls.
        Assert.True(await w.Worker.ProcessWorkflowTaskAsync(default));
        w.Blobs.Stored.Clear();
        w.Faults.Clear();

        await w.DriveAsync();

        IReadOnlyList<HistoryEvent> stored = await w.HistoryAsync(run);
        ActivityFailed failed = stored.OfType<ActivityFailed>().Single();
        Assert.False(failed.WillRetry); // trying again cannot help
        var crypto = new PayloadCrypto(w.Protector, w.Offload);
        ActivityFailed opened = (ActivityFailed)(await crypto.OpenHistoryAsync([failed], default)).Single();
        Assert.Equal(nameof(BlobMissingException), opened.ErrorType);
        Assert.Contains("missing", opened.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, opened.Message, StringComparison.Ordinal);
        // Replaying the run needs every payload in its history, so the run stops, loudly, on the same missing blob; it is not completed on a guess.
        Assert.All(w.Faults, f => Assert.IsType<BlobMissingException>(f.Error));
        Assert.NotEmpty(w.Faults);
        Assert.DoesNotContain(stored, e => e is WorkflowCompleted or WorkflowFailed);
    }

    [Fact]
    public async Task A_reader_without_the_blob_store_is_told_so_and_not_given_the_reference_as_a_payload()
    {
        var w = new World();
        RunKey run = await w.StartAsync(Big(4000));
        await w.DriveAsync();
        var noOffload = new KairosClient(w.Store, w.Time, payloadProtector: w.Protector);

        PayloadBlobException e = await Assert.ThrowsAsync<PayloadBlobException>(async () => await noOffload.GetOpenedHistoryAsync(run));

        Assert.Contains("not configured", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blob_cannot_be_moved_to_another_field_or_run_because_it_is_sealed_for_one()
    {
        var w = new World();
        RunKey run = await w.StartAsync(Big(4000, "FIELD-ONE"));
        await w.DriveAsync();
        // An attacker with the blob store (not the key) copies the start blob over the activity result blob: same length is not needed, the reference's hash is checked first.
        var hashes = w.Blobs.Stored.Keys.Select(k => k.Hash).ToArray();
        Assert.True(hashes.Length >= 2);
        var crypto = new PayloadCrypto(w.Protector, w.Offload);
        byte[] start = Big(4000, "FIELD-ONE");
        byte[] sealedStart = await crypto.SealAsync(A, run.RunId, "start", 0, start, default);
        // Opened under another field's context, the reference does not open at all.
        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await crypto.OpenAsync(A, run.RunId, "act-out", 1, sealedStart, default));
        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await crypto.OpenAsync(A, Guid.NewGuid(), "start", 0, sealedStart, default));
        Assert.Equal(start, await crypto.OpenAsync(A, run.RunId, "start", 0, sealedStart, default));
    }

    [Fact]
    public async Task A_valid_blob_sealed_for_one_field_does_not_open_when_a_reference_to_it_is_sealed_for_another()
    {
        var w = new World();
        var crypto = new PayloadCrypto(w.Protector, w.Offload);
        Guid run = Guid.NewGuid();
        byte[] payload = Big(4000);
        // The blob is the start payload's ciphertext. A forged field that refers to it is sealed for the activity-result field.
        _ = await crypto.SealAsync(A, run, "start", 0, payload, default);
        BlobRef startBlob = w.Blobs.Stored.Keys.Select(k => new BlobRef(k.Hash, w.Blobs.Stored[k].Length)).Single();
        byte[] reference = ReferenceFor(startBlob);
        byte[] forged = await w.Protector.ProtectAsync(A, reference, PayloadCrypto.Context(run, "act-out", 1), default);

        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await crypto.OpenAsync(A, run, "act-out", 1, forged, default));
    }

    private static byte[] ReferenceFor(BlobRef reference) => BlobReferenceBytes(reference);

    private static byte[] BlobReferenceBytes(BlobRef reference)
    {
        // The reference layout is internal to the engine; build it the way it is documented: magic, version, hash, length. The magic is read back from a real one.
        return (byte[])typeof(PayloadCrypto).Assembly.GetType("Kairos.BlobReferences")!
            .GetMethod("Encode")!.Invoke(null, [reference])!;
    }

    // ---- the chain covers the reference, and payloads that look like references -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_hash_chain_covers_the_reference_so_a_changed_reference_is_detected_before_anything_is_opened()
    {
        var w = new World();
        RunKey run = await w.StartAsync(Big(4000));
        await w.DriveAsync();
        List<HistoryEvent> history = [.. await w.HistoryAsync(run)];
        HashChain.Verify(history); // as stored: intact

        WorkflowStarted started = (WorkflowStarted)history[0];
        byte[] changed = (byte[])started.Input.Clone();
        changed[^1] ^= 1;
        history[0] = started with { Input = changed };

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(history));
    }

    [Fact]
    public async Task A_payload_that_begins_like_a_reference_is_a_payload_and_never_a_read_of_somebody_elses_blob()
    {
        var w = new World();
        var crypto = new PayloadCrypto(w.Protector, w.Offload);
        Guid run = Guid.NewGuid();
        // A hostile client chooses a small input that is byte for byte a reference to another tenant's (or its own) blob.
        byte[] victim = Big(4000, "VICTIM-SECRET");
        _ = await crypto.SealAsync(B, Guid.NewGuid(), "start", 0, victim, default);
        BlobRef target = w.Blobs.Stored.Keys.Where(k => k.Scope == B).Select(k => new BlobRef(k.Hash, w.Blobs.Stored[k].Length)).Single();
        byte[] lookalike = ReferenceFor(target);
        Assert.True(lookalike.Length < Threshold); // small: stored inline

        byte[] sealedBytes = await crypto.SealAsync(A, run, "start", 0, lookalike, default);
        byte[] opened = await crypto.OpenAsync(A, run, "start", 0, sealedBytes, default);

        Assert.Equal(lookalike, opened); // the payload, as sent
        Assert.Single(w.Blobs.Stored); // and nothing was fetched or stored for it
    }

    [Fact]
    public async Task A_large_payload_that_begins_like_a_reference_round_trips_through_the_blob_store()
    {
        var w = new World();
        var crypto = new PayloadCrypto(w.Protector, w.Offload);
        Guid run = Guid.NewGuid();
        byte[] seed = ReferenceFor(new BlobRef(new string('a', 64), 7));
        byte[] payload = [.. seed, .. Big(5000)];

        byte[] sealedBytes = await crypto.SealAsync(A, run, "start", 0, payload, default);

        Assert.Equal(payload, await crypto.OpenAsync(A, run, "start", 0, sealedBytes, default));
        Assert.Equal(1, w.Blobs.Puts);
    }

    // ---- configuration ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Offload_without_a_protector_is_refused_because_a_blob_holds_ciphertext_only()
    {
        var store = new InMemoryKairosStore();
        var offload = new BlobOffloadOptions(new Blobs());

        Assert.Throws<ArgumentException>(() => new KairosClient(store, TimeProvider.System, blobOffload: offload));
        Assert.Throws<ArgumentException>(() => new WorkerRuntime(store, A, new WorkflowRegistry(), new ActivityRegistry(), TimeProvider.System, null, null, offload));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobOffloadOptions(new Blobs(), 0).Validated());
        Assert.Equal(256 * 1024, new BlobOffloadOptions(new Blobs()).ThresholdBytes);
    }
}
