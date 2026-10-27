using System.Text.Json;
using Kairos.Agents;
using Kairos.Agents.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Samples.AgentFramework;

/// <summary>The account service the credit tool calls.</summary>
public interface ICredits
{
    Task<string> IssueAsync(string customerId, decimal amount, CancellationToken cancellationToken);
}

/// <summary>
/// An agent whose owner marks a tool <c>AllowWithoutApproval</c> while the operators keep a rule of their own about it. The mark never outranks the rule.
/// </summary>
public static class CreditAgentSample
{
    #region credit-rule
    /// <summary>
    /// A rule held by the operators' policy engine: a credit of more than 100 needs a person from finance. What matters here is that a decision names the rule that made it; a decision with a name in parentheses is the
    /// policy's default for a tool nobody has a rule about.
    /// </summary>
    public sealed class CreditLimitPolicy : IRecordedToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken)
        {
            if (context.Approval is { IsApproved: true })
            {
                return ValueTask.FromResult(new ToolDecision(ToolVerdict.Auto, Record: Record("credit-limit", "allow")));
            }

            using JsonDocument arguments = JsonDocument.Parse(context.ArgumentsJson);
            bool large = arguments.RootElement.TryGetProperty("amount", out JsonElement amount) && amount.GetDecimal() > 100m;
            return ValueTask.FromResult(large
                ? new ToolDecision(ToolVerdict.RequireApproval, "credits over 100 need finance", new ApprovalRequirement(Roles: ["finance"]), Record("credit-limit", "require_approval"))
                : new ToolDecision(ToolVerdict.RequireApproval, "a tool that changes things waits for approval unless allowed", new ApprovalRequirement(), Record("(default)", "require_approval")));
        }

        private static PolicyDecisionRecord Record(string rule, string outcome) => new(rule, 1, new string('0', 64), outcome, []);
    }
    #endregion

    /// <summary>The agent: one tool that credits a customer's account.</summary>
    public static ChatClientAgent Create(IChatClient model, ICredits credits)
    {
        AIFunction issue = AIFunctionFactory.Create(
            async (string customerId, decimal amount, CancellationToken cancellationToken) => await credits.IssueAsync(customerId, amount, cancellationToken),
            "issue_credit", "Credits a customer's account.");
        return new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "credits",
            Description = "Credits customers' accounts.",
            ChatOptions = new ChatOptions { Instructions = "Credit the customer what they ask for.", Tools = [issue] },
        });
    }

    #region credit-register
    /// <summary>
    /// The owner marks <c>issue_credit</c> as allowed without approval; the operators' rule is configured beside it. A small credit runs without asking. A credit over 100 still waits for finance: the mark lifts the
    /// default hold on a tool, it does not take the decision away from a rule that matched the call.
    /// </summary>
    public static IServiceCollection AddCreditAgent(this IServiceCollection services, Action<KairosOptions> kairos, IChatClient model, ICredits credits) =>
        services
            .AddKairos(kairos)
            .AddAgentFramework(af => af
                .ConfigureAgents(agents => agents.UsePolicy(new CreditLimitPolicy()))
                .AddAgent(Create(model, credits), agent => agent.AllowWithoutApproval("issue_credit")));
    #endregion
}
