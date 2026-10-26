using System.Text;

namespace Kairos.Testing;

// The aggregates a store answers without opening a payload: usage by workflow type and day, and the approvals runs are waiting for. Whatever a store keeps to answer them, the answer must
// equal what scanning the histories gives (RunAggregates is that reference), must be as new as the last event, and must stay inside the tenant.
public abstract partial class StoreContractTests
{
    private const string Approval = RunProjection.ApprovalRequestedMarker;

    /// <summary>
    /// "agent" workflows: ask for an approval (readable marker), take two model steps (the second fails once, so the first counts as saved), then wait for a signal; an input ending in "!" also
    /// marks the approval resolved.
    /// </summary>
    private static WorkflowRegistry AgentWorkflows(string type)
    {
        var registry = new WorkflowRegistry();
        registry.Register(type, "default", async (ctx, input) =>
        {
            string id = Str(input).TrimEnd('!');
            _ = ctx.IsPatched(RunProjection.ApprovalRequestedMarker + id);
            _ = await ctx.ExecuteActivityAsync("model", B8("one"));
            _ = await ctx.ExecuteActivityAsync("model", B8("two"), RetryPolicy.Default with { InitialInterval = TimeSpan.FromSeconds(1), MaximumAttempts = 3 });
            _ = await ctx.WaitForSignalAsync("decide");
            if (Str(input).EndsWith('!'))
            {
                _ = ctx.IsPatched(RunProjection.ApprovalResolvedMarker + id);
            }

            return input;
        });
        return registry;
    }

    private static ActivityRegistry ModelActivities(string model)
    {
        return new ActivityRegistry().Register("model", (context, input, _) =>
        {
            bool isSecond = Str(input) == "two";
            context.ReportUsage(new UsageRecord(isSecond ? 300 : 100, isSecond ? 40 : 20, isSecond ? 100 : 50, model));
            if (isSecond && context.Attempt == 1)
            {
                throw new InvalidOperationException("the model call failed once, after it was paid for");
            }

            return Task.FromResult(input);
        });
    }

    private async Task<RunKey> StartAgentAsync(IKairosStore store, KairosClient client, TenantScope scope, string type, string model, string input)
    {
        StartRunResult start = await client.StartAsync(scope, "agent-" + input + "-" + type, type, B8(input), cancellationToken: Ct);
        var run = new RunKey(scope.TenantId, scope.NamespaceId, start.RunId);
        await DriveAgentAsync(store, scope, type, model);
        return run;
    }

    private async Task DriveAgentAsync(IKairosStore store, TenantScope scope, string type, string model)
    {
        for (int round = 0; round < 3; round++)
        {
            await DriveAsync(store, scope, AgentWorkflows(type), ModelActivities(model));
            Time.Advance(TimeSpan.FromSeconds(2)); // past the retry's backoff
        }
    }

    private static async Task<UsageSummary> ScanAsync(IKairosStore store, TenantScope scope, DateOnly from, DateOnly to) =>
        await RunAggregates.ScanUsageAsync(store, scope, from, to, Ct);

    private static void AssertSame(UsageSummary expected, UsageSummary actual)
    {
        Assert.Equal(expected.Buckets, actual.Buckets);
        Assert.Equal(expected.Runs, actual.Runs);
        Assert.Equal(0, actual.Behind);
    }

