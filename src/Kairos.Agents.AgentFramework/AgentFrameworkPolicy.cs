namespace Kairos.Agents.AgentFramework;

/// <summary>
/// Adds what Agent Framework agents say about their own tools to the configured tool policy, without replacing it: a tool wrapped in an approval requirement keeps needing approval (no policy may waive what the author
/// asked for), <c>RequireApproval</c> at registration does the same, and <c>AllowWithoutApproval</c> lifts the free policy's default hold on a side-effecting tool, with the decision <b>recorded</b> so that an audit
/// shows the call was let through by the application's owner. A denial always stands, and so does a rule of a policy engine that matched the call.
/// </summary>
internal class AgentFrameworkPolicy(IToolPolicy inner, IReadOnlyDictionary<string, ToolBinding> tools) : IToolPolicy
{
    public const string AllowRule = "af.allow-without-approval";
    public const string AuthorRule = "af.author-requires-approval";
    public const string RegistrationRule = "af.registration-requires-approval";

    public async ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ToolDecision decision = await inner.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        if (!tools.TryGetValue(context.Tool.Name, out ToolBinding? tool) || decision.Verdict == ToolVerdict.Deny)
        {
            return decision;
        }

        bool approved = context.Approval is { IsApproved: true };
        if (!approved && tool.RequiredApproval is { } required && (decision.Verdict == ToolVerdict.Auto || !MatchedARule(decision)))
        {
            // Who may approve is the application's to say: it replaces the free policy's default requirement, but not a rule of a policy engine that matched the call.
            return Hold(decision, "the application requires approval for this tool", required, RegistrationRule);
        }

        if (!approved && decision.Verdict == ToolVerdict.Auto && tool.AuthorRequiresApproval)
        {
            return Hold(decision, "the agent's author requires approval for this tool", new ApprovalRequirement(), AuthorRule);
        }

        if (decision.Verdict == ToolVerdict.RequireApproval && tool.AllowWithoutApproval && !MatchedARule(decision))
        {
            PolicyDecisionRecord? was = decision.Record;
            return new ToolDecision(ToolVerdict.Auto, "the application's owner allows this tool without approval", null,
                new PolicyDecisionRecord(AllowRule, was?.PolicyVersion ?? 0, was?.PolicyHash ?? "", "allow", []));
        }

        return decision;
    }

    private static ToolDecision Hold(ToolDecision decision, string reason, ApprovalRequirement requirement, string rule) =>
        new(ToolVerdict.RequireApproval, reason, requirement,
            new PolicyDecisionRecord(rule, decision.Record?.PolicyVersion ?? 0, decision.Record?.PolicyHash ?? "", "require_approval", []));

    /// <summary>True when a rule of a policy engine decided (the fixed names that start with a parenthesis are its defaults).</summary>
    private static bool MatchedARule(ToolDecision decision) =>
        decision.Record is { RuleId: { Length: > 0 } rule } && !rule.StartsWith('(');
}

/// <summary>The same, for a policy that records its decisions: the adapter's own decisions are then recorded in the policy step too.</summary>
internal sealed class RecordedAgentFrameworkPolicy(IToolPolicy inner, IReadOnlyDictionary<string, ToolBinding> tools)
    : AgentFrameworkPolicy(inner, tools), IRecordedToolPolicy;
