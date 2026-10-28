using System.Diagnostics;
using System.Text;
using Kairos.Agents;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing.Agents;
using Kairos.Worker;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// What a conversation costs in history (every step carries the session) and what it costs to wait for an answer, measured with tool output of a realistic size. The tests print their figures
/// (<c>[measure]</c> lines) and assert only the shape; the review notes quote the figures.
/// </summary>
public sealed class MeasurementTests
{
    /// <summary>An order record of about <paramref name="kilobytes"/> KB, as JSON: what a real order or CRM lookup returns.</summary>
    internal static string Record(int kilobytes, int seed)
    {
        var sb = new StringBuilder("{\"orderId\":\"A-").Append(seed).Append("\",\"lines\":[");
        int i = 0;
        while (sb.Length < kilobytes * 1024)
        {
            sb.Append(i++ == 0 ? "" : ",").Append("{\"sku\":\"SKU-").Append(seed * 1000 + i).Append("\",\"name\":\"Item ").Append(i).Append(" of the order, long enough to look real\",\"qty\":").Append(i % 5 + 1).Append(",\"price\":").Append(9.99m + i).Append(",\"status\":\"shipped\"}");
        }

        return sb.Append("]}").ToString();
    }

    /// <summary>The model of the measured support agent: per user message two lookups (4 KB and 2 KB of tool output), then an answer of a few sentences.</summary>
    internal static ScriptedChatClient SupportModel() => new(messages =>
    {
        int users = messages.Count(m => m.Role == ChatRole.User);
        int resultsThisTurn = 0;
        for (int i = messages.Count - 1; i >= 0 && messages[i].Role != ChatRole.User; i--)
        {
            resultsThisTurn += messages[i].Contents.OfType<FunctionResultContent>().Count();
        }

        return resultsThisTurn switch
        {
            0 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{users}-1", "lookup_order", AfHarness.Args($$"""{"orderId":"A-{{users}}"}"""))]),
            1 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{users}-2", "lookup_customer", AfHarness.Args($$"""{"customerId":"C-{{users}}"}"""))]),
            _ => new ChatMessage(ChatRole.Assistant, "Your order is on its way. " + string.Concat(Enumerable.Repeat("Thank you for your patience and for choosing us. ", 6))),
        };
    });

    internal static ChatClientAgent SupportAgent(IChatClient model, Action<ChatClientAgentOptions>? more = null)
    {
        AIFunction order = AIFunctionFactory.Create((string orderId) => Record(4, orderId.Length), "lookup_order", "Finds an order.");
        AIFunction customer = AIFunctionFactory.Create((string customerId) => Record(2, customerId.Length), "lookup_customer", "Finds a customer.");
        var options = new ChatClientAgentOptions { Name = "support", Description = "support agent", ChatOptions = new ChatOptions { Instructions = "Be brief.", Tools = [order, customer] } };
        more?.Invoke(options);
        return new ChatClientAgent(model, options);
    }

    private static long Size(HistoryEvent e) => e switch
    {
        ActivityScheduled s => s.Input.Length,
        ActivityCompleted c => c.Result.Length,
        SignalReceived s => s.Payload.Length,
        WorkflowStarted w => w.Input.Length,
        WorkflowContinued c => c.Input.Length,
        _ => 0,
    };

    /// <summary>Runs <paramref name="messages"/> messages through one conversation; returns, per message, the bytes recorded for it, the events of the run it is in, and the size of the session after it.</summary>
    internal static async Task<(List<(int Message, long SessionBytes, long TurnBytes, int Events)> Rows, long Total)> ConversationAsync(AfHarness h, int messages)
    {
        var rows = new List<(int, long, long, int)>();
        long previous = 0;
        for (int message = 1; message <= messages; message++)
        {
            await h.AskAsync("support", "long", $"question {message} about my order");
            IReadOnlyList<HistoryEvent> history = await FullHistoryAsync(h);
            long total = history.Sum(Size);
            rows.Add((message, SessionBytesAfter(history), total - previous, history.Count));
            previous = total;
        }

        return (rows, previous);
    }

    /// <summary>The current run's history, and the earlier runs' of the conversation, so that totals keep counting after a hand-over.</summary>
    private static async Task<IReadOnlyList<HistoryEvent>> FullHistoryAsync(AfHarness h) => await h.HistoryAsync(await h.RunOfAsync("support", "long"));

    internal static long SessionBytesAfter(IReadOnlyList<HistoryEvent> history) =>
        history.OfType<ActivityCompleted>().Select(c => SessionBytesOf(c.Result)).Where(n => n > 0).LastOrDefault();

    /// <summary>The size of the session a step result carries or stands for; set by the snapshot work (0 for a result that has none).</summary>
    internal static long SessionBytesOf(byte[] result)
    {
        AfStepResult r = AfCodec.Read(result, AfJson.Default.AfStepResult);
        return r.SessionBytes ?? r.Snapshot?.GetRawText().Length ?? 0;
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public async Task What_a_realistic_conversation_costs_in_history(int messages)
    {
        ScriptedChatClient model = SupportModel();
        var h = new AfHarness(af => af.Configure(o => o.MaxSnapshotBytes = 16 * 1024 * 1024).AddAgent(SupportAgent(model), r => r.ReadOnly("lookup_order", "lookup_customer")));

        (List<(int Message, long SessionBytes, long TurnBytes, int Events)> rows, long total) = await ConversationAsync(h, messages);

        Console.Error.WriteLine($"[measure] {messages} messages: message | session bytes | bytes recorded for this message | events in run");
        foreach ((int m, long s, long t, int e) in rows.Where(r => r.Message is 1 or 2 or 5 or 10 or 20 or 50 or 100))
        {
            Console.Error.WriteLine($"[measure] {m,7} | {s,13:N0} | {t,31:N0} | {e,14}");
        }

        Console.Error.WriteLine($"[measure] {messages} messages: total recorded {total:N0} bytes ({total / messages:N0} per message on average); session after {messages}: {rows[^1].SessionBytes:N0} bytes");
        Assert.True(rows[^1].SessionBytes > rows[0].SessionBytes * 5, "the session should grow with the conversation");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public async Task What_a_conversation_with_compaction_costs_in_history(int messages)
    {
        ScriptedChatClient model = SupportModel();
        var h = new AfHarness(af => af.Configure(o => o.MaxSnapshotBytes = 16 * 1024 * 1024).AddAgent(SupportAgent(model, o => o.ChatHistoryProvider = Kairos.Samples.AgentFramework.ConversationSample.LongConversationHistory()), r => r.ReadOnly("lookup_order", "lookup_customer")));

        (List<(int Message, long SessionBytes, long TurnBytes, int Events)> rows, long total) = await ConversationAsync(h, messages);

        Console.Error.WriteLine($"[measure] WITH COMPACTION {messages} messages: message | session bytes | bytes recorded for this message | events in run");
        foreach ((int m, long s, long t, int e) in rows.Where(r => r.Message is 1 or 2 or 5 or 10 or 12 or 15 or 20 or 50 or 100))
        {
            Console.Error.WriteLine($"[measure] {m,7} | {s,13:N0} | {t,31:N0} | {e,14}");
        }

        Console.Error.WriteLine($"[measure] WITH COMPACTION {messages} messages: total recorded {total:N0} bytes ({total / messages:N0} per message on average); session after {messages}: {rows[^1].SessionBytes:N0} bytes");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task What_the_interval_between_whole_sessions_costs_in_history(int every)
    {
        var h = new AfHarness(af => af.Configure(o => { o.MaxSnapshotBytes = 16 * 1024 * 1024; o.FullSnapshotEvery = every; }).AddAgent(SupportAgent(SupportModel()), r => r.ReadOnly("lookup_order", "lookup_customer")));

        (List<(int Message, long SessionBytes, long TurnBytes, int Events)> rows, long total) = await ConversationAsync(h, 100);

        Console.Error.WriteLine($"[measure] FullSnapshotEvery={every}, 100 messages: total recorded {total:N0} bytes ({total / 100:N0} per message); message 50 recorded {rows[49].TurnBytes:N0}");
    }

    [Fact]
    public async Task What_it_costs_to_wait_for_an_answer_in_a_conversation_of_a_hundred_messages()
    {
        CountingStore? counting = null;
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var h = new AfHarness(
            af => af.Configure(o => o.MaxSnapshotBytes = 16 * 1024 * 1024).AddAgent(SupportAgent(SupportModel()), r => r.ReadOnly("lookup_order", "lookup_customer")),
            protector: protector,
            wrapStore: store => counting = new CountingStore(store));
        await ConversationAsync(h, 100);
        DurableAIAgent support = h.Agents.Get("support");
        DurableAgentSession session = support.Session("long");

        // 1. A look while the agent has not answered yet (the message is sent, no worker has taken it): what every wait pays on every look.
        DurableTurn pending = await support.SendAsync([new ChatMessage(ChatRole.User, "and another")], session);
        counting!.Reset();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            Assert.Null(await support.TryReadAsync(pending));
        }

        clock.Stop();
        (long calls, long events, long bytes, double ms) waiting = (counting.Calls, counting.Events, counting.Bytes, clock.Elapsed.TotalMilliseconds);
        await h.DriveAsync();
        Assert.NotNull(await support.TryReadAsync(pending));

        // 2. The first look after the answer is there, for five more messages (a look is made once per message, so each is a new message).
        long callsFound = 0, eventsFound = 0, bytesFound = 0;
        double msFound = 0;
        for (int i = 0; i < 5; i++)
        {
            DurableTurn turn = await support.SendAsync([new ChatMessage(ChatRole.User, $"question {i}")], session);
            await h.DriveAsync();
            counting.Reset();
            clock.Restart();
            Assert.NotNull(await support.TryReadAsync(turn));
            clock.Stop();
            callsFound += counting.Calls;
            eventsFound += counting.Events;
            bytesFound += counting.Bytes;
            msFound += clock.Elapsed.TotalMilliseconds;
        }

        Console.Error.WriteLine("[measure] one look at a 100-message conversation, sealed with client-side encryption, per look:");
        Console.Error.WriteLine($"[measure]   answer is there : {msFound / 5:N1} ms, {callsFound / 5.0:N1} history reads, {eventsFound / 5:N0} events, {bytesFound / 5:N0} bytes read");
        Console.Error.WriteLine($"[measure]   still waiting   : {waiting.ms / 20:N1} ms, {waiting.calls / 20.0:N1} history reads, {waiting.events / 20:N0} events, {waiting.bytes / 20:N0} bytes read");
    }

    [Fact]
    public async Task Many_conversations_at_once_on_one_agent_instance_do_not_disturb_each_other()
    {
        var model = new ScriptedChatClient(messages =>
        {
            string who = messages.First(m => m.Role == ChatRole.User).Text;
            int users = messages.Count(m => m.Role == ChatRole.User);
            return new ChatMessage(ChatRole.Assistant, $"{who}/{users}");
        });
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.UseStore(new InMemoryKairosStore());
            o.WorkerCount = 4;
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(5);
        }).AddAgentFramework(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));
        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            AIAgent chat = host.Services.GetRequiredKeyedService<AIAgent>("chat");
            var conversations = Enumerable.Range(0, 24).Select(async i =>
            {
                AgentSession session = await chat.CreateSessionAsync();
                var answers = new List<string>();
                for (int turn = 1; turn <= 3; turn++)
                {
                    AgentResponse reply = await chat.RunAsync($"person-{i}", session).WaitAsync(TimeSpan.FromSeconds(60));
                    answers.Add(reply.Text);
                }

                return (i, answers);
            });

            foreach ((int i, List<string> answers) in await Task.WhenAll(conversations))
            {
                Assert.Equal([$"person-{i}/1", $"person-{i}/2", $"person-{i}/3"], answers); // each conversation saw its own history, and only its own
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
