using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>The edges: a budget that refuses again after it was raised, definitions that keep changing, a conversation that moves to a fresh run while a message is repeated, a nested agent that does.</summary>
public sealed class RobustnessTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    [Fact]
    public async Task A_budget_that_refuses_again_after_it_was_raised_ends_the_run_instead_of_asking_forever()
    {
        var guard = new CountingGuard { Decide = _ => ModelCallPermit.Refuse("daily-tokens", "pause") };
        var h = new AfHarness(af => af.ConfigureAgents(b => b.UseModelCallGuard(guard)).AddAgent(AfHarness.AgentWith(AfHarness.Echo("answer"), "a"), r => r.Model("gpt-test", 100)));
        DurableTurn turn = await h.SendAsync("a", "s", "go");
        RunKey run = await h.RunOfAsync("a", "s");

        await h.Approvals.ApproveAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice);
        await h.DriveAsync();

        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(async () => await h.Agents.Get("a").TryReadAsync(turn));
        Assert.Contains("again", e.Message, StringComparison.Ordinal);
        Assert.Empty(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal(2, guard.Reserved); // asked once, raised once, refused once more: no third question
    }

    [Fact]
    public async Task A_definition_that_keeps_changing_under_a_run_ends_it_after_a_few_approvals()
    {
        ScriptedChatClient model = AfHarness.LookupRefundAnswer();
        var h1 = new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(model, instructions: "v1"), r => r.ReadOnly("lookup_order")));
        (_, DurableTurn turn) = await h1.AskAsync("support", "s", "refund A-1042");
        RunKey run = await h1.RunOfAsync("support", "s");
        await h1.Approvals.ApproveAsync(run, AgentHistory.PendingApprovals(await h1.HistoryAsync(run)).Single().ApprovalId, Alice);

        // Two workers with two different builds take turns on the run: each step is run by the other build than the one before, and finds the other's definition.
        AfHarness[] builds = [new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(model, instructions: "v2"), r => r.ReadOnly("lookup_order")), redeployOf: h1), h1];
        var allowed = new HashSet<string>();
        int steps = 0;
        for (int round = 0; round < 60; round++)
        {
            while (await h1.Worker.ProcessWorkflowTaskAsync(default))
            {
            }

            IReadOnlyList<HistoryEvent> history = await h1.HistoryAsync(run);
            HashSet<int> done = [.. history.OfType<ActivityCompleted>().Select(c => c.ActivityId)];
            ActivityScheduled? next = history.OfType<ActivityScheduled>().LastOrDefault(a => !done.Contains(a.ActivityId));
            AfHarness worker = next?.ActivityType == AgentFrameworkNames.Step ? builds[steps++ % 2] : h1; // each model step is run by the other build than the one before
            await worker.Worker.ProcessActivityTaskAsync(default);

            foreach (PendingApproval p in AgentHistory.PendingApprovals(await h1.HistoryAsync(run)).Where(p => allowed.Add(p.ApprovalId)))
            {
                await h1.Approvals.ApproveAsync(run, p.ApprovalId, new ApproverIdentity("user:bob", "Bob", ["ops"]));
            }
        }

        await Assert.ThrowsAsync<DefinitionChangedException>(async () => await h1.Agents.Get("support").TryReadAsync(turn));
        Assert.InRange(allowed.Count, 3, 5); // a few were allowed, then it stopped asking
    }

    [Fact]
    public async Task A_message_repeated_after_the_conversation_moved_to_a_fresh_run_is_still_answered_once()
    {
        ScriptedChatClient model = new(messages => new ChatMessage(ChatRole.Assistant, $"answer {messages.Count(m => m.Role == ChatRole.User)}"));
        var h = new AfHarness(af => af.Configure(o => o.ContinueAfterEvents = 8).AddAgent(AfHarness.AgentWith(model, "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableAgentSession session = chat.Session("s");
        await chat.SendAsync([new ChatMessage(ChatRole.User, "one")], session, "m-1");
        await h.DriveAsync();
        for (int i = 2; i <= 4; i++)
        {
            await chat.SendAsync([new ChatMessage(ChatRole.User, $"m{i}")], session, $"m-{i}");
            await h.DriveAsync();
        }

        int before = model.CallCount;
        Assert.Equal(4, before);
        await chat.SendAsync([new ChatMessage(ChatRole.User, "one")], session, "m-4"); // the last one again
        await h.DriveAsync();

        Assert.Equal(before, model.CallCount);
    }

    [Fact]
    public async Task A_conversation_closed_while_it_was_about_to_hand_over_ends_instead()
    {
        ScriptedChatClient model = new(_ => new ChatMessage(ChatRole.Assistant, "answer"));
        var h = new AfHarness(af => af.Configure(o => o.ContinueAfterEvents = 8).AddAgent(AfHarness.AgentWith(model, "chat")));
        DurableAIAgent chat = h.Agents.Get("chat");
        DurableAgentSession session = chat.Session("s");
        for (int i = 1; i <= 3; i++)
        {
            await h.AskAsync("chat", "s", $"m{i}");
        }

        await chat.SendAsync([new ChatMessage(ChatRole.User, "last")], session, "m-last");
        await chat.CloseAsync(session); // a message and a close arrive together, with the history past the hand-over point
        await h.DriveAsync();

        RunKey current = await h.RunOfAsync("chat", "s");
        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(current));
    }

    [Fact]
    public async Task A_session_of_another_agent_is_refused()
    {
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "one")).AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "two")));
        AgentSession sessionOfOne = await h.Agents.Get("one").CreateSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await h.Agents.Get("two").RunAsync("hi", sessionOfOne));
    }

    [Fact]
    public async Task A_nested_agent_whose_run_hands_over_while_the_parent_waits_still_answers()
    {
        var seen = new List<FunctionResultContent>();
        ScriptedChatClient manager = new(messages =>
        {
            FunctionResultContent[] results = [.. messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
            if (results.Length == 0)
            {
                return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ask-1", "researcher", AfHarness.Args("""{"query":"x"}"""))]);
            }

            seen.AddRange(results);
            return new ChatMessage(ChatRole.Assistant, "researcher says " + results[0].Result);
        });
        int refunds = 0;
        AIFunction refund = AIFunctionFactory.Create((string q) => { Interlocked.Increment(ref refunds); return "refunded"; }, "issue_refund", "d");
        ScriptedChatClient researcher = AfHarness.CallsToolThenAnswers("issue_refund", """{"q":"x"}""", "all done");
        var h = new AfHarness(af =>
        {
            af.Configure(o => o.ContinueAfterEvents = 2); // the child run hands over before it does anything, and again as it grows
            af.AddAgent(AfHarness.AgentWith(manager, "manager", DurableAgentFunction.For("researcher")), r => r.ReadOnly("researcher"));
            af.AddAgent(AfHarness.AgentWith(researcher, "researcher", new ApprovalRequiredAIFunction(refund)));
        });

        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("manager", "s", "go");
        Assert.Null(reply);
        for (int i = 0; i < 6; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(45)); // the parent looks in on the child now and then while a person has not decided
            await h.DriveAsync(jumpClock: false);
        }

        Assert.Null(await h.ReadAsync("manager", turn)); // still waiting: it did not conclude that the child had ended
        RunSummary[] children = [.. (await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default)).Where(r => r.WorkflowId.StartsWith("af/researcher/", StringComparison.Ordinal))];
        RunSummary current = Assert.Single(children, r => r.State == RunState.Running);
        await h.Approvals.ApproveAsync(current.Run, AgentHistory.PendingApprovals(await h.HistoryAsync(current.Run)).Single().ApprovalId, Alice);
        await h.DriveAsync();

        Assert.Equal("researcher says all done", AfHarness.Text(await h.ReadAsync("manager", turn)));
        Assert.Equal(1, refunds);
    }

    [Fact]
    public async Task A_parent_that_waits_a_long_time_for_a_child_looks_in_less_and_less_often()
    {
        ScriptedChatClient manager = new(messages => messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, "done")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ask-1", "researcher", AfHarness.Args("""{"query":"x"}"""))]));
        AIFunction refund = AIFunctionFactory.Create((string q) => "refunded", "issue_refund", "d");
        var h = new AfHarness(af =>
        {
            af.AddAgent(AfHarness.AgentWith(manager, "manager", DurableAgentFunction.For("researcher")), r => r.ReadOnly("researcher"));
            af.AddAgent(AfHarness.AgentWith(AfHarness.CallsToolThenAnswers("issue_refund", """{"q":"x"}"""), "researcher", new ApprovalRequiredAIFunction(refund)));
        });
        await h.AskAsync("manager", "s", "go");

        for (int i = 0; i < 24 * 6; i++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(10)); // a day passes while a person has not decided
            await h.DriveAsync(jumpClock: false);
        }

        RunKey parent = await h.RunOfAsync("manager", "s");
        int looks = (await h.HistoryAsync(parent)).OfType<ActivityScheduled>().Count(a => a.ActivityType == AgentFrameworkNames.ChildCheck);
        Assert.InRange(looks, 3, 130); // not every 15 seconds for a day (5,760 looks), but about every quarter of an hour at the most (96 in a day)
    }
}
