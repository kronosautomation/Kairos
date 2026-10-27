using Kairos.Agents;
using Kairos.Testing.Agents;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// Golden histories of durable workflows (<c>Corpus/v6</c>), recorded through the adapter and replayed by every later build: the workflow driver is code in a package, and a run in flight when a new version is deployed replays
/// against it. A change to the driver that no longer reproduces these histories would break every workflow in flight, so it fails here. Never re-record a file to make a test pass; record new files for new behaviour. Record once per
/// release into a directory of its own: <c>KAIROS_CORPUS_RECORD_AFW=&lt;dir&gt; dotnet test --filter Record_the_workflow_corpus</c>.
/// </summary>
public sealed class WorkflowCorpusTests
{
    private const string ReplayWith = "af-workflow";
    private const string Recorded = "v6";
    private static readonly ApproverIdentity Alice = WorkflowTests.Alice;

    private static readonly (string File, string Description)[] Files =
    [
        ("afw-plain.khist", "a workflow of four supersteps: one step per superstep, a checkpoint whole in the first and as changes after it, and the output"),
        ("afw-approval.khist", "a request through a port registered as an approval: the workflow waits for a verified person, who approves, and goes on"),
        ("afw-signal.khist", "a request through a port answered by signal: the workflow waits, the answer arrives, the workflow goes on"),
        ("afw-restored.khist", "the answer to a request arrived on a worker that did not hold the checkpoint: the step said so, the workflow sent the checkpoint once, and the step ran"),
        ("afw-agent.khist", "an executor asked a durable agent: the conversation is named by the step and is listed as the workflow's"),
        ("afw-cancelled.khist", "a workflow waiting for a request is cancelled: the cancel and the end, with the conversation its executors started asked to stop"),
        ("afw-continue-first.khist", "a long workflow: the first run, ended by handing over to a fresh run with the checkpoint"),
        ("afw-continue-second.khist", "a long workflow: the fresh run, started with the checkpoint and the outputs so far"),
        ("afw-version-change.khist", "a run whose Agent Framework package changed while it waited: a person allowed it, and the override is in history"),
    ];

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Corpus", Recorded);

    private static Action<AgentFrameworkBuilder> Registrations(string file, Ledger ledger, ScriptedChatClient? writer = null) => af =>
    {
        switch (file)
        {
            case "afw-plain.khist":
            case "afw-continue-first.khist":
            case "afw-continue-second.khist":
                af.AddWorkflow<string>("chain", _ => TestWorkflows.Chain(ledger));
                break;
            case "afw-approval.khist":
            case "afw-restored.khist":
                af.AddWorkflow<string>("approval", _ => TestWorkflows.Approval(ledger), r => r.RequestsAsApprovals("approve", new ApprovalRequirement { Roles = ["finance"] }));
                break;
            case "afw-signal.khist":
            case "afw-version-change.khist":
                af.AddWorkflow<string>("question", _ => TestWorkflows.Question(ledger));
                break;
            case "afw-agent.khist":
            case "afw-cancelled.khist":
                af.AddAgent(AfHarness.AgentWith(writer ?? AfHarness.Echo("an old pond"), "writer"));
                af.AddWorkflow<string>("agentic", ctx => TestWorkflows.AgentThenQuestion(ledger, ctx.Agents));
                break;
            default:
                throw new InvalidOperationException(file);
        }
    };

    private static string WorkflowOf(string file) => file switch
    {
        "afw-plain.khist" or "afw-continue-first.khist" or "afw-continue-second.khist" => "chain",
        "afw-approval.khist" or "afw-restored.khist" => "approval",
        "afw-signal.khist" or "afw-version-change.khist" => "question",
        _ => "agentic",
    };

