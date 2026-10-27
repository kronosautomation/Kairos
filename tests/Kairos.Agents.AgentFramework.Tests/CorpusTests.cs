using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// Golden histories recorded through the adapter (<c>Corpus/v4</c>) and replayed by every later build: the driver workflow is code in a package, and a run in flight when a new version is deployed replays against it. A change to the
/// driver that no longer reproduces these histories would break every conversation in flight, so it fails here. Never re-record a file to make a test pass; record new files for new behaviour.
/// Record once per release: <c>KAIROS_CORPUS_RECORD_AF=&lt;dir&gt; dotnet test --filter Record_the_agent_framework_corpus</c>.
/// </summary>
public sealed class CorpusTests
{
    private const string ReplayWith = "af-conversation";
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly ApproverIdentity Bob = new("user:bob", "Bob", ["ops"]);

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Corpus", "v4");

    /// <summary>What each file is, and the registrations to replay it with (the agents of the recording, by name).</summary>
    private static readonly (string File, string Description)[] Files =
    [
        ("af-plain.khist", "a conversation with an agent without tools: one message, one model call, the answer"),
        ("af-tools-approval.khist", "a support run: a read-only lookup, then a refund the author required approval for, approved by a verified person, then the answer"),
        ("af-rejected.khist", "the same run with the refund rejected: the tool is not run and the agent is told"),
        ("af-budget-pause.khist", "a model call a budget refused and paused; a person raised it; the call went through"),
        ("af-continue-first.khist", "a long conversation: the first run, ended by handing over to a fresh run"),
        ("af-continue-second.khist", "a long conversation: the fresh run, started with the session and the unanswered message"),
        ("af-nested-parent.khist", "an agent that asked a nested durable agent: the parent run, waiting for the child's answer by signal"),
        ("af-nested-child.khist", "a nested durable agent answering one question for its parent and telling it"),
        ("af-definition-change.khist", "a run whose agent definition changed while it waited: a person allowed it, and the override is in history"),
        ("af-turn-level.khist", "an agent recorded a turn at a time (it cannot be run one model call at a time)"),
    ];

