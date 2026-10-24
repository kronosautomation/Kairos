using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// I6 against real storage: someone with superuser access edits history rows directly. Reads, verification and workers must
/// reject the result rather than act on it.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class TamperTests(PostgresFixture fixture)
{
    private static readonly TenantScope Scope = new(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), Guid.Parse("cccccccc-0000-0000-0000-0000000000c3"));

    private sealed record Fixture(TestDatabase Db, PostgresKairosStore Store, KairosClient Client, VirtualTimeProvider Time, RunKey Run);

    private async Task<Fixture> FinishedRunAsync()
    {
        TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        var client = new KairosClient(store, time);
        WorkflowRegistry wf = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, input) =>
        {
            byte[] a = await ctx.ExecuteActivityAsync("echo", input);
            await ctx.WaitForSignalAsync("go");
            return a;
        });
        ActivityRegistry act = new ActivityRegistry().Register("echo", (_, i, _) => Task.FromResult(i));
        StartRunResult start = await client.StartAsync(Scope, "tamper", "wf", [9, 9, 9]);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        var worker = new WorkerRuntime(store, Scope, wf, act, time);
        await Driver.DriveAsync(worker);
        await client.SignalAsync(run, "go", []);
        await Driver.DriveAsync(worker);
        Assert.Equal(RunState.Completed, await client.GetStateAsync(run));
        HashChain.Verify(await client.GetHistoryAsync(run));
        return new Fixture(db, store, client, time, run);
    }

    private static async Task Superuser(TestDatabase db, string sql)
    {
        await using NpgsqlCommand cmd = db.Superuser.CreateCommand(sql);
        Assert.True(await cmd.ExecuteNonQueryAsync() > 0, "tamper statement matched no rows");
    }

    [Fact]
    public async Task Edited_event_body_is_caught_by_verification()
    {
        Fixture f = await FinishedRunAsync();
        await using TestDatabase _ = f.Db;
        // Flip the last byte of the ActivityScheduled input payload (seq 3).
        await Superuser(f.Db, "UPDATE kairos.history_events SET body = set_byte(body, length(body) - 1, get_byte(body, length(body) - 1) # 255) WHERE seq = 3");

        // Either the body no longer decodes or its hash no longer matches; both surface as an integrity failure at or after seq 3.
        try
        {
            HashChain.Verify(await f.Client.GetHistoryAsync(f.Run));
            Assert.Fail("Tampering went undetected.");
        }
        catch (HistoryIntegrityException ex)
        {
            Assert.True(ex.Sequence >= 3);
        }
    }

    [Fact]
    public async Task Edited_stored_hash_is_caught()
    {
        Fixture f = await FinishedRunAsync();
        await using TestDatabase _ = f.Db;
        await Superuser(f.Db, "UPDATE kairos.history_events SET hash = sha256('forged') WHERE seq = 2");

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(f.Client.GetHistoryAsync(f.Run).AsTask().GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Deleted_middle_event_is_caught()
    {
        Fixture f = await FinishedRunAsync();
        await using TestDatabase _ = f.Db;
        await Superuser(f.Db, "DELETE FROM kairos.history_events WHERE seq = 3");

        Assert.Throws<HistoryIntegrityException>(() => HashChain.Verify(f.Client.GetHistoryAsync(f.Run).AsTask().GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Reordered_sequence_numbers_are_caught_when_read()
    {
        Fixture f = await FinishedRunAsync();
        await using TestDatabase _ = f.Db;
        await Superuser(f.Db, "UPDATE kairos.history_events SET seq = -seq WHERE seq IN (2, 3)");
        await Superuser(f.Db, "UPDATE kairos.history_events SET seq = CASE seq WHEN -2 THEN 3 WHEN -3 THEN 2 END WHERE seq IN (-2, -3)");

        // The row's seq column no longer matches the sequence number sealed inside its body.
        await Assert.ThrowsAsync<HistoryIntegrityException>(async () => await f.Client.GetHistoryAsync(f.Run));
    }

    [Fact]
    public async Task A_worker_refuses_to_advance_a_tampered_run()
    {
        TestDatabase db = await fixture.NewDatabaseAsync();
        await using TestDatabase _ = db;
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        var client = new KairosClient(store, time);
        WorkflowRegistry wf = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, _) =>
        {
            await ctx.WaitForSignalAsync("go");
            await ctx.ExecuteActivityAsync("echo", ReadOnlyMemory<byte>.Empty);
            return [];
        });
        ActivityRegistry act = new ActivityRegistry().Register("echo", (_, i, _) => Task.FromResult(i));
        StartRunResult start = await client.StartAsync(Scope, "t2", "wf", [1, 2, 3, 4]);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        var faults = new List<TaskFault>();
        var worker = new WorkerRuntime(store, Scope, wf, act, time) { OnFault = faults.Add };
        await Driver.DriveAsync(worker);

        await Superuser(db, "UPDATE kairos.history_events SET body = set_byte(body, length(body) - 1, get_byte(body, length(body) - 1) # 255) WHERE seq = 1");
        await client.SignalAsync(run, "go", []);
        await worker.ProcessWorkflowTaskAsync(CancellationToken.None);

        Assert.Single(faults);
        Assert.IsType<HistoryIntegrityException>(faults[0].Error);
        Assert.Equal(RunState.Running, await client.GetStateAsync(run));
        Assert.DoesNotContain((short)HistoryEventType.ActivityScheduled, await ReadRawAsync(db, run)); // nothing was built on top of it
    }

    private static async Task<List<short>> ReadRawAsync(TestDatabase db, RunKey run)
    {
        await using NpgsqlCommand cmd = db.Superuser.CreateCommand($"SELECT get_byte(body, 1) FROM kairos.history_events WHERE run_id = '{run.RunId}' ORDER BY seq");
        var types = new List<short>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            types.Add((short)reader.GetInt32(0));
        }

        return types;
    }
}
