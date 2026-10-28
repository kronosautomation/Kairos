using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

/// <summary>A1: nobody who can merely send a signal can approve, and a decision is only as good as the evidence that proves who made it.</summary>
public sealed class ApproverVerificationTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);
    private static readonly ApproverIdentity Bob = new("user:bob", "Bob", ["finance"]);
    private static readonly ApproverIdentity Mallory = new("user:mallory", "Mallory", ["support"]);

    private static async Task<(AgentHarness H, RunKey Run, string Id)> WaitingAsync(
        string? onBehalfOf = null, bool verify = true, ApprovalRequirement? requirement = null)
    {
        var h = new AgentHarness(
            b => b.AddModel("m", AgentHarness.Echo()).AddTool(AgentHarness.WriteTool("refund", _ => "refunded"))
                .ConfigureBaselinePolicy(p => p.RequireApproval("refund", requirement ?? new ApprovalRequirement(Roles: ["finance"]))),
            verifyApprovers: verify);
        h.Workflow("call", async (ctx, _) =>
        {
            ctx.OnBehalfOf = onBehalfOf;
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", null));
            return Encoding.UTF8.GetBytes(r.Outcome.ToString());
        });
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        return (h, run, AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId);
    }

    private static string RawDecision(string id, string subject, string roles) =>
        "{\"approvalId\":\"" + id + "\",\"approved\":true,\"approver\":{\"subject\":\"" + subject + "\",\"roles\":[" + roles + "]}}";

    private static string Token(AgentHarness h, RunKey run, string id, bool approved, ApproverIdentity who, TimeSpan? life = null) =>
        AgentHarness.Issue(run, id, approved, who, h.Time.GetUtcNow(), life);

    private static async Task AssertStillWaitingAsync(AgentHarness h, RunKey run)
    {
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));
    }

    // ---- the reserved signal name ---------------------------------------------------------------------------

    [Fact]
    public async Task A_plain_signal_in_the_reserved_approval_namespace_is_refused()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.Client.SignalAsync(run, AgentClient.ApprovalSignalName(id), Encoding.UTF8.GetBytes("{}"), signalId: "raw"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.Client.SignalAsync(run, ReservedSignals.ApprovalPrefix + "anything", [], CancellationToken.None));

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task A_forged_decision_written_straight_to_the_store_is_not_accepted_without_evidence()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();
        string forged = RawDecision(id, "user:alice", "\"finance\"");

        // Bypasses the client entirely, as anyone with write access to the store could.
        await h.Store.SignalRunAsync(run, AgentClient.ApprovalSignalName(id), "forged", Encoding.UTF8.GetBytes(forged), h.Time.GetUtcNow(), CancellationToken.None);

        await AssertStillWaitingAsync(h, run);
        Assert.False((await LastVerificationAsync(h, run)).Ok);
    }

    private static async Task<ApprovalVerifyResultView> LastVerificationAsync(AgentHarness h, RunKey run)
    {
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        ActivityScheduled step = history.OfType<ActivityScheduled>().Last(a => a.ActivityType == AgentActivityNames.ApprovalVerify);
        ActivityCompleted done = history.OfType<ActivityCompleted>().Single(c => c.ActivityId == step.ActivityId);
        using var doc = System.Text.Json.JsonDocument.Parse(done.Result);
        return new ApprovalVerifyResultView(doc.RootElement.GetProperty("ok").GetBoolean(), doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null);
    }

    private sealed record ApprovalVerifyResultView(bool Ok, string? Reason);

    // ---- evidence must name this run, this approval, this answer and this subject ----------------------------

    [Fact]
    public async Task Evidence_that_names_a_different_subject_is_refused()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();

        // Mallory holds a genuine token for Bob and tries to use it to speak as Alice.
        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = Token(h, run, id, true, Bob) });

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task Evidence_issued_for_a_different_approval_is_refused()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();

        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = Token(h, run, "another-approval", true, Alice) });

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task Evidence_issued_for_a_different_run_is_refused()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();
        var otherRun = new RunKey(run.TenantId, run.NamespaceId, Guid.NewGuid());

        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = Token(h, otherRun, id, true, Alice) });

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task Evidence_for_a_rejection_cannot_be_turned_into_an_approval()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();

        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = Token(h, run, id, approved: false, Alice) });

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task An_expired_or_foreign_token_is_refused()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();
        string expired = Token(h, run, id, true, Alice, TimeSpan.FromSeconds(1));
        h.Time.Advance(TimeSpan.FromMinutes(1));
        string foreign = AgentHarness.Issue(run, id, true, Alice, h.Time.GetUtcNow(), signer: LocalApprovalEvidenceSigner.Create(AgentHarness.ApprovalSigner.KeyId)); // the right key id, somebody else's key

        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = expired });
        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = foreign });
        await h.Agent.ApproveAsync(run, id, Alice with { Evidence = "garbage.garbage.garbage" });

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task The_roles_in_the_token_decide_not_the_roles_the_caller_claims()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();
        string supportToken = Token(h, run, id, true, Mallory); // genuinely Mallory, genuinely a support agent

        // The decision claims to carry the finance role; the evidence says otherwise.
        await h.Agent.ApproveAsync(run, id, Mallory with { Roles = ["finance"], Evidence = supportToken });

        await AssertStillWaitingAsync(h, run);
    }

    [Fact]
    public async Task Self_approval_through_a_forged_subject_is_refused()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync(onBehalfOf: "user:alice");

        // Alice cannot approve her own request, and posing as Bob without Bob's evidence does not get her past that.
        await h.Agent.ApproveAsync(run, id, Alice);
        await h.Agent.ApproveAsync(run, id, Bob with { Evidence = Token(h, run, id, true, Alice) });
        await AssertStillWaitingAsync(h, run);

        await h.Agent.ApproveAsync(run, id, Bob); // the genuine Bob
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
    }

    [Fact]
    public async Task A_correctly_signed_decision_is_accepted_and_the_verified_identity_is_what_is_recorded()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();

        await h.Agent.ApproveAsync(run, id, Alice);

        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.True((await LastVerificationAsync(h, run)).Ok);
    }

    // ---- no verifier configured ---------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_verifier_a_request_that_names_roles_refuses_every_decision()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync(verify: false);

        await h.Client.DecideApprovalAsync(run, id, Encoding.UTF8.GetBytes(RawDecision(id, "user:alice", "\"finance\"")), "claim");

        await AssertStillWaitingAsync(h, run);
        Assert.Contains("no approver verifier", (await LastVerificationAsync(h, run)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_verifier_a_request_that_names_nobody_accepts_any_identity()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync(verify: false, requirement: new ApprovalRequirement());

        await h.Client.DecideApprovalAsync(run, id, Encoding.UTF8.GetBytes(RawDecision(id, "user:anyone", "")), "claim");

        Assert.Equal(RunState.Completed, await h.RunAsync(run));
    }

    [Fact]
    public async Task Without_a_verifier_and_without_saying_so_every_decision_is_refused_even_when_the_request_names_nobody()
    {
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()), verifyApprovers: false, allowUnverifiedApprovers: false);
        h.Workflow("ask", async (ctx, _) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "x", Requirement = new ApprovalRequirement() })).ThrowIfNotApproved();
            return [];
        });
        RunKey run = await h.StartAsync("ask");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Client.DecideApprovalAsync(run, id, Encoding.UTF8.GetBytes(RawDecision(id, "user:anyone", "")), "claim");

        await AssertStillWaitingAsync(h, run);
        Assert.Contains("AllowUnverifiedApprovers", Assert.Single(h.Runtime.StartupWarnings), StringComparison.Ordinal);
        Assert.False(h.Runtime.ApproversUnverified);
    }

    [Fact]
    public async Task Saying_so_in_code_accepts_an_unrestricted_decision_warns_and_still_refuses_a_restricted_one()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync(verify: false, requirement: new ApprovalRequirement(Roles: ["finance"]));

        await h.Client.DecideApprovalAsync(run, id, Encoding.UTF8.GetBytes(RawDecision(id, "user:alice", "\"finance\"")), "claim");

        await AssertStillWaitingAsync(h, run); // a role was required and nothing can prove it
        Assert.True(h.Runtime.ApproversUnverified);
        Assert.Contains("OFF", Assert.Single(h.Runtime.StartupWarnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_verifier_means_no_warning_and_a_verified_posture()
    {
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()));

        Assert.Empty(h.Runtime.StartupWarnings);
        Assert.False(h.Runtime.ApproversUnverified);
    }

    [Fact]
    public async Task A_verifier_that_throws_leaves_the_approval_open()
    {
        var h = new AgentHarness(
            b => b.AddModel("m", AgentHarness.Echo()).UseApproverVerifier(new BrokenVerifier()),
            verifyApprovers: false);
        h.Workflow("ask", async (ctx, _) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "x" })).ThrowIfNotApproved();
            return [];
        });
        RunKey run = await h.StartAsync("ask");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.ApproveAsync(run, id, Alice);

        await AssertStillWaitingAsync(h, run);
    }

    private sealed class BrokenVerifier : IApproverVerifier
    {
        public ValueTask<VerifiedApprover?> VerifyAsync(ApproverEvidenceContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("identity provider unreachable");
    }

    [Fact]
    public async Task Replaying_the_run_never_asks_the_verifier_again()
    {
        var verifier = new CountingVerifier(AgentHarness.NewVerifier());
        var h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()).UseApproverVerifier(verifier), verifyApprovers: false);
        h.Workflow("ask", async (ctx, _) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "x", Requirement = new ApprovalRequirement(Roles: ["finance"]) })).ThrowIfNotApproved();
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5));
            await ctx.Workflow.DelayAsync(TimeSpan.FromMinutes(5));
            return [];
        });
        RunKey run = await h.StartAsync("ask");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.ApproveAsync(run, id, Alice);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal(1, verifier.Calls); // three workflow tasks replayed the decision; the verifier ran once
    }

    private sealed class CountingVerifier(IApproverVerifier inner) : IApproverVerifier
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<VerifiedApprover?> VerifyAsync(ApproverEvidenceContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return inner.VerifyAsync(context, cancellationToken);
        }
    }

    // ---- the client and the processor keep the namespace closed ----------------------------------------------

    [Fact]
    public async Task A_bus_message_cannot_decide_an_approval()
    {
        (AgentHarness h, RunKey run, string id) = await WaitingAsync();
        var source = new OneMessageSource(new SignalRunEvent(
            "evt-1", new TenantScope(run.TenantId, run.NamespaceId), run.RunId, AgentClient.ApprovalSignalName(id),
            Encoding.UTF8.GetBytes(RawDecision(id, "user:alice", "\"finance\""))));
        var processor = new InboundEventProcessor(source, h.Client);

        Assert.Equal(1, await processor.ProcessAsync(10, CancellationToken.None));

        Assert.True(source.Acknowledged); // dropped, not redelivered forever
        await AssertStillWaitingAsync(h, run);
    }

    private sealed class OneMessageSource(InboundEvent inbound) : IEventSource
    {
        private bool _taken;

        public bool Acknowledged { get; private set; }

        public ValueTask<IInboundMessage?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_taken)
            {
                return ValueTask.FromResult<IInboundMessage?>(null);
            }

            _taken = true;
            return ValueTask.FromResult<IInboundMessage?>(new Message(this, inbound));
        }

        private sealed class Message(OneMessageSource owner, InboundEvent inbound) : IInboundMessage
        {
            public InboundEvent Payload => inbound;

            public ValueTask AcknowledgeAsync(CancellationToken cancellationToken)
            {
                owner.Acknowledged = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask AbandonAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
    }
}
