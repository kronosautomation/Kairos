using System.Security.Cryptography;
using System.Text;
using Kairos.Agents;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Samples.Agents.Tests;

/// <summary>Every region the agent pages quote is run here against the in-memory store with a virtual clock and a scripted model.</summary>
public sealed class AgentSamplesTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly HmacApprovalTokens Approvals = new(RandomNumberGenerator.GetBytes(32));

    private static AgentsRuntime Support(IChatClient model, IRefundApi api) => SupportAgent.Configure(model, api, new HmacToolCredentialIssuer(Key), Approvals);

    private sealed class FakeRefundApi : IRefundApi
    {
        public List<string> Refunds { get; } = [];

        public List<string> Tokens { get; } = [];

        public Task<string> FindOrderAsync(string orderId, string bearerToken, CancellationToken cancellationToken)
        {
            Tokens.Add(bearerToken);
            return Task.FromResult($"order {orderId}: shipped");
        }

        public Task<string> RefundAsync(string orderId, decimal amount, string idempotencyKey, string bearerToken, CancellationToken cancellationToken)
        {
            Tokens.Add(bearerToken);
            if (!Refunds.Contains(idempotencyKey))
            {
                Refunds.Add(idempotencyKey);
            }

            return Task.FromResult($"refunded {amount} on {orderId}");
        }
    }

    private sealed class Rig
    {
        private readonly WorkflowRegistry _workflows = new();
        private readonly ActivityRegistry _activities = new();

        public Rig(AgentsRuntime? agents = null)
        {
            Agents = agents;
            agents?.RegisterInto(_activities);
            Client = new KairosClient(Store, Time);
            Worker = new WorkerRuntime(Store, Scope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false });
        }

        public AgentsRuntime? Agents { get; }

        public InMemoryKairosStore Store { get; } = new();

        public VirtualTimeProvider Time { get; } = new();

        public KairosClient Client { get; }

        public WorkerRuntime Worker { get; }

        public List<string> Ran { get; } = [];

        public void Workflow(string type, Func<IAgentContext, byte[], Task<byte[]>> body) =>
            _workflows.Register(type, KairosClient.DefaultBuildId, Agents!.Workflow(body));

        public void Activity(string type) => _activities.Register(type, (_, input, _) =>
        {
            lock (Ran)
            {
                Ran.Add(type);
            }

            return Task.FromResult(input);
        });

        public void FailingActivity(string type) => _activities.Register(type, (_, _, _) => throw new InvalidOperationException("declined"));

        public async Task<RunKey> StartAsync(string type, string input = "")
        {
            StartRunResult s = await Client.StartAsync(Scope, $"wf-{type}", type, Encoding.UTF8.GetBytes(input));
            return new RunKey(Scope.TenantId, Scope.NamespaceId, s.RunId);
        }

        public async Task<RunState> RunAsync(RunKey run, bool jumpClock = true)
        {
            while (true)
            {
                await Driver.DriveAsync(Worker);
                RunState state = (await Client.GetStateAsync(run))!.Value;
                if (state != RunState.Running || !jumpClock || Store.NextWakeup(Scope, Time.GetUtcNow()) is not { } wake)
                {
                    return state;
                }

                Time.AdvanceTo(wake);
            }
        }
    }

    private static ScriptedChatClient SupportModel() => new(messages =>
    {
        int results = messages.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
        return results switch
        {
            0 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup_order", new Dictionary<string, object?> { ["orderId"] = "8841" })]),
            1 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "issue_refund", new Dictionary<string, object?> { ["orderId"] = "8841", ["amount"] = 250 })]),
            _ => new ChatMessage(ChatRole.Assistant, "Your refund of 250 is on its way."),
        };
    });

    [Fact]
    public async Task The_support_agent_looks_up_then_waits_for_finance_then_refunds_once()
    {
        var api = new FakeRefundApi();
        ScriptedChatClient model = SupportModel();
        var rig = new Rig(Support(model, api));
        rig.Workflow("support", SupportAgent.RunAsync);

        RunKey run = await rig.StartAsync("support", "refund order 8841");
        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false)); // the refund is waiting for a person

        // The customer cannot approve their own refund; finance can.
        IReadOnlyList<PendingApproval> pending = await ApprovalExamples.WaitingOnPeopleAsync(rig.Client, run);
        Assert.Equal("issue_refund", Assert.Single(pending).Tool);
        await ApprovalExamples.ApproveAsync(rig.Client, Approvals, run, pending[0].ApprovalId, new ClaimsSummary("customer:42", "The customer", ["finance"]));
        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false));
        await ApprovalExamples.ApproveAsync(rig.Client, Approvals, run, pending[0].ApprovalId, new ClaimsSummary("user:dana", "Dana", ["finance"]));
        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Single(api.Refunds);
        Assert.Equal(3, model.CallCount);
        IReadOnlyList<HistoryEvent> history = await rig.Client.GetOpenedHistoryAsync(run);
        Assert.Equal("Your refund of 250 is on its way.", Encoding.UTF8.GetString(((WorkflowCompleted)history[^1]).Result));

        // The lookup used a token that can read but not refund, and the refund used one that can.
        var verifier = new HmacToolCredentialIssuer(Key);
        Assert.False(CredentialExamples.MayRefund(verifier, api.Tokens[0], rig.Time.GetUtcNow().AddYears(-1)));
        Assert.Equal(2, api.Tokens.Distinct().Count());
    }

    [Fact]
    public async Task A_rejected_refund_is_not_made_and_the_model_is_told()
    {
        var api = new FakeRefundApi();
        var rig = new Rig(Support(SupportModel(), api));
        rig.Workflow("support", SupportAgent.RunAsync);
        RunKey run = await rig.StartAsync("support", "refund order 8841");
        await rig.RunAsync(run, jumpClock: false);
        PendingApproval pending = (await ApprovalExamples.WaitingOnPeopleAsync(rig.Client, run))[0];

        await new AgentClient(rig.Client, rig.Time, Approvals).RejectAsync(run, pending.ApprovalId, new ApproverIdentity("user:dana", "Dana", ["finance"]), "outside the policy window");
        Assert.Equal(RunState.Completed, await rig.RunAsync(run));

        Assert.Empty(api.Refunds);
    }

    [Fact]
    public async Task Sign_off_publishes_after_an_editor_approves()
    {
        var rig = new Rig(new AgentsBuilder().UseApproverVerifier(Approvals).Build());
        rig.Activity("publish");
        rig.Workflow("publish", ApprovalExamples.PublishWithSignOffAsync);
        RunKey run = await rig.StartAsync("publish", "report");
        Assert.Equal(RunState.Running, await rig.RunAsync(run, jumpClock: false));
        PendingApproval pending = (await ApprovalExamples.WaitingOnPeopleAsync(rig.Client, run))[0];

        await ApprovalExamples.ApproveAsync(rig.Client, Approvals, run, pending.ApprovalId, new ClaimsSummary("user:eve", "Eve", ["editor"]));
        Assert.Equal(RunState.Completed, await rig.RunAsync(run, jumpClock: false));
        Assert.Equal(["publish"], rig.Ran);
    }

    [Fact]
    public async Task A_sign_off_nobody_gives_expires_and_the_run_fails()
    {
        var rig = new Rig(new AgentsBuilder().UseApproverVerifier(Approvals).Build());
        rig.Activity("publish");
        rig.Workflow("publish", ApprovalExamples.PublishWithSignOffAsync);
        RunKey run = await rig.StartAsync("publish", "report");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run)); // the clock jumps a day; the approval expires
        Assert.Empty(rig.Ran);
    }

    [Fact]
    public async Task A_declined_card_releases_the_hotel_then_the_flight()
    {
        var rig = new Rig(new AgentsBuilder().UseApproverVerifier(Approvals).Build());
        foreach (string a in new[] { "reserve-flight", "reserve-hotel", "release-flight", "release-hotel" })
        {
            rig.Activity(a);
        }

        rig.FailingActivity("charge-card");
        rig.Workflow("trip", SagaExamples.BookTripAsync);
        RunKey run = await rig.StartAsync("trip", "NYC");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        Assert.Equal(["reserve-flight", "reserve-hotel", "release-hotel", "release-flight"], rig.Ran);
        Assert.Null(SagaHistory.CheckCompensationsComplete(await rig.Client.GetOpenedHistoryAsync(run)));
    }

    [Fact]
    public async Task Cancelling_a_waiting_trip_undoes_what_was_booked()
    {
        var rig = new Rig(new AgentsBuilder().UseApproverVerifier(Approvals).Build());
        foreach (string a in new[] { "reserve-flight", "reserve-hotel", "release-flight", "release-hotel" })
        {
            rig.Activity(a);
        }

        rig.Workflow("trip", async (ctx, trip) =>
        {
            byte[]? result = null;
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("reserve-flight", trip);
                saga.Compensate("release-flight", "release-flight", trip);
                await ctx.Workflow.ExecuteActivityAsync("reserve-hotel", trip);
                saga.Compensate("release-hotel", "release-hotel", trip);
                (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Charge the card" })).ThrowIfNotApproved();
                result = trip;
            });
            return result!;
        });
        RunKey run = await rig.StartAsync("trip", "NYC");
        Assert.Equal(RunState.Running, await rig.RunAsync(run));

        Assert.True(await SagaExamples.CancelAsync(rig.Client, run));
        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        Assert.Equal(["reserve-flight", "reserve-hotel", "release-hotel", "release-flight"], rig.Ran);
    }

    [Fact]
    public async Task A_custom_undo_runs_all_of_its_steps()
    {
        var rig = new Rig(new AgentsBuilder().UseApproverVerifier(Approvals).Build());
        foreach (string a in new[] { "reserve-stock", "release-stock", "notify-warehouse" })
        {
            rig.Activity(a);
        }

        rig.FailingActivity("confirm-order");
        rig.Workflow("order", SagaExamples.ReserveAndConfirmAsync);
        RunKey run = await rig.StartAsync("order", "o-1");

        Assert.Equal(RunState.Failed, await rig.RunAsync(run));
        Assert.Equal(["reserve-stock", "release-stock", "notify-warehouse"], rig.Ran);
    }

    [Fact]
    public async Task The_service_side_check_accepts_a_matching_scope_only()
    {
        var issuer = new HmacToolCredentialIssuer(Key);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ToolCredential read = await issuer.IssueAsync(new ToolCredentialRequest(run, "issue_refund", ["issue_refund:read"], TimeSpan.FromMinutes(5), null), now, CancellationToken.None);
        ToolCredential write = await issuer.IssueAsync(new ToolCredentialRequest(run, "issue_refund", ["issue_refund:write"], TimeSpan.FromMinutes(5), null), now, CancellationToken.None);

        Assert.False(CredentialExamples.MayRefund(issuer, read.Token, now));
        Assert.True(CredentialExamples.MayRefund(issuer, write.Token, now));
        Assert.False(CredentialExamples.MayRefund(issuer, write.Token, now.AddMinutes(10)));
        Assert.False(CredentialExamples.MayRefund(issuer, null, now));
    }

    // ---- recorded model fixtures -----------------------------------------------------------------------------

    [Fact]
    public async Task A_recorded_conversation_drives_the_agent_without_a_model()
    {
        #region fixture
        // Each exchange is what a real model once said to exactly this conversation. Record them with RecordingChatClient, or write them by hand.
        var user = new List<ChatMessage> { new(ChatRole.User, "refund order 8841") };
        var afterLookup = new List<ChatMessage>(user)
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup_order", new Dictionary<string, object?> { ["orderId"] = "8841" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "order 8841: shipped")]),
        };

        ModelFixture fixture = new ModelFixture()
            .AddToolCall(user, "c1", "lookup_order", new Dictionary<string, object?> { ["orderId"] = "8841" })
            .AddError(afterLookup, FixtureErrorKind.RateLimited, "429 Too Many Requests") // the provider pushes back once...
            .AddReply(afterLookup, new ChatMessage(ChatRole.Assistant, "It shipped yesterday."), inputTokens: 120, outputTokens: 8); // ...then answers

        var model = new FixtureChatClient(fixture); // fails loudly on any conversation that was not recorded
        #endregion

        var rig = new Rig(Support(model, new FakeRefundApi()));
        rig.Workflow("support", SupportAgent.RunAsync);
        RunKey run = await rig.StartAsync("support", "refund order 8841");

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));
        Assert.Equal("It shipped yesterday.", Encoding.UTF8.GetString(((WorkflowCompleted)(await rig.Client.GetOpenedHistoryAsync(run))[^1]).Result));
        Assert.Equal(3, model.CallCount);
    }
}
