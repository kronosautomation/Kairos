using Kairos.Agents;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>A nested agent runs as a durable child run: governed like any other agent, with its own tools, approvals and budgets, and holding no compute while the parent waits.</summary>
public sealed class NestedAgentTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    private static ScriptedChatClient Manager(List<FunctionResultContent> seen) => new(messages =>
    {
        FunctionResultContent[] results = [.. messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
        if (results.Length == 0)
        {
            return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ask-1", "researcher", AfHarness.Args("""{"query":"where is order A-1042?"}"""))]);
        }

        seen.Clear();
        seen.AddRange(results);
        return new ChatMessage(ChatRole.Assistant, "the researcher says: " + results[0].Result);
    });

    private static ScriptedChatClient Researcher(Func<string, string>? answer = null) => new(messages =>
    {
        string query = messages.First(m => m.Role == ChatRole.User).Text;
        return new ChatMessage(ChatRole.Assistant, answer?.Invoke(query) ?? $"found: {query}");
    });

    private static AfHarness Build(ScriptedChatClient manager, ScriptedChatClient researcher, bool faults = false, params AITool[] researcherTools) =>
        new(
            af =>
            {
                af.AddAgent(AfHarness.AgentWith(manager, "manager", DurableAgentFunction.For("researcher", "Looks things up.")), r => r.ReadOnly("researcher"));
                af.AddAgent(AfHarness.AgentWith(researcher, "researcher", researcherTools));
            },
            faultInjection: faults);

    [Fact]
    public async Task A_nested_agent_answers_the_parent_through_a_child_run()
    {
        var seen = new List<FunctionResultContent>();
        ScriptedChatClient manager = Manager(seen);
        ScriptedChatClient researcher = Researcher();
        AfHarness h = Build(manager, researcher);

        (AgentResponse? reply, _) = await h.AskAsync("manager", "s", "help me", onBehalfOf: "customer:42");

        Assert.Equal("the researcher says: found: where is order A-1042?", AfHarness.Text(reply));
        Assert.Equal((2, 1), (manager.CallCount, researcher.CallCount));
        RunKey parent = await h.RunOfAsync("manager", "s");
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(parent);
        Assert.Contains(history.OfType<ActivityScheduled>(), a => a.ActivityType == AgentActivityNames.Tool); // the call went through the tool step: policy and credentials apply to it
        Assert.Contains(history.OfType<SignalReceived>(), s => s.SignalName.StartsWith("af.child.", StringComparison.Ordinal)); // and the child's answer came back as a signal
        Assert.Equal("ask-1", Assert.Single(seen).CallId);
    }

    [Fact]
    public async Task The_child_is_a_run_of_its_own_whose_history_holds_the_nested_agents_steps()
    {
        AfHarness h = Build(Manager([]), Researcher());
        await h.AskAsync("manager", "s", "help me");

        RunSummary[] runs = [.. await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)];

        Assert.Equal(2, runs.Length);
        RunSummary child = Assert.Single(runs, r => r.WorkflowId.StartsWith("af/researcher/", StringComparison.Ordinal));
        Assert.Equal(RunState.Completed, child.State); // a nested agent answers one question and ends
        IReadOnlyList<HistoryEvent> childHistory = await h.HistoryAsync(child.Run);
        Assert.Contains(childHistory.OfType<ActivityScheduled>(), a => a.ActivityType == AgentFrameworkNames.Step);
        Assert.Contains(childHistory.OfType<ActivityScheduled>(), a => a.ActivityType == AgentFrameworkNames.ChildNotify);
    }

    [Fact]
    public async Task The_nested_agents_tools_go_through_the_tool_policy_and_its_approvals_wait_in_the_child_without_holding_the_parent()
    {
        int refunds = 0;
        AIFunction refund = AIFunctionFactory.Create((string orderId) => { Interlocked.Increment(ref refunds); return "refunded"; }, "issue_refund", "Refunds.");
        ScriptedChatClient researcher = AfHarness.CallsToolThenAnswers("issue_refund", """{"orderId":"A-1042"}""", "refunded it");
        var seen = new List<FunctionResultContent>();
        AfHarness h = Build(Manager(seen), researcher, researcherTools: new ApprovalRequiredAIFunction(refund));

        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("manager", "s", "refund A-1042", onBehalfOf: "customer:42");

        Assert.Null(reply); // the parent waits for the child, which waits for a person
        Assert.Equal(0, refunds);
        RunSummary child = Assert.Single(await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default), r => r.WorkflowId.StartsWith("af/researcher/", StringComparison.Ordinal));
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(child.Run)));
        Assert.Equal(("issue_refund", "customer:42"), (pending.Tool, pending.RequestedBy)); // who the parent acts for goes down with the question
        Assert.Empty(AgentHistory.PendingApprovals(await h.HistoryAsync(await h.RunOfAsync("manager", "s"))));

        await h.Approvals.ApproveAsync(child.Run, pending.ApprovalId, Alice);
        await h.DriveAsync();

        Assert.Equal("the researcher says: refunded it", AfHarness.Text(await h.ReadAsync("manager", turn)));
        Assert.Equal(1, refunds);
    }

    [Fact]
    public async Task A_nested_agent_that_fails_tells_the_parent_the_kind_of_failure_and_the_parent_carries_on()
    {
        ScriptedChatClient researcher = new(_ => throw new NotSupportedException("secret detail"));
        AfHarness h = Build(Manager([]), researcher);

        (AgentResponse? reply, _) = await h.AskAsync("manager", "s", "help me");

        string answer = AfHarness.Text(reply);
        Assert.Contains("The nested agent failed (ActivityFailureException)", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Surviving_a_crash_at_any_store_operation_a_nested_run_answers_once()
    {
        // The scenario is small on purpose: every store operation of the parent and the child is a place to die.
        AfHarness baseline = Build(Manager([]), Researcher(), faults: true);
        baseline.Faults!.FaultsEnabled = false;
        (AgentResponse? expected, _) = await baseline.AskAsync("manager", "s", "help me");
        long operations = baseline.Faults.OperationCount;
        Assert.True(operations > 25);

        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                ScriptedChatClient researcher = Researcher();
                AfHarness h = Build(Manager([]), researcher, faults: true);
                h.Faults!.FaultsEnabled = false;
                long at = k;
                FaultKind crash = kind;
                h.Faults.ForcedFault = index => index == at ? crash : FaultKind.None;

                (AgentResponse? reply, _) = await h.AskAsync("manager", "s", "help me");

                string where = $"crash {kind} at store operation {k}";
                Assert.True(AfHarness.Text(expected) == AfHarness.Text(reply), $"{where}: {AfHarness.Text(reply)}");
                Assert.InRange(researcher.CallCount, 1, 2);
                RunSummary[] children = [.. (await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)).Where(r => r.WorkflowId.StartsWith("af/researcher/", StringComparison.Ordinal))];
                Assert.True(children.Length == 1, $"{where}: {children.Length} child runs");
            }
        }
    }
}
