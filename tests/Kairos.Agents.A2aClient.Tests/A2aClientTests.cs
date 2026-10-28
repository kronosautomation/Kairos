using System.Text;
using Kairos.Agents;
using Kairos.Agents.A2aClient;
using Kairos.Testing;
using Kairos.Testing.Simulation;

namespace Kairos.Agents.A2aClient.Tests;

/// <summary>A remote agent whose behaviour the test scripts. Counts what reached it.</summary>
public sealed class FakeAgent : IA2aAgentEndpoint
{
    private int _sends;
    private int _polls;

    public List<string> MessageIds { get; } = [];

    public List<(string? Text, string? TaskId)> Received { get; } = [];

    public List<string> Cancelled { get; } = [];

    public int Sends => Volatile.Read(ref _sends);

    public int Polls => Volatile.Read(ref _polls);

    public int FailFirst { get; set; }

    /// <summary>Null: answer with a message at once. Otherwise a task that is working for this many polls.</summary>
    public int? WorkingPolls { get; set; }

    public A2aState Final { get; set; } = A2aState.Completed;

    public A2aReply Answer { get; set; } = new(A2aState.Message, "done", null, null, "ctx-1", null);

    public ValueTask<A2aReply> SendAsync(string messageId, string? text, string? dataJson, string? taskId, string? contextId, CancellationToken cancellationToken)
    {
        lock (MessageIds)
        {
            MessageIds.Add(messageId);
            Received.Add((text, taskId));
        }

        if (Interlocked.Increment(ref _sends) <= FailFirst)
        {
            throw new IOException("connection reset");
        }

        return ValueTask.FromResult(WorkingPolls is null ? Answer : new A2aReply(A2aState.Working, null, null, "remote-1", "ctx-1", null));
    }

