using System.Text;
using Kairos.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Samples.Agents;

/// <summary>The refund service the agent's tools call. It honours an idempotency key and checks the bearer token's scope.</summary>
public interface IRefundApi
{
    Task<string> FindOrderAsync(string orderId, string bearerToken, CancellationToken cancellationToken);

    Task<string> RefundAsync(string orderId, decimal amount, string idempotencyKey, string bearerToken, CancellationToken cancellationToken);
}

/// <summary>A customer-support agent: it looks orders up on its own and issues refunds only when a finance approver agrees.</summary>
public static class SupportAgent
{
    private const string OrderSchema = """{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""";
    private const string RefundSchema = """{"type":"object","properties":{"orderId":{"type":"string"},"amount":{"type":"number"}},"required":["orderId","amount"]}""";

    #region tools
    public static AgentTool LookupOrder(IRefundApi api) => new(
        "lookup_order", "Finds an order and says where it is.", OrderSchema, ToolAccess.ReadOnly,
        async (call, ct) =>
        {
            // The credential is limited to this run and this tool, can only read, and expires in minutes.
            string orderId = call.Arguments.GetProperty("orderId").GetString()!;
            return await api.FindOrderAsync(orderId, call.Credential!.Token, ct);
        });

    public static AgentTool IssueRefund(IRefundApi api) => new(
        "issue_refund", "Refunds part or all of an order.", RefundSchema, ToolAccess.SideEffecting,
        async (call, ct) =>
        {
            string orderId = call.Arguments.GetProperty("orderId").GetString()!;
            decimal amount = call.Arguments.GetProperty("amount").GetDecimal();

            // The key is the same on every attempt of this step, so a retry after a lost answer refunds once.
            return await api.RefundAsync(orderId, amount, call.IdempotencyKey, call.Credential!.Token, ct);
        });
    #endregion

    #region configure
    public static AgentsRuntime Configure(IChatClient model, IRefundApi api, byte[] credentialKey) => new AgentsBuilder()
        .AddModel("support-model", model)
        .AddTool(LookupOrder(api))
        .AddTool(IssueRefund(api))
        .ConfigureBaselinePolicy(policy => policy.RequireApproval(
            "issue_refund", new ApprovalRequirement(Roles: ["finance"], Timeout: TimeSpan.FromDays(2))))
        .UseCredentials(new HmacToolCredentialIssuer(credentialKey))
        .Build();
    #endregion

    #region policy
    public static AgentsRuntime ConfigureWithOwnPolicy(IChatClient model, IRefundApi api) => new AgentsBuilder()
        .AddModel("support-model", model)
        .AddTool(LookupOrder(api))
        .AddTool(IssueRefund(api))
        .ConfigureBaselinePolicy(policy => policy
            .Allow("send_receipt") // a side-effecting tool that runs without asking
            .Deny("delete_customer") // never runs, and an approval cannot change that
            .RequireApproval("issue_refund", new ApprovalRequirement(Roles: ["finance"])))
        .Build();
    #endregion

    #region host
    public static IServiceCollection AddSupportAgent(this IServiceCollection services, string connectionString, AgentsRuntime agents) =>
        services.AddKairos(o => o
            .UsePostgres(connectionString)
            .AddActivities(agents.Activities)
            .AddWorkflow("support", agents.Workflow(RunAsync)));
    #endregion

    #region agent-loop
    public static async Task<byte[]> RunAsync(IAgentContext ctx, byte[] input)
    {
        ctx.OnBehalfOf = "customer:42"; // who the agent acts for; the approver may not be this person
        var conversation = new List<ChatMessage> { new(ChatRole.User, Encoding.UTF8.GetString(input)) };
        AgentToolSet tools = ctx.Tools("lookup_order", "issue_refund");

        for (int turn = 0; turn < 10; turn++)
        {
            AgentReply reply = await ctx.ChatAsync("support-model", conversation, tools);
            conversation.AddRange(reply.Messages);
            if (reply.ToolCalls.Count == 0)
            {
                return Encoding.UTF8.GetBytes(reply.Text);
            }

            foreach (FunctionCallContent call in reply.ToolCalls)
            {
                conversation.Add(await ctx.InvokeToolAsync(call)); // may wait days for an approval
            }
        }

        return Encoding.UTF8.GetBytes("I could not finish this; a person will follow up.");
    }
    #endregion
}

