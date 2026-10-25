using System.Text;
using Kairos.Agents;
using Kairos.Agents.McpClient;
using Kairos.Testing;
using Kairos.Testing.Simulation;

namespace Kairos.Agents.McpClient.Tests;

/// <summary>A remote server whose behaviour the test scripts. Counts what reached it.</summary>
public sealed class FakeServer : IMcpToolEndpoint
{
    private int _calls;
    private int _polls;

    public List<string> CallKeys { get; } = [];

    public List<string> Cancelled { get; } = [];

    public List<(string Tool, string Args)> Seen { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public int Polls => Volatile.Read(ref _polls);

    /// <summary>Calls that throw before this many have happened (a flaky link).</summary>
    public int FailFirst { get; set; }

    /// <summary>Null: answer at once. Otherwise the call becomes a remote task that is working for this many polls.</summary>
    public int? WorkingPolls { get; set; }

    public McpRemoteStatus Final { get; set; } = McpRemoteStatus.Completed;

    public McpToolResult Answer { get; set; } = new(false, "done");

    public ValueTask<McpCallOutcome> CallAsync(string tool, string argumentsJson, string idempotencyKey, bool useTasks, CancellationToken cancellationToken)
    {
        lock (CallKeys)
        {
            CallKeys.Add(idempotencyKey);
            Seen.Add((tool, argumentsJson));
        }

        int n = Interlocked.Increment(ref _calls);
        if (n <= FailFirst)
        {
            throw new IOException("connection reset");
        }

        return ValueTask.FromResult(WorkingPolls is null || !useTasks ? new McpCallOutcome(Answer, null, 0) : new McpCallOutcome(null, "remote-1", 1000));
    }

    public ValueTask<McpTaskPoll> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        int n = Interlocked.Increment(ref _polls);
        if (n <= WorkingPolls)
        {
            return ValueTask.FromResult(new McpTaskPoll(McpRemoteStatus.Working, null, null, 0));
        }

        return ValueTask.FromResult(Final switch
        {
            McpRemoteStatus.Completed => new McpTaskPoll(McpRemoteStatus.Completed, Answer, null, 0),
            McpRemoteStatus.Failed => new McpTaskPoll(McpRemoteStatus.Failed, null, """{"message":"remote exploded"}""", 0),
            McpRemoteStatus.InputRequired => new McpTaskPoll(McpRemoteStatus.InputRequired, null, null, 2),
            McpRemoteStatus.Cancelled => new McpTaskPoll(McpRemoteStatus.Cancelled, null, null, 0),
            _ => new McpTaskPoll(McpRemoteStatus.Working, null, null, 0),
        });
    }

