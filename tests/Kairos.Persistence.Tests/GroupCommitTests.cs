using Kairos.Persistence.Postgres;
using Kairos.Testing;

namespace Kairos.Persistence.Tests;

/// <summary>Group commit keeps the guards of the single-task path for every task of a batch, and one bad task never costs its neighbours their commit.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class GroupCommitTests(PostgresFixture fixture)
{
    private static readonly TenantScope Scope = new(Guid.Parse("44444444-0000-0000-0000-000000000001"), Guid.Parse("44444444-0000-0000-0000-0000000000a1"));
    private static readonly string[] Builds = [KairosClient.DefaultBuildId];

    private static async Task<WorkflowTask[]> LeaseAsync(PostgresKairosStore store, int count)
    {
        var client = new KairosClient(store, TimeProvider.System);
        for (int i = 0; i < count; i++)
        {
            await client.StartAsync(Scope, "gc-" + Guid.NewGuid().ToString("N"), "wf", []);
        }

        var tasks = new List<WorkflowTask>();
        for (int i = 0; i < count; i++)
        {
            tasks.Add((await store.PollWorkflowTaskAsync(Scope, Builds, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None))!);
        }

        return [.. tasks];
    }

    private static WorkflowCompleted Done(WorkflowTask task) => new WorkflowCompleted(Scope.TenantId, Scope.NamespaceId, task.Run.RunId, 0, DateTimeOffset.UtcNow, []);

    [Fact]
    public async Task A_batch_applies_every_task_and_leaves_valid_hash_chains()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        WorkflowTask[] tasks = await LeaseAsync(store, 6);

        CompletionOutcome[] outcomes = await store.CompleteWorkflowTasksAsync([.. tasks.Select(t => new WorkflowTaskCompletion(t, [Done(t)]))], DateTimeOffset.UtcNow, default);

        Assert.All(outcomes, o => Assert.Equal(CompletionOutcome.Applied, o));
        foreach (WorkflowTask t in tasks)
        {
            IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(t.Run, default);
            HashChain.Verify(history);
            Assert.IsType<WorkflowCompleted>(history[^1]);
        }
    }

    [Fact]
    public async Task A_stale_lease_and_a_forged_head_fail_alone()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        WorkflowTask[] t = await LeaseAsync(store, 4);
        WorkflowTask stale = t[1] with { LeaseToken = t[1].LeaseToken + 1000 };
        HistoryEvent head = t[2].History[^1];
        WorkflowTask forged = t[2] with { History = [head with { Hash = [.. head.Hash.Select(b => (byte)(b ^ 0xFF))] }] };

        CompletionOutcome[] outcomes = await store.CompleteWorkflowTasksAsync(
            [new(t[0], [Done(t[0])]), new(stale, [Done(stale)]), new(forged, [Done(forged)]), new(t[3], [Done(t[3])])], DateTimeOffset.UtcNow, default);

        Assert.Equal(CompletionOutcome.Applied, outcomes[0]);
        Assert.Equal(CompletionOutcome.StaleLease, outcomes[1]);
        Assert.NotEqual(CompletionOutcome.Applied, outcomes[2]);
        Assert.Equal(CompletionOutcome.Applied, outcomes[3]);
        Assert.Single(await store.GetHistoryAsync(t[1].Run, default)); // the victims are untouched
        Assert.Single(await store.GetHistoryAsync(t[2].Run, default));
    }

    [Fact]
    public async Task The_committer_refuses_a_poison_request_alone_and_commits_the_rest()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        await using var committer = new WorkflowTaskGroupCommitter(store, maxBatch: 16, linger: TimeSpan.FromMilliseconds(50), writers: 1);
        WorkflowTask[] t = await LeaseAsync(store, 4);
        HistoryEvent foreign = new WorkflowCompleted(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid(), 0, DateTimeOffset.UtcNow, []); // an event of another run

        Task<CompletionOutcome> a = committer.SubmitAsync(t[0], [Done(t[0])], DateTimeOffset.UtcNow);
        Task<CompletionOutcome> poison = committer.SubmitAsync(t[1], [foreign], DateTimeOffset.UtcNow);
        Task<CompletionOutcome> c = committer.SubmitAsync(t[2], [Done(t[2])], DateTimeOffset.UtcNow);
        Task<CompletionOutcome> d = committer.SubmitAsync(t[3], [Done(t[3])], DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(() => poison);
        Assert.Equal(CompletionOutcome.Applied, await a);
        Assert.Equal(CompletionOutcome.Applied, await c);
        Assert.Equal(CompletionOutcome.Applied, await d);
        Assert.Single(await store.GetHistoryAsync(t[1].Run, default));
    }

    [Fact]
    public async Task Concurrent_completions_are_batched_and_every_one_is_answered_once()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        await using var committer = new WorkflowTaskGroupCommitter(store, maxBatch: 64, linger: TimeSpan.Zero, writers: 2);
        WorkflowTask[] tasks = await LeaseAsync(store, 40);

        CompletionOutcome[] outcomes = await Task.WhenAll(tasks.Select(t => Task.Run(() => committer.SubmitAsync(t, [Done(t)], DateTimeOffset.UtcNow))));

        Assert.All(outcomes, o => Assert.Equal(CompletionOutcome.Applied, o));
        Assert.Equal(40, committer.Completions);
        Assert.InRange(committer.Batches, 1, 40);
        foreach (WorkflowTask t in tasks)
        {
            Assert.IsType<WorkflowCompleted>((await store.GetHistoryAsync(t.Run, default))[^1]);
        }
    }

    [Fact]
    public async Task A_completion_submitted_twice_is_applied_once()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        PostgresKairosStore store = db.NewStore();
        await using var committer = new WorkflowTaskGroupCommitter(store, maxBatch: 8, linger: TimeSpan.Zero, writers: 1);
        WorkflowTask t = (await LeaseAsync(store, 1))[0];

        Task<CompletionOutcome> first = committer.SubmitAsync(t, [Done(t)], DateTimeOffset.UtcNow);
        Task<CompletionOutcome> again = committer.SubmitAsync(t, [Done(t)], DateTimeOffset.UtcNow);

        CompletionOutcome[] both = await Task.WhenAll(first, again);
        Assert.Equal(1, both.Count(o => o == CompletionOutcome.Applied));
        Assert.Single((await store.GetHistoryAsync(t.Run, default)).OfType<WorkflowCompleted>());
    }
}