    public ValueTask<A2aReply> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _polls) <= WorkingPolls)
        {
            return ValueTask.FromResult(new A2aReply(A2aState.Working, null, null, taskId, "ctx-1", null));
        }

        return ValueTask.FromResult(Final switch
        {
            A2aState.Completed => Answer with { State = A2aState.Completed, TaskId = taskId },
            A2aState.Failed => new A2aReply(A2aState.Failed, null, null, taskId, "ctx-1", "the remote exploded"),
            A2aState.Rejected => new A2aReply(A2aState.Rejected, null, null, taskId, "ctx-1", "not something I do"),
            A2aState.Canceled => new A2aReply(A2aState.Canceled, null, null, taskId, "ctx-1", null),
            A2aState.InputRequired => new A2aReply(A2aState.InputRequired, null, null, taskId, "ctx-1", "Which invoice?"),
            A2aState.AuthRequired => new A2aReply(A2aState.AuthRequired, null, null, taskId, "ctx-1", null),
            _ => new A2aReply(A2aState.Working, null, null, taskId, "ctx-1", null),
        });
    }

    public ValueTask CancelTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        lock (Cancelled)
        {
            Cancelled.Add(taskId);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class A2aRig
{
    private static readonly TenantScope Scope = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"));

    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();

    public A2aRig(Action<A2aClientsBuilder> configure, bool faults = false)
    {
        Time = new VirtualTimeProvider();
        Memory = new InMemoryKairosStore();
        if (faults)
        {
            Faults = new FaultInjectingStore(Memory, new SimRandom(1), new SimFaultOptions(), new SimStats());
        }

        Store = (IKairosStore?)Faults ?? Memory;
        Client = new KairosClient(Store, Time);
        Agents = new AgentsBuilder().UseTimeProvider(Time).AllowUnverifiedApprovers().Build();
        Agents.RegisterInto(_activities);
        var builder = new A2aClientsBuilder();
        configure(builder);
        builder.Build().RegisterInto(_activities);
        Worker = NewWorker();
    }

    public static A2aRig For(FakeAgent agent, Action<A2aClientsBuilder>? more = null, bool faults = false) =>
        new(b =>
        {
            b.AddAgent("billing", agent).ReadOnlyAgents("billing");
            more?.Invoke(b);
        }, faults);

    public VirtualTimeProvider Time { get; }

    public InMemoryKairosStore Memory { get; }

    public IKairosStore Store { get; }

    public FaultInjectingStore? Faults { get; }

    public KairosClient Client { get; }

    public AgentsRuntime Agents { get; }

    public WorkerRuntime Worker { get; private set; }

    public WorkerRuntime NewWorker() => new(Store, Scope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false, FailedTaskRetryDelay = TimeSpan.FromSeconds(1) });

    public void Workflow(string type, Func<IAgentContext, byte[], Task<byte[]>> body) => _workflows.Register(type, KairosClient.DefaultBuildId, Agents.Workflow(body));

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

public sealed class A2aClientTests
{
    private static A2aRig Calling(FakeAgent agent, string name = "billing", Action<A2aClientsBuilder>? more = null, A2aCallOptions? options = null)
    {
        A2aRig rig = A2aRig.For(agent, more);
        rig.Workflow("call", async (ctx, _) =>
        {
            A2aAgentResult r = await ctx.CallA2aAgentAsync(name, "Pay invoice 42", options);
            return Encoding.UTF8.GetBytes(r.Text ?? "");
        });
        return rig;
    }

    [Fact]
    public async Task An_answer_is_recorded_and_replay_never_contacts_the_agent_again()
    {
        var agent = new FakeAgent();
        A2aRig rig = A2aRig.For(agent);
        rig.Workflow("call", async (ctx, _) =>
        {
            A2aAgentResult r = await ctx.CallA2aAgentAsync("billing", "hello");
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5));
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5));
            return Encoding.UTF8.GetBytes(r.Text!);
        });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));
        Assert.Equal("done", await rig.ResultAsync(run));
        Assert.Equal(1, agent.Sends);
    }

    [Fact]
    public async Task Every_attempt_sends_the_same_message_id_which_is_the_steps_idempotency_key()
    {
        var agent = new FakeAgent { FailFirst = 2 };
        A2aRig rig = Calling(agent);
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Equal(3, agent.Sends);
        Assert.Single(agent.MessageIds.Distinct());
        ActivityScheduled step = (await rig.HistoryAsync(run)).OfType<ActivityScheduled>().Single(a => a.ActivityType == A2aClientRuntime.SendActivity);
        Assert.Equal($"{run.RunId:N}:{step.ActivityId}", agent.MessageIds[0]);
    }

    [Fact]
    public async Task A_task_is_started_once_and_polled_with_durable_timers()
    {
        var agent = new FakeAgent { WorkingPolls = 3, Answer = new A2aReply(A2aState.Completed, "paid", null, "remote-1", "ctx-1", null) };
        A2aRig rig = Calling(agent);
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Equal("paid", await rig.ResultAsync(run));
        Assert.Equal(1, agent.Sends);
        Assert.Equal(4, agent.Polls);
        Assert.Equal(4, (await rig.HistoryAsync(run)).OfType<TimerStarted>().Count());
    }

    [Theory]
    [InlineData(A2aState.Failed, nameof(A2aAgentFailedException))]
    [InlineData(A2aState.Rejected, nameof(A2aAgentRejectedException))]
    [InlineData(A2aState.Canceled, nameof(A2aAgentCancelledException))]
    [InlineData(A2aState.InputRequired, nameof(A2aInputRequiredException))]
    [InlineData(A2aState.AuthRequired, nameof(A2aAuthRequiredException))]
    public async Task A_task_that_does_not_complete_ends_the_call_with_the_matching_exception(A2aState final, string expected)
    {
        var agent = new FakeAgent { WorkingPolls = 1, Final = final };
        A2aRig rig = Calling(agent);
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        WorkflowFailed failure = await rig.FailureAsync(run);
        Assert.Equal(expected, failure.ErrorType);
        if (final == A2aState.Failed)
        {
            Assert.Contains("the remote exploded", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_question_from_the_agent_can_be_answered_by_continuing_the_same_task()
    {
        var agent = new FakeAgent { WorkingPolls = 0, Final = A2aState.InputRequired };
        A2aRig rig = A2aRig.For(agent);
        rig.Workflow("call", async (ctx, _) =>
        {
            try
            {
                await ctx.CallA2aAgentAsync("billing", "Pay it");
                return Encoding.UTF8.GetBytes("no question");
            }
            catch (A2aInputRequiredException ask)
            {
                agent.WorkingPolls = null; // the next message gets a plain answer
                A2aAgentResult answer = await ctx.CallA2aAgentAsync("billing", "Invoice 42", new A2aCallOptions { TaskId = ask.TaskId });
                return Encoding.UTF8.GetBytes($"{ask.Question} -> {answer.Text}");
            }
        });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));
        Assert.Equal("Which invoice? -> done", await rig.ResultAsync(run));
        Assert.Equal(("Invoice 42", "remote-1"), agent.Received[^1]);
    }

    [Fact]
    public async Task A_task_that_never_finishes_is_cancelled_remotely_and_times_out()
    {
        var agent = new FakeAgent { WorkingPolls = int.MaxValue };
        A2aRig rig = Calling(agent, options: new A2aCallOptions { Timeout = TimeSpan.FromMinutes(10) });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));

        Assert.Equal(nameof(TimeoutException), (await rig.FailureAsync(run)).ErrorType);
        Assert.Equal(["remote-1"], agent.Cancelled);
    }

    [Fact]
    public async Task An_unknown_agent_fails_at_once_without_retrying()
    {
        var agent = new FakeAgent();
        A2aRig rig = A2aRig.For(agent);
        rig.Workflow("call", async (ctx, _) =>
        {
            await ctx.CallA2aAgentAsync("nobody", "hi");
            return [];
        });
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        ActivityFailed failure = Assert.Single((await rig.HistoryAsync(run)).OfType<ActivityFailed>());
        Assert.Equal(nameof(UnknownA2aAgentException), failure.ErrorType);
        Assert.False(failure.WillRetry);
    }

    [Fact]
    public async Task Redaction_applies_to_what_is_recorded()
    {
        var agent = new FakeAgent { Answer = new A2aReply(A2aState.Message, "card 4111-1111-1111-1111", null, null, null, null) };
        A2aRig rig = Calling(agent, more: b => b.UseRedactor(t => t.Replace("4111-1111-1111-1111", "[card]", StringComparison.Ordinal)));
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run);

        Assert.Equal("card [card]", await rig.ResultAsync(run));
    }

    // ---- policy and approvals ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_not_declared_read_only_waits_for_a_person_and_then_is_called_with_the_approval()
    {
        var agent = new FakeAgent();
        A2aRig rig = Calling(agent, name: "payments", more: b => b.AddAgent("payments", agent));
        RunKey run = await rig.StartAsync("call");

        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(0, agent.Sends);
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await rig.HistoryAsync(run)));
        Assert.Equal("a2a.payments", pending.Tool);

        await new AgentClient(rig.Client, rig.Time).ApproveAsync(run, pending.ApprovalId, new ApproverIdentity("user:dana"));
        Assert.Equal(RunState.Completed, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(1, agent.Sends);
    }

    [Fact]
    public async Task A_rejected_call_never_reaches_the_agent()
    {
        var agent = new FakeAgent();
        A2aRig rig = Calling(agent, name: "payments", more: b => b.AddAgent("payments", agent));
        RunKey run = await rig.StartAsync("call");
        await rig.RunAsync(run, jumpClock: false);
        string id = AgentHistory.PendingApprovals(await rig.HistoryAsync(run)).Single().ApprovalId;

        await new AgentClient(rig.Client, rig.Time).RejectAsync(run, id, new ApproverIdentity("user:dana"));
        Assert.Equal(RunState.Failed, await rig.RunAsync(run, jumpClock: false));

        Assert.Equal(nameof(ApprovalRejectedException), (await rig.FailureAsync(run)).ErrorType);
        Assert.Equal(0, agent.Sends);
    }

    [Fact]
    public async Task A_denied_agent_is_refused_and_an_allowed_one_runs_without_asking()
    {
        var denied = new FakeAgent();
        A2aRig rigDenied = Calling(denied, name: "wiper", more: b => b.AddAgent("wiper", denied).UsePolicy(new BaselineToolPolicy().Deny("a2a.wiper")));
        RunKey run = await rigDenied.StartAsync("call");
        Assert.Equal(RunState.Failed, await rigDenied.RunAsync(run, jumpClock: false));
        Assert.Equal(nameof(A2aAgentDeniedException), (await rigDenied.FailureAsync(run)).ErrorType);
        Assert.Equal(0, denied.Sends);

        var allowed = new FakeAgent();
        A2aRig rigAllowed = Calling(allowed, name: "archiver", more: b => b.AddAgent("archiver", allowed).UsePolicy(new BaselineToolPolicy().Allow("a2a.archiver")));
        RunKey run2 = await rigAllowed.StartAsync("call");
        Assert.Equal(RunState.Completed, await rigAllowed.RunAsync(run2, jumpClock: false));
    }

    // ---- crashes --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_crash_at_any_store_operation_never_sends_the_message_under_two_ids()
    {
        static A2aRig Build(FakeAgent agent)
        {
            A2aRig rig = A2aRig.For(agent, faults: true);
            rig.Faults!.FaultsEnabled = false;
            rig.Workflow("call", async (ctx, _) => Encoding.UTF8.GetBytes((await ctx.CallA2aAgentAsync("billing", "Pay invoice 42")).Text!));
            return rig;
        }

        var baselineAgent = new FakeAgent { WorkingPolls = 2, Answer = new A2aReply(A2aState.Completed, "done", null, "remote-1", "ctx-1", null) };
        A2aRig baseline = Build(baselineAgent);
        RunKey baseRun = await baseline.StartAsync("call");
        Assert.Equal(RunState.Completed, await baseline.RunAsync(baseRun));
        long operations = baseline.Faults!.OperationCount;
        Assert.True(operations > 10);

        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                var agent = new FakeAgent { WorkingPolls = 2, Answer = new A2aReply(A2aState.Completed, "done", null, "remote-1", "ctx-1", null) };
                A2aRig rig = Build(agent);
                long at = k;
                FaultKind crash = kind;
                rig.Faults!.ForcedFault = index => index == at ? crash : FaultKind.None;

                RunKey run = await rig.StartAsync("call");
                Assert.Equal(RunState.Completed, await rig.RunAsync(run));

                Assert.Equal("done", await rig.ResultAsync(run));
                Assert.True(agent.Sends is >= 1 and <= 2, $"crash {kind} at operation {k}: {agent.Sends} sends");
                Assert.Single(agent.MessageIds.Distinct());
                HashChain.Verify(await rig.HistoryAsync(run));
            }
        }
    }
}
