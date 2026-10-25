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
}
