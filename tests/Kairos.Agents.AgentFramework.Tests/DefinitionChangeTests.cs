using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// A run is pinned to the definition of its agent when it starts. A worker with another definition (a redeploy that changed instructions or tools) does not silently carry on: a person decides, as an approval,
/// and the decision names the verified principal who made it.
/// </summary>
public sealed class DefinitionChangeTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly ApproverIdentity Bob = new("user:bob", "Bob", ["ops"]);

    /// <summary>A run that is in flight: the lookup is done and the refund waits for a person.</summary>
    private static async Task<(AfHarness H, DurableTurn Turn, RunKey Run, string RefundApproval)> InFlightAsync(Action<AgentRegistration>? register = null)
    {
        ScriptedChatClient model = AfHarness.LookupRefundAnswer();
        var h = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(model, instructions: "version one"), r => { r.ReadOnly("lookup_order"); register?.Invoke(r); }));
        (_, DurableTurn turn) = await h.AskAsync("support", "s", "refund A-1042");
        RunKey run = await h.RunOfAsync("support", "s");
        return (h, turn, run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId);
    }

    private static AfHarness Redeploy(AfHarness previous, string instructions, Action<AgentRegistration>? register = null) =>
        new(af => af.AddAgent(AfHarness.SupportAgent(AfHarness.LookupRefundAnswer(), instructions: instructions), r => { r.ReadOnly("lookup_order"); register?.Invoke(r); }), redeployOf: previous);

    [Fact]
    public async Task A_redeploy_that_changes_the_agent_asks_a_person_and_the_audit_names_who_allowed_it()
    {
        (AfHarness h1, DurableTurn turn, RunKey run, string refundApproval) = await InFlightAsync();
        AfHarness h2 = Redeploy(h1, "version two");

        await h2.Approvals.ApproveAsync(run, refundApproval, Alice); // the refund the run was waiting for
        await h2.DriveAsync();

        // The next step ran on a worker with another definition: nothing was called; a person is asked.
        Assert.Null(await h2.ReadAsync("support", turn));
        PendingApproval change = Assert.Single(AgentHistory.PendingApprovals(await h2.HistoryAsync(run)));
        Assert.Contains("changed definition of agent 'support'", change.Action, StringComparison.Ordinal);
        Assert.Null(change.Tool);

        await h2.Approvals.ApproveAsync(run, change.ApprovalId, Bob);
        await h2.DriveAsync();

        Assert.Equal("Refund issued.", AfHarness.Text(await h2.ReadAsync("support", turn)));
        DefinitionOverrideAuditRecord record = Assert.Single(AgentFrameworkAudit.Read(await h2.HistoryAsync(run)).OfType<DefinitionOverrideAuditRecord>());
        Assert.Equal(("support", "user:bob", change.ApprovalId), (record.Agent, record.Approver, record.ApprovalId));
        Assert.Equal(["ops"], record.Roles);
        Assert.NotEqual(record.FromHash, record.ToHash);
        Assert.Equal(64, record.ToHash.Length);

        // The run is pinned to the new definition from here on: another message does not ask again.
        (AgentResponse? again, _) = await h2.AskAsync("support", "s", "thanks");
        Assert.NotNull(again);
        Assert.DoesNotContain(AgentHistory.PendingApprovals(await h2.HistoryAsync(run)), p => p.Action.Contains("changed definition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_approval_is_also_in_the_standard_audit_with_the_principal_the_verifier_established()
    {
        (AfHarness h1, DurableTurn turn, RunKey run, string refundApproval) = await InFlightAsync();
        AfHarness h2 = Redeploy(h1, "version two");
        await h2.Approvals.ApproveAsync(run, refundApproval, Alice);
        await h2.DriveAsync();
        string changeId = AgentHistory.PendingApprovals(await h2.HistoryAsync(run)).Single().ApprovalId;

        await h2.Approvals.ApproveAsync(run, changeId, Bob);
        await h2.DriveAsync();

        ApprovalAuditRecord approval = AgentAudit.Read(await h2.HistoryAsync(run)).OfType<ApprovalAuditRecord>().Single(a => a.ApprovalId == changeId);
        Assert.Equal(("user:bob", ApprovalAuditOutcome.Approved), (approval.Approver, approval.Outcome));
        Assert.NotNull(await h2.ReadAsync("support", turn));
    }

    [Fact]
    public async Task A_change_nobody_allows_ends_the_run_and_nothing_is_called()
    {
        (AfHarness h1, DurableTurn turn, RunKey run, string refundApproval) = await InFlightAsync();
        var model2 = AfHarness.LookupRefundAnswer();
        var h2 = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(model2, instructions: "version two"), r => r.ReadOnly("lookup_order")), redeployOf: h1);
        await h2.Approvals.ApproveAsync(run, refundApproval, Alice);
        await h2.DriveAsync();
        string changeId = AgentHistory.PendingApprovals(await h2.HistoryAsync(run)).Single().ApprovalId;

        await h2.Approvals.RejectAsync(run, changeId, Bob, "not tested");
        await h2.DriveAsync();

        Assert.Equal(0, model2.CallCount); // the new definition never got to act on the old conversation
        await Assert.ThrowsAsync<DefinitionChangedException>(async () => await h2.Agents.Get("support").TryReadAsync(turn));
        Assert.Empty(AgentFrameworkAudit.Read(await h2.HistoryAsync(run)).OfType<DefinitionOverrideAuditRecord>());
    }

    [Fact]
    public async Task Only_the_people_the_registration_names_may_allow_a_change()
    {
        (AfHarness h1, DurableTurn turn, RunKey run, string refundApproval) = await InFlightAsync();
        AfHarness h2 = Redeploy(h1, "version two", r => r.OnDefinitionChange(new ApprovalRequirement(Roles: ["ops"])));
        await h2.Approvals.ApproveAsync(run, refundApproval, Alice);
        await h2.DriveAsync();
        string changeId = AgentHistory.PendingApprovals(await h2.HistoryAsync(run)).Single().ApprovalId;

        await h2.Approvals.ApproveAsync(run, changeId, Alice); // finance, not ops
        await h2.DriveAsync();
        Assert.Null(await h2.ReadAsync("support", turn));
        Assert.Equal(changeId, Assert.Single(AgentHistory.PendingApprovals(await h2.HistoryAsync(run))).ApprovalId); // still waiting

        await h2.Approvals.ApproveAsync(run, changeId, Bob);
        await h2.DriveAsync();
        Assert.NotNull(await h2.ReadAsync("support", turn));
    }

    [Fact]
    public async Task A_redeploy_that_changes_nothing_that_matters_asks_nobody()
    {
        (AfHarness h1, DurableTurn turn, RunKey run, string refundApproval) = await InFlightAsync();
        // The same definition, and a different classification of tools: classification is policy, applied when a call is made, so it is not part of the definition a run is pinned to.
        AfHarness h2 = Redeploy(h1, "version one", r => r.RequireApproval("issue_refund"));

        await h2.Approvals.ApproveAsync(run, refundApproval, Alice);
        await h2.DriveAsync();

        Assert.Equal("Refund issued.", AfHarness.Text(await h2.ReadAsync("support", turn)));
        Assert.Empty(AgentFrameworkAudit.Read(await h2.HistoryAsync(run)).OfType<DefinitionOverrideAuditRecord>());
    }

    [Fact]
    public async Task A_changed_tool_schema_is_a_changed_definition()
    {
        (AfHarness h1, DurableTurn turn, RunKey run, string refundApproval) = await InFlightAsync();
        AIFunction lookup = AIFunctionFactory.Create((string orderId, string region) => "shipped", "lookup_order", "Finds an order and says where it is.");
        AIFunction refund = AIFunctionFactory.Create((string orderId) => "refunded", "issue_refund", "Refunds an order.");
        var h2 = new AfHarness(af => af.AddAgent(
            new ChatClientAgent(AfHarness.LookupRefundAnswer(), new ChatClientAgentOptions { Name = "support", Description = "Customer support", ChatOptions = new ChatOptions { Instructions = "version one", Tools = [lookup, new ApprovalRequiredAIFunction(refund)] } }),
            r => r.ReadOnly("lookup_order")), redeployOf: h1);

        await h2.Approvals.ApproveAsync(run, refundApproval, Alice);
        await h2.DriveAsync();

        Assert.Contains("changed definition", Assert.Single(AgentHistory.PendingApprovals(await h2.HistoryAsync(run))).Action, StringComparison.Ordinal);
        Assert.NotNull(turn);
    }
}
