using System.Text;
using System.Text.Json;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

/// <summary>
/// The hooks a policy engine plugs into: a policy that records its decisions before the tool can run, several approvers for one approval, and a guard that is asked before every model request.
/// The engine itself is Pro; these tests use small policies and guards of their own.
/// </summary>
public sealed class RecordedPolicyTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly ApproverIdentity Bob = new("user:bob", "Bob", ["finance"]);
    private static readonly ApproverIdentity Mallory = new("user:mallory", "Mallory", ["support"]);

    /// <summary>A policy that decides as it is told and records every evaluation.</summary>
    private sealed class ScriptedPolicy(Func<ToolPolicyContext, ToolDecision> decide) : IRecordedToolPolicy
    {
        public List<ToolPolicyContext> Seen { get; } = [];

        public Func<ToolPolicyContext, ToolDecision> Decide { get; set; } = decide;

        public ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken)
        {
            Seen.Add(context);
            return ValueTask.FromResult(Decide(context));
        }
    }

    private static PolicyDecisionRecord Record(string rule, string effect, params string[] fields) => new(rule, 3, new string('a', 64), effect, fields);

    private static ToolDecision Allowed(string rule = "r-allow") => new(ToolVerdict.Auto, Record: Record(rule, "allow", "amount"));

    private static ToolDecision Denied(string rule = "r-deny") => new(ToolVerdict.Deny, "denied by rule", Record: Record(rule, "deny", "amount"));

    private static ToolDecision NeedsApproval(int count = 1, string rule = "r-approve") =>
        new(ToolVerdict.RequireApproval, "needs a person", new ApprovalRequirement(Roles: ["finance"], Count: count), Record(rule, "require_approval", "amount"));

    private static AgentHarness Harness(IToolPolicy policy, Action<int>? onRun = null) =>
        new(b => b.AddModel("m", AgentHarness.Echo()).UsePolicy(policy).AddTool(AgentHarness.WriteTool("refund", _ =>
        {
            onRun?.Invoke(1);
            return "refunded";
        })));

    private static void Call(AgentHarness h, string args = """{"amount":250,"note":"CANARY-ARG-7710"}""") =>
        h.Workflow("call", async (ctx, _) =>
        {
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", AgentHarness.WireArgs(args)));
            return Encoding.UTF8.GetBytes(r.Outcome.ToString());
        });

    private static async Task<List<ActivityScheduled>> StepsAsync(AgentHarness h, RunKey run) =>
        [.. (await h.HistoryAsync(run)).OfType<ActivityScheduled>().Where(s => s.ActivityType.StartsWith("kairos.agent.", StringComparison.Ordinal))];

    // ---- the decision is recorded before the tool runs ---------------------------------------------------------------

    [Fact]
    public async Task A_policy_is_told_which_step_it_is_deciding_for_so_a_retry_can_be_told_from_a_second_call()
    {
        int asked = 0;
        var policy = new ScriptedPolicy(_ => ++asked == 1 ? throw new InvalidOperationException("the policy store blinked") : Allowed());
        AgentHarness h = Harness(policy);
        h.Workflow("two", async (ctx, _) =>
        {
            await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", AgentHarness.WireArgs("{}")));
            await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c2", "refund", AgentHarness.WireArgs("{}")));
            return [];
        });

        RunKey run = await h.StartAsync("two");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        string[] keys = [.. policy.Seen.Select(c => c.StepKey)];
        Assert.Equal(3, keys.Length); // the first call was asked twice (its first try failed), the second once
        Assert.Equal(keys[0], keys[1]); // a retry of the same step is the same step
        Assert.NotEqual(keys[1], keys[2]);
        Assert.All(keys, k => Assert.StartsWith(run.RunId.ToString("N") + ":", k, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_recording_policy_decides_in_a_step_of_its_own_before_the_tool_step_and_is_not_asked_again()
    {
        int ran = 0;
        var policy = new ScriptedPolicy(_ => Allowed());
        AgentHarness h = Harness(policy, _ => ran++);
        Call(h);

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        List<ActivityScheduled> steps = await StepsAsync(h, run);
        Assert.Equal([AgentActivityNames.Policy, AgentActivityNames.Tool], steps.Select(s => s.ActivityType));
        Assert.Equal(1, ran);
        Assert.Single(policy.Seen); // the tool step honoured the recorded decision
        AgentAuditSummary(await h.HistoryAsync(run), out string? rule, out long version, out string? hash);
        Assert.Equal(("r-allow", 3L, new string('a', 64)), (rule, version, hash));
    }

    private static void AgentAuditSummary(IReadOnlyList<HistoryEvent> history, out string? rule, out long version, out string? hash)
    {
        PolicyDecisionAuditRecord decision = Assert.Single(AgentAudit.Read(history).OfType<PolicyDecisionAuditRecord>());
        (rule, version, hash) = (decision.Rule, decision.PolicyVersion, decision.PolicyHash);
    }

    [Fact]
    public async Task The_baseline_policy_keeps_one_step_per_call_and_records_no_policy_step()
    {
        AgentHarness h = new(b => b.AddModel("m", AgentHarness.Echo()).ConfigureBaselinePolicy(p => p.Allow("refund")).AddTool(AgentHarness.WriteTool("refund", _ => "refunded")));
        Call(h);

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal([AgentActivityNames.Tool], (await StepsAsync(h, run)).Select(s => s.ActivityType));
    }

    [Fact]
    public async Task A_denied_call_never_reaches_a_tool_step_and_the_denial_is_in_history()
    {
        int ran = 0;
        AgentHarness h = Harness(new ScriptedPolicy(_ => Denied()), _ => ran++);
        Call(h);

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(0, ran);
        Assert.Equal([AgentActivityNames.Policy], (await StepsAsync(h, run)).Select(s => s.ActivityType));
        PolicyDecisionAuditRecord decision = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>());
        Assert.Equal((ToolVerdict.Deny, "r-deny"), (decision.Verdict, decision.Rule));
        Assert.Equal("Denied", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task The_policy_is_told_who_started_the_run_and_when()
    {
        var policy = new ScriptedPolicy(_ => Allowed());
        AgentHarness h = Harness(policy);
        Call(h);

        RunKey run = await h.StartAsAsync(new Principal(AgentHarness.Scope, Role.Operator, "ci-pipeline", null), "call");
        await h.RunAsync(run);

        ToolPolicyContext seen = Assert.Single(policy.Seen);
        Assert.Equal("Operator:ci-pipeline", seen.StartedBy);
        Assert.Equal(h.Time.GetUtcNow(), seen.Now);
        Assert.Equal("", (await RunWithoutStarterAsync()).StartedBy);

        async Task<ToolPolicyContext> RunWithoutStarterAsync()
        {
            var other = new ScriptedPolicy(_ => Allowed());
            AgentHarness h2 = Harness(other);
            Call(h2);
            await h2.RunAsync(await h2.StartAsync("call"));
            return Assert.Single(other.Seen);
        }
    }

    [Fact]
    public async Task What_the_record_holds_is_names_only_and_a_policy_cannot_put_argument_values_into_history_metadata()
    {
        AgentHarness h = Harness(new ScriptedPolicy(_ => Allowed()));
        Call(h, """{"amount":250,"note":"CANARY-ARG-7710"}""");

        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);

        // Everything a server can read of a history (it holds ciphertext for the rest): event kinds, activity types, ids, patch markers, signal names, error types.
        IReadOnlyList<HistoryEvent> stored = await h.Store.GetHistoryAsync(run, default);
        var readable = new StringBuilder();
        foreach (HistoryEvent e in stored)
        {
            readable.Append(e.GetType().Name).Append(' ');
            switch (e)
            {
                case ActivityScheduled s: readable.Append(s.ActivityType).Append(s.ActivityId); break;
                case PatchMarker m: readable.Append(m.PatchId); break;
                case SignalReceived sig: readable.Append(sig.SignalName).Append(sig.SignalId); break;
                case ActivityFailed f: readable.Append(f.ErrorType); break;
            }
        }

        Assert.DoesNotContain("CANARY-ARG-7710", readable.ToString(), StringComparison.Ordinal);
        PolicyDecisionAuditRecord decision = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>());
        Assert.DoesNotContain("CANARY-ARG-7710", decision.ToString(), StringComparison.Ordinal);
    }

    // ---- approval, and a policy that changes meanwhile -----------------------------------------------------------------

    [Fact]
    public async Task An_approved_call_runs_on_the_recorded_decision_even_if_the_policy_has_changed_while_a_person_decided()
    {
        int ran = 0;
        var policy = new ScriptedPolicy(_ => NeedsApproval());
        AgentHarness h = Harness(policy, _ => ran++);
        Call(h);
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;

        policy.Decide = _ => Denied("later-deny-all"); // an administrator edits the policy while the approval is open
        await h.Agent.ApproveAsync(run, id, Alice);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(1, ran);
        Assert.Single(policy.Seen);
        Assert.Equal("r-approve", Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>()).Rule);
    }

    [Fact]
    public async Task A_new_call_uses_the_policy_current_at_that_moment()
    {
        var policy = new ScriptedPolicy(_ => Allowed("before"));
        AgentHarness h = Harness(policy);
        h.Workflow("two", async (ctx, _) =>
        {
            await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", AgentHarness.WireArgs("{}")));
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(1));
            ToolStepResult second = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c2", "refund", AgentHarness.WireArgs("{}")));
            return Encoding.UTF8.GetBytes(second.Outcome.ToString());
        });
        RunKey run = await h.StartAsync("two");

        await h.RunAsync(run, jumpClock: false);
        policy.Decide = _ => Denied("after");
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(RunState.Completed, await h.RunAsync(run, jumpClock: false));

        Assert.Equal(["before", "after"], AgentAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>().Select(d => d.Rule));
        Assert.Equal("Denied", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task A_recorded_decision_is_honoured_only_for_the_arguments_it_was_made_for()
    {
        int ran = 0;
        AgentHarness h = Harness(new ScriptedPolicy(_ => Allowed()), _ => ran++);
        ActivityFunc tool = h.Runtime.Activities[AgentActivityNames.Tool];
        var context = new ActivityContext(new RunKey(AgentHarness.Scope.TenantId, AgentHarness.Scope.NamespaceId, Guid.NewGuid()), 5, AgentActivityNames.Tool, 1, "k");
        string args = """{"amount":1}""";
        string Input(string digest, string effect = "allow") =>
            JsonSerializer.Serialize(new { name = "refund", callId = "c", argumentsJson = args, decision = new { ruleId = "r", effect, argumentsDigest = digest } });
        string rightDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(args)));

        string swapped = Encoding.UTF8.GetString(await tool(context, Encoding.UTF8.GetBytes(Input(new string('0', 64))), default));
        string denied = Encoding.UTF8.GetString(await tool(context, Encoding.UTF8.GetBytes(Input(rightDigest, "deny")), default));
        string needsApproval = Encoding.UTF8.GetString(await tool(context, Encoding.UTF8.GetBytes(Input(rightDigest, "require_approval")), default));
        string good = Encoding.UTF8.GetString(await tool(context, Encoding.UTF8.GetBytes(Input(rightDigest)), default));

        Assert.Contains("denied", swapped, StringComparison.Ordinal);
        Assert.Contains("denied", denied, StringComparison.Ordinal);
        Assert.Contains("approval-required", needsApproval, StringComparison.Ordinal);
        Assert.Contains("completed", good, StringComparison.Ordinal);
        Assert.Equal(1, ran); // only the last one ran the tool
    }

    // ---- several approvers ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approval_that_needs_two_people_waits_for_a_second_different_one()
    {
        int ran = 0;
        AgentHarness h = Harness(new ScriptedPolicy(_ => NeedsApproval(count: 2)), _ => ran++);
        Call(h);
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;

        await h.Agent.ApproveAsync(run, id, Alice);
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))); // one of two is not a decision
        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = null }); // the same person again counts once
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        await h.Agent.ApproveAsync(run, id, Mallory); // not finance: refused by the verifier
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        Assert.Equal(0, ran);

        await h.Agent.ApproveAsync(run, id, Bob);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(1, ran);
        Assert.Empty(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        ApprovalAuditRecord audit = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<ApprovalAuditRecord>());
        Assert.Equal(("user:alice, user:bob", ApprovalAuditOutcome.Approved), (audit.Approver, audit.Outcome));
        Assert.Equal(1, audit.Refused);
    }

    [Fact]
    public async Task One_rejection_ends_an_approval_that_needs_two_people()
    {
        int ran = 0;
        AgentHarness h = Harness(new ScriptedPolicy(_ => NeedsApproval(count: 2)), _ => ran++);
        Call(h);
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;

        await h.Agent.ApproveAsync(run, id, Alice);
        await h.Agent.RejectAsync(run, id, Bob, "no");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(0, ran);
        Assert.Equal("Rejected", await h.ResultTextAsync(run));
    }

    // ---- the model call guard ------------------------------------------------------------------------------------------------

    private sealed class ScriptedGuard : IModelCallGuard
    {
        public List<ModelCallInfo> Reserved { get; } = [];

        public List<(ModelCallInfo Call, UsageRecord? Usage)> Settled { get; } = [];

        public Func<ModelCallInfo, ModelCallPermit> Decide { get; set; } = _ => ModelCallPermit.Allow("res");

        public List<ModelCallInfo> Released { get; } = [];

        public bool FailToSettle { get; set; }

        public bool FailToRelease { get; set; }

        public ValueTask<ModelCallPermit> ReserveAsync(ModelCallInfo request, CancellationToken cancellationToken)
        {
            Reserved.Add(request);
            return ValueTask.FromResult(Decide(request));
        }

        public ValueTask SettleAsync(ModelCallInfo request, ModelCallPermit permit, UsageRecord? usage, CancellationToken cancellationToken)
        {
            Settled.Add((request, usage));
            return FailToSettle ? throw new InvalidOperationException("the ledger is down") : ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(ModelCallInfo request, ModelCallPermit permit, CancellationToken cancellationToken)
        {
            Released.Add(request);
            return FailToRelease ? throw new InvalidOperationException("the ledger is down") : ValueTask.CompletedTask;
        }
    }

    private static AgentHarness Chatting(ScriptedChatClient model, ScriptedGuard guard, Action<AgentsBuilder>? more = null)
    {
        var h = new AgentHarness(b =>
        {
            b.AddModel("m", model).UseModelCallGuard(guard);
            more?.Invoke(b);
        });
        h.Workflow("chat", async (ctx, input) =>
        {
            AgentReply reply = await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "hi")], options: new AgentChatOptions(MaxOutputTokens: 500));
            return Encoding.UTF8.GetBytes(reply.Text);
        });
        return h;
    }

    [Fact]
    public async Task A_model_call_is_reserved_before_it_is_made_and_settled_with_the_usage_it_reported()
    {
        ScriptedChatClient model = AgentHarness.Echo("hello");
        var guard = new ScriptedGuard();
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        ModelCallInfo call = Assert.Single(guard.Reserved);
        Assert.Equal(("m", 500, 1), (call.Model, call.MaxOutputTokens, call.Attempt));
        Assert.EndsWith($":{call.ActivityId}", call.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal(new UsageRecord(10, 5, 0, ""), Assert.Single(guard.Settled).Usage! with { Model = "" });
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task A_call_that_fails_for_good_releases_what_was_held_on_its_last_attempt_only()
    {
        var model = new ScriptedChatClient(_ => throw new InvalidOperationException("the provider is down"));
        var guard = new ScriptedGuard();
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(3, guard.Reserved.Count); // three attempts reuse one reservation
        ModelCallInfo released = Assert.Single(guard.Released); // and it is given back once, by the attempt that was the last
        Assert.Equal(3, released.Attempt);
        Assert.Equal(guard.Reserved[0].IdempotencyKey, released.IdempotencyKey);
        Assert.Empty(guard.Settled);
    }

    [Fact]
    public async Task An_error_the_step_never_retries_releases_on_the_first_attempt()
    {
        var model = new ScriptedChatClient(_ => throw new InvalidOperationException("the request is refused"));
        var guard = new ScriptedGuard();
        AgentHarness h = Chatting(model, guard, b => b.WithModelRetry(new RetryPolicy { MaximumAttempts = 5, NonRetryableErrorTypes = [nameof(InvalidOperationException)] }));

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Single(guard.Reserved);
        Assert.Equal(1, Assert.Single(guard.Released).Attempt);
    }

    [Fact]
    public async Task A_call_that_fails_and_then_succeeds_releases_nothing_and_settles_once()
    {
        int calls = 0;
        var model = new ScriptedChatClient(_ => ++calls == 1 ? throw new TimeoutException("slow") : new ChatMessage(ChatRole.Assistant, "hello"));
        var guard = new ScriptedGuard();
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Empty(guard.Released);
        Assert.Single(guard.Settled);
    }

    [Fact]
    public async Task A_guard_that_cannot_release_does_not_change_how_the_step_failed()
    {
        async Task<(string ErrorType, string Message)> FailWith(bool guardBroken)
        {
            var model = new ScriptedChatClient(_ => throw new InvalidOperationException("the provider is down"));
            var guard = new ScriptedGuard { FailToRelease = guardBroken };
            AgentHarness h = Chatting(model, guard);
            RunKey run = await h.StartAsync("chat");
            Assert.Equal(RunState.Failed, await h.RunAsync(run));
            Assert.Single(guard.Released);
            WorkflowFailed failed = await h.FailureAsync(run);
            return (failed.ErrorType, failed.Message);
        }

        Assert.Equal(await FailWith(guardBroken: false), await FailWith(guardBroken: true));
    }

    [Fact]
    public async Task A_refused_call_is_not_made_and_with_fail_the_run_ends_with_the_limit_named()
    {
        ScriptedChatClient model = AgentHarness.Echo();
        var guard = new ScriptedGuard { Decide = _ => ModelCallPermit.Refuse("namespace-day-money") };
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Assert.Equal(0, model.CallCount);
        WorkflowFailed failed = await h.FailureAsync(run);
        Assert.Equal(nameof(BudgetExceededException), failed.ErrorType);
        Assert.Contains("namespace-day-money", failed.Message, StringComparison.Ordinal);
        Assert.Empty(guard.Settled);
    }

    [Fact]
    public async Task With_pause_a_person_is_asked_to_raise_the_budget_and_one_call_goes_through_on_approval()
    {
        ScriptedChatClient model = AgentHarness.Echo("hello");
        var guard = new ScriptedGuard
        {
            Decide = call => call.AllowanceApprovalId is null
                ? ModelCallPermit.Refuse("run-tokens", "pause", new ApprovalRequirement(Roles: ["finance"]))
                : ModelCallPermit.Allow("allowance"),
        };
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        PendingApproval request = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Contains("run-tokens", request.Action, StringComparison.Ordinal);
        Assert.Equal(0, model.CallCount);

        await h.Agent.ApproveAsync(run, request.ApprovalId, Alice);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(1, model.CallCount);
        Assert.Equal(request.ApprovalId, guard.Reserved[^1].AllowanceApprovalId);
        Assert.Equal("hello", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task With_pause_a_rejected_request_ends_the_run_and_the_model_is_never_called()
    {
        ScriptedChatClient model = AgentHarness.Echo();
        var guard = new ScriptedGuard { Decide = _ => ModelCallPermit.Refuse("run-tokens", "pause", new ApprovalRequirement(Roles: ["finance"])) };
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;
        await h.Agent.RejectAsync(run, id, Alice, "no");

        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Equal(0, model.CallCount);
        Assert.Equal(nameof(BudgetExceededException), (await h.FailureAsync(run)).ErrorType);
    }

    [Fact]
    public async Task A_guard_that_cannot_decide_stops_the_call_and_the_step_is_retried_never_made_unguarded()
    {
        ScriptedChatClient model = AgentHarness.Echo();
        int asked = 0;
        var guard = new ScriptedGuard
        {
            Decide = _ => ++asked == 1 ? throw new InvalidOperationException("the ledger is down") : ModelCallPermit.Allow("res"),
        };
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(2, guard.Reserved.Count); // the same activity asked twice
        Assert.Equal(guard.Reserved[0].IdempotencyKey, guard.Reserved[1].IdempotencyKey);
        Assert.Equal((1, 2), (guard.Reserved[0].Attempt, guard.Reserved[1].Attempt));
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task A_ledger_that_cannot_settle_does_not_make_the_step_call_the_model_again()
    {
        ScriptedChatClient model = AgentHarness.Echo();
        var guard = new ScriptedGuard { FailToSettle = true };
        AgentHarness h = Chatting(model, guard);

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(1, model.CallCount); // the call was paid for; whoever reconciles settles it from the recorded usage
        Assert.Single(guard.Reserved);
    }

    [Fact]
    public async Task Without_a_guard_nothing_changes_for_a_model_call()
    {
        ScriptedChatClient model = AgentHarness.Echo();
        var h = new AgentHarness(b => b.AddModel("m", model));
        h.Workflow("chat", async (ctx, _) => Encoding.UTF8.GetBytes((await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "hi")])).Text));

        RunKey run = await h.StartAsync("chat");

        Assert.Equal(RunState.Completed, await h.RunAsync(run));
    }

    // ---- authorization ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_an_administrator_may_manage_policy_and_only_an_administrator_or_operator_may_read_it()
    {
        var scope = AgentHarness.Scope;
        foreach (Role role in Enum.GetValues<Role>())
        {
            var principal = new Principal(scope, role, "x", null);
            Assert.Equal(role == Role.Admin, AccessPolicy.IsAllowed(principal, Operation.ManagePolicy, scope));
            Assert.Equal(role is Role.Admin or Role.Operator, AccessPolicy.IsAllowed(principal, Operation.ReadPolicy, scope));
        }

        Assert.False(AccessPolicy.IsAllowed(new Principal(new TenantScope(Guid.NewGuid(), scope.NamespaceId), Role.Admin, "x", null), Operation.ManagePolicy, scope));
    }
}
