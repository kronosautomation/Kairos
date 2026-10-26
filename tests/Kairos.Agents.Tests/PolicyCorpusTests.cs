using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

/// <summary>
/// A golden history with recorded policy decisions (<c>Corpus/v3</c>), replayed by every later build. Replay reads the decisions that were recorded: a policy that has changed since (or is broken)
/// cannot change what a running workflow already decided, and the history keeps verifying and keeps its bytes. Never re-record this file to make a test pass; the workflow below is frozen with it.
/// Record once per release: <c>KAIROS_CORPUS_RECORD_POLICY=&lt;dir&gt; dotnet test --filter Record_the_policy_corpus</c>.
/// </summary>
public sealed class PolicyCorpusTests
{
    private static string File_ => Path.Combine(AppContext.BaseDirectory, "Corpus", "v3", "policy-decisions.khist");

    private sealed class FrozenPolicy(Func<string, JsonElement, ToolDecision> decide) : IRecordedToolPolicy
    {
        public int Evaluations { get; private set; }

        public ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken)
        {
            Evaluations++;
            using JsonDocument args = JsonDocument.Parse(context.ArgumentsJson);
            return ValueTask.FromResult(decide(context.Tool.Name, args.RootElement.Clone()));
        }
    }

    private static PolicyDecisionRecord R(string rule, string effect, params string[] fields) => new(rule, 7, new string('c', 64), effect, fields);

    /// <summary>The policy of the recording: small refunds run, a wipe is denied, large refunds need two finance approvers.</summary>
    private static ToolDecision Recorded(string tool, JsonElement args) =>
        tool == "wipe"
            ? new ToolDecision(ToolVerdict.Deny, "denied by rule 'no-wipe'", Record: R("no-wipe", "deny"))
            : args.GetProperty("amount").GetInt32() < 100
                ? new ToolDecision(ToolVerdict.Auto, Record: R("small-refunds", "allow", "amount"))
                : new ToolDecision(ToolVerdict.RequireApproval, "a person must approve", new ApprovalRequirement(Roles: ["finance"], Count: 2), R("large-refunds", "require_approval", "amount"));

    private static AgentsRuntime Runtime(IToolPolicy policy) =>
        new AgentsBuilder().UseTimeProvider(new Kairos.Testing.VirtualTimeProvider()).UsePolicy(policy)
            .AddModel("m", new Kairos.Testing.Agents.ScriptedChatClient(_ => new ChatMessage(ChatRole.Assistant, "x")))
            .AddTool(AgentHarness.WriteTool("refund", _ => "refunded")).AddTool(AgentHarness.WriteTool("wipe", _ => "wiped")).Build();

    /// <summary>FROZEN: replayed against recorded histories. Changing it breaks replay of every run recorded by an earlier build.</summary>
    private static async Task<byte[]> Body(IAgentContext ctx, byte[] input)
    {
        var outcomes = new List<string>();
        foreach ((string tool, int amount) in new[] { ("refund", 20), ("wipe", 0), ("refund", 250) })
        {
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c-" + outcomes.Count, tool, AgentHarness.WireArgs($$"""{"amount":{{amount}}}""")));
            outcomes.Add(r.Outcome.ToString());
        }

        return Encoding.UTF8.GetBytes(string.Join(',', outcomes));
    }

    [Fact]
    public async Task Record_the_policy_corpus()
    {
        string? target = Environment.GetEnvironmentVariable("KAIROS_CORPUS_RECORD_POLICY");
        if (string.IsNullOrEmpty(target))
        {
            return; // recording is a release step, not part of normal runs
        }

        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()).UsePolicy(new FrozenPolicy(Recorded))
            .AddTool(AgentHarness.WriteTool("refund", _ => "refunded")).AddTool(AgentHarness.WriteTool("wipe", _ => "wiped")));
        h.Workflow("policy-corpus", Body);
        RunKey run = await h.StartAsAsync(new Principal(AgentHarness.Scope, Role.Operator, "ci-pipeline", null), "policy-corpus");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;
        await h.Agent.ApproveAsync(run, id, new ApproverIdentity("user:alice", "Alice", ["finance"]));
        await h.Agent.ApproveAsync(run, id, new ApproverIdentity("user:bob", "Bob", ["finance"]));
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Directory.CreateDirectory(target);
        await System.IO.File.WriteAllTextAsync(
            Path.Combine(target, "policy-decisions.khist"),
            HistoryFile.Serialize(await h.HistoryAsync(run), "v3", "agent run with recorded policy decisions: allow, deny, and a two-person approval", "policy-corpus"));
    }

    private static List<HistoryEvent> Load(out HistoryFileContents file)
    {
        Assert.True(System.IO.File.Exists(File_), "the v3 corpus file is missing: " + File_);
        file = HistoryFile.ParseVerified(System.IO.File.ReadAllText(File_));
        return [.. file.History];
    }

    [Fact]
    public void The_file_verifies_and_every_event_keeps_its_exact_bytes()
    {
        List<HistoryEvent> history = Load(out HistoryFileContents file);

        Assert.Equal("policy-corpus", file.ReplayWith);
        foreach (HistoryEvent e in history)
        {
            Assert.Equal(e.Hash, HashChain.ComputeHash(HistoryEventCodec.Decode(HistoryEventCodec.Encode(e), e.PrevHash, e.Hash), e.PrevHash));
        }

        Assert.Contains(history.OfType<ActivityScheduled>(), s => s.ActivityType == AgentActivityNames.Policy);
    }

    [Fact]
    public void The_decisions_it_recorded_are_read_back_unchanged()
    {
        IReadOnlyList<AgentAuditRecord> records = AgentAudit.Read(Load(out _));

        PolicyDecisionAuditRecord[] decisions = [.. records.OfType<PolicyDecisionAuditRecord>()];
        Assert.Equal(
            [("small-refunds", ToolVerdict.Auto), ("no-wipe", ToolVerdict.Deny), ("large-refunds", ToolVerdict.RequireApproval)],
            decisions.Select(d => (d.Rule!, d.Verdict)));
        Assert.All(decisions.Where(d => d.Rule != "no-wipe"), d => Assert.Equal((7L, new string('c', 64)), (d.PolicyVersion, d.PolicyHash)));
        ApprovalAuditRecord approval = Assert.Single(records.OfType<ApprovalAuditRecord>());
        Assert.Equal(("user:alice, user:bob", ApprovalAuditOutcome.Approved), (approval.Approver, approval.Outcome));
    }

    [Theory]
    [InlineData("the policy that recorded it")]
    [InlineData("a policy that denies everything")]
    [InlineData("a policy that allows everything")]
    [InlineData("a policy that throws")]
    public void Replay_uses_the_recorded_decisions_whatever_the_policy_is_now(string policyNow)
    {
        List<HistoryEvent> history = Load(out _);
        var policy = new FrozenPolicy((tool, args) => policyNow switch
        {
            "the policy that recorded it" => Recorded(tool, args),
            "a policy that denies everything" => new ToolDecision(ToolVerdict.Deny, "no", Record: R("deny-all", "deny")),
            "a policy that allows everything" => new ToolDecision(ToolVerdict.Auto, Record: R("allow-all", "allow")),
            _ => throw new InvalidOperationException("the policy store is down"),
        });
        AgentsRuntime runtime = Runtime(policy);

        ReplayResult replay = WorkflowReplayer.Replay(runtime.Workflow(Body), history);

        Assert.True(replay.IsClosed);
        Assert.Empty(replay.NewCommands);
        Assert.Equal(0, policy.Evaluations); // nothing was asked: the decisions are in history
        Assert.Equal("Completed,Denied,Completed", Encoding.UTF8.GetString(((WorkflowCompleted)history[^1]).Result));
        Assert.Equal(
            ["small-refunds", "no-wipe", "large-refunds"],
            AgentAudit.Read(history).OfType<PolicyDecisionAuditRecord>().Select(d => d.Rule));
    }
}
