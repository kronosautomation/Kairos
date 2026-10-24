using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing;

namespace Kairos.SelfHosted.Tests;

public sealed class CloudEventsTests
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b1"));
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, 123, TimeSpan.Zero);

    private static RunEventNotice Notice(HistoryEventType type = HistoryEventType.ActivityCompleted) =>
        new("evt-1", new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid()), type, 7, At);

    [Fact]
    public void A_run_event_is_a_valid_cloudevent_with_the_required_attributes()
    {
        RunEventNotice n = Notice();

        using JsonDocument doc = JsonDocument.Parse(CloudEventsFormat.Serialize(n));
        JsonElement root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("specversion").GetString());
        Assert.Equal("evt-1", root.GetProperty("id").GetString());
        Assert.Equal("com.kronosautomation.kairos.run.event.ActivityCompleted", root.GetProperty("type").GetString());
        Assert.Equal(n.Run.RunId.ToString("D"), root.GetProperty("subject").GetString());
        Assert.StartsWith("/kairos/", root.GetProperty("source").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", root.GetProperty("time").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_events_round_trip_alone_and_in_batches()
    {
        RunEventNotice[] batch = [Notice(HistoryEventType.WorkflowStarted), Notice(), Notice(HistoryEventType.WorkflowCompleted)];

        Assert.Equal(batch[1], Assert.Single(CloudEventsFormat.ParseRunEvents(CloudEventsFormat.Serialize(batch[1]))));
        Assert.Equal(batch, CloudEventsFormat.ParseRunEvents(CloudEventsFormat.SerializeBatch(batch)));
    }

    [Fact]
    public void Start_and_signal_events_round_trip_for_their_scope()
    {
        var start = new StartWorkflowEvent("s1", A, "order-1", "OrderWorkflow", [1, 2, 3]);
        var signal = new SignalRunEvent("g1", A, Guid.NewGuid(), "approve", [9, 9]);

        InboundEvent s = CloudEventsFormat.ParseInbound(CloudEventsFormat.SerializeStart(start), A);
        InboundEvent g = CloudEventsFormat.ParseInbound(CloudEventsFormat.SerializeSignal(signal), A);

        StartWorkflowEvent parsedStart = Assert.IsType<StartWorkflowEvent>(s);
        Assert.Equal(("s1", "order-1", "OrderWorkflow"), (parsedStart.EventId, parsedStart.WorkflowId, parsedStart.WorkflowType));
        Assert.Equal(start.Input, parsedStart.Input);
        SignalRunEvent parsedSignal = Assert.IsType<SignalRunEvent>(g);
        Assert.Equal((signal.RunId, "approve", "g1"), (parsedSignal.RunId, parsedSignal.SignalName, parsedSignal.EventId));
        Assert.Equal(signal.Payload, parsedSignal.Payload);
    }

    [Fact]
    public void An_event_naming_another_scope_is_rejected_for_both_event_types()
    {
        byte[] start = CloudEventsFormat.SerializeStart(new StartWorkflowEvent("s1", A, "w", "t", []));
        byte[] signal = CloudEventsFormat.SerializeSignal(new SignalRunEvent("g1", A, Guid.NewGuid(), "x", []));

        FormatException e1 = Assert.Throws<FormatException>(() => CloudEventsFormat.ParseInbound(start, B));
        Assert.Throws<FormatException>(() => CloudEventsFormat.ParseInbound(signal, B));
        Assert.DoesNotContain(A.TenantId.ToString(), e1.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"specversion":"0.3","id":"1","type":"com.kronosautomation.kairos.workflow.start","data":{}}""")]
    [InlineData("""{"specversion":"1.0","id":"1","type":"com.example.other","data":{"tenantId":"aaaaaaaa-0000-0000-0000-000000000001","namespaceId":"aaaaaaaa-0000-0000-0000-0000000000a1"}}""")]
    [InlineData("""{"specversion":"1.0","id":"","type":"com.kronosautomation.kairos.workflow.start","data":{}}""")]
    [InlineData("""{"specversion":"1.0","specversion":"1.0","id":"1","type":"x","data":{}}""")]
    [InlineData("""{"specversion":"1.0","id":"1","type":"com.kronosautomation.kairos.workflow.start"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""null""")]
    [InlineData("")]
    public void Malformed_or_unsupported_events_are_rejected_with_one_exception_type(string json)
    {
        Assert.Throws<FormatException>(() => CloudEventsFormat.ParseInbound(Encoding.UTF8.GetBytes(json), A));
    }

    [Fact]
    public void Duplicate_attributes_inside_data_are_rejected_because_two_readers_could_disagree()
    {
        string json = "{\"specversion\":\"1.0\",\"id\":\"1\",\"type\":\"" + CloudEventsFormat.StartWorkflowType + "\",\"data\":{\"tenantId\":\"" + A.TenantId
            + "\",\"tenantId\":\"" + B.TenantId + "\",\"namespaceId\":\"" + A.NamespaceId + "\",\"workflowId\":\"w\",\"workflowType\":\"t\",\"input\":\"\"}}";

        Assert.Throws<FormatException>(() => CloudEventsFormat.ParseInbound(Encoding.UTF8.GetBytes(json), A));
    }

    [Fact]
    public void Oversized_and_deeply_nested_events_are_rejected()
    {
        byte[] big = CloudEventsFormat.SerializeStart(new StartWorkflowEvent("s1", A, "w", "t", new byte[4096]));
        string deep = new string('[', 200) + new string(']', 200);

        Assert.Throws<FormatException>(() => CloudEventsFormat.ParseInbound(big, A, maxBytes: 1024));
        Assert.Throws<FormatException>(() => CloudEventsFormat.ParseInbound(Encoding.UTF8.GetBytes(deep), A));
    }

    [Fact]
    public void Mutated_events_either_parse_or_fail_with_FormatException_and_nothing_else()
    {
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("KAIROS_FUZZ_ITER"), out int n) ? n : 3000;
        byte[][] seeds =
        [
            CloudEventsFormat.SerializeStart(new StartWorkflowEvent("s1", A, "order-1", "OrderWorkflow", [1, 2, 3])),
            CloudEventsFormat.SerializeSignal(new SignalRunEvent("g1", A, Guid.NewGuid(), "approve", [9])),
            CloudEventsFormat.SerializeBatch([Notice(), Notice(HistoryEventType.WorkflowCompleted)]),
        ];

        for (int seed = 0; seed < iterations; seed++)
        {
            var rng = new SimRandom((ulong)seed);
            byte[] input = [.. seeds[rng.Next(seeds.Length)]];
            for (int m = rng.Between(1, 4); m > 0 && input.Length > 0; m--)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        input[rng.Next(input.Length)] = (byte)rng.Next(256);
                        break;
                    case 1:
                        input = input[..rng.Next(input.Length)];
                        break;
                    default:
                        input = [.. input, .. rng.Bytes(rng.Between(1, 8))];
                        break;
                }
            }

            AssertOnlyFormatException(seed, "ParseInbound", () => CloudEventsFormat.ParseInbound(input, A));
            AssertOnlyFormatException(seed, "ParseRunEvents", () => CloudEventsFormat.ParseRunEvents(input));
        }
    }

    private static void AssertOnlyFormatException(int seed, string what, Action parse)
    {
        try
        {
            parse();
        }
        catch (FormatException)
        {
            // the one defined failure
        }
        catch (Exception ex)
        {
            Assert.Fail($"seed {seed}: {what} threw {ex.GetType().Name}");
        }
    }

    [Fact]
    public async Task The_processor_applies_each_message_once_even_when_the_bus_redelivers()
    {
        var time = new VirtualTimeProvider();
        var client = new KairosClient(new InMemoryKairosStore(), time);
        var inbox = new InMemoryInbox(time, TimeSpan.FromSeconds(30));
        var processor = new InboundEventProcessor(inbox, client);
        var start = new StartWorkflowEvent("s1", A, "order-1", "OrderWorkflow", [1]);
        inbox.Enqueue(start);
        inbox.Enqueue(start); // the bus delivered the same request twice

        int done = await processor.ProcessAsync(10, CancellationToken.None);

        Assert.Equal(2, done);
        Assert.Equal(0, inbox.Pending);
        StartRunResult again = await client.StartAsync(A, "order-1", "OrderWorkflow", [1]);
        Assert.False(again.Started); // exactly one run exists for the workflow id
    }

    [Fact]
    public async Task A_signal_to_a_missing_run_is_acknowledged_so_it_does_not_loop_forever()
    {
        var time = new VirtualTimeProvider();
        var inbox = new InMemoryInbox(time, TimeSpan.FromSeconds(30));
        var processor = new InboundEventProcessor(inbox, new KairosClient(new InMemoryKairosStore(), time));
        inbox.Enqueue(new SignalRunEvent("g1", A, Guid.NewGuid(), "x", []));

        Assert.Equal(1, await processor.ProcessAsync(10, CancellationToken.None));
        Assert.Equal(0, inbox.Pending);
    }

    [Fact]
    public async Task A_message_that_cannot_be_applied_is_abandoned_for_redelivery()
    {
        var time = new VirtualTimeProvider();
        var inbox = new InMemoryInbox(time, TimeSpan.FromSeconds(30));
        var processor = new InboundEventProcessor(inbox, new KairosClient(new InMemoryKairosStore(), time));
        inbox.Enqueue(new StartWorkflowEvent("s1", A, string.Empty, "t", [])); // an empty workflow id is refused by the client

        Assert.Equal(0, await processor.ProcessAsync(10, CancellationToken.None));
        Assert.Equal(1, inbox.Pending);
        Assert.NotNull(await inbox.ReceiveAsync(CancellationToken.None)); // visible again without waiting
    }

    [Fact]
    public void A_cloudevents_webhook_body_is_signed_like_the_native_one()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] body = CloudEventsFormat.SerializeBatch([Notice()]);

        string signature = WebhookEventSink.Sign(secret, "1700000000", body);

        Assert.True(WebhookEventSink.Verify(secret, "1700000000", signature, body, DateTimeOffset.FromUnixTimeSeconds(1700000000), TimeSpan.FromMinutes(1)));
    }
}
