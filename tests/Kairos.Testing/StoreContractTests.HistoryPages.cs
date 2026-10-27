namespace Kairos.Testing;

/// <summary>Reading a slice of a run's history, and waiting for it to grow: the same for every store.</summary>
public abstract partial class StoreContractTests
{
    /// <summary>A run with a history of a known length: it started, did <paramref name="steps"/> activities, and finished.</summary>
    private async Task<(IKairosStore Store, KairosClient Client, RunKey Key, IReadOnlyList<HistoryEvent> Whole)> RunWithHistoryAsync(int steps)
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "pages-" + steps, "wf", [], cancellationToken: Ct);
        await DriveAsync(store, A, Workflows("default", async (ctx, input) =>
        {
            for (int i = 0; i < steps; i++)
            {
                await ctx.ExecuteActivityAsync("echo", input);
            }

            return input;
        }), Activities());
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        return (store, client, key, await client.GetHistoryAsync(key, Ct));
    }

    [Fact]
    public async Task A_page_after_a_sequence_holds_the_events_after_it_and_says_where_the_history_ends()
    {
        (IKairosStore store, KairosClient client, RunKey key, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(5);

        HistoryPage page = await store.GetHistoryAfterAsync(key, 4, 3, Ct);

        Assert.Equal(whole.Skip(4).Take(3).Select(e => e.Hash), page.Events.Select(e => e.Hash));
        Assert.Equal(new long[] { 5, 6, 7 }, page.Events.Select(e => e.Sequence));
        Assert.Equal(whole.Count, page.LastSequence);
        HistoryPage verified = await client.GetHistoryAfterAsync(key, 4, 3, Ct);
        Assert.Equal(page.Events.Select(e => e.Hash), verified.Events.Select(e => e.Hash));
    }

    [Fact]
    public async Task Pages_one_after_another_are_the_whole_history()
    {
        (IKairosStore store, _, RunKey key, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(7);

        var joined = new List<HistoryEvent>();
        long after = 0;
        while (true)
        {
            HistoryPage page = await store.GetHistoryAfterAsync(key, after, 4, Ct);
            if (page.Events.Count == 0)
            {
                break;
            }

            joined.AddRange(page.Events);
            after = page.Events[^1].Sequence;
        }

        Assert.Equal(whole.Select(e => e.Hash), joined.Select(e => e.Hash));
        AssertChainIntact(joined);
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_and_still_says_where_the_history_ends()
    {
        (IKairosStore store, _, RunKey key, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(2);

        HistoryPage page = await store.GetHistoryAfterAsync(key, whole.Count + 50, 10, Ct);

        Assert.Empty(page.Events);
        Assert.Equal(whole.Count, page.LastSequence);
    }

    [Fact]
    public async Task The_tail_is_the_last_events_in_order_and_all_of_them_when_the_history_is_shorter()
    {
        (IKairosStore store, _, RunKey key, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(6);

        HistoryPage tail = await store.GetHistoryTailAsync(key, 3, Ct);
        HistoryPage everything = await store.GetHistoryTailAsync(key, 500, Ct);

        Assert.Equal(whole.TakeLast(3).Select(e => e.Hash), tail.Events.Select(e => e.Hash));
        Assert.Equal(whole.Count, tail.LastSequence);
        Assert.Equal(whole.Select(e => e.Hash), everything.Events.Select(e => e.Hash));
    }

    [Fact]
    public async Task A_run_that_does_not_exist_has_an_empty_page_and_no_end()
    {
        (IKairosStore store, _, _, _) = await RunWithHistoryAsync(1);
        var nobody = new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid());

        Assert.Equal((0, 0L), ((await store.GetHistoryAfterAsync(nobody, 0, 10, Ct)).Events.Count, (await store.GetHistoryTailAsync(nobody, 10, Ct)).LastSequence));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, HistoryPage.MaxLimit + 1)]
    public async Task A_page_asked_for_with_impossible_arguments_is_refused(long after, int limit)
    {
        (IKairosStore store, _, RunKey key, _) = await RunWithHistoryAsync(1);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(async () => await store.GetHistoryAfterAsync(key, after, limit, Ct));
    }

    [Fact]
    public async Task A_page_checks_its_own_hashes_and_a_changed_event_is_found()
    {
        (_, _, _, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(4);
        HistoryEvent[] page = [.. whole.Skip(2).Take(4)];
        HashChain.VerifyPage(page);

        HistoryEvent[] changed = [.. page];
        changed[1] = changed[1] with { Timestamp = changed[1].Timestamp.AddSeconds(1) };
        HistoryEvent[] gap = [page[0], page[2]];
        HistoryEvent[] reordered = [page[1], page[0], page[2]];

        Assert.Throws<HistoryIntegrityException>(() => HashChain.VerifyPage(changed));
        Assert.Throws<HistoryIntegrityException>(() => HashChain.VerifyPage(gap));
        Assert.Throws<HistoryIntegrityException>(() => HashChain.VerifyPage(reordered));
    }

    [Fact]
    public async Task Waiting_returns_at_once_when_the_run_is_already_beyond_the_sequence()
    {
        (IKairosStore store, _, RunKey key, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(2);

        Assert.True(await store.WaitForHistoryAsync(key, whole.Count - 1, TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task Waiting_gives_up_after_the_time_it_was_given_when_nothing_is_written()
    {
        (IKairosStore store, _, RunKey key, IReadOnlyList<HistoryEvent> whole) = await RunWithHistoryAsync(2);

        Assert.False(await store.WaitForHistoryAsync(key, whole.Count, TimeSpan.FromMilliseconds(120), Ct));
    }

    [Fact]
    public async Task Waiting_wakes_when_something_is_written_and_does_not_wait_out_its_time()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "wake-me", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        long length = (await store.GetHistoryTailAsync(key, 1, Ct)).LastSequence;

        ValueTask<bool> waiting = store.WaitForHistoryAsync(key, length, TimeSpan.FromSeconds(20), Ct);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        Assert.True(await client.SignalAsync(key, "ping", B8("x"), Ct));

        DateTime began = DateTime.UtcNow;
        Assert.True(await waiting);
        Assert.True(DateTime.UtcNow - began < TimeSpan.FromSeconds(10), "the wait should end when the signal is written, not when its time is up");
    }

    [Fact]
    public async Task Waiting_for_a_run_that_does_not_exist_ends_false()
    {
        (IKairosStore store, _) = await NewAsync();

        Assert.False(await store.WaitForHistoryAsync(new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid()), 0, TimeSpan.FromMilliseconds(60), Ct));
    }

    [Fact]
    public async Task Many_waiters_for_one_run_are_all_woken_by_one_write()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "many-wake", "wf", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        long length = (await store.GetHistoryTailAsync(key, 1, Ct)).LastSequence;

        Task<bool>[] waiters = [.. Enumerable.Range(0, 8).Select(_ => store.WaitForHistoryAsync(key, length, TimeSpan.FromSeconds(20), Ct).AsTask())];
        await Task.Delay(100);
        Assert.True(await client.SignalAsync(key, "ping", B8("x"), Ct));

        Assert.All(await Task.WhenAll(waiters), woken => Assert.True(woken));
    }
}
