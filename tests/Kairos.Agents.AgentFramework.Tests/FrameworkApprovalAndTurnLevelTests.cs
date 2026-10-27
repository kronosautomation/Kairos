using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>Approvals the agent's own framework asks for (a hosted tool at the provider, an approval wrapper in a turn-level agent) become the same exact-call Kairos approvals tool steps use.</summary>
public sealed class FrameworkApprovalAndTurnLevelTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    /// <summary>An agent that cannot be stepped (a ToolApprovalAgent runs and approves by itself) whose tool needs approval: the framework asks, and runs the tool itself inside the turn once told it may.</summary>
    private static (AIAgent Agent, ScriptedChatClient Model, Func<int> Searches, List<FunctionResultContent> Results) ApprovalWrapped()
    {
        int searches = 0;
        var results = new List<FunctionResultContent>();
        AIFunction search = AIFunctionFactory.Create((string query) => { Interlocked.Increment(ref searches); return "found it"; }, "hosted_search", "Searches.");
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("hosted_search", """{"query":"order A-1042"}""", "done", messages =>
        {
            results.Clear();
            results.AddRange(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        });
        AIAgent agent = AfHarness.AgentWith(model, "hosted", new ApprovalRequiredAIFunction(search)).AsBuilder().Use(inner => new ToolApprovalAgent(inner, new ToolApprovalAgentOptions())).Build();
        return (agent, model, () => Volatile.Read(ref searches), results);
    }

    [Fact]
    public async Task A_request_from_the_agents_own_framework_becomes_an_exact_call_approval_and_the_answer_goes_back()
    {
        (AIAgent agent, ScriptedChatClient model, Func<int> searches, _) = ApprovalWrapped();
        var h = new AfHarness(af => af.AddAgent(agent, r => r.AllowTurnLevel()));

        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("hosted", "s", "look it up");

        Assert.Null(reply);
        Assert.Equal(0, searches());
        RunKey run = await h.RunOfAsync("hosted", "s");
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal("hosted_search", pending.Tool);
        Assert.Contains("order A-1042", pending.Details, StringComparison.Ordinal);

        await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice, "fine");
        await h.DriveAsync();

        Assert.Equal("done", AfHarness.Text(await h.ReadAsync("hosted", turn)));
        Assert.Equal(1, searches()); // the framework ran the tool, inside the turn, once a person had agreed
        Assert.Equal(2, model.CallCount);
    }

    [Fact]
    public async Task A_rejected_request_tells_the_agent_so_and_the_tool_never_runs()
    {
        (AIAgent agent, _, Func<int> searches, List<FunctionResultContent> results) = ApprovalWrapped();
        var h = new AfHarness(af => af.AddAgent(agent, r => r.AllowTurnLevel()));
        (_, DurableTurn turn) = await h.AskAsync("hosted", "s", "look it up");
        RunKey run = await h.RunOfAsync("hosted", "s");

        await h.Approvals.RejectAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice, "no");
        await h.DriveAsync();

        Assert.Equal("done", AfHarness.Text(await h.ReadAsync("hosted", turn)));
        Assert.Equal(0, searches());
        Assert.Contains("user:alice", Assert.Single(results).Result?.ToString(), StringComparison.Ordinal); // the agent was told who refused
    }

    [Fact]
    public async Task Who_may_decide_a_request_from_the_framework_is_the_registrations_to_say()
    {
        (AIAgent agent, _, _, _) = ApprovalWrapped();
        var h = new AfHarness(af => af.AddAgent(agent, r => r.AllowTurnLevel().OnFrameworkApproval(new ApprovalRequirement(Roles: ["security"], Count: 2))));
        await h.AskAsync("hosted", "s", "look it up");
        RunKey run = await h.RunOfAsync("hosted", "s");

        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));

        Assert.Equal(["security"], pending.Requirement.Roles);
        Assert.Equal(2, pending.Requirement.Count);
    }

    // ---- turn level ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_turn_level_agent_is_recorded_a_turn_at_a_time_and_a_recorded_turn_is_never_run_again()
    {
        var opaque = new OpaqueAgent("remote", (_, turn) => $"reply {turn}");
        var h = new AfHarness(af => af.AddAgent(opaque, r => r.AllowTurnLevel()));

        (AgentResponse? first, _) = await h.AskAsync("remote", "s", "one");
        (AgentResponse? second, _) = await h.AskAsync("remote", "s", "two");

        Assert.Equal(["reply 1", "reply 2"], [AfHarness.Text(first), AfHarness.Text(second)]);
        Assert.Equal(2, opaque.Calls);
        RunKey run = await h.RunOfAsync("remote", "s");
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        Assert.Equal([AgentFrameworkNames.Turn, AgentFrameworkNames.Turn], history.OfType<ActivityScheduled>().Select(a => a.ActivityType));

        // Replay returns the recorded turns and calls nothing.
        Assert.Equal(2, opaque.Calls);
        var replay = Kairos.WorkflowReplayer.Replay(h.Runtime.Conversation, history);
        Assert.Empty(replay.NewCommands);
        Assert.Equal(2, opaque.Calls);
    }

    [Fact]
    public async Task A_crash_in_a_turn_repeats_that_turn_at_most_once_and_never_an_earlier_one()
    {
        var opaque = new OpaqueAgent("remote", (_, turn) => $"reply {turn}");
        var h = new AfHarness(af => af.AddAgent(opaque, r => r.AllowTurnLevel()), faultInjection: true);
        h.Faults!.FaultsEnabled = false;
        h.Faults.ForcedFault = index => index == 14 ? Kairos.Testing.Simulation.FaultKind.Before : Kairos.Testing.Simulation.FaultKind.None;

        (AgentResponse? first, _) = await h.AskAsync("remote", "s", "one");
        (AgentResponse? second, _) = await h.AskAsync("remote", "s", "two");

        Assert.Equal(["reply 1", "reply 2"], [AfHarness.Text(first), AfHarness.Text(second)]);
        Assert.InRange(opaque.Calls, 2, 3);
    }
}
