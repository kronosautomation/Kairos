using System.Diagnostics;
using System.Text;
using Kairos.Agents;
using Kairos.Agents.Tests;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing.Agents;
using Kairos.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Kairos.Observability.Tests;

/// <summary>
/// The promise: no payload byte, and nothing derived from one, reaches a span, a log or a metric. Every test here runs real workflows with a canary in every place a payload
/// or a message can be (inputs, results, signals, prompts, replies, tool arguments and results, error messages, approval comments, who the agent acts for), with
/// client-side encryption off and on, and then reads back everything a backend could receive.
/// </summary>
[Collection(TelemetryTests.Name)]
public sealed class TelemetryConfidentialityTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    [Fact]
    public void The_detector_finds_a_canary_in_every_place_a_backend_could_receive_one()
    {
        // A control: if this ever passes without a finding, the other tests prove nothing.
        foreach (string place in new[] { "tag", "name", "status", "event", "link", "baggage", "metric" })
        {
            using var telemetry = new Collector();
            using Activity span = KairosTelemetry.Source.StartActivity(place == "name" ? $"op {Canary.Text}" : "op")!;
            switch (place)
            {
                case "tag": span.SetTag("k", Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary.Text))); break;
                case "status": span.SetStatus(ActivityStatusCode.Error, Canary.Text); break;
                case "event": span.AddEvent(new ActivityEvent("e", tags: new ActivityTagsCollection { ["x"] = Canary.Text })); break;
                case "link": span.SetTag("link", "n/a"); break;
                case "baggage": span.AddBaggage("b", Canary.Text); break;
                case "metric": KairosMetrics.TimersFired.Add(1, new KeyValuePair<string, object?>("leak", Canary.Text)); break;
            }

            span.Stop();
            if (place != "link")
            {
                Assert.NotEmpty(Canary.FoundIn(telemetry.Everything()));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_canary_reaches_any_span_or_metric_in_a_workflow_that_uses_everything(bool encrypted)
    {
        using var telemetry = new Collector();
        IPayloadProtector? protector = encrypted ? new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral()) : null;
        int attempts = 0;
        var model = new ScriptedChatClient(messages => messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, $"reply {Canary.Text}")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", AgentHarness.WireArgs($$"""{"q":"{{Canary.Text}}"}"""))]));
        var h = new AgentHarness(
            b =>
            {
                b.AddModel("m", model);
                b.AddTool(AgentHarness.ReadTool("lookup", i => $"found {Canary.Text} for {i.Arguments}"));
                b.AddTool(AgentHarness.WriteTool("refund", _ => $"refunded {Canary.Text}"));
                b.ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement(Roles: ["finance"])));
            },
            protector: protector);
        h.Activity("flaky", (_, input, _) => ++attempts < 3 ? throw new InvalidOperationException($"failed on {Encoding.UTF8.GetString(input)}") : Task.FromResult(input));
        h.Workflow("everything", async (ctx, input) =>
        {
            ctx.OnBehalfOf = $"user:{Canary.Text}";
            string answer = await AgentHarness.AgentLoopAsync(ctx, "m", $"my secret is {Canary.Text}", "lookup");
            byte[] flaky = await ctx.Workflow.ExecuteActivityAsync("flaky", input, RetryPolicy.Default with { InitialInterval = TimeSpan.FromSeconds(1) });
            byte[] signal = await ctx.Workflow.WaitForSignalAsync("note");
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(1));
            ToolStepResult refund = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c2", "refund", AgentHarness.WireArgs($$"""{"why":"{{Canary.Text}}"}""")));
            return Encoding.UTF8.GetBytes($"{answer}|{Encoding.UTF8.GetString(flaky)}|{Encoding.UTF8.GetString(signal)}|{refund.Outcome}");
        });

        RunKey run = await h.StartAsync("everything", input: $"input {Canary.Text}");
        await h.RunAsync(run);
        Assert.True(await h.Client.SignalAsync(run, "note", Encoding.UTF8.GetBytes($"signal {Canary.Text}")));
        await h.RunAsync(run);
        string approval = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
        Assert.True(await h.Agent.ApproveAsync(run, approval, Alice, $"ok {Canary.Text}"));
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Contains(Canary.Text, await h.ResultTextAsync(run)); // the canary really travelled through the run

        string everything = telemetry.Everything();
        Assert.True(telemetry.Spans.Count > 15, "the run should have produced spans");
        Assert.Empty(Canary.FoundIn(everything));
        Assert.DoesNotContain("Alice", everything, StringComparison.Ordinal);
        foreach (string name in new[] { "kairos.run.start", "kairos.workflow_task", "kairos.activity.attempt", "kairos.timer", "kairos.signal", "kairos.approval", "kairos.run" })
        {
            Assert.NotEmpty(telemetry.Named(name));
        }
    }

    [Fact]
    public async Task Replay_errors_name_events_and_commands_and_never_quote_a_payload()
    {
        var engine = new Kairos.Testing.TestEngine();
        engine.RegisterWorkflow("wf", async (ctx, input) =>
        {
            await ctx.ExecuteActivityAsync("first", input);
            return input;
        });
        engine.RegisterActivity("first", input => Task.FromResult(input));
        Guid run = engine.StartWorkflow("wf", Encoding.UTF8.GetBytes($"payload {Canary.Text}"), Guid.NewGuid(), Guid.NewGuid());
        await engine.RunAsync(run);
        IReadOnlyList<HistoryEvent> history = engine.GetHistory(run);

        // The same history against code that now does something else first: the error must not carry the data.
        NonDeterminismException error = Assert.Throws<NonDeterminismException>(() => WorkflowReplayer.Replay(
            async (ctx, input) =>
            {
                await ctx.DelayAsync(TimeSpan.FromSeconds(1));
                return input;
            },
            history));
        Assert.Empty(Canary.FoundIn(error.Message + error));
    }

    // ---- the hosted path: AddKairos with an OpenTelemetry SDK, logs and health ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AddKairos_subscribes_the_hosts_opentelemetry_and_nothing_leaks_into_exported_spans_metrics_or_logs()
    {
        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new CapturingLogs();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        // The host knows nothing of Kairos: no AddSource, no AddMeter.
        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t.AddInMemoryExporter(spans))
            .WithMetrics(m => m.AddInMemoryExporter(metrics));

        var agents = new AgentsBuilder()
            .AddModel("m", new ScriptedChatClient(messages => messages.Any(x => x.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatMessage(ChatRole.Assistant, $"done {Canary.Text}")
                : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", AgentHarness.WireArgs($$"""{"q":"{{Canary.Text}}"}"""))])))
            .AddTool(AgentHarness.ReadTool("lookup", _ => $"result {Canary.Text}"))
            .Build();
        int attempts = 0;
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.UseStore(new InMemoryKairosStore());
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(20);
            o.AddActivities(agents.Activities);
            o.AddActivity("flaky", (_, input, _) => ++attempts < 2 ? throw new InvalidOperationException($"no {Canary.Text}") : Task.FromResult(input));
            o.AddWorkflow("hosted", agents.Workflow(async (ctx, input) =>
            {
                string text = await AgentHarness.AgentLoopAsync(ctx, "m", $"hello {Canary.Text}", "lookup");
                await ctx.Workflow.ExecuteActivityAsync("flaky", input, RetryPolicy.Default with { InitialInterval = TimeSpan.FromMilliseconds(10), BackoffCoefficient = 1 });
                return Encoding.UTF8.GetBytes(text);
            }));
        });

        using IHost host = builder.Build();
        HealthCheckService health = host.Services.GetRequiredService<HealthCheckService>();
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync()).Entries["kairos"].Status); // the workers have not started
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<KairosClient>();
            StartRunResult started = await client.StartAsync(KairosOptions.DefaultScope, "wf-1", "hosted", Encoding.UTF8.GetBytes($"in {Canary.Text}"));
            var key = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, started.RunId);
            await WaitAsync(async () => await client.GetStateAsync(key) == RunState.Completed);
            await WaitAsync(async () => (await health.CheckHealthAsync()).Entries["kairos"].Status == HealthStatus.Healthy);
        }
        finally
        {
            await host.StopAsync();
        }

        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        string[] names = [.. spans.Select(s => s.OperationName)];
        Assert.Contains("kairos.run.start", names);
        Assert.Contains("kairos.workflow_task", names);
        Assert.Contains("kairos.activity.attempt", names);
        Assert.Contains("chat m", names);
        string[] instruments = [.. metrics.Select(m => m.Name)];
        Assert.Contains("kairos.runs.started", instruments);
        Assert.Contains("kairos.replay.duration", instruments);
        Assert.Contains("gen_ai.client.token.usage", instruments);

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
        Assert.NotEmpty(logs.Lines); // the host did log something, and it was inspected
        Assert.Empty(Canary.FoundIn(string.Join('\n', logs.Lines)));
    }

    [Fact]
    public async Task Ready_work_nobody_claims_shows_up_in_the_queue_depth_gauge()
    {
        using var telemetry = new Collector();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.UseStore(new InMemoryKairosStore());
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(20);
            o.WorkerCount = 1;
            o.AddWorkflow("known", (_, input) => Task.FromResult(input)); // only the default build is registered here
        });

        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            var client = host.Services.GetRequiredService<KairosClient>();
            await client.StartAsync(KairosOptions.DefaultScope, "wf-orphan", "known", [], buildId: "some-other-build");
            await WaitAsync(() =>
            {
                telemetry.Poll();
                return Task.FromResult(telemetry.Points.Any(p => p.Instrument == "kairos.queue.depth" && (string?)p.Tags["queue"] == "workflow_task" && p.Value >= 1));
            });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(25);
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
}
