namespace Kairos.Conformance;

/// <summary>What every <see cref="IEventSink"/> must do: deliver every event with a stable id, in order per run, at least once.</summary>
public abstract class EventSinkConformance
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    protected abstract IEventSink CreateSink();

    /// <summary>Everything the receiving side has seen so far (duplicates included).</summary>
    protected abstract Task<IReadOnlyList<RunEventNotice>> ReceivedAsync();

    /// <summary>Makes the next <paramref name="count"/> deliveries fail transiently. False when the adapter has no retry to exercise.</summary>
    protected virtual Task<bool> FailNextDeliveriesAsync(int count) => Task.FromResult(false);

    protected static RunEventNotice Notice(Guid run, long seq, HistoryEventType type = HistoryEventType.ActivityCompleted) =>
        new($"{run:N}:{seq}", new RunKey(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), run), type, seq, At.AddSeconds(seq));

    [Fact]
    public async Task Published_events_arrive_with_their_stable_ids_and_all_their_fields()
    {
        IEventSink sink = CreateSink();
        var run = Guid.NewGuid();
        RunEventNotice[] batch = [Notice(run, 1, HistoryEventType.WorkflowStarted), Notice(run, 2), Notice(run, 3, HistoryEventType.WorkflowCompleted)];

        await sink.PublishAsync(batch, CancellationToken.None);

        IReadOnlyList<RunEventNotice> received = await ReceivedAsync();
        Assert.Equal(batch, received.Distinct().ToArray());
    }

    [Fact]
    public async Task Events_of_one_run_keep_their_order_across_batches()
    {
        IEventSink sink = CreateSink();
        var run = Guid.NewGuid();
        var other = Guid.NewGuid();

        await sink.PublishAsync([Notice(run, 1), Notice(other, 1)], CancellationToken.None);
        await sink.PublishAsync([Notice(run, 2), Notice(other, 2), Notice(run, 3)], CancellationToken.None);

        long[] seen = [.. (await ReceivedAsync()).Distinct().Where(e => e.Run.RunId == run).Select(e => e.Sequence)];
        Assert.Equal([1L, 2, 3], seen);
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op()
    {
        IEventSink sink = CreateSink();

        await sink.PublishAsync([], CancellationToken.None);

        Assert.Empty(await ReceivedAsync());
    }

    [Fact]
    public async Task Event_ids_are_stable_so_redelivery_can_be_deduplicated()
    {
        IEventSink sink = CreateSink();
        var run = Guid.NewGuid();
        RunEventNotice notice = Notice(run, 5);

        await sink.PublishAsync([notice], CancellationToken.None);
        await sink.PublishAsync([notice], CancellationToken.None); // the caller retried; at-least-once allows the duplicate

        IReadOnlyList<RunEventNotice> received = await ReceivedAsync();
        Assert.All(received, e => Assert.Equal(notice.EventId, e.EventId));
        Assert.Single(received.Select(e => e.EventId).Distinct());
    }

    [Fact]
    public async Task Transient_delivery_failures_are_retried_until_the_event_arrives()
    {
        IEventSink sink = CreateSink();
        if (!await FailNextDeliveriesAsync(2))
        {
            return;
        }

        var run = Guid.NewGuid();
        await sink.PublishAsync([Notice(run, 1)], CancellationToken.None);

        Assert.Contains(await ReceivedAsync(), e => e.EventId == $"{run:N}:1");
    }
}

/// <summary>What every <see cref="IEventSource"/> must do: at-least-once, acknowledged messages stay gone, unacknowledged ones come back.</summary>
public abstract class EventSourceConformance
{
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));

    protected abstract IEventSource CreateSource();

    protected abstract Task EnqueueAsync(IEventSource source, InboundEvent inbound);

    /// <summary>Moves time forward past the source's redelivery timeout.</summary>
    protected abstract Task PassRedeliveryTimeoutAsync(IEventSource source);

    protected static StartWorkflowEvent Start(string id) => new(id, A, "order-" + id, "wf", [1, 2, 3]);

    [Fact]
    public async Task An_empty_source_returns_null()
    {
        Assert.Null(await CreateSource().ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enqueued_messages_arrive_once_each_in_order_until_acknowledged()
    {
        IEventSource source = CreateSource();
        await EnqueueAsync(source, Start("e1"));
        await EnqueueAsync(source, new SignalRunEvent("e2", A, Guid.NewGuid(), "go", [9]));

        IInboundMessage first = (await source.ReceiveAsync(CancellationToken.None))!;
        IInboundMessage second = (await source.ReceiveAsync(CancellationToken.None))!;

        Assert.Equal(["e1", "e2"], [first.Payload.EventId, second.Payload.EventId]);
        Assert.Null(await source.ReceiveAsync(CancellationToken.None)); // both in flight, none visible
        Assert.IsType<StartWorkflowEvent>(first.Payload);
        Assert.Equal("go", Assert.IsType<SignalRunEvent>(second.Payload).SignalName);
    }

    [Fact]
    public async Task An_acknowledged_message_never_returns()
    {
        IEventSource source = CreateSource();
        await EnqueueAsync(source, Start("e1"));
        IInboundMessage message = (await source.ReceiveAsync(CancellationToken.None))!;

        await message.AcknowledgeAsync(CancellationToken.None);
        await PassRedeliveryTimeoutAsync(source);

        Assert.Null(await source.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unacknowledged_message_is_redelivered_after_the_timeout()
    {
        IEventSource source = CreateSource();
        await EnqueueAsync(source, Start("e1"));
        IInboundMessage first = (await source.ReceiveAsync(CancellationToken.None))!;

        Assert.Null(await source.ReceiveAsync(CancellationToken.None)); // still leased
        await PassRedeliveryTimeoutAsync(source);
        IInboundMessage? again = await source.ReceiveAsync(CancellationToken.None);

        Assert.Equal(first.Payload.EventId, again?.Payload.EventId);
    }

    [Fact]
    public async Task An_abandoned_message_is_redelivered_without_waiting()
    {
        IEventSource source = CreateSource();
        await EnqueueAsync(source, Start("e1"));
        IInboundMessage first = (await source.ReceiveAsync(CancellationToken.None))!;

        await first.AbandonAsync(CancellationToken.None);

        Assert.Equal("e1", (await source.ReceiveAsync(CancellationToken.None))?.Payload.EventId);
    }

    [Fact]
    public async Task A_stale_delivery_cannot_acknowledge_a_message_that_was_redelivered()
    {
        IEventSource source = CreateSource();
        await EnqueueAsync(source, Start("e1"));
        IInboundMessage slow = (await source.ReceiveAsync(CancellationToken.None))!;
        await PassRedeliveryTimeoutAsync(source);
        IInboundMessage fresh = (await source.ReceiveAsync(CancellationToken.None))!;

        await slow.AcknowledgeAsync(CancellationToken.None); // the first consumer was too slow; its ack must not delete the live delivery
        await PassRedeliveryTimeoutAsync(source);

        Assert.Equal("e1", (await source.ReceiveAsync(CancellationToken.None))?.Payload.EventId);
        await fresh.AcknowledgeAsync(CancellationToken.None);
    }
}