    public ValueTask CancelAsync(string taskId, CancellationToken cancellationToken)
    {
        lock (Cancelled)
        {
            Cancelled.Add(taskId);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class McpRig
{
    private static readonly TenantScope Scope = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"));

    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();

    public McpRig(FakeServer server, Action<McpClientsBuilder>? configure = null, bool faults = false)
    {
        Server = server;
        Time = new VirtualTimeProvider();
        Memory = new InMemoryKairosStore();
        if (faults)
        {
            Faults = new FaultInjectingStore(Memory, new SimRandom(1), new SimFaultOptions(), new SimStats());
        }

        Store = (IKairosStore?)Faults ?? Memory;
        Client = new KairosClient(Store, Time);
        Agents = new AgentsBuilder().UseTimeProvider(Time).Build();
        Agents.RegisterInto(_activities);
        McpClientsBuilder builder = new McpClientsBuilder().AddServer("crm", server).ReadOnlyTools("crm", "lookup");
        configure?.Invoke(builder);
        builder.Build().RegisterInto(_activities);
        Worker = NewWorker();
    }

    public FakeServer Server { get; }

    public VirtualTimeProvider Time { get; }

    public InMemoryKairosStore Memory { get; }

    public IKairosStore Store { get; }

    public FaultInjectingStore? Faults { get; }

    public KairosClient Client { get; }

    public AgentsRuntime Agents { get; }

    public WorkerRuntime Worker { get; private set; }

    public WorkerRuntime NewWorker() => new(Store, Scope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false, FailedTaskRetryDelay = TimeSpan.FromSeconds(1) });

    public void Workflow(string type, Func<IAgentContext, byte[], Task<byte[]>> body) =>
        _workflows.Register(type, KairosClient.DefaultBuildId, Agents.Workflow(body));

    public async Task<RunKey> StartAsync(string type)
    {
        StartRunResult s = await Retry(async () => await Client.StartAsync(Scope, "wf-" + type, type, []));
        return new RunKey(Scope.TenantId, Scope.NamespaceId, s.RunId);
    }

    public async Task<RunState> RunAsync(RunKey run, bool jumpClock = true)
    {
        while (true)
        {
            try
            {
                await Driver.DriveAsync(Worker);
            }
            catch (SimulatedFaultException)
            {
                Worker = NewWorker();
                continue;
            }

            RunState state = (await Client.GetStateAsync(run))!.Value;
            if (state != RunState.Running || !jumpClock || Memory.NextWakeup(Scope, Time.GetUtcNow()) is not { } wake)
            {
                return state;
            }

            Time.AdvanceTo(wake);
        }
    }

    public static async Task<T> Retry<T>(Func<Task<T>> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (SimulatedFaultException) when (attempt < 50)
            {
            }
        }
    }

    public async Task<IReadOnlyList<HistoryEvent>> HistoryAsync(RunKey run) => await Client.GetOpenedHistoryAsync(run);

    public async Task<string> ResultAsync(RunKey run) => Encoding.UTF8.GetString(((WorkflowCompleted)(await HistoryAsync(run))[^1]).Result);

    public async Task<WorkflowFailed> FailureAsync(RunKey run) => (WorkflowFailed)(await HistoryAsync(run))[^1];
}

public sealed class McpClientTests
{
    private static McpRig CallingWorkflow(FakeServer server, string tool = "lookup", Action<McpClientsBuilder>? configure = null, McpCallOptions? options = null)
    {
        var rig = new McpRig(server, configure);
        rig.Workflow("call", async (ctx, _) =>
        {
            McpToolResult r = await ctx.CallMcpToolAsync("crm", tool, new Dictionary<string, object?> { ["id"] = 42, ["name"] = "Ada" }, options);
            return Encoding.UTF8.GetBytes($"{r.IsError}|{r.Text}");
        });
        return rig;
    }

