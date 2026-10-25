using System.Security.Cryptography;
using System.Text;
using A2A;
using Kairos.A2A;
using Kairos.Agents;
using Kairos.Testing;

namespace Kairos.A2A.Tests;

public sealed class KairosA2aTasksTests
{
    private static readonly TenantScope ScopeA = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly TenantScope ScopeB = new(ScopeA.TenantId, Guid.Parse("00000000-0000-0000-0000-0000000000b2"));

    private sealed class Rig
    {
        private readonly WorkflowRegistry _workflows = new();
        private readonly ActivityRegistry _activities = new();

        public Rig(KairosA2aTasksOptions? options = null, TenantScope? scope = null, InMemoryKairosStore? shared = null, VirtualTimeProvider? time = null)
        {
            Scope = scope ?? ScopeA;
            Time = time ?? new VirtualTimeProvider();
            Kairos = shared ?? new InMemoryKairosStore();
            Client = new KairosClient(Kairos, Time);
            Agents = new AgentsBuilder().UseTimeProvider(Time).Build();
            Agents.RegisterInto(_activities);
            Worker = new WorkerRuntime(Kairos, Scope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false });
            Tasks = new KairosA2aTasks(Client, Scope, options);
        }

        public TenantScope Scope { get; }

        public VirtualTimeProvider Time { get; }

        public InMemoryKairosStore Kairos { get; }

        public KairosClient Client { get; }

        public AgentsRuntime Agents { get; }

        public WorkerRuntime Worker { get; }

        public KairosA2aTasks Tasks { get; }

        public void Workflow(string type, Func<IAgentContext, byte[], Task<byte[]>> body) => _workflows.Register(type, KairosClient.DefaultBuildId, Agents.Workflow(body));

        public void Plain(string type, WorkflowFunc body) => _workflows.Register(type, KairosClient.DefaultBuildId, body);

        public void Activity(string type) => _activities.Register(type, (_, input, _) => Task.FromResult(input));

        public async Task<RunKey> StartAsync(string type, string id, byte[]? input = null)
        {
            StartRunResult s = await Client.StartAsync(Scope, id, type, input ?? []);
            return new RunKey(Scope.TenantId, Scope.NamespaceId, s.RunId);
        }