    // ---- recording (a release step) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Record_the_workflow_corpus()
    {
        string? target = Environment.GetEnvironmentVariable("KAIROS_CORPUS_RECORD_AFW");
        if (string.IsNullOrEmpty(target))
        {
            return; // recording is a release step, not part of normal runs
        }

        Directory.CreateDirectory(target);
        var recorded = new Dictionary<string, IReadOnlyList<HistoryEvent>>();

        var ledger = new Ledger();
        var h = new AfHarness(Registrations("afw-plain.khist", ledger));
        DurableWorkflowRun run = await WorkflowTests.StartAsync(h, "chain", "x");
        await WorkflowTests.FinishAsync(h, run);
        recorded["afw-plain.khist"] = await h.HistoryAsync(run.Run);

        h = new AfHarness(Registrations("afw-approval.khist", ledger));
        run = await WorkflowTests.StartAsync(h, "approval", "refund A-1");
        await WorkflowTests.FinishAsync(h, run);
        await h.Approvals.ApproveAsync(run.Run, AgentHistory.PendingApprovals(await h.HistoryAsync(run.Run)).Single().ApprovalId, Alice);
        await WorkflowTests.FinishAsync(h, run);
        recorded["afw-approval.khist"] = await h.HistoryAsync(run.Run);

        h = new AfHarness(Registrations("afw-signal.khist", ledger));
        run = await WorkflowTests.StartAsync(h, "question", "what colour?");
        WorkflowView waiting = await WorkflowTests.FinishAsync(h, run);
        await run.RespondAsync(waiting.Pending[0].RequestId, "blue");
        await WorkflowTests.FinishAsync(h, run);
        recorded["afw-signal.khist"] = await h.HistoryAsync(run.Run);

        h = new AfHarness(Registrations("afw-restored.khist", ledger));
        run = await WorkflowTests.StartAsync(h, "approval", "refund A-1");
        await WorkflowTests.FinishAsync(h, run);
        var elsewhere = new AfHarness(Registrations("afw-restored.khist", ledger), redeployOf: h); // a worker that has never seen the checkpoint
        await elsewhere.Approvals.ApproveAsync(run.Run, AgentHistory.PendingApprovals(await elsewhere.HistoryAsync(run.Run)).Single().ApprovalId, Alice);
        await WorkflowTests.FinishAsync(elsewhere, run);
        recorded["afw-restored.khist"] = await elsewhere.HistoryAsync(run.Run);

        h = new AfHarness(Registrations("afw-agent.khist", ledger));
        run = await WorkflowTests.StartAsync(h, "agentic", "write a haiku");
        await h.DriveParallelAsync();
        recorded["afw-agent.khist"] = await h.HistoryAsync(run.Run);

        h = new AfHarness(Registrations("afw-cancelled.khist", ledger));
        run = await WorkflowTests.StartAsync(h, "agentic", "write a haiku");
        await h.DriveParallelAsync();
        await run.CancelAsync("changed our mind");
        await h.DriveParallelAsync();
        recorded["afw-cancelled.khist"] = await h.HistoryAsync(run.Run);

        h = new AfHarness(af => Registrations("afw-continue-first.khist", ledger)(af.Configure(o => o.ContinueAfterEvents = 8)));
        run = await WorkflowTests.StartAsync(h, "chain", "x");
        await WorkflowTests.FinishAsync(h, run);
        var histories = new List<IReadOnlyList<HistoryEvent>>();
        foreach (RunSummary r in await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default))
        {
            histories.Add(await h.HistoryAsync(r.Run));
        }

        IReadOnlyList<HistoryEvent> firstRun = histories.Single(x => ((WorkflowStarted)x[0]).PreviousRunId == Guid.Empty);
        IReadOnlyList<HistoryEvent> secondRun = histories.First(x => ((WorkflowStarted)x[0]).PreviousRunId == firstRun[0].RunId);
        Assert.IsType<WorkflowContinued>(firstRun[^1]);
        recorded["afw-continue-first.khist"] = firstRun;
        recorded["afw-continue-second.khist"] = secondRun;

