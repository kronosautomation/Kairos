using Kairos.Persistence.Postgres;
using Kairos.Testing;

namespace Kairos.Persistence.Tests;

/// <summary>
/// L3: with several app instances, a worker whose clock runs ahead must not steal another worker's live lease. In Database mode the
/// lease clock is the database's; the control case shows the same skew does steal when the caller's clock is trusted.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ClockSkewTests(PostgresFixture fixture)
{
    private static readonly TenantScope Scope = new(Guid.Parse("dddddddd-0000-0000-0000-000000000004"), Guid.Parse("dddddddd-0000-0000-0000-0000000000d4"));
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(10);
    private static readonly string[] Builds = [KairosClient.DefaultBuildId];

    private static async Task<RunKey> StartAsync(IKairosStore store)
    {
        StartRunResult start = await new KairosClient(store, TimeProvider.System).StartAsync(Scope, "skew-" + Guid.NewGuid(), "wf", []);
        return new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
    }

    [Fact]
    public async Task A_worker_with_a_fast_clock_cannot_steal_a_live_workflow_lease()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewDatabaseClockStore();
        await StartAsync(store);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        WorkflowTask task = (await store.PollWorkflowTaskAsync(Scope, Builds, now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.NotNull(task);

        DateTimeOffset fast = now + Skew; // another instance, ten minutes ahead
        Assert.Equal(0, await store.ReapExpiredLeasesAsync(Scope, fast, CancellationToken.None));
        Assert.Null(await store.PollWorkflowTaskAsync(Scope, Builds, fast, TimeSpan.FromMinutes(1), CancellationToken.None));

        var done = new WorkflowCompleted(Scope.TenantId, Scope.NamespaceId, task.Run.RunId, 0, now, []);
        Assert.Equal(CompletionOutcome.Applied, await store.CompleteWorkflowTaskAsync(task, [done], now, CancellationToken.None));
    }

    [Fact]
    public async Task A_worker_with_a_fast_clock_cannot_steal_a_live_activity_lease_and_heartbeats_use_database_time()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewDatabaseClockStore();
        var wf = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, async (ctx, input) => await ctx.ExecuteActivityAsync("a", input));
        var act = new ActivityRegistry().Register("a", (_, i, _) => Task.FromResult(i));
        await StartAsync(store);
        await new WorkerRuntime(store, Scope, wf, act, TimeProvider.System).ProcessWorkflowTaskAsync(CancellationToken.None);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ActivityTask task = (await store.PollActivityTaskAsync(Scope, now, TimeSpan.FromMinutes(1), CancellationToken.None))!;
        Assert.NotNull(task);

        DateTimeOffset fast = now + Skew;
        Assert.Equal(0, await store.ReapExpiredLeasesAsync(Scope, fast, CancellationToken.None));
        Assert.Null(await store.PollActivityTaskAsync(Scope, fast, TimeSpan.FromMinutes(1), CancellationToken.None));

        // A slow-clocked holder renewing does not shorten or lengthen anything by its own skew.
        Assert.Equal(CompletionOutcome.Applied, await store.HeartbeatActivityTaskAsync(task, now - Skew, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.Equal(0, await store.ReapExpiredLeasesAsync(Scope, now, CancellationToken.None));
    }

    [Fact]
    public async Task Control_when_the_callers_clock_is_trusted_the_same_skew_does_steal_the_lease()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore(); // LeaseClock.Caller
        await StartAsync(store);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.NotNull(await store.PollWorkflowTaskAsync(Scope, Builds, now, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(1, await store.ReapExpiredLeasesAsync(Scope, now + Skew, CancellationToken.None));
    }
}
