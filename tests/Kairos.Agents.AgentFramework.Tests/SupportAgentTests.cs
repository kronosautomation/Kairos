using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

public sealed class SupportAgentTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    [Fact]
    public async Task A_support_agent_looks_up_an_order_then_waits_for_a_person_to_approve_the_refund()
    {
        int lookups = 0, refunds = 0;
        ScriptedChatClient model = AfHarness.LookupRefundAnswer();
        var h = new AfHarness(af => af.AddAgent(
            AfHarness.SupportAgent(model, lookup: _ => { lookups++; return "shipped"; }, refund: _ => { refunds++; return "refunded"; }),
            r => r.ReadOnly("lookup_order")));

        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("support", "s1", "please refund order A-1042");

        Assert.Null(reply); // the run is waiting for a person
        RunKey run = await h.RunOfAsync("support", "s1");
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal("issue_refund", pending.Tool);
        Assert.Equal(1, lookups);
        Assert.Equal(0, refunds);
        Assert.Equal(2, model.CallCount);

        Assert.True(await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice));
        await h.DriveAsync();

        reply = await h.ReadAsync("support", turn);
        Assert.Equal("Refund issued.", AfHarness.Text(reply));
        Assert.Equal(1, lookups);
        Assert.Equal(1, refunds);
        Assert.Equal(3, model.CallCount);
        Assert.Equal(15 * 3, reply!.Usage!.TotalTokenCount);
    }

    [Fact]
    public async Task A_rejected_refund_is_never_made_and_the_model_is_told()
    {
        int refunds = 0;
        List<ChatMessage> seen = [];
        ScriptedChatClient model = AfHarness.LookupRefundAnswer("I could not refund it.", m => { seen.Clear(); seen.AddRange(m); });
        var h = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(model, refund: _ => { refunds++; return "refunded"; }), r => r.ReadOnly("lookup_order")));

        (_, DurableTurn turn) = await h.AskAsync("support", "s1", "refund A-1042");
        RunKey run = await h.RunOfAsync("support", "s1");
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
        await h.Approvals.RejectAsync(run, id, Alice, "not eligible");
        await h.DriveAsync();

        AgentResponse? reply = await h.ReadAsync("support", turn);
        Assert.Equal("I could not refund it.", AfHarness.Text(reply));
        Assert.Equal(0, refunds);
        Assert.Contains(seen.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => r.Result?.ToString()?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true);
    }
}