        h = new AfHarness(Registrations("afw-version-change.khist", ledger));
        run = await WorkflowTests.StartAsync(h, "question", "x");
        waiting = await WorkflowTests.FinishAsync(h, run);
        var newer = new AfHarness(Registrations("afw-version-change.khist", ledger), redeployOf: h);
        newer.Runtime.WorkflowPackageVersion = "1.25.0.0";
        await run.RespondAsync(waiting.Pending[0].RequestId, "blue");
        await newer.DriveAsync();
        await newer.Approvals.ApproveAsync(run.Run, AgentHistory.PendingApprovals(await newer.HistoryAsync(run.Run)).Single().ApprovalId, Alice);
        await WorkflowTests.FinishAsync(newer, run);
        recorded["afw-version-change.khist"] = await newer.HistoryAsync(run.Run);

        foreach ((string file, string description) in Files)
        {
            await File.WriteAllTextAsync(Path.Combine(target, file), HistoryFile.Serialize(recorded[file], Recorded, description, ReplayWith));
        }
    }

    // ---- replay (every build) ----------------------------------------------------------------------------------------

    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach ((string file, _) in Files)
        {
            data.Add(file);
        }

        return data;
    }

    private static List<HistoryEvent> Load(string file, out HistoryFileContents contents)
    {
        string path = Path.Combine(Dir, file);
        Assert.True(File.Exists(path), "the v6 corpus file is missing: " + path);
        contents = HistoryFile.ParseVerified(File.ReadAllText(path));
        return [.. contents.History];
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void The_file_verifies_and_every_event_keeps_its_exact_bytes(string file)
    {
        List<HistoryEvent> history = Load(file, out HistoryFileContents contents);

        Assert.Equal(ReplayWith, contents.ReplayWith);
        Assert.NotEmpty(history);
        foreach (HistoryEvent e in history)
        {
            Assert.Equal(e.Hash, HashChain.ComputeHash(HistoryEventCodec.Decode(HistoryEventCodec.Encode(e), e.PrevHash, e.Hash), e.PrevHash));
        }
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void Replay_reproduces_the_recorded_run_and_asks_for_nothing_new(string file)
    {
        List<HistoryEvent> history = Load(file, out _);
        var builder = new AgentFrameworkBuilder();
        Registrations(file, new Ledger())(builder);
        AgentFrameworkRuntime runtime = AgentFrameworkRuntime.Create(builder);

        ReplayResult replay = WorkflowReplayer.Replay(runtime.WorkflowRun, history);

        Assert.Empty(replay.NewCommands);
        Assert.Equal(history[^1] is WorkflowCompleted or WorkflowFailed or WorkflowContinued, replay.IsClosed);
        _ = WorkflowOf(file);
    }

    [Fact]
    public void The_recorded_runs_still_hold_what_they_were_recorded_to_hold()
    {
        List<HistoryEvent> plain = Load("afw-plain.khist", out _);
        Assert.IsType<WorkflowCompleted>(plain[^1]);
        Assert.Equal(4, plain.OfType<ActivityScheduled>().Count(a => a.ActivityType == AgentFrameworkNames.WorkflowStep));

        Assert.Equal("user:alice", Assert.Single(AgentFrameworkAudit.Read(Load("afw-approval.khist", out _)).OfType<ApprovalAuditRecord>()).Approver);
        Assert.Contains(Load("afw-signal.khist", out _), e => e is SignalReceived s && s.SignalName.StartsWith("af.workflow.response/", StringComparison.Ordinal));
        Assert.Contains(Load("afw-restored.khist", out _), e => e is ActivityCompleted c && System.Text.Encoding.UTF8.GetString(c.Result).Contains("need-snapshot", StringComparison.Ordinal));
        Assert.Contains(Load("afw-agent.khist", out _), e => e is ActivityCompleted c && System.Text.Encoding.UTF8.GetString(c.Result).Contains("af/writer/wf.", StringComparison.Ordinal));
        Assert.Contains(Load("afw-cancelled.khist", out _), e => e is ActivityScheduled a && a.ActivityType == AgentFrameworkNames.WorkflowCancel);
        Assert.IsType<WorkflowContinued>(Load("afw-continue-first.khist", out _)[^1]);
        Assert.Equal("user:alice", Assert.Single(AgentFrameworkAudit.Read(Load("afw-version-change.khist", out _)).OfType<WorkflowOverrideAuditRecord>()).Approver);
    }
}
