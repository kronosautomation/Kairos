using System.Text;
using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>How much a conversation moves through history when every step carries the whole session (design question 4), measured with tool output of a realistic size, and many conversations at once on one agent.</summary>
public sealed class MeasurementTests
{
    /// <summary>An order record of about <paramref name="kilobytes"/> KB, as JSON: what a real order or CRM lookup returns.</summary>
    private static string Record(int kilobytes, int seed)
    {
        var sb = new StringBuilder("{\"orderId\":\"A-").Append(seed).Append("\",\"lines\":[");
        int i = 0;
        while (sb.Length < kilobytes * 1024)
        {
            sb.Append(i++ == 0 ? "" : ",").Append("{\"sku\":\"SKU-").Append(seed * 1000 + i).Append("\",\"name\":\"Item ").Append(i).Append(" of the order, long enough to look real\",\"qty\":").Append(i % 5 + 1).Append(",\"price\":").Append(9.99m + i).Append(",\"status\":\"shipped\"}");
        }

        return sb.Append("]}").ToString();
    }

    [Fact]
    public async Task What_a_realistic_conversation_costs_in_history()
    {
        int toolCalls = 0;
        var model = new ScriptedChatClient(messages =>
        {
            // Each user message: two lookups (4 KB and 2 KB of tool output), then an answer of a few sentences.
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
        AIFunction order = AIFunctionFactory.Create((string orderId) => { toolCalls++; return Record(4, orderId.Length); }, "lookup_order", "Finds an order.");
        AIFunction customer = AIFunctionFactory.Create((string customerId) => Record(2, customerId.Length), "lookup_customer", "Finds a customer.");
        var h = new AfHarness(af => af.Configure(o => o.MaxSnapshotBytes = 8 * 1024 * 1024).AddAgent(AfHarness.AgentWith(model, "support", order, customer), r => r.ReadOnly("lookup_order", "lookup_customer")));

        var rows = new List<(int Message, long SnapshotBytes, long TurnBytes, int Events)>();
        long previousTotal = 0;
        for (int message = 1; message <= 20; message++)
        {
            await h.AskAsync("support", "long", $"question {message} about my order");
            IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(await h.RunOfAsync("support", "long"));
            long total = history.Sum(Size);
            long snapshot = history.OfType<ActivityCompleted>().Select(c => AfCodec.Read(c.Result, AfJson.Default.AfStepResult)).Where(r => r.Snapshot is not null).Select(r => (long)r.Snapshot!.Value.GetRawText().Length).LastOrDefault();
            rows.Add((message, snapshot, total - previousTotal, history.Count));
            previousTotal = total;
        }

        Console.Error.WriteLine("[measure] message | session bytes | bytes recorded for this message | events in run");
        foreach ((int m, long s, long t, int e) in rows.Where(r => r.Message is 1 or 2 or 5 or 10 or 15 or 20))
        {
            Console.Error.WriteLine($"[measure] {m,7} | {s,13:N0} | {t,31:N0} | {e,14}");
        }

        Console.Error.WriteLine($"[measure] total recorded: {previousTotal:N0} bytes for 20 messages ({previousTotal / 20:N0} per message on average); session after 20: {rows[^1].SnapshotBytes:N0} bytes; {toolCalls} order lookups of about 4 KB");

        Assert.True(rows[^1].SnapshotBytes > rows[0].SnapshotBytes * 10, "the session should grow with the conversation");
        Assert.True(rows[^1].TurnBytes > rows[0].TurnBytes * 5, "every step carries the whole session, so the cost of a message grows with the conversation");
    }

    private static long Size(HistoryEvent e) => e switch
    {
        ActivityScheduled s => s.Input.Length,
        ActivityCompleted c => c.Result.Length,
        SignalReceived s => s.Payload.Length,
        WorkflowStarted w => w.Input.Length,
        _ => 0,
    };

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
