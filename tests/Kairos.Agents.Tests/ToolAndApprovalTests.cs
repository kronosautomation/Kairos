using System.Text;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

public sealed class ToolAndApprovalTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly ApproverIdentity Mallory = new("user:mallory", "Mallory", ["support"]);

    private static FunctionCallContent Call(string tool, string args = "{}") => new("c1", tool, AgentHarness.WireArgs(args));

    private static AgentHarness Harness(ScriptedChatClient? model = null, Action<AgentsBuilder>? more = null, params AgentTool[] tools) =>
        new(b =>
        {
            b.AddModel("m", model ?? AgentHarness.Echo());
            foreach (AgentTool t in tools)
            {
                b.AddTool(t);
            }

            more?.Invoke(b);
        });

    private static void SingleCall(AgentHarness h, string tool, string args = "{}", string? onBehalfOf = null) =>
        h.Workflow("call", async (ctx, _) =>
        {
            ctx.OnBehalfOf = onBehalfOf;
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(Call(tool, args));
            return Encoding.UTF8.GetBytes($"{r.Outcome}|{((FunctionResultContent)r.Message.Contents[0]).Result}");
        });

    // ---- read-only baseline --------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_only_tool_runs_without_asking_and_gets_the_steps_idempotency_key()
    {
        string? seenKey = null;
        AgentHarness h = Harness(tools: AgentHarness.ReadTool("lookup", i =>
        {
            seenKey = i.IdempotencyKey;
            return "found";
        }));
        SingleCall(h, "lookup");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal("Completed|found", await h.ResultTextAsync(run));
        ActivityScheduled step = (await h.HistoryAsync(run)).OfType<ActivityScheduled>().Single(a => a.ActivityType == AgentActivityNames.Tool);
        Assert.Equal($"{run.RunId:N}:{step.ActivityId}", seenKey); // run id + step number
    }

    [Fact]
    public async Task A_side_effecting_tool_is_held_for_approval_by_default()
    {
        int effects = 0;
        AgentHarness h = Harness(tools: AgentHarness.WriteTool("refund", _ =>
        {
            effects++;
            return "refunded";
        }));
        SingleCall(h, "refund");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Running, await h.RunAsync(run)); // waiting for a person

        Assert.Equal(0, effects);
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
        Assert.Equal("refund", pending.Tool);
    }

    [Fact]
    public async Task An_allow_policy_lets_a_write_tool_run_without_asking()
    {
        int effects = 0;
        AgentHarness h = Harness(more: b => b.ConfigureBaselinePolicy(p => p.Allow("refund")), tools: AgentHarness.WriteTool("refund", _ =>
        {
            effects++;
            return "refunded";
        }));
        SingleCall(h, "refund");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal(1, effects);
        Assert.Equal("Completed|refunded", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task A_denied_tool_never_runs_and_an_approval_cannot_override_the_denial()
    {
        int effects = 0;
        AgentHarness h = Harness(more: b => b.ConfigureBaselinePolicy(p => p.Deny("wipe")), tools: AgentHarness.WriteTool("wipe", _ =>
        {
            effects++;
            return "wiped";
        }));
        SingleCall(h, "wipe");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(0, effects);
        Assert.StartsWith("Denied|", await h.ResultTextAsync(run), StringComparison.Ordinal);
        Assert.Empty(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
    }

    [Fact]
    public async Task A_tool_that_reports_an_error_tells_the_model_instead_of_retrying()
    {
        int calls = 0;
        AgentHarness h = Harness(tools: new AgentTool("lookup", "x", """{"type":"object"}""", ToolAccess.ReadOnly, (_, _) =>
        {
            calls++;
            throw new ToolException("order 9 does not exist");
        }));
        SingleCall(h, "lookup");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("Failed|order 9 does not exist", await h.ResultTextAsync(run));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_tool_that_keeps_crashing_is_retried_then_reported_to_the_model()
    {
        int calls = 0;
        AgentHarness h = Harness(tools: new AgentTool("lookup", "x", """{"type":"object"}""", ToolAccess.ReadOnly, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("db down");
        }));
        SingleCall(h, "lookup");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal(3, calls);
        Assert.StartsWith("Failed|The tool 'lookup' failed (InvalidOperationException)", await h.ResultTextAsync(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_asking_for_a_tool_that_does_not_exist_gets_an_error_message()
    {
        AgentHarness h = Harness();
        SingleCall(h, "ghost");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("Failed|There is no tool named 'ghost'.", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task A_tool_that_fails_after_its_effect_takes_effect_once_per_key()
    {
        AgentHarness h = null!;
        h = Harness(more: b => b.ConfigureBaselinePolicy(p => p.Allow("charge")), tools: new AgentTool("charge", "x", """{"type":"object"}""", ToolAccess.SideEffecting, (i, _) =>
        {
            h.External.Apply(i.IdempotencyKey);
            if (h.External.Calls[i.IdempotencyKey] < 3)
            {
                throw new InvalidOperationException("lost the answer after charging");
            }

            return ValueTask.FromResult("charged");
        }));
        SingleCall(h, "charge");

        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal("Completed|charged", await h.ResultTextAsync(run));
        Assert.Single(h.External.Effects);
        Assert.Equal(3, h.External.Calls.Values.Single()); // three attempts, one key, one effect
    }

    // ---- approvals -----------------------------------------------------------------------------------------

    private static async Task<(AgentHarness H, RunKey Run, string ApprovalId)> WaitingForApprovalAsync(
        ApprovalRequirement? requirement = null, string? onBehalfOf = null, Func<ToolInvocation, string>? handler = null)
    {
        AgentHarness h = Harness(
            more: b => b.ConfigureBaselinePolicy(p => p.RequireApproval("refund", requirement ?? new ApprovalRequirement(Roles: ["finance"]))),
            tools: AgentHarness.WriteTool("refund", handler ?? (_ => "refunded")));
        SingleCall(h, "refund", onBehalfOf: onBehalfOf);
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        return (h, run, Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId);
    }

    [Fact]
    public async Task An_approved_call_runs_and_the_approvers_identity_is_in_history()
    {
        ApprovalDecision? seen = null;
        (AgentHarness h, RunKey run, string id) = await WaitingForApprovalAsync(handler: null);
        h.Workflow("noop", (_, _) => Task.FromResult(Array.Empty<byte>()));

        Assert.True(await h.Agent.ApproveAsync(run, id, Alice, "looks right"));
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("Completed|refunded", await h.ResultTextAsync(run));

        // The decision is in the recorded signal, and the tool step that ran carried it.
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        SignalReceived signal = Assert.Single(history.OfType<SignalReceived>());
        string payload = Encoding.UTF8.GetString(signal.Payload);
        Assert.Contains("user:alice", payload, StringComparison.Ordinal);
        ActivityScheduled second = history.OfType<ActivityScheduled>().Last(a => a.ActivityType == AgentActivityNames.Tool);
        Assert.Contains("user:alice", Encoding.UTF8.GetString(second.Input), StringComparison.Ordinal);
        Assert.Empty(AgentHistory.PendingApprovals(history));
        Assert.Null(seen);
    }

    [Fact]
    public async Task The_handler_sees_who_approved()
    {
        ApproverIdentity? approver = null;
        AgentHarness h = Harness(
            more: b => b.ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement())),
            tools: AgentHarness.WriteTool("refund", i =>
            {
                approver = i.Approval?.Approver;
                return "ok";
            }));
        SingleCall(h, "refund");
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.ApproveAsync(run, id, Alice);
        await h.RunAsync(run);

        Assert.Equal("user:alice", approver?.Subject);
        Assert.Equal("Alice", approver?.Name);
    }

    [Fact]
    public async Task A_rejected_call_never_runs_and_the_model_is_told_who_said_no()
    {
        int effects = 0;
        (AgentHarness h, RunKey run, string id) = await WaitingForApprovalAsync(handler: _ =>
        {
            effects++;
            return "refunded";
        });

        await h.Agent.RejectAsync(run, id, Alice, "not a valid claim");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(0, effects);
        Assert.Equal("Rejected|A person rejected the call by user:alice, so it was not made.", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task A_decision_from_the_wrong_identity_is_ignored_and_the_run_keeps_waiting()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingForApprovalAsync();

        await h.Agent.ApproveAsync(run, id, Mallory); // a support agent, not finance
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))); // the inbox still shows it

        await h.Agent.ApproveAsync(run, id, Alice);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
    }

    [Fact]
    public async Task The_person_the_agent_acts_for_cannot_approve_their_own_request()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingForApprovalAsync(
            new ApprovalRequirement(Roles: ["finance"]), onBehalfOf: "user:alice");

        await h.Agent.ApproveAsync(run, id, Alice); // finance, but it is her own request
        Assert.Equal(RunState.Running, await h.RunAsync(run));

        var bob = new ApproverIdentity("user:bob", "Bob", ["finance"]);
        await h.Agent.ApproveAsync(run, id, bob);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
    }

    [Fact]
    public async Task A_decision_for_another_approval_or_garbage_is_ignored()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingForApprovalAsync();

        await h.Client.SignalAsync(run, AgentClient.ApprovalSignalName(id), Encoding.UTF8.GetBytes("not json"), signalId: "junk");
        await h.Client.SignalAsync(run, AgentClient.ApprovalSignalName(id),
            Encoding.UTF8.GetBytes("""{"approvalId":"someone-elses","approved":true,"approver":{"subject":"user:alice","roles":["finance"]}}"""), signalId: "wrong-id");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
    }

    [Fact]
    public async Task Approving_twice_with_a_retry_signals_once_and_a_second_decision_changes_nothing()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingForApprovalAsync();

        Assert.True(await h.Agent.ApproveAsync(run, id, Alice));
        Assert.True(await h.Agent.ApproveAsync(run, id, Alice)); // a retried request: same signal id
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Single((await h.HistoryAsync(run)).OfType<SignalReceived>());
        Assert.False(await h.Agent.RejectAsync(run, id, Alice)); // too late: the run is closed
        Assert.Equal("Completed|refunded", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task An_approval_that_arrives_after_the_timeout_is_too_late()
    {
        AgentHarness h = Harness(
            more: b => b.ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement(Timeout: TimeSpan.FromHours(1)))),
            tools: AgentHarness.WriteTool("refund", _ => "refunded"));
        SingleCall(h, "refund");
        RunKey run = await h.StartAsync("call");

        Assert.Equal(RunState.Completed, await h.RunAsync(run)); // the clock jumped to the timeout and the approval expired
        Assert.Equal("Expired|Nobody approved the call in time, so it was not made.", await h.ResultTextAsync(run));

        string id = (await h.HistoryAsync(run)).OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentActivityNames.ApprovalRequest)
            .Select(a => System.Text.Json.JsonDocument.Parse(a.Input).RootElement.GetProperty("approvalId").GetString()!).Single();
        Assert.False(await h.Agent.ApproveAsync(run, id, Alice)); // closed run: nothing to deliver to
    }

    [Fact]
    public async Task A_notifier_hears_about_each_request_once_and_a_broken_notifier_does_not_fail_the_run()
    {
        var notices = new List<ApprovalNotice>();
        AgentHarness h = Harness(
            more: b => b.UseApprovalNotifier(new RecordingNotifier(notices)),
            tools: AgentHarness.WriteTool("refund", _ => "refunded"));
        SingleCall(h, "refund", args: """{"amount":250}""");
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);

        ApprovalNotice notice = Assert.Single(notices);
        Assert.Equal("refund", notice.Request.Tool);
        Assert.Contains("250", notice.Request.Details, StringComparison.Ordinal);

        AgentHarness broken = Harness(
            more: b => b.UseApprovalNotifier(new ThrowingNotifier()),
            tools: AgentHarness.WriteTool("refund", _ => "refunded"));
        SingleCall(broken, "refund");
        RunKey run2 = await broken.StartAsync("call");
        Assert.Equal(RunState.Running, await broken.RunAsync(run2)); // still waiting, not failed
        Assert.Single(AgentHistory.PendingApprovals(await broken.HistoryAsync(run2)));
    }

    [Fact]
    public async Task A_free_standing_approval_in_a_workflow_returns_the_decision_with_the_approver()
    {
        AgentHarness h = Harness();
        h.Workflow("publish", async (ctx, _) =>
        {
            ApprovalDecision d = await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Publish the report", Requirement = new ApprovalRequirement(Roles: ["finance"]) });
            d.ThrowIfNotApproved();
            return Encoding.UTF8.GetBytes(d.Approver!.Subject + "|" + d.Comment);
        });
        RunKey run = await h.StartAsync("publish");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;

        await h.Agent.ApproveAsync(run, id, Alice, "ship it");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("user:alice|ship it", await h.ResultTextAsync(run));
    }

    [Fact]
    public async Task A_rejected_free_standing_approval_throws_when_asked_to()
    {
        AgentHarness h = Harness();
        h.Workflow("publish", async (ctx, _) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Publish" })).ThrowIfNotApproved();
            return [];
        });
        RunKey run = await h.StartAsync("publish");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.RejectAsync(run, id, Alice);
        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Equal(nameof(ApprovalRejectedException), (await h.FailureAsync(run)).ErrorType);
    }

    private sealed class RecordingNotifier(List<ApprovalNotice> notices) : IApprovalNotifier
    {
        public ValueTask NotifyAsync(ApprovalNotice notice, CancellationToken cancellationToken)
        {
            notices.Add(notice);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IApprovalNotifier
    {
        public ValueTask NotifyAsync(ApprovalNotice notice, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Slack is down");
    }
}
