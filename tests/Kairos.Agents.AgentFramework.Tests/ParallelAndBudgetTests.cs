using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

public sealed class ParallelAndBudgetTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    [Fact]
    public async Task Several_tool_calls_in_one_answer_are_scheduled_together_and_answered_in_order()
    {
        var order = new List<string>();
        var seen = new List<ChatMessage>();
        var model = new ScriptedChatClient(messages =>
        {
            seen.Clear();
            seen.AddRange(messages);
            return messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatMessage(ChatRole.Assistant, "both done")
                : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c-a", "tool_a", AfHarness.Args("""{"q":"a"}""")), new FunctionCallContent("c-b", "tool_b", AfHarness.Args("""{"q":"b"}"""))]);
        });
        AIFunction a = AIFunctionFactory.Create((string q) => { lock (order) { order.Add("a"); } return "result-a"; }, "tool_a", "a");
        AIFunction b = AIFunctionFactory.Create((string q) => { lock (order) { order.Add("b"); } return "result-b"; }, "tool_b", "b");
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "agent", a, b), r => r.ReadOnly("tool_a", "tool_b")));

        (AgentResponse? reply, _) = await h.AskAsync("agent", "s", "go");

        Assert.Equal("both done", AfHarness.Text(reply));
        RunKey run = await h.RunOfAsync("agent", "s");
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        int[] tools = [.. history.Select((e, i) => (e, i)).Where(x => x.e is ActivityScheduled { ActivityType: AgentActivityNames.Tool }).Select(x => x.i)];
        Assert.Equal(2, tools.Length);
        Assert.DoesNotContain(history.Skip(tools[0]).Take(tools[1] - tools[0]), e => e is ActivityCompleted); // both were scheduled before either finished: parallel stays parallel

        FunctionResultContent[] results = [.. seen.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
        Assert.All(seen.Where(m => m.Contents.OfType<FunctionResultContent>().Any()), m => Assert.Equal(ChatRole.Tool, m.Role)); // providers want tool results in the tool role
        Assert.Equal(["c-a", "c-b"], results.Select(r => r.CallId));
        Assert.Equal(["result-a", "result-b"], results.Select(r => r.Result?.ToString()));
        Assert.Equal(2, model.CallCount);
    }

    private static (AfHarness H, ScriptedChatClient Model, CountingGuard Guard) Guarded(Func<ModelCallInfo, ModelCallPermit>? decide = null, Func<IReadOnlyList<ChatMessage>, ChatMessage>? model = null)
    {
        var guard = new CountingGuard { Decide = decide };
        ScriptedChatClient client = model is null ? AfHarness.LookupRefundAnswer() : new ScriptedChatClient(model);
        var h = new AfHarness(af => af
            .ConfigureAgents(b => b.UseModelCallGuard(guard))
            .AddAgent(AfHarness.SupportAgent(client), r => r.ReadOnly("lookup_order").Model("gpt-test", 400)));
        return (h, client, guard);
    }

    [Fact]
    public async Task Every_model_call_is_reserved_before_and_settled_with_the_usage_after_under_the_steps_key()
    {
        (AfHarness h, ScriptedChatClient model, CountingGuard guard) = Guarded();

        (_, DurableTurn turn) = await h.AskAsync("support", "s", "refund A-1042");
        RunKey run = await h.RunOfAsync("support", "s");
        await h.Approvals.ApproveAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice);
        await h.DriveAsync();

        Assert.Equal("Refund issued.", AfHarness.Text(await h.ReadAsync("support", turn)));
        Assert.Equal((3, 3, 0), (guard.Reserved, guard.Settled, guard.Released));
        Assert.All(guard.Calls, c => Assert.Equal(("gpt-test", 400, 1), (c.Model, c.MaxOutputTokens, c.Attempt)));
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        string[] keys = [.. history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentFrameworkNames.Step).Select(a => $"{run.RunId:N}:{a.ActivityId}")];
        Assert.Equal(keys, guard.Calls.Select(c => c.IdempotencyKey));
        Assert.All(guard.Usage, u => Assert.Equal((10L, 5L), (u!.InputTokens, u.OutputTokens)));
        Assert.Equal(3, model.CallCount);
    }

    [Fact]
    public async Task A_budget_that_is_reached_ends_the_run_before_the_model_is_called()
    {
        (AfHarness h, ScriptedChatClient model, _) = Guarded(r => ModelCallPermit.Refuse("daily-tokens"));

        DurableTurn turn = await h.SendAsync("support", "s", "refund A-1042");

        Assert.Equal(0, model.CallCount);
        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(async () => await h.Agents.Get("support").TryReadAsync(turn));
        Assert.Equal("daily-tokens", e.Limit);
    }

    [Fact]
    public async Task A_budget_that_pauses_asks_a_person_and_the_approval_lets_that_one_call_through()
    {
        bool first = true;
        (AfHarness h, ScriptedChatClient model, CountingGuard guard) = Guarded(info =>
        {
            if (first)
            {
                first = false;
                return ModelCallPermit.Refuse("daily-tokens", "pause", new ApprovalRequirement(Roles: ["finance"]));
            }

            return ModelCallPermit.Allow("r");
        });

        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("support", "s", "refund A-1042");
        Assert.Null(reply);
        Assert.Equal(0, model.CallCount);
        RunKey run = await h.RunOfAsync("support", "s");
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Contains("daily-tokens", pending.Action, StringComparison.Ordinal);
        Assert.Equal(["finance"], pending.Requirement.Roles);

        await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice);
        await h.DriveAsync();

        Assert.Equal(1, model.CallCount.CompareTo(0)); // the run went on
        Assert.Equal(pending.ApprovalId, guard.Calls[1].AllowanceApprovalId); // the guard was told which approval let the call through
        Assert.Null(guard.Calls[0].AllowanceApprovalId);
        Assert.NotNull(turn);
    }

    [Fact]
    public async Task A_budget_nobody_raises_ends_the_run()
    {
        (AfHarness h, ScriptedChatClient model, _) = Guarded(info => info.AllowanceApprovalId is null ? ModelCallPermit.Refuse("daily-tokens", "pause") : ModelCallPermit.Allow("r")); // a guard that lets a raised call through

        (_, DurableTurn turn) = await h.AskAsync("support", "s", "refund A-1042");
        RunKey run = await h.RunOfAsync("support", "s");
        await h.Approvals.RejectAsync(run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId, Alice);
        await h.DriveAsync();

        Assert.Equal(0, model.CallCount); // a rejected raise lets nothing through
        BudgetExceededException e = await Assert.ThrowsAsync<BudgetExceededException>(async () => await h.Agents.Get("support").TryReadAsync(turn));
        Assert.Contains("nobody raised it", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_that_fails_for_good_gives_its_reservation_back_once()
    {
        (AfHarness h, ScriptedChatClient model, CountingGuard guard) = Guarded(model: _ => throw new InvalidOperationException("provider is down"));

        DurableTurn turn = await h.SendAsync("support", "s", "hi");

        Assert.Equal(3, model.CallCount); // the model retry policy: three attempts
        Assert.Equal((3, 0, 1), (guard.Reserved, guard.Settled, guard.Released)); // each attempt asked again (the guard keys on the step, so a retry reuses its reservation); one release at the end
        Assert.Single(guard.Calls.Select(c => c.IdempotencyKey).Distinct());
        await Assert.ThrowsAsync<ConversationEndedException>(async () => await h.Agents.Get("support").TryReadAsync(turn));
    }
}
