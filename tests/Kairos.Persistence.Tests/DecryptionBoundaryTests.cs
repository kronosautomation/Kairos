using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Kairos.Worker;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The decryption boundary (docs/SECURITY-ARCHITECTURE.md section 11, requirement 1; Railway, Oct 2026). With client-side encryption on, an
/// attacker who owns everything on the Kairos side (the database, the server code, every secret in the server's configuration and CI,
/// and a worker-role credential) must fail to read, forge or relocate any payload. The key provider stays on the customer's side.
/// A control with the real key proves the harness can read the data when it should.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class DecryptionBoundaryTests(PostgresFixture fixture)
{
    private const string Canary = "CANARY-PAN-4111-1111-1111-1111";
    private static readonly TenantScope Scope = new(Guid.Parse("dddddddd-0000-0000-0000-00000000000d"), Guid.Parse("dddddddd-0000-0000-0000-0000000000dd"));

    private static WorkflowRegistry Workflows() => new WorkflowRegistry().Register("wf", "default", async (ctx, input) =>
    {
        byte[] charged = await ctx.ExecuteActivityAsync("charge", input);
        byte[] approval = await ctx.WaitForSignalAsync("approve");
        return [.. charged, .. approval];
    });

    private static ActivityRegistry Activities() => new ActivityRegistry()
        .Register("charge", (_, i, _) => Task.FromResult(Encoding.UTF8.GetBytes("charged:" + Encoding.UTF8.GetString(i))));

    /// <summary>The customer's side: key ring, client, worker. None of these objects is ever given to the "server".</summary>
    private sealed class Customer
    {
        public Customer(PostgresKairosStore store)
        {
            Protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral("customer-key-1"));
            Time = new VirtualTimeProvider();
            Client = new KairosClient(store, Time, payloadProtector: Protector);
            Faults = [];
            Worker = new WorkerRuntime(store, Scope, Workflows(), Activities(), Time, new WorkerOptions { AutoHeartbeat = false }, Protector) { OnFault = Faults.Add };
        }

        public EnvelopePayloadProtector Protector { get; }

        public VirtualTimeProvider Time { get; }

        public KairosClient Client { get; }

        public WorkerRuntime Worker { get; }

        public List<TaskFault> Faults { get; }

        public async Task<RunKey> StartAndChargeAsync(string id)
        {
            StartRunResult start = await Client.StartAsync(Scope, id, "wf", Encoding.UTF8.GetBytes(Canary));
            await Driver.DriveAsync(Worker); // schedules and completes the charge; now waiting for a signal
            return new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        }
    }

    /// <summary>Every payload-carrying event, exactly as the server stores it.</summary>
    private static async Task<List<HistoryEvent>> EverythingTheServerHoldsAsync(TestDatabase db)
    {
        var events = new List<HistoryEvent>();
        await using NpgsqlCommand cmd = db.Superuser.CreateCommand(
            "SELECT body, prev_hash, hash FROM kairos.history_events ORDER BY run_id, seq");
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(HistoryEventCodec.Decode(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2)));
        }

        return events.Where(e => e is WorkflowStarted or ActivityScheduled or ActivityCompleted or SignalReceived or WorkflowCompleted).ToList();
    }

    private static async Task<int> OpenableByAsync(IPayloadProtector attacker, IEnumerable<HistoryEvent> events)
    {
        var crypto = new PayloadCrypto(attacker);
        int opened = 0;
        foreach (HistoryEvent e in events)
        {
            try
            {
                await crypto.OpenHistoryAsync([e], CancellationToken.None);
                opened++;
            }
            catch (PayloadProtectionException)
            {
                // refused, as it must be
            }
        }

        return opened;
    }

    private static byte[] Key(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>Everything an attacker on the server side can lay hands on, offered as master keys, plus keys they make up.</summary>
    private static IEnumerable<IPayloadProtector> Attackers(TestDatabase db, string stolenWorkerApiKey, byte[] pepper, byte[] signingKey, byte[] webhookSecret)
    {
        string[] serverSecrets =
        [
            db.DataPlaneConnectionString, stolenWorkerApiKey, Convert.ToHexString(pepper), Convert.ToBase64String(signingKey),
            Convert.ToHexString(webhookSecret), "kairos_migrator-test-only", "dataplane-test-only", "controlplane-test-only", "postgres", "",
            Scope.TenantId.ToString(), Scope.NamespaceId.ToString(), Canary,
        ];
        foreach (string secret in serverSecrets)
        {
            // Same key id as the customer's, so the id gives no hint: only the key material differs.
            yield return new EnvelopePayloadProtector(new LocalKeyProvider(new Dictionary<string, byte[]> { ["customer-key-1"] = Key(secret) }, "customer-key-1"));
        }

        yield return new EnvelopePayloadProtector(new LocalKeyProvider(new Dictionary<string, byte[]> { ["customer-key-1"] = new byte[32] }, "customer-key-1"));
        yield return new EnvelopePayloadProtector(new LocalKeyProvider(new Dictionary<string, byte[]> { ["customer-key-1"] = RandomNumberGenerator.GetBytes(32) }, "customer-key-1"));
        yield return new EnvelopePayloadProtector(new AlwaysZeroKeyProvider());
    }

    /// <summary>A key service the attacker controls that "unwraps" everything to zeros.</summary>
    private sealed class AlwaysZeroKeyProvider : IKeyProvider
    {
        public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken ct) =>
            ValueTask.FromResult(new WrappedKey("customer-key-1", new byte[60]));

        public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken ct) =>
            ValueTask.FromResult(new byte[32]);
    }

    [Fact]
    public async Task A_compromised_server_with_every_server_side_secret_cannot_open_any_payload()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var customer = new Customer(store);
        RunKey run = await customer.StartAndChargeAsync("boundary-1");
        await customer.Client.SignalAsync(run, "approve", Encoding.UTF8.GetBytes("+" + Canary));
        await Driver.DriveAsync(customer.Worker);
        Assert.Equal(RunState.Completed, await customer.Client.GetStateAsync(run));

        // What the attacker can see: every payload, as stored.
        List<HistoryEvent> stolen = await EverythingTheServerHoldsAsync(db);
        Assert.True(stolen.Count >= 5, "The scenario did not produce payload-carrying events to attack.");

        // The server-side secrets, including a worker-role credential issued to CI.
        byte[] pepper = RandomNumberGenerator.GetBytes(32), signingKey = RandomNumberGenerator.GetBytes(32), webhookSecret = RandomNumberGenerator.GetBytes(32);
        string stolenWorkerKey = new ApiKeyAuthenticator(pepper).Issue(Scope, Role.Worker, "ci", DateTimeOffset.UtcNow);

        int attackers = 0;
        foreach (IPayloadProtector attacker in Attackers(db, stolenWorkerKey, pepper, signingKey, webhookSecret))
        {
            attackers++;
            Assert.Equal(0, await OpenableByAsync(attacker, stolen));
        }

        Assert.True(attackers >= 15);

        // Control: with the customer's key, the same harness opens every payload, so "0" above means something.
        Assert.Equal(stolen.Count, await OpenableByAsync(customer.Protector, stolen));
    }

    [Fact]
    public async Task The_server_api_hands_a_stolen_worker_credential_ciphertext_only_and_forged_results_are_rejected_not_delivered()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var customer = new Customer(store);
        StartRunResult start = await customer.Client.StartAsync(Scope, "boundary-2", "wf", Encoding.UTF8.GetBytes(Canary));
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await customer.Worker.ProcessWorkflowTaskAsync(CancellationToken.None); // schedules the charge

        // The attacker holds only what a worker-role credential can do through the server API.
        ActivityTask task = (await store.PollActivityTaskAsync(Scope, customer.Time.GetUtcNow(), TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.DoesNotContain(Canary, Encoding.Latin1.GetString(task.Input), StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetBytes(Canary), task.Input);

        // It cannot read the input, so it forges a result in the clear and tries to complete the activity with it.
        var forged = new ActivityCompleted(Scope.TenantId, Scope.NamespaceId, run.RunId, 0, customer.Time.GetUtcNow(), task.ActivityId, Encoding.UTF8.GetBytes("charged:FORGED"));
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteActivityTaskAsync(task, forged, null, customer.Time.GetUtcNow(), CancellationToken.None));

        // The customer's worker refuses the forgery: it never reaches workflow code and the run does not advance.
        await customer.Worker.ProcessWorkflowTaskAsync(CancellationToken.None);
        Assert.IsType<PayloadProtectionException>(Assert.Single(customer.Faults).Error);
        Assert.Equal(RunState.Running, await customer.Client.GetStateAsync(run));
        Assert.DoesNotContain(await customer.Client.GetHistoryAsync(run), e => e is WorkflowCompleted);
    }

    [Fact]
    public async Task A_server_that_rewrites_history_and_re_seals_the_hash_chain_still_cannot_relocate_a_ciphertext()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var customer = new Customer(store);
        RunKey victim = await customer.StartAndChargeAsync("boundary-victim");
        RunKey donor = await customer.StartAndChargeAsync("boundary-donor");

        // Take the donor's sealed activity result and plant it in the victim's history. The attacker owns the database and the
        // Kairos code, so it recomputes the whole hash chain and the run head: the integrity check alone will not catch this.
        IReadOnlyList<HistoryEvent> donorHistory = await store.GetHistoryAsync(donor, CancellationToken.None);
        IReadOnlyList<HistoryEvent> victimHistory = await store.GetHistoryAsync(victim, CancellationToken.None);
        byte[] donorResult = donorHistory.OfType<ActivityCompleted>().Single().Result;
        var tampered = new List<HistoryEvent>(victimHistory.Select(e => e is ActivityCompleted c ? c with { Result = donorResult } : e));

        byte[] prev = HashChain.GenesisHash.ToArray();
        for (int i = 0; i < tampered.Count; i++)
        {
            HistoryEvent resealed = StoreRules.Seal(tampered[i], i + 1, prev);
            prev = resealed.Hash;
            await using NpgsqlCommand cmd = db.Superuser.CreateCommand(
                "UPDATE kairos.history_events SET body = @b, prev_hash = @p, hash = @h WHERE run_id = @r AND seq = @s");
            cmd.Parameters.AddWithValue("b", HistoryEventCodec.Encode(resealed));
            cmd.Parameters.AddWithValue("p", resealed.PrevHash);
            cmd.Parameters.AddWithValue("h", resealed.Hash);
            cmd.Parameters.AddWithValue("r", victim.RunId);
            cmd.Parameters.AddWithValue("s", (long)(i + 1));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand head = db.Superuser.CreateCommand("UPDATE kairos.runs SET last_hash = @h WHERE run_id = @r"))
        {
            head.Parameters.AddWithValue("h", prev);
            head.Parameters.AddWithValue("r", victim.RunId);
            await head.ExecuteNonQueryAsync();
        }

        HashChain.Verify(await store.GetHistoryAsync(victim, CancellationToken.None)); // the forged chain is internally valid

        // The victim is signalled; the customer's worker replays, tries to open the planted ciphertext and refuses.
        await customer.Client.SignalAsync(victim, "approve", Encoding.UTF8.GetBytes("+ok"));
        await Driver.DriveAsync(customer.Worker);

        Assert.Contains(customer.Faults, f => f.Run == victim && f.Error is PayloadProtectionException);
        Assert.Equal(RunState.Running, await customer.Client.GetStateAsync(victim));
        Assert.DoesNotContain(await customer.Client.GetHistoryAsync(victim), e => e is WorkflowCompleted);
        Assert.Equal(RunState.Running, await customer.Client.GetStateAsync(donor)); // the donor is untouched
    }

    [Fact]
    public void Server_side_assemblies_contain_no_key_handling_and_do_not_reference_the_client_side_ones()
    {
        string[] forbiddenTypes =
        [
            nameof(IKeyProvider), nameof(IPayloadProtector), nameof(PayloadCrypto), nameof(WrappedKey), nameof(KeyUnwrapException),
            nameof(EnvelopePayloadProtector), nameof(LocalKeyProvider),
        ];
        string[] forbiddenAssemblies = ["Kairos.Worker", "Kairos.Cloud.SelfHosted"];

        // Today the server-side code is the Postgres store; the standalone Server and Admin hosts join this list in Milestone 7.
        string[] serverAssemblies = ["Kairos.Persistence.Postgres", "Kairos.Server", "Kairos.Admin"];
        int checkedCount = 0;
        foreach (string name in serverAssemblies)
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (assembly is null && File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll")))
            {
                assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
            }

            if (assembly is null)
            {
                continue;
            }

            checkedCount++;
            using var pe = new PEReader(File.OpenRead(assembly.Location));
            MetadataReader md = pe.GetMetadataReader();

            foreach (TypeReferenceHandle handle in md.TypeReferences)
            {
                string typeName = md.GetString(md.GetTypeReference(handle).Name);
                Assert.DoesNotContain(typeName, forbiddenTypes);
            }

            foreach (AssemblyReferenceHandle handle in md.AssemblyReferences)
            {
                Assert.DoesNotContain(md.GetString(md.GetAssemblyReference(handle).Name), forbiddenAssemblies);
            }
        }

        Assert.True(checkedCount >= 1, "No server-side assembly was inspected.");
    }
}
