namespace Kairos.Testing;

// The event feed: what happened to runs, handed to a sink (a topic, a queue) with a stable id per event. Whatever a store keeps to know how far it got, every event must reach the sink at
// least once and in order within a run, nothing is lost when the sink fails, a second publisher repeats rather than skips, and a scope's feed never carries another scope's events.
public abstract partial class StoreContractTests
{
    private sealed class RecordingSink : IEventSink
    {
        public List<RunEventNotice> Seen { get; } = [];

        public bool Failing { get; set; }

        public ValueTask PublishAsync(IReadOnlyList<RunEventNotice> events, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("the topic is not reachable");
            }

            Seen.AddRange(events);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Every_event_reaches_the_sink_once_in_order_with_a_stable_id_and_the_feed_then_carries_on_from_where_it_got_to()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        StartRunResult start = await client.StartAsync(A, "feed-1", "feed", B8("in"), cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        var sink = new RecordingSink();

        int first = await store.PublishEventsAsync(A, sink, 100, Ct);
        await client.SignalAsync(run, "later", [], Ct);
        int second = await store.PublishEventsAsync(A, sink, 100, Ct);
        int third = await store.PublishEventsAsync(A, sink, 100, Ct);

        IReadOnlyList<HistoryEvent> history = await store.GetHistoryAsync(run, Ct);
        Assert.Equal(history.Count, sink.Seen.Count);
        Assert.Equal(sink.Seen.Count, first + second);
        Assert.Equal(0, third);
        Assert.Equal(history.Select(e => e.Sequence), sink.Seen.Select(e => e.Sequence));
        Assert.Equal(history.Select(e => e.Type), sink.Seen.Select(e => e.Type));
        Assert.All(sink.Seen, e => Assert.Equal(($"{run.RunId:D}:{e.Sequence}", run), (e.EventId, e.Run)));
        Assert.True(first > 0 && second > 0);
    }

    [Fact]
    public async Task A_sink_that_fails_loses_nothing_the_events_are_offered_again()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        StartRunResult start = await client.StartAsync(A, "feed-2", "feed", B8("in"), cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        var sink = new RecordingSink { Failing = true };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PublishEventsAsync(A, sink, 100, Ct));
        Assert.Empty(sink.Seen);

        sink.Failing = false;
        int accepted = await store.PublishEventsAsync(A, sink, 100, Ct);

        Assert.Equal((await store.GetHistoryAsync(run, Ct)).Count, accepted);
        Assert.Equal(accepted, sink.Seen.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task The_feed_of_a_scope_carries_only_its_own_runs_and_a_budget_limits_how_many_runs_one_call_reads()
    {
        IKairosStore store = await CreateStoreAsync();
        var client = new KairosClient(store, Time);
        RunKey[] mine = new RunKey[3];
        for (int i = 0; i < mine.Length; i++)
        {
            StartRunResult s = await client.StartAsync(A, "feed-a-" + i, "feed", B8("in"), cancellationToken: Ct);
            mine[i] = new RunKey(A.TenantId, A.NamespaceId, s.RunId);
        }

        StartRunResult theirs = await client.StartAsync(B, "feed-b", "feed", B8("in"), cancellationToken: Ct);
        var sink = new RecordingSink();

        await store.PublishEventsAsync(A, sink, 2, Ct);
        Assert.Equal(2, sink.Seen.Select(e => e.Run).Distinct().Count());
        await store.PublishEventsAsync(A, sink, 2, Ct);

        Assert.Equal(mine.Select(r => r.RunId).Order(), sink.Seen.Select(e => e.Run.RunId).Distinct().Order());
        Assert.DoesNotContain(sink.Seen, e => e.Run.RunId == theirs.RunId);
        var theirSink = new RecordingSink();
        await store.PublishEventsAsync(B, theirSink, 10, Ct);
        Assert.Equal(theirs.RunId, Assert.Single(theirSink.Seen.Select(e => e.Run.RunId).Distinct()));
    }
}
