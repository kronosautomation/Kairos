using Kairos.Agents;
using Kairos.Agents.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Samples.AgentFramework;

/// <summary>The order service the agent's tools call. It honours an idempotency key.</summary>
public interface IOrders
{
    Task<string> FindAsync(string orderId, CancellationToken cancellationToken);

    Task<string> RefundAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>A customer-support agent written the usual Agent Framework way, made durable by one line.</summary>
public static class SupportAgentSample
{
    #region agent
    /// <summary>The agent is a plain <c>ChatClientAgent</c>: instructions, a chat client and tools. Nothing in it knows about Kairos.</summary>
    public static ChatClientAgent Create(IChatClient model, IOrders orders)
    {
        AIFunction lookup = AIFunctionFactory.Create(
            async (string orderId, CancellationToken cancellationToken) => await orders.FindAsync(orderId, cancellationToken),
            "lookup_order", "Finds an order and says where it is.");

        AIFunction refund = AIFunctionFactory.Create(
            // Kairos tells the tool which step it is serving. Pass the key on, and a retry after a lost answer refunds once.
            async (string orderId, CancellationToken cancellationToken) =>
                await orders.RefundAsync(orderId, AgentFrameworkToolContext.Current!.IdempotencyKey, cancellationToken),
            "issue_refund", "Refunds an order.");

        return new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "support",
            Description = "Answers questions about orders and refunds them.",
            ChatOptions = new ChatOptions
            {
                Instructions = "You help customers with their orders. Look the order up before you say anything about it.",
                Tools = [lookup, new ApprovalRequiredAIFunction(refund)], // the refund waits for a person: Agent Framework's own way to say so
            },
        });
    }
    #endregion

    #region register
    /// <summary>
    /// The one line that makes the agent durable. <c>lookup_order</c> only reads, so it is marked: a tool that is not marked is treated as one that changes the outside
    /// world and waits for approval unless a policy allows it. The model name and reply size are what the budget reserves against.
    /// </summary>
    public static IServiceCollection AddSupportAgent(this IServiceCollection services, Action<KairosOptions> kairos, IChatClient model, IOrders orders, TokenBudget budget) =>
        services
            .AddKairos(kairos)
            .AddAgentFramework(af => af
                .ConfigureAgents(agents => agents.UseModelCallGuard(budget).AllowUnverifiedApprovers()) // sample only: a real host proves who decides with UseApproverVerifier
                .AddAgent(Create(model, orders), agent => agent.ReadOnly("lookup_order").Model("support-model", maxOutputTokens: 400)));
    #endregion

    #region use
    /// <summary>Talks to the agent. <c>support</c> is an ordinary <c>AIAgent</c>; what it does is recorded step by step, and it can wait for days for a person.</summary>
    public static async Task<string> AskAsync(IServiceProvider services, string question, CancellationToken cancellationToken)
    {
        AIAgent support = services.GetRequiredKeyedService<AIAgent>("support");
        AgentSession conversation = await support.CreateSessionAsync(cancellationToken);
        AgentResponse answer = await support.RunAsync(question, conversation, cancellationToken: cancellationToken);
        return answer.Text;
    }
    #endregion

    #region approve
    /// <summary>The approvals inbox: the refund the agent wants to make, and the person who allows it. The person is whoever your application authenticated.</summary>
    public static async Task<bool> ApproveFirstAsync(IServiceProvider services, RunKey run, ApproverIdentity approver, CancellationToken cancellationToken)
    {
        KairosClient client = services.GetRequiredService<KairosClient>();
        IReadOnlyList<PendingApproval> waiting = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(run, cancellationToken));
        if (waiting.Count == 0)
        {
            return false;
        }

        // "Refund an order": which tool, with what arguments, is in waiting[0]. Approving decides this exact call.
        return await services.GetRequiredService<AgentClient>().ApproveAsync(run, waiting[0].ApprovalId, approver, cancellationToken: cancellationToken);
    }
    #endregion
}

#region budget
/// <summary>
/// A token budget for the day, as an <see cref="IModelCallGuard"/>: it reserves what a call may cost <b>before</b> the call, so calls that run at the same time cannot overspend,
/// and a call it refuses pauses the run for a person to raise it. This one keeps its count in memory to show the shape; the policy engine keeps it in PostgreSQL, atomically, per
/// tenant and per agent.
/// </summary>
public sealed class TokenBudget(long tokensPerDay) : IModelCallGuard
{
    private readonly object _gate = new();
    private long _used;

    /// <summary>Tokens spent or held so far.</summary>
    public long Used
    {
        get
        {
            lock (_gate)
            {
                return _used;
            }
        }
    }

    public ValueTask<ModelCallPermit> ReserveAsync(ModelCallInfo request, CancellationToken cancellationToken)
    {
        long worstCase = request.MaxOutputTokens ?? 1000;
        lock (_gate)
        {
            // A call that a person let through is allowed once, whatever is left.
            if (request.AllowanceApprovalId is null && _used + worstCase > tokensPerDay)
            {
                return ValueTask.FromResult(ModelCallPermit.Refuse("daily-tokens", onExceed: "pause", approval: new ApprovalRequirement(Roles: ["finance"])));
            }

            _used += worstCase;
            return ValueTask.FromResult(ModelCallPermit.Allow(reservation: worstCase.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    public ValueTask SettleAsync(ModelCallInfo request, ModelCallPermit permit, UsageRecord? usage, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _used += (usage?.TotalTokens ?? 0) - long.Parse(permit.Reservation!, System.Globalization.CultureInfo.InvariantCulture); // the call cost what it cost, not the worst case
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(ModelCallInfo request, ModelCallPermit permit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _used -= long.Parse(permit.Reservation!, System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    }
}
#endregion

/// <summary>The controls native Kairos agents have, set on the Agent Framework builder in the same place.</summary>
public static class AgentControlsSample
{
    #region configure-agents
    public static AgentFrameworkBuilder ConfigureControls(
        AgentFrameworkBuilder af, IModelCallGuard budget, Func<string, string> redact, IToolCredentialIssuer issuer, IApproverVerifier verifier) =>
        af.ConfigureAgents(agents => agents.UseModelCallGuard(budget).UseRedactor(redact).UseCredentials(issuer).UseApproverVerifier(verifier));
    #endregion
}
