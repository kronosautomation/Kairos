using System.Security.Cryptography;
using System.Text;
using Kairos.Agents;

namespace Kairos.Agents.Tests;

/// <summary>What an auditor asks of an agent run, read back from its history: approvals with the verified approver, the policy's decision for each tool call, and the credential each call was given.</summary>
public sealed class AgentAuditTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly ApproverIdentity Mallory = new("user:mallory", "Mallory", ["support"]);
    private static readonly byte[] CredentialKey = RandomNumberGenerator.GetBytes(32);
    private const string Canary = "CANARY-ARGUMENT-4471";

    private static void SingleCall(AgentHarness h, string tool, string args = "{}", string? onBehalfOf = null) =>
        h.Workflow("call", async (ctx, _) =>
        {
            ctx.OnBehalfOf = onBehalfOf;
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c1", tool, AgentHarness.WireArgs(args)));
            return Encoding.UTF8.GetBytes($"{r.Outcome}");
        });

    private static AgentHarness Refunds(Action<AgentsBuilder>? more = null, IPayloadProtector? protector = null) =>
        new(b =>
        {
            b.AddModel("m", AgentHarness.Echo())
                .UseCredentials(new HmacToolCredentialIssuer(CredentialKey))
                .ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement(Roles: ["finance"])).Deny("wipe").Allow("notify"))
                .AddTool(AgentHarness.WriteTool("refund", _ => "refunded"))
                .AddTool(AgentHarness.WriteTool("wipe", _ => "wiped"))
                .AddTool(AgentHarness.WriteTool("notify", _ => "sent"))
                .AddTool(AgentHarness.ReadTool("lookup", _ => "found"));
            more?.Invoke(b);
        }, protector: protector);

    [Fact]
    public async Task An_allowed_call_has_a_policy_decision_and_a_credential_and_never_the_token()
    {
        string? token = null;
        AgentHarness h = new(b => b.AddModel("m", AgentHarness.Echo()).UseCredentials(new HmacToolCredentialIssuer(CredentialKey))
            .ConfigureBaselinePolicy(p => p.Allow("notify"))
            .AddTool(new AgentTool("notify", "n", """{"type":"object"}""", ToolAccess.SideEffecting, (i, _) =>
            {
                token = i.Credential!.Token;
                return ValueTask.FromResult("sent");
            })));
        SingleCall(h, "notify", $$"""{"to":"{{Canary}}"}""", onBehalfOf: "customer-77");
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        IReadOnlyList<AgentAuditRecord> records = AgentAudit.Read(await h.HistoryAsync(run));

        PolicyDecisionAuditRecord decision = Assert.Single(records.OfType<PolicyDecisionAuditRecord>());
        Assert.Equal((ToolVerdict.Auto, "notify", null), (decision.Verdict, decision.Tool, decision.Reason));
        CredentialAuditRecord credential = Assert.Single(records.OfType<CredentialAuditRecord>());
        Assert.Equal(["notify:read", "notify:write"], credential.Scopes);
        Assert.Equal("customer-77", credential.OnBehalfOf);
        Assert.Equal(decision.Step, credential.Step);
        Assert.True(credential.ExpiresAt > credential.At && credential.ExpiresAt - credential.At <= TimeSpan.FromMinutes(15));
        Assert.NotNull(token);
        Assert.DoesNotContain(token, Dump(records), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, Dump(records), StringComparison.Ordinal); // the arguments are not part of the audit
    }

    [Fact]
    public async Task A_read_only_tool_gets_a_read_only_credential_and_a_denied_one_gets_none()
    {
        AgentHarness h = Refunds();
        h.Workflow("two", async (ctx, _) =>
        {
            await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c1", "lookup", AgentHarness.WireArgs("{}")));
            await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c2", "wipe", AgentHarness.WireArgs("{}")));
            return [];
        });
        RunKey run = await h.StartAsync("two");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        IReadOnlyList<AgentAuditRecord> records = AgentAudit.Read(await h.HistoryAsync(run));

        Assert.Equal([ToolVerdict.Auto, ToolVerdict.Deny], records.OfType<PolicyDecisionAuditRecord>().Select(d => d.Verdict));
        Assert.Contains("denied by policy", records.OfType<PolicyDecisionAuditRecord>().Last().Reason, StringComparison.Ordinal);
        CredentialAuditRecord only = Assert.Single(records.OfType<CredentialAuditRecord>());
        Assert.Equal("lookup", only.Tool);
        Assert.Equal(["lookup:read"], only.Scopes);
    }

    [Fact]
    public async Task An_approval_is_unresolved_until_a_decision_is_accepted_and_then_names_the_verified_approver()
    {
        AgentHarness h = Refunds();
        SingleCall(h, "refund");
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Running, await h.RunAsync(run));

        ApprovalAuditRecord waiting = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<ApprovalAuditRecord>());
        Assert.Equal((ApprovalAuditOutcome.Unresolved, "refund", null, 0), (waiting.Outcome, waiting.Tool, waiting.Approver, waiting.Refused));
        Assert.Equal(ToolVerdict.RequireApproval, Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<PolicyDecisionAuditRecord>()).Verdict);

        string id = waiting.ApprovalId;
        await h.Agent.ApproveAsync(run, id, Mallory); // a support agent: refused by the verifier
        await h.RunAsync(run);
        Assert.Equal(1, Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<ApprovalAuditRecord>()).Refused);

        await h.Agent.ApproveAsync(run, id, Alice);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        IReadOnlyList<AgentAuditRecord> done = AgentAudit.Read(await h.HistoryAsync(run));
        ApprovalAuditRecord approval = Assert.Single(done.OfType<ApprovalAuditRecord>());
        Assert.Equal(ApprovalAuditOutcome.Approved, approval.Outcome);
        Assert.Equal("user:alice", approval.Approver);
        Assert.Equal(["finance"], approval.Roles);
        Assert.Equal(1, approval.Refused);
        Assert.True(approval.DecidedAt >= approval.RequestedAt);
        Assert.Equal(2, done.OfType<PolicyDecisionAuditRecord>().Count()); // asked once (hold), then again with the approval attached (allow)
        Assert.Equal([ToolVerdict.RequireApproval, ToolVerdict.Auto], done.OfType<PolicyDecisionAuditRecord>().Select(d => d.Verdict));
    }

    [Fact]
    public async Task A_rejection_is_recorded_as_a_rejection()
    {
        AgentHarness h = Refunds();
        SingleCall(h, "refund");
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;

        await h.Agent.RejectAsync(run, id, Alice, "no");
        await h.RunAsync(run);

        ApprovalAuditRecord approval = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<ApprovalAuditRecord>());
        Assert.Equal((ApprovalAuditOutcome.Rejected, "user:alice"), (approval.Outcome, approval.Approver));
    }

    [Fact]
    public async Task What_the_approver_said_about_themselves_is_not_what_is_recorded()
    {
        AgentHarness h = Refunds();
        SingleCall(h, "refund");
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;

        // The signal claims to be an executive; the verifier establishes who really decided from the signed evidence.
        var claimed = new ApproverIdentity("user:alice", "The CEO", ["finance"]);
        await h.Agent.ApproveAsync(run, id, claimed, "x");
        await h.RunAsync(run);

        ApprovalAuditRecord approval = Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<ApprovalAuditRecord>());
        Assert.Equal("user:alice", approval.Approver);
        Assert.DoesNotContain("CEO", Dump([approval]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sealed_history_is_refused_by_read_and_gives_its_approval_signals_without_opening()
    {
        AgentHarness h = Refunds(protector: new ReversingProtector());
        SingleCall(h, "refund");
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;
        await h.Agent.ApproveAsync(run, id, Alice);
        await h.RunAsync(run);
        IReadOnlyList<HistoryEvent> stored = await h.Store.GetHistoryAsync(run, default);

        Assert.Throws<FormatException>(() => AgentAudit.Read(stored));
        ApprovalAuditRecord signal = Assert.Single(AgentAudit.ApprovalSignals(stored));
        Assert.Equal((id, ApprovalAuditOutcome.Sealed, null), (signal.ApprovalId, signal.Outcome, signal.Approver));
        Assert.Equal(ApprovalAuditOutcome.Approved, Assert.Single(AgentAudit.Read(await h.HistoryAsync(run)).OfType<ApprovalAuditRecord>()).Outcome); // opened with the key
    }

    [Fact]
    public async Task Records_follow_the_order_history_recorded_them_in()
    {
        AgentHarness h = Refunds();
        h.Workflow("many", async (ctx, _) =>
        {
            await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c1", "notify", AgentHarness.WireArgs("{}")));
            await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c2", "lookup", AgentHarness.WireArgs("{}")));
            return [];
        });
        RunKey run = await h.StartAsync("many");
        await h.RunAsync(run);

        IReadOnlyList<AgentAuditRecord> records = AgentAudit.Read(await h.HistoryAsync(run));

        Assert.Equal(["notify", "lookup"], records.OfType<PolicyDecisionAuditRecord>().Select(d => d.Tool));
        Assert.Equal(records.OrderBy(r => r.At).Select(r => r.At), records.Select(r => r.At));
    }

    // ---- the markers the server lists approvals from -----------------------------------------------------------------

    private static async Task<string[]> WaitingAsync(AgentHarness h, RunKey run) =>
        [.. RunProjection.Of(await h.Store.GetHistoryAsync(run, default)).Pending.Select(p => p.ApprovalId)]; // from the stored history: no key, nothing opened

    private static AgentHarness TwoApprovals(IPayloadProtector? protector = null, TimeSpan? firstTimeout = null)
    {
        AgentHarness h = new(b => b.AddModel("m", AgentHarness.Echo())
                .ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement(Roles: ["finance"], Timeout: firstTimeout)))
                .AddTool(AgentHarness.WriteTool("refund", _ => "refunded")), protector: protector);
        h.Workflow("two", async (ctx, _) =>
        {
            await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c1", "refund", AgentHarness.WireArgs("{}")));
            await ctx.InvokeToolWithOutcomeAsync(new Microsoft.Extensions.AI.FunctionCallContent("c2", "refund", AgentHarness.WireArgs("{}")));
            return [];
        });
        return h;
    }

    [Fact]
    public async Task A_waiting_approval_is_listed_from_readable_markers_and_leaves_the_list_when_it_is_decided_while_the_run_goes_on()
    {
        AgentHarness h = TwoApprovals(new ReversingProtector());
        RunKey run = await h.StartAsync("two");
        await h.RunAsync(run);
        string first = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;
        Assert.Equal([first], await WaitingAsync(h, run));

        await h.Agent.ApproveAsync(run, first, Alice);
        await h.RunAsync(run); // the run now waits for a second approval

        string second = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run))).ApprovalId;
        Assert.NotEqual(first, second);
        Assert.Equal([second], await WaitingAsync(h, run)); // the first is resolved, the second is waiting, and the run is still open

        await h.Agent.ApproveAsync(run, second, Alice);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Empty(await WaitingAsync(h, run));
    }

    [Fact]
    public async Task A_rejected_approval_and_one_that_timed_out_also_leave_the_list()
    {
        AgentHarness h = TwoApprovals(firstTimeout: TimeSpan.FromMinutes(5));
        RunKey run = await h.StartAsync("two");
        await h.RunAsync(run, jumpClock: false);
        string first = Assert.Single(await WaitingAsync(h, run));

        h.Time.Advance(TimeSpan.FromMinutes(6));
        await h.RunAsync(run, jumpClock: false); // the first expired; the run asked for the second

        string second = Assert.Single(await WaitingAsync(h, run));
        Assert.NotEqual(first, second);
        await h.Agent.RejectAsync(run, second, Alice, "no");
        Assert.Equal(RunState.Completed, await h.RunAsync(run, jumpClock: false));
        Assert.Empty(await WaitingAsync(h, run));
    }

    [Fact]
    public async Task A_decision_that_the_verifier_refused_leaves_the_approval_waiting()
    {
        AgentHarness h = TwoApprovals();
        RunKey run = await h.StartAsync("two");
        await h.RunAsync(run);
        string id = Assert.Single(await WaitingAsync(h, run));

        await h.Agent.ApproveAsync(run, id, Mallory); // not finance
        await h.RunAsync(run);

        Assert.Equal([id], await WaitingAsync(h, run));
    }

    private static string Dump(IEnumerable<AgentAuditRecord> records) => string.Join("\n", records.Select(r => r.ToString()));

    private sealed class ReversingProtector : IPayloadProtector
    {
        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<byte[]>([.. SHA256.HashData(context.Span), .. plaintext.ToArray().Reverse()]);

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] all = protectedPayload.ToArray();
            return all.Length >= 32 && all.AsSpan(0, 32).SequenceEqual(SHA256.HashData(context.Span))
                ? ValueTask.FromResult(all.Skip(32).Reverse().ToArray())
                : throw new PayloadProtectionException();
        }
    }
}
