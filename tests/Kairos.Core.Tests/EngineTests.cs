using System.Text;

namespace Kairos.Core.Tests;

public sealed class EngineTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Namespace = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    private static HistoryEventType[] Types(IReadOnlyList<HistoryEvent> history) => [.. history.Select(e => e.Type)];

    private static string ResultOf(IReadOnlyList<HistoryEvent> history) => S(((WorkflowCompleted)history[^1]).Result);

    [Fact]
    public async Task Happy_path_runs_activity_and_completes()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("greet", input => Task.FromResult(B("hello " + S(input))));
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("greet", input));

        Guid run = engine.StartWorkflow("wf", B("world"), Tenant, Namespace);
        WorkflowStatus status = await engine.RunAsync(run);

        Assert.Equal(WorkflowStatus.Completed, status);
        IReadOnlyList<HistoryEvent> history = engine.GetHistory(run);
        Assert.Equal(
            [HistoryEventType.WorkflowStarted, HistoryEventType.ActivityScheduled, HistoryEventType.ActivityCompleted, HistoryEventType.WorkflowCompleted],
            Types(history));
        Assert.Equal("hello world", ResultOf(history));
        Assert.All(history, e => Assert.Equal((Tenant, Namespace, run), (e.TenantId, e.NamespaceId, e.RunId)));
        Assert.Equal([1L, 2, 3, 4], history.Select(e => e.Sequence));
        HashChain.Verify(history);
    }

    [Fact]
    public async Task Activity_retries_with_backoff_then_succeeds()
    {
        int calls = 0;
        var engine = new InMemoryWorkflowEngine();
        DateTimeOffset start = engine.Now;
        engine.RegisterActivity("flaky", _ => ++calls < 3
            ? throw new InvalidOperationException("boom " + calls)
            : Task.FromResult(B("ok")));
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("flaky", input));

        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        Assert.Equal(WorkflowStatus.Completed, await engine.RunAsync(run));

        IReadOnlyList<HistoryEvent> history = engine.GetHistory(run);
        ActivityFailed[] failures = [.. history.OfType<ActivityFailed>()];
        Assert.Equal(2, failures.Length);
        Assert.All(failures, f => Assert.True(f.WillRetry));
        Assert.Equal([1, 2], failures.Select(f => f.Attempt));
        Assert.Equal("InvalidOperationException", failures[0].ErrorType);
        Assert.Equal(3, calls);
        Assert.Equal("ok", ResultOf(history));
        Assert.Equal(start + TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(2), engine.Now); // 1s then 2s backoff
    }

    [Fact]
    public async Task Exhausted_retries_surface_to_workflow_as_activity_failure()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("bad", _ => throw new TimeoutException("nope"));
        engine.RegisterWorkflow("wf", async (ctx, input) =>
        {
            try
            {
                return await ctx.ExecuteActivityAsync("bad", input, RetryPolicy.Default with { MaximumAttempts = 2 });
            }
            catch (ActivityFailureException ex)
            {
                return B("caught " + ex.ErrorType);
            }
        });

        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        Assert.Equal(WorkflowStatus.Completed, await engine.RunAsync(run));

        IReadOnlyList<HistoryEvent> history = engine.GetHistory(run);
        Assert.Equal([true, false], history.OfType<ActivityFailed>().Select(f => f.WillRetry));
        Assert.Equal("caught TimeoutException", ResultOf(history));
    }

    [Fact]
    public async Task Non_retryable_error_fails_workflow_without_retry()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("bad", _ => throw new ArgumentException("fatal"));
        engine.RegisterWorkflow("wf", async (ctx, input) =>
            await ctx.ExecuteActivityAsync("bad", input, RetryPolicy.Default with { NonRetryableErrorTypes = ["ArgumentException"] }));

        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        Assert.Equal(WorkflowStatus.Failed, await engine.RunAsync(run));

        IReadOnlyList<HistoryEvent> history = engine.GetHistory(run);
        Assert.Single(history.OfType<ActivityFailed>());
        Assert.Equal("ActivityFailureException", ((WorkflowFailed)history[^1]).ErrorType);
    }

    [Fact]
    public async Task Timer_advances_workflow_time()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterWorkflow("wf", async (ctx, _) =>
        {
            DateTimeOffset before = ctx.Now;
            await ctx.DelayAsync(TimeSpan.FromHours(1));
            return B((ctx.Now - before).ToString());
        });

        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        Assert.Equal(WorkflowStatus.Completed, await engine.RunAsync(run));

        IReadOnlyList<HistoryEvent> history = engine.GetHistory(run);
        Assert.Equal(
            [HistoryEventType.WorkflowStarted, HistoryEventType.TimerStarted, HistoryEventType.TimerFired, HistoryEventType.WorkflowCompleted],
            Types(history));
        Assert.Equal(TimeSpan.FromHours(1).ToString(), ResultOf(history));
    }

    [Fact]
    public async Task Signal_unblocks_waiting_workflow_and_early_signals_are_buffered()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterWorkflow("wf", async (ctx, _) =>
        {
            byte[] first = await ctx.WaitForSignalAsync("go");
            await ctx.DelayAsync(TimeSpan.FromSeconds(1));
            byte[] second = await ctx.WaitForSignalAsync("go"); // already buffered by then
            return B(S(first) + "+" + S(second));
        });

        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        Assert.Equal(WorkflowStatus.Running, await engine.RunAsync(run));

        engine.Signal(run, "go", B("a"));
        Assert.Equal(WorkflowStatus.Running, await engine.RunAsync(run)); // blocked on the second signal

        engine.Signal(run, "go", B("b"));
        Assert.Equal(WorkflowStatus.Completed, await engine.RunAsync(run));
        Assert.Equal("a+b", ResultOf(engine.GetHistory(run)));
    }

    [Fact]
    public async Task Crash_and_resume_replays_history_without_rerunning_activities()
    {
        int stepACalls = 0, stepBCalls = 0;

        InMemoryWorkflowEngine NewEngine()
        {
            var e = new InMemoryWorkflowEngine();
            e.RegisterActivity("a", _ => { stepACalls++; return Task.FromResult(B("A")); });
            e.RegisterActivity("b", _ => { stepBCalls++; return Task.FromResult(B("B")); });
            e.RegisterWorkflow("wf", async (ctx, _) =>
            {
                Guid id = ctx.NewGuid();
                byte[] a = await ctx.ExecuteActivityAsync("a", ReadOnlyMemory<byte>.Empty);
                byte[] approval = await ctx.WaitForSignalAsync("approve");
                byte[] b = await ctx.ExecuteActivityAsync("b", ReadOnlyMemory<byte>.Empty);
                return B($"{id}|{S(a)}{S(approval)}{S(b)}|{ctx.Now:O}");
            });
            return e;
        }

        InMemoryWorkflowEngine before = NewEngine();
        Guid run = before.StartWorkflow("wf", [], Tenant, Namespace);
        Assert.Equal(WorkflowStatus.Running, await before.RunAsync(run));
        HistoryEvent[] persisted = [.. before.GetHistory(run)]; // what survives the "crash"

        Assert.Equal(1, stepACalls);

        InMemoryWorkflowEngine after = NewEngine(); // brand-new process, only the history carries over
        after.Load(persisted);
        after.Signal(run, "approve", B("Y"));
        Assert.Equal(WorkflowStatus.Completed, await after.RunAsync(run));

        Assert.Equal(1, stepACalls); // not re-executed after recovery
        Assert.Equal(1, stepBCalls);
        string result = ResultOf(after.GetHistory(run));
        Assert.Contains("|AYB|", result, StringComparison.Ordinal);

        // A full replay of the finished history regenerates the identical result (guid and Now included).
        ReplayResult replay = WorkflowReplayer.Replay(
            NewEngineWorkflow(), after.GetHistory(run));
        Assert.True(replay.IsClosed);
        Assert.Empty(replay.NewCommands);

        static WorkflowFunc NewEngineWorkflow() => async (ctx, _) =>
        {
            Guid id = ctx.NewGuid();
            byte[] a = await ctx.ExecuteActivityAsync("a", ReadOnlyMemory<byte>.Empty);
            byte[] approval = await ctx.WaitForSignalAsync("approve");
            byte[] b = await ctx.ExecuteActivityAsync("b", ReadOnlyMemory<byte>.Empty);
            return B($"{id}|{S(a)}{S(approval)}{S(b)}|{ctx.Now:O}");
        };
    }

    [Fact]
    public async Task Changed_activity_is_detected_as_non_determinism()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("a", _ => Task.FromResult(B("x")));
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("a", input));
        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        await engine.RunAsync(run);

        WorkflowFunc changed = async (ctx, input) => await ctx.ExecuteActivityAsync("renamed", input);
        var ex = Assert.Throws<NonDeterminismException>(() => WorkflowReplayer.Replay(changed, engine.GetHistory(run)));
        Assert.Contains("ActivityScheduled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_replaced_by_timer_is_detected_as_non_determinism()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("a", _ => Task.FromResult(B("x")));
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("a", input));
        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        await engine.RunAsync(run);

        WorkflowFunc changed = async (ctx, _) =>
        {
            await ctx.DelayAsync(TimeSpan.FromSeconds(1));
            return [];
        };
        Assert.Throws<NonDeterminismException>(() => WorkflowReplayer.Replay(changed, engine.GetHistory(run)));
    }

    [Fact]
    public async Task Workflow_that_stops_emitting_commands_is_detected_as_non_determinism()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("a", _ => Task.FromResult(B("x")));
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("a", input));
        Guid run = engine.StartWorkflow("wf", [], Tenant, Namespace);
        await engine.RunAsync(run);

        WorkflowFunc changed = (_, _) => Task.FromResult(B("done immediately"));
        Assert.Throws<NonDeterminismException>(() => WorkflowReplayer.Replay(changed, engine.GetHistory(run)));
    }

    [Fact]
    public async Task Changed_activity_input_is_detected_as_non_determinism()
    {
        var engine = new InMemoryWorkflowEngine();
        engine.RegisterActivity("a", _ => Task.FromResult(B("x")));
        engine.RegisterWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("a", input));
        Guid run = engine.StartWorkflow("wf", B("one"), Tenant, Namespace);
        await engine.RunAsync(run);

        WorkflowFunc changed = async (ctx, _) => await ctx.ExecuteActivityAsync("a", B("two"));
        Assert.Throws<NonDeterminismException>(() => WorkflowReplayer.Replay(changed, engine.GetHistory(run)));
    }

    [Fact]
    public void Deterministic_guids_are_stable_per_run_and_distinct_within_a_run()
    {
        var run = Guid.NewGuid();
        var a = new WorkflowExecution((_, _) => Task.FromResult(Array.Empty<byte>()), Tenant, Namespace, run);
        var b = new WorkflowExecution((_, _) => Task.FromResult(Array.Empty<byte>()), Tenant, Namespace, run);

        Guid a1 = a.NewGuid(), a2 = a.NewGuid();
        Assert.NotEqual(a1, a2);
        Assert.Equal(a1, b.NewGuid());
        Assert.Equal(a2, b.NewGuid());
    }

    [Fact]
    public void Retry_policy_backoff_is_exponential_and_capped()
    {
        var policy = new RetryPolicy { InitialInterval = TimeSpan.FromSeconds(1), BackoffCoefficient = 2, MaximumInterval = TimeSpan.FromSeconds(5) };
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetBackoff(4));
    }
}