    private static AfHarness Plain(out ScriptedChatClient model)
    {
        model = AfHarness.Echo("hello back");
        return new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo("hello back"), "chat")));
    }

    private static AgentFrameworkRuntime ReplayRuntime(string file)
    {
        var builder = new AgentFrameworkBuilder();
        switch (file)
        {
            case "af-plain.khist":
                builder.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat"));
                break;
            case "af-tools-approval.khist":
            case "af-rejected.khist":
                builder.AddAgent(AfHarness.SupportAgent(AfHarness.Echo()), r => r.ReadOnly("lookup_order"));
                break;
            case "af-budget-pause.khist":
                builder.ConfigureAgents(b => b.UseModelCallGuard(new CountingGuard())).AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "a"), r => r.Model("gpt-test", 100));
                break;
            case "af-continue-first.khist":
            case "af-continue-second.khist":
                builder.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat"));
                break;
            case "af-nested-parent.khist":
            case "af-nested-child.khist":
                builder.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "manager", DurableAgentFunction.For("researcher")), r => r.ReadOnly("researcher"));
                builder.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "researcher"));
                break;
            case "af-definition-change.khist":
                builder.AddAgent(AfHarness.SupportAgent(AfHarness.Echo(), instructions: "version two"), r => r.ReadOnly("lookup_order"));
                break;
            case "af-turn-level.khist":
                builder.AddAgent(new OpaqueAgent("remote", (_, _) => "x"), r => r.AllowTurnLevel());
                break;
            default:
                throw new InvalidOperationException(file);
        }

        return AgentFrameworkRuntime.Create(builder);
    }

    // ---- recording (a release step) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Record_the_agent_framework_corpus()
    {
        string? target = Environment.GetEnvironmentVariable("KAIROS_CORPUS_RECORD_AF");
        if (string.IsNullOrEmpty(target))
        {
            return; // recording is a release step, not part of normal runs
        }

        Directory.CreateDirectory(target);
        var recorded = new Dictionary<string, IReadOnlyList<HistoryEvent>>();

        AfHarness h = Plain(out _);
        await h.AskAsync("chat", "s", "hello");
        recorded["af-plain.khist"] = await h.HistoryAsync(await h.RunOfAsync("chat", "s"));

        h = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(AfHarness.LookupRefundAnswer()), r => r.ReadOnly("lookup_order")));
        await h.AskAsync("support", "s", "refund A-1042", onBehalfOf: "customer:42");
        RunKey run = await h.RunOfAsync("support", "s");
        await h.Approvals.ApproveAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice);
        await h.DriveAsync();
        recorded["af-tools-approval.khist"] = await h.HistoryAsync(run);

        h = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(AfHarness.LookupRefundAnswer("not refunded")), r => r.ReadOnly("lookup_order")));
        await h.AskAsync("support", "s", "refund A-1042");
        run = await h.RunOfAsync("support", "s");
        await h.Approvals.RejectAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice, "no");
        await h.DriveAsync();
        recorded["af-rejected.khist"] = await h.HistoryAsync(run);

        bool refused = false;
        var guard = new CountingGuard
        {
            Decide = info =>
            {
                if (!refused && info.AllowanceApprovalId is null)
                {
                    refused = true;
                    return ModelCallPermit.Refuse("daily-tokens", "pause");
                }

                return ModelCallPermit.Allow("r");
            },
        };
        h = new AfHarness(af => af.ConfigureAgents(b => b.UseModelCallGuard(guard)).AddAgent(AfHarness.AgentWith(AfHarness.Echo("answer"), "a"), r => r.Model("gpt-test", 100)));
        await h.AskAsync("a", "s", "go");
        run = await h.RunOfAsync("a", "s");
        await h.Approvals.ApproveAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice);
        await h.DriveAsync();
        recorded["af-budget-pause.khist"] = await h.HistoryAsync(run);

        h = new AfHarness(af => af.Configure(o => o.ContinueAfterEvents = 8).AddAgent(AfHarness.AgentWith(AfHarness.Echo("answer"), "chat")));
        for (int i = 1; i <= 3; i++)
        {
            await h.AskAsync("chat", "long", $"message {i}");
        }

        var histories = new List<IReadOnlyList<HistoryEvent>>();
        foreach (RunSummary r in await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default))
        {
            histories.Add(await h.HistoryAsync(r.Run));
        }

        // The chain of runs: the one nothing came before, and the one that names it.
        IReadOnlyList<HistoryEvent> firstRun = histories.Single(x => ((WorkflowStarted)x[0]).PreviousRunId == Guid.Empty);
        IReadOnlyList<HistoryEvent> secondRun = histories.Single(x => ((WorkflowStarted)x[0]).PreviousRunId == firstRun[0].RunId);
        Assert.IsType<WorkflowContinued>(firstRun[^1]);
        recorded["af-continue-first.khist"] = firstRun;
        recorded["af-continue-second.khist"] = secondRun;

        var manager = new ScriptedChatClient(messages =>
        {
            FunctionResultContent[] results = [.. messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
            return results.Length == 0
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ask-1", "researcher", AfHarness.Args("""{"query":"where is it?"}"""))])
                : new ChatMessage(ChatRole.Assistant, "researcher says " + results[0].Result);
        });
        h = new AfHarness(af =>
        {
            af.AddAgent(AfHarness.AgentWith(manager, "manager", DurableAgentFunction.For("researcher")), r => r.ReadOnly("researcher"));
            af.AddAgent(AfHarness.AgentWith(AfHarness.Echo("it is on its way"), "researcher"));
        });
        await h.AskAsync("manager", "s", "help me");
        RunSummary[] pair = [.. await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)];
        recorded["af-nested-parent.khist"] = await h.HistoryAsync(pair.Single(r => r.WorkflowId.StartsWith("af/manager/", StringComparison.Ordinal)).Run);
        recorded["af-nested-child.khist"] = await h.HistoryAsync(pair.Single(r => r.WorkflowId.StartsWith("af/researcher/", StringComparison.Ordinal)).Run);

        h = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(AfHarness.LookupRefundAnswer(), instructions: "version one"), r => r.ReadOnly("lookup_order")));
        await h.AskAsync("support", "s", "refund A-1042");
        run = await h.RunOfAsync("support", "s");
        string refundApproval = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
        var h2 = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(AfHarness.LookupRefundAnswer(), instructions: "version two"), r => r.ReadOnly("lookup_order")), redeployOf: h);
        await h2.Approvals.ApproveAsync(run, refundApproval, Alice);
        await h2.DriveAsync();
        await h2.Approvals.ApproveAsync(run, AgentHistory.PendingApprovals(await h2.HistoryAsync(run)).Single().ApprovalId, Bob);
        await h2.DriveAsync();
        recorded["af-definition-change.khist"] = await h2.HistoryAsync(run);

        var opaque = new OpaqueAgent("remote", (_, turn) => $"reply {turn}");
        h = new AfHarness(af => af.AddAgent(opaque, r => r.AllowTurnLevel()));
        await h.AskAsync("remote", "s", "one");
        await h.AskAsync("remote", "s", "two");
        recorded["af-turn-level.khist"] = await h.HistoryAsync(await h.RunOfAsync("remote", "s"));

        foreach ((string file, string description) in Files)
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(target, file), HistoryFile.Serialize(recorded[file], "v4", description, ReplayWith));
        }
    }

    // ---- replay (every build) ----------------------------------------------------------------------------------------

    public static TheoryData<string> FileNames() => [.. Files.Select(f => f.File)];

    private static List<HistoryEvent> Load(string file, out HistoryFileContents contents)
    {
        string path = Path.Combine(Dir, file);
        Assert.True(System.IO.File.Exists(path), "the v4 corpus file is missing: " + path);
        contents = HistoryFile.ParseVerified(System.IO.File.ReadAllText(path));
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
        AgentFrameworkRuntime runtime = ReplayRuntime(file);

        ReplayResult replay = WorkflowReplayer.Replay(runtime.Conversation, history);

        Assert.Empty(replay.NewCommands);
        Assert.Equal(history[^1] is WorkflowCompleted or WorkflowFailed or WorkflowContinued, replay.IsClosed);
    }

    [Fact]
    public void The_recorded_runs_still_hold_what_they_were_recorded_to_hold()
    {
        IReadOnlyList<AgentAuditRecord> approval = AgentFrameworkAudit.Read(Load("af-tools-approval.khist", out _));
        Assert.Equal("user:alice", Assert.Single(approval.OfType<ApprovalAuditRecord>()).Approver);

        DefinitionOverrideAuditRecord overridden = Assert.Single(AgentFrameworkAudit.Read(Load("af-definition-change.khist", out _)).OfType<DefinitionOverrideAuditRecord>());
        Assert.Equal(("support", "user:bob"), (overridden.Agent, overridden.Approver));

        Assert.IsType<WorkflowContinued>(Load("af-continue-first.khist", out _)[^1]);
        Assert.Contains(Load("af-nested-parent.khist", out _), e => e is SignalReceived s && s.SignalName.StartsWith("af.child.", StringComparison.Ordinal));
        Assert.All(Load("af-turn-level.khist", out _).OfType<ActivityScheduled>(), a => Assert.Equal(AgentFrameworkNames.Turn, a.ActivityType));
        Assert.Equal(45L, AgentUsageOf("af-tools-approval.khist")); // three model calls of 15 tokens
    }

    private static long AgentUsageOf(string file) => AgentHistory.TotalUsage(Load(file, out _)).TotalTokens ?? 0;
}
