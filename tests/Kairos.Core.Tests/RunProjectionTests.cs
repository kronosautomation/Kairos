namespace Kairos.Core.Tests;

/// <summary>What the server reads out of a history without opening anything: usage by day and model, and the approvals a run waits for.</summary>
public sealed class RunProjectionTests
{
    private static readonly Guid T = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid N = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid R = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 23, 59, 0, TimeSpan.Zero);

    private sealed class Builder
    {
        public List<HistoryEvent> Events { get; } = [new WorkflowStarted(T, N, R, 1, T0, "refunds", "b", [1, 2, 3])];

        private long Next => Events.Count + 1;

        public Builder Completed(int minutes, UsageRecord? usage, int id = 1)
        {
            Events.Add(new ActivityCompleted(T, N, R, Next, T0.AddMinutes(minutes), id, [9]) { Usage = usage });
            return this;
        }

        public Builder Failed(int minutes, UsageRecord? usage, int id = 1)
        {
            Events.Add(new ActivityFailed(T, N, R, Next, T0.AddMinutes(minutes), id, 1, "Boom", "text", true) { Usage = usage });
            return this;
        }

        public Builder Marker(int minutes, string id)
        {
            Events.Add(new PatchMarker(T, N, R, Next, T0.AddMinutes(minutes), id));
            return this;
        }

        public Builder Close(int minutes, string how = "completed")
        {
            Events.Add(how switch
            {
                "completed" => new WorkflowCompleted(T, N, R, Next, T0.AddMinutes(minutes), [1]),
                "failed" => new WorkflowFailed(T, N, R, Next, T0.AddMinutes(minutes), "E", "m"),
                "continued" => new WorkflowContinued(T, N, R, Next, T0.AddMinutes(minutes), Guid.NewGuid(), []),
                _ => new RunTerminated(T, N, R, Next, T0.AddMinutes(minutes), "stop"),
            });
            return this;
        }

        public List<HistoryEvent> Sealed()
        {
            var sealedEvents = new List<HistoryEvent>();
            byte[] prev = HashChain.GenesisHash.ToArray();
            foreach (HistoryEvent e in Events)
            {
                HistoryEvent s = HashChain.Seal(e, prev);
                prev = s.Hash;
                sealedEvents.Add(s);
            }

            return sealedEvents;
        }
    }

    private static UsageRecord Use(long input, long output, long cached, string model = "m") => new(input, output, cached, model);

    // ---- usage ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Usage_is_summed_by_the_utc_day_of_each_attempt_and_by_model()
    {
        IReadOnlyList<HistoryEvent> history = new Builder()
            .Completed(0, Use(100, 20, 10, "a"))    // 23:59 on the 26th
            .Completed(5, Use(200, 30, 0, "a"))     // 00:04 on the 27th
            .Completed(6, Use(50, 5, 0, "b"))
            .Completed(7, null)                      // not a model step
            .Sealed();

        RunProjectionResult p = RunProjection.Of(history);

        Assert.Equal(
            [
                new RunUsageDay(new DateOnly(2026, 10, 26), "a", 1, 0, 100, 20, 10, 0, 0, 0),
                new RunUsageDay(new DateOnly(2026, 10, 27), "a", 1, 0, 200, 30, 0, 0, 0, 0),
                new RunUsageDay(new DateOnly(2026, 10, 27), "b", 1, 0, 50, 5, 0, 0, 0, 0),
            ],
            p.Usage);
        Assert.Equal(history[^1].Sequence, p.LastSequence);
    }

    [Fact]
    public void A_failed_attempt_is_spent_and_what_completed_before_a_failure_counts_as_saved()
    {
        IReadOnlyList<HistoryEvent> history = new Builder()
            .Completed(1, Use(100, 20, 10), id: 1)   // before the failure: saved
            .Failed(2, Use(300, 40, 100), id: 2)     // paid for, then failed
            .Completed(3, Use(300, 40, 100), id: 2)  // after the failure: paid for once, not saved
            .Sealed();

        RunUsageDay day = Assert.Single(RunProjection.Of(history).Usage);

        Assert.Equal((2L, 1L, 700L, 100L, 210L), (day.Steps, day.FailedAttempts, day.InputTokens, day.OutputTokens, day.CachedInputTokens));
        Assert.Equal((100L, 20L, 10L), (day.SavedInputTokens, day.SavedOutputTokens, day.SavedCachedInputTokens));
    }

    [Fact]
    public void Without_a_failure_nothing_is_saved_and_a_failure_without_usage_still_makes_earlier_steps_saved()
    {
        IReadOnlyList<HistoryEvent> clean = new Builder().Completed(1, Use(10, 1, 0)).Sealed();
        IReadOnlyList<HistoryEvent> interrupted = new Builder().Completed(1, Use(10, 1, 0)).Failed(2, null, id: 5).Sealed();

        Assert.Equal(0, Assert.Single(RunProjection.Of(clean).Usage).SavedInputTokens);
        Assert.Equal(10, Assert.Single(RunProjection.Of(interrupted).Usage).SavedInputTokens);
    }

    [Fact]
    public void A_sealed_history_and_a_metadata_only_copy_project_to_the_same_thing()
    {
        IReadOnlyList<HistoryEvent> full = new Builder()
            .Marker(1, RunProjection.ApprovalRequestedMarker + "ap-1")
            .Completed(2, Use(100, 20, 10, "a"))
            .Failed(3, Use(5, 1, 0, "a"))
            .Sealed();

        RunProjectionResult whole = RunProjection.Of(full);
        RunProjectionResult metadata = RunProjection.Of(HistoryRedaction.MetadataOnly(full));

        Assert.Equal(whole.Usage, metadata.Usage);
        Assert.Equal(whole.Pending, metadata.Pending);
        Assert.Equal(whole.LastSequence, metadata.LastSequence);
    }

    // ---- approvals -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_approval_is_pending_from_its_request_marker_until_its_resolved_marker_and_ordered_by_time()
    {
        IReadOnlyList<HistoryEvent> history = new Builder()
            .Marker(1, RunProjection.ApprovalRequestedMarker + "late")
            .Marker(0, RunProjection.ApprovalRequestedMarker + "early")
            .Marker(2, RunProjection.ApprovalRequestedMarker + "done")
            .Marker(3, RunProjection.ApprovalResolvedMarker + "done")
            .Marker(4, "some-patch")
            .Marker(5, "kairos.approval.other:x")
            .Sealed();

        RunProjectionResult p = RunProjection.Of(history);

        Assert.Equal(["early", "late"], p.Pending.Select(a => a.ApprovalId));
        Assert.Equal(T0, p.Pending[0].RequestedAt);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("continued")]
    [InlineData("terminated")]
    public void A_run_that_closed_waits_for_nobody(string how)
    {
        IReadOnlyList<HistoryEvent> history = new Builder().Marker(1, RunProjection.ApprovalRequestedMarker + "ap").Close(2, how).Sealed();

        Assert.Empty(RunProjection.Of(history).Pending);
    }

    [Fact]
    public void A_resolved_marker_for_an_approval_that_was_never_requested_is_harmless_and_a_request_can_be_repeated()
    {
        IReadOnlyList<HistoryEvent> history = new Builder()
            .Marker(1, RunProjection.ApprovalResolvedMarker + "ghost")
            .Marker(2, RunProjection.ApprovalRequestedMarker + "ap")
            .Marker(3, RunProjection.ApprovalRequestedMarker + "ap")
            .Sealed();

        Assert.Equal(["ap"], RunProjection.Of(history).Pending.Select(a => a.ApprovalId));
    }

    [Fact]
    public void An_empty_history_projects_to_nothing()
    {
        RunProjectionResult p = RunProjection.Of([]);

        Assert.Equal((0L, 0, 0), (p.LastSequence, p.Usage.Count, p.Pending.Count));
    }

    // ---- combining and paging -----------------------------------------------------------------------------------------

    [Fact]
    public void Runs_are_combined_by_agent_day_and_model_inside_the_window_and_counted_once_each()
    {
        RunProjectionResult one = RunProjection.Of(new Builder().Completed(0, Use(100, 10, 0, "a")).Completed(5, Use(100, 10, 0, "a")).Sealed());
        RunProjectionResult two = RunProjection.Of(new Builder().Completed(0, Use(7, 1, 0, "a")).Sealed());

        UsageSummary both = RunAggregates.Combine([("refunds", one), ("refunds", two), ("reports", one)], new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27));
        UsageSummary onlyFirstDay = RunAggregates.Combine([("refunds", one), ("refunds", two)], new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 26));
        UsageSummary none = RunAggregates.Combine([("refunds", one)], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2));

        Assert.Equal(
            [("refunds", new DateOnly(2026, 10, 26), 107L), ("refunds", new DateOnly(2026, 10, 27), 100L), ("reports", new DateOnly(2026, 10, 26), 100L), ("reports", new DateOnly(2026, 10, 27), 100L)],
            both.Buckets.Select(b => (b.WorkflowType, b.Day, b.InputTokens)));
        Assert.Equal([new WorkflowTypeRuns("refunds", 2), new WorkflowTypeRuns("reports", 1)], both.Runs);
        Assert.Equal([new WorkflowTypeRuns("refunds", 2)], onlyFirstDay.Runs);
        Assert.Empty(none.Buckets);
        Assert.Empty(none.Runs);
    }

    [Fact]
    public void Pages_follow_the_cursor_with_ties_broken_by_id_and_report_the_total()
    {
        var run = new RunKey(T, N, R);
        PendingApprovalItem[] items =
        [
            new(run, "w", "c", T0), new(run, "w", "a", T0), new(run, "w", "b", T0), new(run, "w", "z", T0.AddMinutes(-1)),
        ];

        PendingApprovalPage first = RunAggregates.Page(items, 2, null);
        PendingApprovalPage second = RunAggregates.Page(items, 2, first.Items[^1].Cursor);
        PendingApprovalPage third = RunAggregates.Page(items, 2, second.Items[^1].Cursor);

        Assert.Equal(["z", "a"], first.Items.Select(i => i.ApprovalId));
        Assert.Equal(["b", "c"], second.Items.Select(i => i.ApprovalId));
        Assert.Empty(third.Items);
        Assert.All(new[] { first, second, third }, p => Assert.Equal(4, p.Total));
    }
}