    [Fact]
    public async Task A_call_is_recorded_and_replay_never_calls_the_server_again()
    {
        var server = new FakeServer();
        McpRig rig = new(server);
        rig.Workflow("call", async (ctx, _) =>
        {
            McpToolResult r = await ctx.CallMcpToolAsync("crm", "lookup");
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5)); // later workflow tasks replay the step
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5));
            return Encoding.UTF8.GetBytes(r.Text);
        });

        RunKey run = await rig.StartAsync("call");
        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Equal("done", await rig.ResultAsync(run));
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task Arguments_reach_the_server_as_json()
    {
        var server = new FakeServer();
        McpRig rig = CallingWorkflow(server);
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run);

        (string tool, string args) = Assert.Single(server.Seen);
        Assert.Equal("lookup", tool);
        Assert.Equal("""{"id":42,"name":"Ada"}""", args);
    }

    [Fact]
    public async Task Every_attempt_of_a_step_carries_the_same_idempotency_key()
    {
        var server = new FakeServer { FailFirst = 2 };
        McpRig rig = CallingWorkflow(server);
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Equal(3, server.Calls);
        Assert.Single(server.CallKeys.Distinct());
        ActivityScheduled step = (await rig.HistoryAsync(run)).OfType<ActivityScheduled>().Single(a => a.ActivityType == McpClientRuntime.CallActivity);
        Assert.Equal($"{run.RunId:N}:{step.ActivityId}", server.CallKeys[0]);
    }

    [Fact]
    public async Task A_tool_that_reports_an_error_is_a_result_not_an_exception()
    {
        var server = new FakeServer { Answer = new McpToolResult(true, "order not found") };
        McpRig rig = CallingWorkflow(server);
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run);

        Assert.Equal("True|order not found", await rig.ResultAsync(run));
    }

    [Fact]
    public async Task An_unknown_server_fails_at_once_without_retrying()
    {
        var server = new FakeServer();
        McpRig rig = new(server);
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.CallMcpToolAsync("nowhere", "lookup");
            return [];
        });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        ActivityFailed failure = Assert.Single((await rig.HistoryAsync(run)).OfType<ActivityFailed>());
        Assert.Equal(nameof(UnknownMcpServerException), failure.ErrorType);
        Assert.False(failure.WillRetry);
    }

    [Fact]
    public async Task Redaction_applies_to_what_is_recorded()
    {
        var server = new FakeServer { Answer = new McpToolResult(false, "card 4111-1111-1111-1111") };
        McpRig rig = CallingWorkflow(server, configure: b => b.UseRedactor(t => t.Replace("4111-1111-1111-1111", "[card]", StringComparison.Ordinal)));
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run);

        Assert.Equal("False|card [card]", await rig.ResultAsync(run));
        foreach (ActivityCompleted done in (await rig.HistoryAsync(run)).OfType<ActivityCompleted>())
        {
            Assert.DoesNotContain("4111", Encoding.UTF8.GetString(done.Result), StringComparison.Ordinal);
        }
    }

    // ---- remote tasks ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_remote_task_is_started_once_and_polled_with_timers_until_it_completes()
    {
        var server = new FakeServer { WorkingPolls = 4, Answer = new McpToolResult(false, "report ready") };
        McpRig rig = CallingWorkflow(server);
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Equal("False|report ready", await rig.ResultAsync(run));
        Assert.Equal(1, server.Calls); // the work was started once...
        Assert.Equal(5, server.Polls); // ...and asked about five times
        Assert.Equal(5, (await rig.HistoryAsync(run)).OfType<TimerStarted>().Count());
    }

    [Fact]
    public async Task A_remote_task_failure_cancellation_and_request_for_input_become_exceptions()
    {
        foreach ((McpRemoteStatus final, string expected) in new[]
        {
            (McpRemoteStatus.Failed, nameof(McpToolFailedException)),
            (McpRemoteStatus.Cancelled, nameof(McpToolCancelledException)),
            (McpRemoteStatus.InputRequired, nameof(McpInputRequiredException)),
        })
        {
            var server = new FakeServer { WorkingPolls = 1, Final = final };
            McpRig rig = CallingWorkflow(server);
            RunKey run = await rig.StartAsync("call");

            Assert.Equal(RunState.Failed, await rig.RunAsync(run));
            Assert.Equal(expected, (await rig.FailureAsync(run)).ErrorType);
        }
    }

    [Fact]
    public async Task A_remote_failure_carries_the_servers_error()
    {
        var server = new FakeServer { WorkingPolls = 0, Final = McpRemoteStatus.Failed };
        McpRig rig = CallingWorkflow(server);
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run);

        Assert.Contains("remote exploded", (await rig.FailureAsync(run)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_task_that_never_finishes_is_cancelled_remotely_and_times_out()
    {
        var server = new FakeServer { WorkingPolls = int.MaxValue };
        McpRig rig = CallingWorkflow(server, options: new McpCallOptions { Timeout = TimeSpan.FromMinutes(10) });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));

        Assert.Equal(nameof(TimeoutException), (await rig.FailureAsync(run)).ErrorType);
        Assert.Equal(["remote-1"], server.Cancelled);
    }

    [Fact]
    public async Task Asking_for_no_tasks_gets_the_answer_in_one_step()
    {
        var server = new FakeServer { WorkingPolls = 3 }; // would be a task if asked
        McpRig rig = CallingWorkflow(server, options: new McpCallOptions { UseTasks = false });
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run);

        Assert.Equal("False|done", await rig.ResultAsync(run));
        Assert.Equal(0, server.Polls);
    }

    // ---- policy and approvals ----------------------------------------------------------------------------------------

    private static readonly ApproverIdentity Dana = new("user:dana", "Dana");

    [Fact]
    public async Task A_tool_not_declared_read_only_waits_for_a_person_and_then_runs_with_the_approval()
    {
        var server = new FakeServer();
        McpRig rig = CallingWorkflow(server, tool: "delete_customer");
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(0, server.Calls);
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await rig.HistoryAsync(run)));
        Assert.Equal("crm.delete_customer", pending.Tool);

        await new AgentClient(rig.Client, rig.Time).ApproveAsync(run, pending.ApprovalId, Dana);
        Assert.Equal(RunState.Completed, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task A_rejected_call_never_reaches_the_server_and_rolls_a_saga_back()
    {
        var server = new FakeServer();
        var undone = new List<string>();
        McpRig rig = new(server);
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                saga.Compensate("noted", async _ => undone.Add("undo")); // runs in workflow code; recorded by replay not by effect
                await ctx.CallMcpToolAsync("crm", "delete_customer");
            });
            return [];
        });
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run, jumpClock: false);
        PendingApproval pending = AgentHistory.PendingApprovals(await rig.HistoryAsync(run)).Single();

        await new AgentClient(rig.Client, rig.Time).RejectAsync(run, pending.ApprovalId, Dana, "not this one");
        Assert.Equal(RunState.Failed, await rig.RunAsync(run, jumpClock: false));

        Assert.Equal(nameof(ApprovalRejectedException), (await rig.FailureAsync(run)).ErrorType);
        Assert.Equal(0, server.Calls);
        Assert.NotEmpty(undone);
    }

    [Fact]
    public async Task A_read_only_tool_runs_without_asking()
    {
        var server = new FakeServer();
        McpRig rig = CallingWorkflow(server, tool: "lookup");
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run, jumpClock: false));
        Assert.Empty(AgentHistory.PendingApprovals(await rig.HistoryAsync(run)));
    }

    [Fact]
    public async Task A_denied_tool_is_refused_and_an_allowed_one_runs_without_asking()
    {
        var denied = new FakeServer();
        McpRig rigDenied = CallingWorkflow(denied, tool: "wipe", configure: b => b.UsePolicy(new BaselineToolPolicy().Deny("crm.wipe")));
        RunKey run = await rigDenied.StartAsync("call");
        Assert.Equal(RunState.Failed, await rigDenied.RunAsync(run, jumpClock: false));
        Assert.Equal(nameof(McpToolDeniedException), (await rigDenied.FailureAsync(run)).ErrorType);
        Assert.Equal(0, denied.Calls);

        var allowed = new FakeServer();
        McpRig rigAllowed = CallingWorkflow(allowed, tool: "archive", configure: b => b.UsePolicy(new BaselineToolPolicy().Allow("crm.archive")));
        RunKey run2 = await rigAllowed.StartAsync("call");
        Assert.Equal(RunState.Completed, await rigAllowed.RunAsync(run2, jumpClock: false));
    }

    // ---- crashes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_crash_at_any_store_operation_never_starts_the_remote_call_twice_with_a_different_key()
    {
        static McpRig Build(FakeServer server)
        {
            var rig = new McpRig(server, faults: true);
            rig.Faults!.FaultsEnabled = false;
            rig.Workflow("call", async (ctx, _) => Encoding.UTF8.GetBytes((await ctx.CallMcpToolAsync("crm", "lookup")).Text));
            return rig;
        }

        var baselineServer = new FakeServer { WorkingPolls = 2 };
        McpRig baseline = Build(baselineServer);
        RunKey baseRun = await baseline.StartAsync("call");
        Assert.Equal(RunState.Completed, await baseline.RunAsync(baseRun));
        long operations = baseline.Faults!.OperationCount;
        Assert.True(operations > 10);

        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                var server = new FakeServer { WorkingPolls = 2 };
                McpRig rig = Build(server);
                long at = k;
                FaultKind crash = kind;
                rig.Faults!.ForcedFault = index => index == at ? crash : FaultKind.None;

                RunKey run = await rig.StartAsync("call");
                Assert.Equal(RunState.Completed, await rig.RunAsync(run));

                string where = $"crash {kind} at operation {k}";
                Assert.Equal("done", await rig.ResultAsync(run));
                // One crash can cost one repeated start (the answer was lost), always with the same key.
                Assert.True(server.Calls is >= 1 and <= 2, $"{where}: {server.Calls} calls");
                Assert.Single(server.CallKeys.Distinct());
                HashChain.Verify(await rig.HistoryAsync(run));
            }
        }
    }
}
