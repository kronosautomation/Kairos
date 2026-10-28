using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Kairos.Worker;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// Blob offload against the real store: what PostgreSQL holds for a run with multi-megabyte payloads is a few small sealed references (not the payloads, not their plaintext, not the blobs' hashes in the clear),
/// the files hold only ciphertext, the run completes with the right bytes, and every way of breaking it is loud: a changed reference breaks the hash chain, a replaced or deleted blob fails the read by name.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class BlobOffloadOnPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Canary = "CANARY-PAN-4111-1111-1111-1111";
    private static readonly TenantScope Scope = new(Guid.Parse("dddddddd-0000-0000-0000-00000000000d"), Guid.Parse("dddddddd-0000-0000-0000-0000000000dd"));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kairos-offload-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class Rig(PostgresKairosStore store, string directory)
    {
        public IPayloadProtector Protector { get; } = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());

        public FileBlobOffload Blobs { get; } = new(directory);

        public BlobOffloadOptions Offload => new(Blobs, 64 * 1024);

        public KairosClient Client => new(store, TimeProvider.System, payloadProtector: Protector, blobOffload: Offload);

        public WorkerRuntime Worker => new(
            store, Scope,
            new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, input) => await ctx.ExecuteActivityAsync("echo", input)),
            new ActivityRegistry().Register("echo", (_, i, _) => Task.FromResult(i)),
            TimeProvider.System, new WorkerOptions(), Protector, Offload);
    }

    private static byte[] Payload(int length)
    {
        byte[] bytes = new byte[length];
        byte[] seed = Encoding.UTF8.GetBytes(Canary);
        for (int i = 0; i < length; i++)
        {
            bytes[i] = seed[i % seed.Length];
        }

        return bytes;
    }

    private static async Task<RunKey> RunAsync(Rig rig, byte[] input)
    {
        StartRunResult started = await rig.Client.StartAsync(Scope, "wf-" + Guid.NewGuid().ToString("N")[..8], "wf", input);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, started.RunId);
        WorkerRuntime worker = rig.Worker;
        for (int i = 0; i < 100; i++)
        {
            _ = await worker.ProcessWorkflowTaskAsync(default);
            _ = await worker.ProcessActivityTaskAsync(default);
            if ((await rig.Client.GetHistoryAsync(run))[^1] is WorkflowCompleted)
            {
                return run;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("the run did not complete");
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql)
    {
        await using NpgsqlCommand cmd = source.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task The_database_holds_small_references_the_files_hold_ciphertext_and_the_run_completes_with_the_right_bytes()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = database.NewStore();
        var rig = new Rig(store, _directory);
        byte[] input = Payload(3 * 1024 * 1024);

        RunKey run = await RunAsync(rig, input);

        Assert.Equal(input, (await rig.Client.GetOpenedHistoryAsync(run)).OfType<WorkflowCompleted>().Single().Result);
        Assert.True(await ScalarAsync(database.Superuser, "SELECT COALESCE(sum(octet_length(body)), 0) FROM kairos.history_events") < 16 * 1024); // megabytes went out; kilobytes stayed
        Assert.Equal(0, await ScalarAsync(database.Superuser, "SELECT count(*) FROM kairos.history_events WHERE position('CANARY-PAN'::bytea in body) > 0"));
        string[] files = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories);
        Assert.True(files.Length >= 3);
        byte[] marker = Encoding.UTF8.GetBytes("CANARY-PAN");
        Assert.All(files, f => Assert.True(File.ReadAllBytes(f).AsSpan().IndexOf(marker) < 0, "a blob file holds readable plaintext"));
        foreach (string file in files)
        {
            string hash = Path.GetFileName(file);
            Assert.Equal(0, await ScalarAsync(database.Superuser, $"SELECT count(*) FROM kairos.history_events WHERE position(decode('{hash}', 'hex') in body) > 0")); // sealed: the hash is not readable either
        }
    }

    [Fact]
    public async Task A_changed_reference_in_the_database_breaks_the_chain_a_replaced_file_fails_by_name_and_a_deleted_one_too()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = database.NewStore();
        var rig = new Rig(store, _directory);
        RunKey run = await RunAsync(rig, Payload(200 * 1024));

        // A replaced file: same name, other bytes.
        string file = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).First();
        byte[] original = await File.ReadAllBytesAsync(file);
        byte[] damaged = (byte[])original.Clone();
        damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(file, damaged);
        await Assert.ThrowsAnyAsync<PayloadBlobException>(async () => await rig.Client.GetOpenedHistoryAsync(run));

        // A deleted file.
        File.Delete(file);
        await Assert.ThrowsAsync<BlobMissingException>(async () => await rig.Client.GetOpenedHistoryAsync(run));
        await File.WriteAllBytesAsync(file, original);
        _ = await rig.Client.GetOpenedHistoryAsync(run); // restored: readable again

        // A changed reference in the database, by someone with the power to change rows: the chain over the stored history no longer verifies.
        await using NpgsqlCommand tamper = database.Superuser.CreateCommand(
            "UPDATE kairos.history_events SET body = set_byte(body, octet_length(body) - 1, get_byte(body, octet_length(body) - 1) # 1) WHERE seq = 1");
        Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<HistoryIntegrityException>(async () => await rig.Client.GetOpenedHistoryAsync(run));
    }
}
