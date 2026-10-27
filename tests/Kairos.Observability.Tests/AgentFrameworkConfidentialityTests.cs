using System.Diagnostics;
using System.Text;
using Kairos.Agents;
using Kairos.Agents.AgentFramework;
using Kairos.Agents.AgentFramework.Tests;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing.Agents;
using Kairos.Worker;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Kairos.Observability.Tests;

/// <summary>
/// The promise holds for Agent Framework agents as for native ones: no payload byte, and nothing derived from one, reaches a span, a log or a metric, and with client-side encryption on the stored history is ciphertext
/// all the way through (the session snapshot, which is Agent Framework's JSON, included). A canary travels through every place text can be: the message, the model's tool call, the tool's result, the model's reply, who the agent
/// acts for, and the approver's comment.
/// </summary>
[Collection(TelemetryTests.Name)]
public sealed class AgentFrameworkConfidentialityTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    private static ScriptedChatClient CanaryModel() => new(messages =>
    {
        int results = messages.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
        return results switch
        {
            0 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup_order", AfHarness.Args($$"""{"orderId":"{{Canary.Text}}"}"""))]),
            1 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "issue_refund", AfHarness.Args($$"""{"orderId":"{{Canary.Text}}"}"""))]),
            _ => new ChatMessage(ChatRole.Assistant, $"done {Canary.Text}"),
        };
    });

    /// <summary>Everything stored for a run, as a string in each form a canary could take in bytes: the bytes as text, as base64 and as hex.</summary>
    private static string Stored(IReadOnlyList<HistoryEvent> history)
    {
        var sb = new StringBuilder();
        void Add(byte[]? bytes)
        {
            if (bytes is { Length: > 0 })
            {
                sb.AppendLine(Encoding.Latin1.GetString(bytes)).AppendLine(Convert.ToBase64String(bytes)).AppendLine(Convert.ToHexString(bytes));
            }
        }

        foreach (HistoryEvent e in history)
        {
            switch (e)
            {
                case WorkflowStarted x: Add(x.Input); break;
                case ActivityScheduled x: Add(x.Input); sb.AppendLine(x.ActivityType); break;
                case ActivityCompleted x: Add(x.Result); break;
                case SignalReceived x: Add(x.Payload); sb.AppendLine(x.SignalName); break;
                case WorkflowCompleted x: Add(x.Result); break;
                case WorkflowContinued x: Add(x.Input); break;
                case ActivityFailed x: sb.AppendLine(x.Message); break;
                case WorkflowFailed x: sb.AppendLine(x.Message); break;
            }
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_canary_reaches_a_span_a_metric_or_the_stored_history_of_a_durable_agent(bool encrypted)
    {
        using var telemetry = new Collector();
        IPayloadProtector? protector = encrypted ? new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral()) : null;
        ScriptedChatClient model = CanaryModel();
        var h = new AfHarness(
            af => af.AddAgent(
                AfHarness.SupportAgent(model, lookup: id => $"found {Canary.Text} for {id}", refund: id => $"refunded {Canary.Text}"),
                r => r.ReadOnly("lookup_order")),
            protector: protector);

        (_, DurableTurn turn) = await h.AskAsync("support", "s", $"my secret is {Canary.Text}", onBehalfOf: $"user:{Canary.Text}");
        RunKey run = await h.RunOfAsync("support", "s");
        string approval = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
        Assert.True(await h.Approvals.ApproveAsync(run, approval, Alice, $"ok {Canary.Text}"));
        await h.DriveAsync();

        AgentResponse? reply = await h.ReadAsync("support", turn);
        Assert.Contains(Canary.Text, AfHarness.Text(reply)); // the canary really travelled through the run

        string everything = telemetry.Everything();
        Assert.True(telemetry.Spans.Count > 8, "the run should have produced spans");
        Assert.Empty(Canary.FoundIn(everything));
        Assert.DoesNotContain("Alice", everything, StringComparison.Ordinal);
        Assert.NotEmpty(telemetry.Named("chat support"));
        Assert.True(telemetry.Sum("gen_ai.client.token.usage") > 0);

        string stored = Stored(await h.Client.GetHistoryAsync(run));
        if (encrypted)
        {
            Assert.Empty(Canary.FoundIn(stored)); // the database and the server see ciphertext, the session snapshot included
        }
        else
        {
            Assert.NotEmpty(Canary.FoundIn(stored)); // the control: without encryption the scan does find it, so a clean result above means something
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string line = $"{category} {logLevel} {formatter(state, exception)} {exception} {(state as IEnumerable<KeyValuePair<string, object?>> is { } kv ? string.Join(";", kv) : "")}";
                lock (owner.Lines)
                {
                    owner.Lines.Add(line);
                }
            }
        }
    }

    [Fact]
    public async Task In_a_host_with_an_opentelemetry_sdk_nothing_reaches_an_exported_span_metric_or_log()
    {
        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new CapturingLogs();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(spans)).WithMetrics(m => m.AddInMemoryExporter(metrics));

        AIFunction lookup = AIFunctionFactory.Create((string orderId) => $"found {Canary.Text}", "lookup_order", "d");
        AIFunction unclassified = AIFunctionFactory.Create((string orderId) => $"sent {Canary.Text}", "send_receipt", "d");
        ScriptedChatClient model = new(messages => messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, $"done {Canary.Text}")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup_order", AfHarness.Args($$"""{"orderId":"{{Canary.Text}}"}"""))]));
        builder.Services.AddKairos(o =>
        {
            o.UseStore(new InMemoryKairosStore());
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(10);
        }).AddAgentFramework(af => af.AddAgent(AfHarness.AgentWith(model, "support", lookup, unclassified), r => r.ReadOnly("lookup_order")));

        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            AIAgent support = host.Services.GetRequiredKeyedService<AIAgent>("support");
            AgentResponse reply = await support.RunAsync($"hello {Canary.Text}", await support.CreateSessionAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains(Canary.Text, reply.Text);
        }
        finally
        {
            await host.StopAsync();
        }

        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        string[] names = [.. spans.Select(s => s.OperationName)];
        Assert.Contains("chat ", names.Select(n => n.Length > 5 ? n[..5] : n));
        Assert.Contains("gen_ai.client.token.usage", metrics.Select(m => m.Name));

        var rendered = new StringBuilder();
        foreach (Activity s in spans)
        {
            rendered.AppendLine(s.DisplayName + s.StatusDescription + string.Join(";", s.TagObjects.Select(t => $"{t.Key}={t.Value}")));
        }

        foreach (Metric m in metrics)
        {
            rendered.AppendLine(m.Name + m.Description + m.Unit);
            foreach (ref readonly MetricPoint p in m.GetMetricPoints())
            {
                foreach (KeyValuePair<string, object?> tag in p.Tags)
                {
                    rendered.Append(tag.Key).Append('=').Append(tag.Value).Append(';');
                }
            }
        }

        Assert.Empty(Canary.FoundIn(rendered.ToString()));
        Assert.Contains(logs.Lines, l => l.Contains("send_receipt", StringComparison.Ordinal)); // the startup warning about an unclassified tool names the tool, and only that
        Assert.Empty(Canary.FoundIn(string.Join('\n', logs.Lines)));
    }
}
