using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>How the tools of an Agent Framework agent go through the Kairos tool policy: nothing runs because the agent says so; classification lives at registration.</summary>
public sealed class ToolPolicyTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    private sealed class Counter
    {
        public int Runs;
    }

    private static AIFunction Counting(string name, Counter counter) => AIFunctionFactory.Create(
        (string q) =>
        {
            Interlocked.Increment(ref counter.Runs);
            return "ok";
        },
        name, name);

    private static async Task<(AfHarness H, DurableTurn Turn, RunKey Run)> RunAsync(Func<AIFunction, AITool> wrap, string tool, Action<AgentRegistration>? register, Action<AgentsBuilder>? agents, Counter counter, string args = """{"q":"x"}""")
    {
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers(tool, args);
        var h = new AfHarness(af =>
        {
            if (agents is not null)
            {
                af.ConfigureAgents(agents);
            }

            af.AddAgent(AfHarness.AgentWith(model, "a", wrap(Counting(tool, counter))), register);
        });
        (_, DurableTurn turn) = await h.AskAsync("a", "s", "go");
        return (h, turn, await h.RunOfAsync("a", "s"));
    }

    [Fact]
    public async Task A_tool_nobody_classified_is_treated_as_side_effecting_and_waits_for_a_person()
    {
        var counter = new Counter();
        (AfHarness h, DurableTurn turn, RunKey run) = await RunAsync(f => f, "send_receipt", null, null, counter);

        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal("send_receipt", pending.Tool);
        Assert.Equal(0, counter.Runs);
        Assert.Null(await h.ReadAsync("a", turn));

        await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice);
        await h.DriveAsync();
        Assert.Equal("all done", AfHarness.Text(await h.ReadAsync("a", turn)));
        Assert.Equal(1, counter.Runs);
    }

    [Fact]
    public async Task A_read_only_tool_runs_without_asking()
    {
        var counter = new Counter();
        (AfHarness h, DurableTurn turn, RunKey run) = await RunAsync(f => f, "lookup", r => r.ReadOnly("lookup"), null, counter);

        Assert.Empty(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal("all done", AfHarness.Text(await h.ReadAsync("a", turn)));
        Assert.Equal(1, counter.Runs);
    }

    [Fact]
    public async Task AllowWithoutApproval_runs_the_tool_and_the_audit_trail_names_who_decided_that()
    {
        var counter = new Counter();
        (AfHarness h, DurableTurn turn, RunKey run) = await RunAsync(f => f, "send_receipt", r => r.AllowWithoutApproval("send_receipt"), null, counter);

        Assert.Equal("all done", AfHarness.Text(await h.ReadAsync("a", turn)));
        Assert.Equal(1, counter.Runs);
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        Assert.Empty(AgentHistory.PendingApprovals(history));
        PolicyDecisionAuditRecord decision = Assert.Single(AgentFrameworkAudit.Read(history).OfType<PolicyDecisionAuditRecord>());
        Assert.Equal(("send_receipt", ToolVerdict.Auto, "af.allow-without-approval"), (decision.Tool, decision.Verdict, decision.Rule));
        Assert.Contains(history.OfType<ActivityScheduled>(), a => a.ActivityType == AgentActivityNames.Policy); // decided and recorded before the tool could run
    }

    [Fact]
    public async Task AllowWithoutApproval_does_not_override_a_denial()
    {
        var counter = new Counter();
        (AfHarness h, DurableTurn turn, RunKey run) = await RunAsync(f => f, "send_receipt", r => r.AllowWithoutApproval("send_receipt"), b => b.ConfigureBaselinePolicy(p => p.Deny("send_receipt")), counter);

        Assert.Equal("all done", AfHarness.Text(await h.ReadAsync("a", turn))); // the model was told it was refused and answered
        Assert.Equal(0, counter.Runs);
        Assert.Empty(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal(ToolVerdict.Deny, Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>()).Verdict);
    }

    /// <summary>A policy that records its decisions, like the policy engine: a rule that matched, or a default.</summary>
    private sealed class Engine(string rule) : IRecordedToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(context.Approval is { IsApproved: true }
                ? new ToolDecision(ToolVerdict.Auto, Record: new PolicyDecisionRecord(rule, 3, new string('a', 64), "allow", []))
                : new ToolDecision(ToolVerdict.RequireApproval, "a person must approve", new ApprovalRequirement(Roles: ["finance"]), new PolicyDecisionRecord(rule, 3, new string('a', 64), "require_approval", ["amount"])));
    }

    [Fact]
    public async Task AllowWithoutApproval_does_not_override_a_rule_of_a_policy_engine_that_matched_the_call()
    {
        var counter = new Counter();
        (AfHarness h, _, RunKey run) = await RunAsync(f => f, "send_receipt", r => r.AllowWithoutApproval("send_receipt"), b => b.UsePolicy(new Engine("refund-limit")), counter);

        Assert.Equal("send_receipt", Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).Tool);
        Assert.Equal(0, counter.Runs);
    }

    [Fact]
    public async Task AllowWithoutApproval_does_lift_the_default_hold_of_a_policy_engine()
    {
        var counter = new Counter();
        (AfHarness h, DurableTurn turn, RunKey run) = await RunAsync(f => f, "send_receipt", r => r.AllowWithoutApproval("send_receipt"), b => b.UsePolicy(new Engine("(no-rule)")), counter);

        Assert.Equal("all done", AfHarness.Text(await h.ReadAsync("a", turn)));
        PolicyDecisionAuditRecord decision = Assert.Single(AgentFrameworkAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>());
        Assert.Equal(("af.allow-without-approval", 3L), (decision.Rule, decision.PolicyVersion)); // still names the version of the policy it was let through under
    }

    [Fact]
    public async Task An_approval_requirement_the_author_wrote_cannot_be_waived_by_a_policy_that_allows_the_tool()
    {
        var counter = new Counter();
        (AfHarness h, _, RunKey run) = await RunAsync(f => new ApprovalRequiredAIFunction(f), "issue_refund", null, b => b.ConfigureBaselinePolicy(p => p.Allow("issue_refund")), counter);

        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal("issue_refund", pending.Tool);
        Assert.Equal(0, counter.Runs);
    }

    [Fact]
    public async Task RequireApproval_at_registration_names_who_may_approve()
    {
        var counter = new Counter();
        (AfHarness h, _, RunKey run) = await RunAsync(f => f, "lookup", r => r.RequireApproval("lookup", new ApprovalRequirement(Roles: ["support-lead"], Count: 2)), null, counter);

        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal(["support-lead"], pending.Requirement.Roles);
        Assert.Equal(2, pending.Requirement.Count);
    }

    [Fact]
    public async Task The_arguments_the_tool_runs_with_are_the_ones_that_were_approved()
    {
        var counter = new Counter();
        (AfHarness h, DurableTurn turn, RunKey run) = await RunAsync(f => new ApprovalRequiredAIFunction(f), "issue_refund", null, null, counter, """{"q":"order A-1"}""");

        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Contains("order A-1", pending.Details, StringComparison.Ordinal);
        await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice);
        await h.DriveAsync();
        Assert.Equal("all done", AfHarness.Text(await h.ReadAsync("a", turn)));
    }
}
