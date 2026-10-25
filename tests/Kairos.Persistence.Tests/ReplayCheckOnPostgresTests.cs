using System.Diagnostics;
using System.Text;
using Kairos.Persistence.Postgres;
using Kairos.Testing;

namespace Kairos.Persistence.Tests;

/// <summary>The build as it was when the runs were recorded.</summary>
public sealed class PgOrdersBuild : IReplayCheckSetup
{
    public void Configure(KairosOptions options) => options.AddWorkflow("pg-order", PgOrders.Original);
}

/// <summary>A new build in which a step moved.</summary>
public sealed class PgOrdersReorderedBuild : IReplayCheckSetup
{
    public void Configure(KairosOptions options) => options.AddWorkflow("pg-order", async (ctx, input) =>
    {
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return await ctx.ExecuteActivityAsync("charge", input);
    });
}

internal static class PgOrders
{
    public static async Task<byte[]> Original(IWorkflowContext ctx, byte[] input)
    {
        byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return receipt;
    }
}

/// <summary>The real <c>kairos</c> executable against real PostgreSQL, connecting as the read-only reporting role.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ReplayCheckOnPostgresTests(PostgresFixture fixture)
{
    private const string Canary = "CANARY-77ab-never-printed";
    private static readonly TenantScope Scope = new(Guid.Parse("33333333-0000-0000-0000-000000000001"), Guid.Parse("33333333-0000-0000-0000-0000000000a1"));

    private static async Task<(int Exit, string Out, string Err)> KairosAsync(string connection, params string[] args)
    {
        string tool = Path.Combine(AppContext.BaseDirectory, "kairos.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(tool);
        foreach (string a in args)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["KAIROS_CONNECTION"] = connection; // never on the command line: it would end up in pipeline logs
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task The_tool_replays_runs_read_from_postgres_and_fails_a_build_that_diverges_without_printing_a_payload()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var clock = new VirtualTimeProvider();
        var client = new KairosClient(store, clock);
        var workflows = new WorkflowRegistry().Register("pg-order", "default", PgOrders.Original);
        var activities = new ActivityRegistry().Register("charge", (_, input, _) => Task.FromResult(input));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false });
        await client.StartAsync(Scope, "closed-1", "pg-order", Encoding.UTF8.GetBytes(Canary));
        await client.StartAsync(Scope, "closed-2", "pg-order", Encoding.UTF8.GetBytes(Canary));
        await Driver.DriveAsync(worker);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Driver.DriveAsync(worker);
        await client.StartAsync(Scope, "open-1", "pg-order", Encoding.UTF8.GetBytes(Canary)); // in flight: its first task has not run
        string build = typeof(PgOrdersBuild).Assembly.Location;
        string[] scope = ["--tenant", Scope.TenantId.ToString(), "--namespace", Scope.NamespaceId.ToString()];

        (int ok, string passed, string passedErr) = await KairosAsync(db.ReadOnlyConnectionString, ["replay-check", "--build", build, "--setup", typeof(PgOrdersBuild).FullName!, .. scope]);
        Assert.True(ok == 0, passed + passedErr);
        Assert.Contains("3 replayed, 0 failed", passed, StringComparison.Ordinal);
        Assert.Contains("open=1 closed=2 replayed=3", passed, StringComparison.Ordinal);

        (int bad, string failed, string failedErr) = await KairosAsync(
            db.ReadOnlyConnectionString, ["replay-check", "--build", build, "--setup", typeof(PgOrdersReorderedBuild).FullName!, .. scope, "--sample", "all"]);
        Assert.Equal(1, bad);
        Assert.Contains("FAIL  nondeterministic  pg-order", failed, StringComparison.Ordinal);
        Assert.Contains("recorded: ActivityScheduled: activity 1 'charge'", failed, StringComparison.Ordinal);
        Assert.Contains("emitted:  StartTimer: timer 1 for 00:00:05", failed, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, passed + passedErr + failed + failedErr, StringComparison.Ordinal);

        // Another tenant's scope sees none of it: the check replays nothing there and says so.
        (int foreign, string foreignOut, _) = await KairosAsync(
            db.ReadOnlyConnectionString,
            ["replay-check", "--build", build, "--setup", typeof(PgOrdersBuild).FullName!, "--tenant", Guid.NewGuid().ToString(), "--namespace", Guid.NewGuid().ToString()]);
        Assert.Equal(1, foreign);
        Assert.Contains("Nothing was replayed", foreignOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_exports_a_run_with_the_data_plane_role_and_audits_it_and_the_file_replays_locally()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        var clock = new VirtualTimeProvider();
        var client = new KairosClient(store, clock);
        var workflows = new WorkflowRegistry().Register("pg-order", "default", PgOrders.Original);
        var activities = new ActivityRegistry().Register("charge", (_, input, _) => Task.FromResult(input));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false });
        StartRunResult start = await client.StartAsync(Scope, "export-1", "pg-order", Encoding.UTF8.GetBytes(Canary));
        await Driver.DriveAsync(worker);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Driver.DriveAsync(worker);
        string[] scope = ["--tenant", Scope.TenantId.ToString(), "--namespace", Scope.NamespaceId.ToString()];
        string dir = Path.Combine(Path.GetTempPath(), "kairos-pg-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The read-only role cannot write the audit log, so it cannot export: an export is never unrecorded.
            string denied = Path.Combine(dir, "denied.khist");
            (int readOnlyExit, _, string readOnlyError) = await KairosAsync(
                db.ReadOnlyConnectionString, ["history", "export", start.RunId.ToString(), "--reason", "should not work", "--out", denied, "--actor", "dev@example.com", .. scope]);
            Assert.Equal(1, readOnlyExit);
            Assert.Contains("audit log", readOnlyError, StringComparison.Ordinal);
            Assert.False(File.Exists(denied));

            string file = Path.Combine(dir, "run.khist");
            (int exit, string output, string error) = await KairosAsync(
                db.DataPlaneConnectionString, ["history", "export", start.RunId.ToString(), "--reason", "reproduce ticket 4411", "--out", file, "--actor", "dev@example.com", .. scope]);
            Assert.True(exit == 0, output + error);

            AuditEntry entry = Assert.Single(await store.GetAuditLogAsync(Scope, default));
            Assert.Equal(("ExportHistory", "dev@example.com", start.RunId), (entry.Action, entry.Actor, entry.RunId));
            HistoryFileContents loaded = HistoryFile.ParseVerified(await File.ReadAllTextAsync(file));
            Assert.Equal($"reproduce ticket 4411 (head {loaded.HeadHash})", entry.Detail);

            // The file replays on a developer machine against local code, with the recorded results; a moved step is reported like replay-check reports it.
            Assert.True((await KairosReplayer.ReplayAsync(file, PgOrders.Original)).Succeeded);
            ReplayOutcome moved = await KairosReplayer.ReplayAsync(file, async (ctx, input) =>
            {
                await ctx.DelayAsync(TimeSpan.FromSeconds(5));
                return await ctx.ExecuteActivityAsync("charge", input);
            });
            Assert.Contains("emitted:  StartTimer: timer 1 for 00:00:05", moved.Text, StringComparison.Ordinal);

            string metadata = Path.Combine(dir, "metadata.khist");
            (int metaExit, string metaOut, string metaError) = await KairosAsync(
                db.DataPlaneConnectionString, ["history", "export", start.RunId.ToString(), "--reason", "shape", "--metadata-only", "--out", metadata, "--actor", "dev@example.com", .. scope]);
            Assert.True(metaExit == 0, metaOut + metaError);
            (int verifyExit, string verifyOut, _) = await KairosAsync(db.DataPlaneConnectionString, ["history", "verify", metadata]);
            Assert.Equal(0, verifyExit);
            Assert.Contains("digest chain verified", verifyOut, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, await File.ReadAllTextAsync(metadata), StringComparison.Ordinal);
            Assert.Equal(2, (await store.GetAuditLogAsync(Scope, default)).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