    [Fact]
    public async Task Usage_by_agent_day_and_model_equals_a_scan_of_the_histories_and_stays_inside_the_tenant()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        DateOnly today = DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);
        await StartAgentAsync(store, client, A, "refunds", "model-x", "a1");
        await StartAgentAsync(store, client, A, "refunds", "model-y", "a2!");
        await StartAgentAsync(store, client, A, "reports", "model-x", "a3");
        await StartAgentAsync(store, client, B, "refunds", "model-b-only", "b1");

        UsageSummary mine = await store.GetUsageSummaryAsync(A, today.AddDays(-1), today.AddDays(1), Ct);
        UsageSummary theirs = await store.GetUsageSummaryAsync(B, today.AddDays(-1), today.AddDays(1), Ct);

        AssertSame(await ScanAsync(store, A, today.AddDays(-1), today.AddDays(1)), mine);
        AssertSame(await ScanAsync(store, B, today.AddDays(-1), today.AddDays(1)), theirs);
        Assert.Equal(["refunds", "reports"], mine.Buckets.Select(b => b.WorkflowType).Distinct());
        Assert.DoesNotContain(mine.Buckets, b => b.Model == "model-b-only");
        Assert.All(theirs.Buckets, b => Assert.Equal("model-b-only", b.Model));
        UsageBucket x = mine.Buckets.Single(b => b is { WorkflowType: "refunds", Model: "model-x" });
        // One run, model-x: two completed steps (100 + 300 input), one failed attempt that was paid for (300 input), and the first step saved because a later attempt failed.
        Assert.Equal((2L, 1L, 700L, 100L, 250L), (x.Steps, x.FailedAttempts, x.InputTokens, x.OutputTokens, x.CachedInputTokens));
        Assert.Equal((100L, 20L, 50L), (x.SavedInputTokens, x.SavedOutputTokens, x.SavedCachedInputTokens));
        Assert.Equal([new WorkflowTypeRuns("refunds", 2), new WorkflowTypeRuns("reports", 1)], mine.Runs);
    }

    [Fact]
    public async Task The_window_is_by_the_day_the_attempt_was_recorded_and_the_answer_is_as_new_as_the_last_event()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        DateOnly today = DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);
        RunKey run = await StartAgentAsync(store, client, A, "refunds", "model-x", "a1");
        UsageSummary first = await store.GetUsageSummaryAsync(A, today, today, Ct);

        Time.Advance(TimeSpan.FromDays(2));
        await client.SignalAsync(run, "decide", [], Ct);
        await StartAgentAsync(store, client, A, "refunds", "model-x", "a2"); // tokens spent two days later
        await DriveAgentAsync(store, A, "refunds", "model-x");
        UsageSummary second = await store.GetUsageSummaryAsync(A, today, today.AddDays(5), Ct);

        Assert.NotEmpty(first.Buckets);
        Assert.Equal(2, second.Buckets.Select(b => b.Day).Distinct().Count());
        AssertSame(await ScanAsync(store, A, today, today.AddDays(5)), second);
        Assert.Empty((await store.GetUsageSummaryAsync(A, today.AddDays(10), today.AddDays(12), Ct)).Buckets);
        Assert.Equal(first.Buckets, (await store.GetUsageSummaryAsync(A, today, today, Ct)).Buckets); // what was recorded on a past day does not move, and a later day is not in a window that ends before it
        Assert.All((await store.GetUsageSummaryAsync(A, today.AddDays(1), today.AddDays(5), Ct)).Buckets, b => Assert.Equal(today.AddDays(2), b.Day));
        Assert.Equal(0, await store.ProjectAggregatesAsync(A, 100, Ct)); // nothing is left to read
    }

    [Fact]
    public async Task Approvals_waiting_are_listed_oldest_first_until_resolved_or_the_run_closes_and_never_across_tenants()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        RunKey first = await StartAgentAsync(store, client, A, "refunds", "m", "ap-1");
        Time.Advance(TimeSpan.FromMinutes(1));
        RunKey second = await StartAgentAsync(store, client, A, "refunds", "m", "ap-2!");
        Time.Advance(TimeSpan.FromMinutes(1));
        await StartAgentAsync(store, client, A, "reports", "m", "ap-3");
        await StartAgentAsync(store, client, B, "refunds", "m", "other-tenant");

        PendingApprovalPage all = await store.GetPendingApprovalsAsync(A, 10, null, Ct);

        Assert.Equal(["ap-1", "ap-2", "ap-3"], all.Items.Select(i => i.ApprovalId));
        Assert.Equal(["refunds", "refunds", "reports"], all.Items.Select(i => i.WorkflowType));
        Assert.Equal((3, 0), (all.Total, all.Behind));
        Assert.Equal([first.RunId, second.RunId], all.Items.Take(2).Select(i => i.Run.RunId));
        Assert.Equal(["other-tenant"], (await store.GetPendingApprovalsAsync(B, 10, null, Ct)).Items.Select(i => i.ApprovalId));

        // Paging by the position of the last item returned.
        PendingApprovalPage page = await store.GetPendingApprovalsAsync(A, 2, null, Ct);
        PendingApprovalPage rest = await store.GetPendingApprovalsAsync(A, 2, page.Items[^1].Cursor, Ct);
        Assert.Equal(["ap-1", "ap-2"], page.Items.Select(i => i.ApprovalId));
        Assert.Equal(["ap-3"], rest.Items.Select(i => i.ApprovalId));
        Assert.Equal(3, page.Total);

        // A run that marks the approval resolved leaves the list while it is still running; a run that closes leaves it too.
        await client.SignalAsync(second, "decide", [], Ct);
        await DriveAgentAsync(store, A, "refunds", "m");
        Assert.Equal(RunState.Completed, await client.GetStateAsync(second, Ct));
        Assert.Equal(["ap-1", "ap-3"], (await store.GetPendingApprovalsAsync(A, 10, null, Ct)).Items.Select(i => i.ApprovalId));
        await client.SignalAsync(first, "decide", [], Ct);
        await DriveAgentAsync(store, A, "refunds", "m");
        Assert.Equal(["ap-3"], (await store.GetPendingApprovalsAsync(A, 10, null, Ct)).Items.Select(i => i.ApprovalId));
        PendingApprovalPage scanned = await RunAggregates.ScanPendingAsync(store, A, 10, null, Ct);
        PendingApprovalPage answered = await store.GetPendingApprovalsAsync(A, 10, null, Ct);
        Assert.Equal(scanned.Items, answered.Items);
        Assert.Equal((scanned.Total, 0), (answered.Total, answered.Behind));
    }

    [Fact]
    public async Task Aggregates_are_derived_from_readable_metadata_only_and_agree_on_a_run_without_markers_or_usage()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        await client.StartAsync(A, "plain", "plain-wf", B8("secret input"), cancellationToken: Ct);
        DateOnly today = DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);

        UsageSummary usage = await store.GetUsageSummaryAsync(A, today, today, Ct);
        PendingApprovalPage pending = await store.GetPendingApprovalsAsync(A, 10, null, Ct);

        Assert.Empty(usage.Buckets);
        Assert.Empty(usage.Runs);
        Assert.Empty(pending.Items);
        Assert.Equal((0, 0), (pending.Total, pending.Behind + usage.Behind));
    }
}