public static class ApprovalExamples
{
    #region approval-request
    public static async Task<byte[]> PublishWithSignOffAsync(IAgentContext ctx, byte[] report)
    {
        ApprovalDecision decision = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action = "Publish the quarterly report",
            Details = $"{report.Length} bytes",
            Requirement = new ApprovalRequirement(Roles: ["editor"], Timeout: TimeSpan.FromDays(1)),
        });

        decision.ThrowIfNotApproved(); // a refusal or a timeout ends the run here
        return await ctx.Workflow.ExecuteActivityAsync("publish", report);
    }
    #endregion

    #region approval-inbox
    public static async Task<IReadOnlyList<PendingApproval>> WaitingOnPeopleAsync(KairosClient client, RunKey run)
    {
        IReadOnlyList<HistoryEvent> history = await client.GetOpenedHistoryAsync(run);
        return AgentHistory.PendingApprovals(history);
    }
    #endregion

    #region approval-decide
    public static async Task ApproveAsync(KairosClient client, RunKey run, string approvalId, ClaimsSummary signedInUser)
    {
        // Authenticate the person in your own application first; Kairos records the identity you pass and checks it against the request.
        var approver = new ApproverIdentity(signedInUser.Subject, signedInUser.DisplayName, signedInUser.Roles);
        await new AgentClient(client, TimeProvider.System).ApproveAsync(run, approvalId, approver, comment: "Checked against the invoice");
    }
    #endregion
}

/// <summary>What the application knows about the signed-in person.</summary>
public sealed record ClaimsSummary(string Subject, string DisplayName, IReadOnlyList<string> Roles);

public static class SagaExamples
{
    #region saga
    public static async Task<byte[]> BookTripAsync(IAgentContext ctx, byte[] trip)
    {
        await ctx.RunSagaAsync(async saga =>
        {
            await ctx.Workflow.ExecuteActivityAsync("reserve-flight", trip);
            saga.Compensate("release-flight", "release-flight", trip); // registered after the step it undoes

            await ctx.Workflow.ExecuteActivityAsync("reserve-hotel", trip);
            saga.Compensate("release-hotel", "release-hotel", trip);

            // If the card is declined, or a person refuses the sign-off, or the run is cancelled, the hotel and then the flight are released.
            await ctx.Workflow.ExecuteActivityAsync("charge-card", trip);
        });

        return Encoding.UTF8.GetBytes("booked");
    }
    #endregion

    #region saga-cancel
    public static ValueTask<bool> CancelAsync(KairosClient client, RunKey run) =>
        new AgentClient(client, TimeProvider.System).RequestCancelAsync(run, "the customer withdrew the order");
    #endregion

    #region saga-custom-undo
    public static async Task<byte[]> ReserveAndConfirmAsync(IAgentContext ctx, byte[] order)
    {
        await ctx.RunSagaAsync(async saga =>
        {
            await ctx.Workflow.ExecuteActivityAsync("reserve-stock", order);

            // An undo can be several steps. Pass the saga's retry policy to each, so they are retried until they work.
            saga.Compensate("release-stock", async retry =>
            {
                await ctx.Workflow.ExecuteActivityAsync("release-stock", order, retry);
                await ctx.Workflow.ExecuteActivityAsync("notify-warehouse", order, retry);
            });

            await ctx.Workflow.ExecuteActivityAsync("confirm-order", order);
        });

        return Encoding.UTF8.GetBytes("confirmed");
    }
    #endregion
}

public static class CredentialExamples
{
    #region verify-credential
    // In the service the tool calls: accept the request only if the token is genuine, unexpired, and carries the scope this call needs.
    public static bool MayRefund(HmacToolCredentialIssuer verifier, string? bearerToken, DateTimeOffset now) =>
        verifier.Verify(bearerToken, "issue_refund:write", now) is not null;
    #endregion
}
