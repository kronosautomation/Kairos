namespace Kairos.Cloud.SelfHosted;

/// <summary>Collects published events in order. For tests, embedded fan-out and local development.</summary>
public sealed class InMemoryEventSink : IEventSink
{
    private readonly List<RunEventNotice> _events = [];

    public IReadOnlyList<RunEventNotice> Published
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask PublishAsync(IReadOnlyList<RunEventNotice> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_events)
        {
            _events.AddRange(events);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An at-least-once inbound queue: a received message is invisible for the visibility timeout, then comes back unless acknowledged.
/// Delivery order is arrival order among visible messages.
/// </summary>
public sealed class InMemoryInbox(TimeProvider time, TimeSpan visibilityTimeout) : IEventSource
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Enqueue(InboundEvent inbound)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        lock (_gate)
        {
            _entries.Add(new Entry(inbound));
        }
    }

    public ValueTask<IInboundMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            foreach (Entry entry in _entries)
            {
                if (entry.InvisibleUntil <= now)
                {
                    entry.InvisibleUntil = now + visibilityTimeout;
                    entry.Deliveries++;
                    return ValueTask.FromResult<IInboundMessage?>(new Message(this, entry, entry.Deliveries));
                }
            }
        }

        return ValueTask.FromResult<IInboundMessage?>(null);
    }

    private sealed class Entry(InboundEvent inbound)
    {
        public InboundEvent Inbound { get; } = inbound;

        public DateTimeOffset InvisibleUntil { get; set; } = DateTimeOffset.MinValue;

        public int Deliveries { get; set; }
    }

    private sealed class Message(InMemoryInbox queue, Entry entry, int delivery) : IInboundMessage
    {
        public InboundEvent Payload => entry.Inbound;

        public ValueTask AcknowledgeAsync(CancellationToken cancellationToken)
        {
            lock (queue._gate)
            {
                if (entry.Deliveries == delivery) // a stale delivery cannot remove a message that was redelivered since
                {
                    queue._entries.Remove(entry);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonAsync(CancellationToken cancellationToken)
        {
            lock (queue._gate)
            {
                if (entry.Deliveries == delivery)
                {
                    entry.InvisibleUntil = DateTimeOffset.MinValue;
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