        public async Task<RunState> RunAsync(RunKey run)
        {
            while (true)
            {
                await Driver.DriveAsync(Worker);
                RunState state = (await Client.GetStateAsync(run))!.Value;
                if (state != RunState.Running || Kairos.NextWakeup(Scope, Time.GetUtcNow()) is not { } wake)
                {
                    return state;
                }

                Time.AdvanceTo(wake);
            }
        }
    }

    private sealed class Questions(Func<IReadOnlyList<HistoryEvent>, IReadOnlyDictionary<string, string>> ask) : IA2aInputProjection
    {
        public IReadOnlyDictionary<string, string> OpenQuestions(IReadOnlyList<HistoryEvent> history) => ask(history);
    }

    [Fact]
    public async Task An_open_run_is_working_and_a_completed_run_is_completed_with_its_result_as_an_artifact()
    {
        var rig = new Rig();
        rig.Workflow("job", async (ctx, input) =>
        {
            await ctx.Workflow.WaitForSignalAsync("go");
            return Encoding.UTF8.GetBytes("""{"paid":true,"invoice":"INV-1"}""");
        });
        RunKey run = await rig.StartAsync("job", "j1");

        AgentTask working = (await rig.Tasks.GetAsync(run, default))!;
        Assert.Equal(TaskState.Working, working.Status.State);
        Assert.False(string.IsNullOrEmpty(working.Id));

        await rig.Client.SignalAsync(run, "go", []);
        await rig.RunAsync(run);

        AgentTask done = (await rig.Tasks.GetAsync(working.Id, default))!;
        Assert.Equal(TaskState.Completed, done.Status.State);
        Artifact artifact = Assert.Single(done.Artifacts!);
        Assert.True(artifact.Parts[0].Data!.Value.GetProperty("paid").GetBoolean());
    }

    [Fact]
    public async Task A_result_that_is_not_json_is_a_text_part()
    {
        var rig = new Rig();
        rig.Plain("job", (ctx, input) => Task.FromResult(Encoding.UTF8.GetBytes("all done")));
        RunKey run = await rig.StartAsync("job", "j1");
        await rig.RunAsync(run);

        AgentTask task = (await rig.Tasks.GetAsync(run, default))!;
        Assert.Equal("all done", task.Artifacts![0].Parts[0].Text);
    }

    [Fact]
    public async Task Failure_rejection_cancellation_and_termination_map_to_their_own_states()
    {
        var rig = new Rig();
        rig.Plain("fails", (ctx, input) => throw new InvalidOperationException("no stock"));
        rig.Workflow("refused", async (ctx, input) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Wire money" })).ThrowIfNotApproved();
            return input;
        });
        rig.Workflow("cancellable", async (ctx, input) =>
        {
            await ctx.RunSagaAsync(async saga => await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Wait" }));
            return input;
        });
        rig.Plain("stuck", async (ctx, input) =>
        {
            await ctx.WaitForSignalAsync("never");
            return input;
        });

        RunKey failed = await rig.StartAsync("fails", "f");
        RunKey refused = await rig.StartAsync("refused", "r");
        RunKey cancellable = await rig.StartAsync("cancellable", "c");
        RunKey terminated = await rig.StartAsync("stuck", "t");
        await rig.RunAsync(failed);
        await rig.RunAsync(refused);
        await rig.RunAsync(cancellable);
        await rig.RunAsync(terminated);

        string approvalId = AgentHistory.PendingApprovals(await rig.Client.GetOpenedHistoryAsync(refused)).Single().ApprovalId;
        await new AgentClient(rig.Client, rig.Time).RejectAsync(refused, approvalId, new ApproverIdentity("user:dana"));
        AgentTask cancelling = (await rig.Tasks.RequestCancelAsync(rig.Tasks.TaskIdFor(cancellable), "changed my mind", default))!;
        await rig.Kairos.FailRunAsync(terminated, "stopped by an operator", "ops", rig.Time.GetUtcNow(), default);
        await rig.RunAsync(refused);
        await rig.RunAsync(cancellable);

        AgentTask failedTask = (await rig.Tasks.GetAsync(failed, default))!;
        Assert.Equal(TaskState.Failed, failedTask.Status.State);
        Assert.Contains("no stock", failedTask.Status.Message!.Parts[0].Text, StringComparison.Ordinal);
        Assert.Equal(TaskState.Rejected, (await rig.Tasks.GetAsync(refused, default))!.Status.State);
        Assert.Equal(TaskState.Canceled, (await rig.Tasks.GetAsync(cancellable, default))!.Status.State);
        Assert.NotNull(cancelling);
        AgentTask terminatedTask = (await rig.Tasks.GetAsync(terminated, default))!;
        Assert.Equal(TaskState.Failed, terminatedTask.Status.State);
        Assert.Contains("stopped by an operator", terminatedTask.Status.Message!.Parts[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_task_that_has_ended_changes_nothing()
    {
        var rig = new Rig();
        rig.Plain("job", (ctx, input) => Task.FromResult(input));
        RunKey run = await rig.StartAsync("job", "j1");
        await rig.RunAsync(run);

        AgentTask task = (await rig.Tasks.RequestCancelAsync(rig.Tasks.TaskIdFor(run), "too late", default))!;

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Null(await rig.Tasks.RequestCancelAsync("kat1_nonsense", "x", default));
    }

    [Fact]
    public async Task Open_questions_make_the_task_input_required_and_the_questions_are_the_status_message()
    {
        var questions = new Questions(history => AgentHistory.PendingApprovals(history).ToDictionary(p => p.ApprovalId, p => p.Action));
        var rig = new Rig(new KairosA2aTasksOptions { Inputs = questions });
        rig.Workflow("pay", async (ctx, input) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Release 500 EUR" })).ThrowIfNotApproved();
            return input;
        });
        RunKey run = await rig.StartAsync("pay", "p1");
        await rig.RunAsync(run);

        AgentTask task = (await rig.Tasks.GetAsync(run, default))!;

        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal("Release 500 EUR", task.Status.Message!.Parts[0].Text);
    }

    [Fact]
    public async Task A_task_follows_its_run_through_continue_as_new_and_keeps_its_id()
    {
        var rig = new Rig();
        rig.Plain("long", async (ctx, input) =>
        {
            if (input.Length == 0)
            {
                throw ctx.ContinueAsNew(new byte[] { 1 });
            }

            await ctx.WaitForSignalAsync("finish");
            return Encoding.UTF8.GetBytes("""{"generation":2}""");
        });
        RunKey first = await rig.StartAsync("long", "l1");
        string id = rig.Tasks.TaskIdFor(first);
        await rig.RunAsync(first);

        Assert.Equal(TaskState.Working, (await rig.Tasks.GetAsync(id, default))!.Status.State);
        RunKey next = (await rig.Kairos.GetCurrentRunAsync(rig.Scope, "l1", default))!.Value;
        await rig.Client.SignalAsync(next, "finish", []);
        await rig.RunAsync(next);

        AgentTask done = (await rig.Tasks.GetAsync(id, default))!;
        Assert.Equal(id, done.Id);
        Assert.Equal(TaskState.Completed, done.Status.State);
    }

    [Fact]
    public async Task A_task_is_read_from_the_database_so_another_process_sees_the_same_task()
    {
        var first = new Rig();
        first.Plain("job", (ctx, input) => Task.FromResult(Encoding.UTF8.GetBytes("ok")));
        RunKey run = await first.StartAsync("job", "j1");
        await first.RunAsync(run);

        var second = new Rig(shared: first.Kairos, time: first.Time);

        Assert.Equal(TaskState.Completed, (await second.Tasks.GetAsync(first.Tasks.TaskIdFor(run), default))!.Status.State);
    }

    [Fact]
    public async Task Another_namespaces_task_id_and_altered_or_guessed_ids_find_nothing()
    {
        var a = new Rig(new KairosA2aTasksOptions { IdKey = RandomNumberGenerator.GetBytes(32) });
        a.Plain("job", (ctx, input) => Task.FromResult(input));
        RunKey run = await a.StartAsync("job", "j1");
        string id = a.Tasks.TaskIdFor(run);
        var b = new Rig(scope: ScopeB, shared: a.Kairos, time: a.Time);
        string flipped = id[..^2] + (id[^2] == 'A' ? 'B' : 'A') + id[^1];

        Assert.NotNull(await a.Tasks.GetAsync(id, default));
        Assert.Null(await b.Tasks.GetAsync(id, default));
        Assert.Null(await b.Tasks.RequestCancelAsync(id, "x", default));
        Assert.Null(await a.Tasks.GetAsync(flipped, default));
        foreach (string junk in new[] { "", "kat1_", "kat1_!!", "kmt1_AAAA", Guid.NewGuid().ToString("N"), new string('x', 400) })
        {
            Assert.Null(await a.Tasks.GetAsync(junk, default));
        }

        Assert.Throws<ArgumentException>(() => b.Tasks.TaskIdFor(run));
        Assert.Throws<ArgumentException>(() => new KairosA2aTasks(a.Client, ScopeA, new KairosA2aTasksOptions { IdKey = new byte[8] }));
    }
}
