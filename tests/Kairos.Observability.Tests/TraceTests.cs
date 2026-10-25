using System.Diagnostics;
using System.Text;
using Kairos.Agents;
using Kairos.Agents.Tests;
using Kairos.Testing;
using Microsoft.Extensions.AI;

namespace Kairos.Observability.Tests;

[Collection(TelemetryTests.Name)]
public sealed class TraceTests
{
    private static readonly byte[] None = [];

    private static async Task<(TestEngine Engine, Guid Run)> RunPlainAsync(Func<IWorkflowContext, byte[], Task<byte[]>> workflow, Action<TestEngine>? setUp = null, Func<TestEngine, Guid, Task>? between = null)
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("wf", (ctx, input) => workflow(ctx, input));
        engine.RegisterActivity("work", input => Task.FromResult(input));
        setUp?.Invoke(engine);
        Guid run = engine.StartWorkflow("wf", None, Guid.NewGuid(), Guid.NewGuid());
        if (between is not null)
        {
            await engine.RunAsync(run);
            await between(engine, run);
        }

        await engine.RunAsync(run);
        return (engine, run);
    }

    [Fact]
    public async Task A_run_produces_run_task_activity_timer_and_signal_spans_in_one_trace()
    {
        using var telemetry = new Collector();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.http", Sample = (ref ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using Activity http = new ActivitySource("test.http").StartActivity("GET /orders")!;

        (TestEngine engine, Guid run) = await RunPlainAsync(
            async (ctx, input) =>
            {
                await ctx.ExecuteActivityAsync("work", input);
                await ctx.DelayAsync(TimeSpan.FromMinutes(5));
                await ctx.WaitForSignalAsync("go");
                return input;
            },
            between: async (e, r) =>
            {
                using Activity? sender = new ActivitySource("test.http").StartActivity("POST /go");
                e.Signal(r, "go", None);
                await Task.CompletedTask;
            });

        Assert.Equal(RunState.Completed, engine.GetStatus(run));
        Activity start = Assert.Single(telemetry.Named("kairos.run.start"));
        Assert.Equal(http.TraceId, start.TraceId); // the run belongs to the request that started it
        Assert.Equal(http.SpanId, start.ParentSpanId);

        string[] names = ["kairos.workflow_task", "kairos.activity.attempt", "kairos.timer", "kairos.signal", "kairos.run"];
        foreach (string name in names)
        {
            Assert.All(telemetry.Named(name), s => Assert.Equal(start.TraceId, s.TraceId));
            Assert.NotEmpty(telemetry.Named(name));
        }

        Assert.All(telemetry.Named("kairos.workflow_task"), t => Assert.Equal(start.SpanId, t.ParentSpanId));
        Activity run1 = Assert.Single(telemetry.Named("kairos.run"));
        Assert.Equal(start.SpanId, run1.ParentSpanId);
        Assert.Equal("completed", run1.GetTagItem(KairosTags.Outcome));
        Assert.Equal(run.ToString("D"), run1.GetTagItem(KairosTags.RunId));
        Assert.True(run1.Duration > TimeSpan.Zero); // from the start event to the closing task, by recorded time

        Activity attempt = Assert.Single(telemetry.Named("kairos.activity.attempt"));
        Assert.Equal("work", attempt.GetTagItem(KairosTags.ActivityType));
        Assert.Equal(1, attempt.GetTagItem(KairosTags.Attempt));
        Assert.Contains(telemetry.Named("kairos.workflow_task"), t => t.SpanId == attempt.ParentSpanId); // a child of the task that scheduled it

        Activity timer = Assert.Single(telemetry.Named("kairos.timer"));
        Assert.Equal(300d, timer.GetTagItem(KairosTags.TimerSeconds));
        Assert.True(timer.Duration >= TimeSpan.FromMinutes(5)); // the span covers the time the timer was pending

        Activity signal = Assert.Single(telemetry.Named("kairos.signal"));
        Assert.Equal("go", signal.GetTagItem(KairosTags.SignalName));
        ActivityLink link = Assert.Single(signal.Links);
        Assert.Equal(telemetry.Named("kairos.signal.send").Single().SpanId, link.Context.SpanId); // the sender's span is linked to the delivery
    }

    [Fact]
    public async Task The_trace_continues_after_a_restart_because_it_comes_from_history()
    {
        using var telemetry = new Collector();
        var engine = new TestEngine();
        int attempts = 0;
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("flaky", input, RetryPolicy.Default with { InitialInterval = TimeSpan.FromSeconds(1) }));
        engine.RegisterActivity("flaky", input => ++attempts < 3 ? throw new InvalidOperationException("boom") : Task.FromResult(input));

        Guid run = engine.StartWorkflow("wf", None, Guid.NewGuid(), Guid.NewGuid());
        Activity start = Assert.Single(telemetry.Named("kairos.run.start"));
        Assert.Null(Activity.Current); // nothing ambient: whatever follows can only know the trace from stored data

        // A different engine (a new process) adopts only the persisted history and carries on.
        IReadOnlyList<HistoryEvent> persisted = engine.GetHistory(run);
        Assert.Equal(start.Id, ((WorkflowStarted)persisted[0]).TraceContext);
        await engine.RunAsync(run);

        Assert.Equal(RunState.Completed, engine.GetStatus(run));
        Activity[] tries = [.. telemetry.Named("kairos.activity.attempt").OrderBy(a => (int)a.GetTagItem(KairosTags.Attempt)!)];
        Assert.Equal(3, tries.Length);
        Assert.All(tries, t => Assert.Equal(start.TraceId, t.TraceId));
        Assert.Single(tries.Select(t => t.ParentSpanId).Distinct()); // retries are siblings under the task that scheduled the activity
        Assert.Equal(["retry", "retry", "completed"], tries.Select(t => (string)t.GetTagItem(KairosTags.Outcome)!));
        Assert.Equal(ActivityStatusCode.Error, tries[0].Status);
        Assert.Equal("InvalidOperationException", tries[0].GetTagItem(KairosTags.ErrorType));
        Assert.Equal(2, telemetry.Sum("kairos.activity.retries"));
    }

    [Fact]
    public async Task A_second_engine_that_only_has_the_history_joins_the_same_trace()
    {
        using var telemetry = new Collector();
        var first = new TestEngine();
        first.RegisterWorkflow("wf", async (ctx, _) => await ctx.WaitForSignalAsync("go"));
        Guid run = first.StartWorkflow("wf", None, Guid.NewGuid(), Guid.NewGuid());
        await first.RunAsync(run);
        string originalTrace = telemetry.Named("kairos.run.start").Single().TraceId.ToHexString();

        var second = new TestEngine();
        second.RegisterWorkflow("wf", async (ctx, _) => await ctx.WaitForSignalAsync("go"));
        Guid adopted = second.Load(first.GetHistory(run));
        second.Signal(adopted, "go", None);
        await second.RunAsync(adopted);

        Assert.Equal(RunState.Completed, second.GetStatus(adopted));
        Activity closing = Assert.Single(telemetry.Named("kairos.run"));
        Assert.Equal(originalTrace, closing.TraceId.ToHexString());
    }

    [Fact]
    public async Task Replaying_never_reports_the_same_timer_signal_or_run_twice()
    {
        using var telemetry = new Collector();
        (TestEngine engine, Guid run) = await RunPlainAsync(
            async (ctx, input) =>
            {
                await ctx.DelayAsync(TimeSpan.FromSeconds(10));
                await ctx.WaitForSignalAsync("a");
                await ctx.DelayAsync(TimeSpan.FromSeconds(10));
                await ctx.WaitForSignalAsync("b");
                await ctx.ExecuteActivityAsync("work", input);
                return input;
            },
            between: (e, r) =>
            {
                e.Signal(r, "a", None);
                return Task.CompletedTask;
            });
        engine.Signal(run, "b", None);
        await engine.RunAsync(run);

        Assert.Equal(RunState.Completed, engine.GetStatus(run));
        Assert.Equal(2, telemetry.Named("kairos.timer").Count());
        Assert.Equal(2, telemetry.Named("kairos.signal").Count());
        Assert.Single(telemetry.Named("kairos.run"));
        Assert.Equal(1, telemetry.Sum("kairos.runs.closed"));
        Assert.Equal(2, telemetry.Sum("kairos.signals.received"));
    }

    [Fact]
    public async Task A_failed_run_is_an_error_span_with_the_error_type_and_no_message()
    {
        using var telemetry = new Collector();
        (TestEngine engine, Guid run) = await RunPlainAsync((_, _) => throw new ArgumentException($"bad input {Canary.Text}"));

        Assert.Equal(RunState.Failed, engine.GetStatus(run));
        Activity closing = Assert.Single(telemetry.Named("kairos.run"));
        Assert.Equal(ActivityStatusCode.Error, closing.Status);
        Assert.Equal("ArgumentException", closing.GetTagItem(KairosTags.ErrorType));
        Assert.Empty(Canary.FoundIn(telemetry.Everything()));
    }

    [Fact]
    public async Task Metrics_cover_replay_history_retries_leases_and_runs()
    {
        using var telemetry = new Collector();
        var engine = new TestEngine();
        engine.RegisterWorkflow("wf", async (ctx, input) =>
        {
            await ctx.ExecuteActivityAsync("work", input);
            await ctx.DelayAsync(TimeSpan.FromSeconds(1));
            return input;
        });
        engine.RegisterActivity("work", input => Task.FromResult(input));
        Guid run = engine.StartWorkflow("wf", Encoding.UTF8.GetBytes("12345"), Guid.NewGuid(), Guid.NewGuid());
        await engine.RunAsync(run);

        Assert.Equal(RunState.Completed, engine.GetStatus(run));
        Assert.Equal(1, telemetry.Sum("kairos.runs.started"));
        Assert.Equal(1, telemetry.Sum("kairos.runs.closed", p => (string?)p.Tags[KairosTags.Outcome] == "completed"));
        Assert.True(telemetry.Sum("kairos.workflow_tasks.processed", p => (string?)p.Tags[KairosTags.Outcome] == "applied") >= 3);
        Assert.Contains(telemetry.Points, p => p.Instrument == "kairos.replay.duration" && p.Unit == "s");
        Assert.Contains(telemetry.Points, p => p.Instrument == "kairos.history.events" && p.Value >= 1);
        Assert.Contains(telemetry.Points, p => p.Instrument == "kairos.history.size" && p.Unit == "By");
        Assert.Equal(1, telemetry.Sum("kairos.activity.attempts", p => (string?)p.Tags[KairosTags.Outcome] == "completed"));
        Assert.Equal(1, telemetry.Sum("kairos.timers.fired"));
        Assert.All(telemetry.Points.Where(p => p.Instrument.StartsWith("kairos.", StringComparison.Ordinal)), p =>
            Assert.DoesNotContain(p.Tags.Keys, k => k.Contains("run", StringComparison.Ordinal) || k.Contains("tenant", StringComparison.Ordinal))); // no unbounded labels on metrics
    }

    [Fact]
    public async Task An_expired_lease_is_counted()
    {
        using var telemetry = new Collector();
        var engine = new TestEngine();
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("hang", input, RetryPolicy.Default with { MaximumAttempts = 2, InitialInterval = TimeSpan.FromSeconds(1) }));
        int calls = 0;
        engine.RegisterActivity("hang", input => ++calls == 1 ? throw new OperationCanceledException() : Task.FromResult(input));
        var scope = new TenantScope(Guid.NewGuid(), Guid.NewGuid());
        Guid run = engine.StartWorkflow("wf", None, scope.TenantId, scope.NamespaceId);

        // Lease an activity task by hand and let it lapse: nobody finishes it.
        var worker = new WorkerRuntime(engine.Store, scope, new WorkflowRegistry().Register("wf", "default", async (ctx, input) => await ctx.ExecuteActivityAsync("hang", input)), new ActivityRegistry(), engine.Time);
        Assert.True(await worker.ProcessWorkflowTaskAsync(default));
        ActivityTask? leased = await engine.Store.PollActivityTaskAsync(scope, engine.Now, TimeSpan.FromSeconds(5), default);
        Assert.NotNull(leased);
        engine.Time.Advance(TimeSpan.FromMinutes(1));
        await worker.FireTimersAsync(default);

        Assert.True(telemetry.Sum("kairos.lease.expiries") >= 1);
        Assert.NotEqual(Guid.Empty, run);
    }
}
