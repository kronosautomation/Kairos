namespace Kairos.Agents;

public enum ToolVerdict
{
    /// <summary>Run the tool now.</summary>
    Auto = 0,

    /// <summary>Run the tool only after a person approves this call.</summary>
    RequireApproval = 1,

    /// <summary>Never run the tool for this call, whoever asks.</summary>
    Deny = 2,
}

/// <summary>
/// Why a policy decided as it did, in terms that carry no run data: which rule, which version of the policy (and its hash), the effect, and the <b>paths</b> of the argument conditions that matched
/// (never what was at them). A policy that implements <see cref="IRecordedToolPolicy"/> returns one with every decision; the agent runtime records it before the tool can run.
/// </summary>
/// <param name="RuleId">The rule that decided, or one of the fixed names <c>(default)</c>, <c>(no-rule)</c> and <c>(policy-unavailable)</c>.</param>
/// <param name="PolicyVersion">The version of the policy that was evaluated (0 when there was none).</param>
/// <param name="PolicyHash">The hash of that version, 64 lower-case hexadecimal digits, or empty when there was none.</param>
/// <param name="Effect">The effect: <c>allow</c>, <c>deny</c> or <c>require_approval</c>.</param>
/// <param name="MatchedFields">Paths of the argument conditions that held. Names only.</param>
public sealed record PolicyDecisionRecord(string RuleId, long PolicyVersion, string PolicyHash, string Effect, IReadOnlyList<string> MatchedFields)
{
    /// <summary>The longest rule id, path or hash that is recorded; longer ones are cut so a record stays small.</summary>
    public const int MaxLength = 128;
}

public readonly record struct ToolDecision(ToolVerdict Verdict, string? Reason = null, ApprovalRequirement? Approval = null, PolicyDecisionRecord? Record = null)
{
    public static ToolDecision Allow() => new(ToolVerdict.Auto);

    public static ToolDecision Refuse(string reason) => new(ToolVerdict.Deny, reason);

    public static ToolDecision NeedsApproval(string reason, ApprovalRequirement? approval = null) => new(ToolVerdict.RequireApproval, reason, approval);
}

/// <param name="Run">The run making the call.</param>
/// <param name="Tool">The tool being called.</param>
/// <param name="ArgumentsJson">The model's arguments.</param>
/// <param name="Approval">The approval that already covers this call, if it was approved; null on the first look.</param>
public sealed record ToolPolicyContext(RunKey Run, AgentTool Tool, string ArgumentsJson, ApprovalDecision? Approval)
{
    /// <summary>Who started the run, as recorded in its history (<c>role:subject</c>), or empty.</summary>
    public string StartedBy { get; init; } = "";

    /// <summary>The time of the evaluation, by the clock the agent runtime was given.</summary>
    public DateTimeOffset Now { get; init; }

    /// <summary>
    /// Names this evaluation across retries (<c>run:activity</c> of the step that makes it), so a policy that counts what it allows counts a retried step once. Empty when the policy is asked outside a step.
    /// </summary>
    public string StepKey { get; init; } = "";
}

/// <summary>
/// A policy whose every decision is recorded <b>before the tool can run</b>. The agent runtime evaluates it in a step of its own (<c>kairos.agent.policy</c>), so the decision and its
/// <see cref="PolicyDecisionRecord"/> are in history first; the tool step then honours the recorded decision and does not evaluate again. Replays read the record; a policy edited later cannot
/// change what was decided. The free baseline does not implement this, and keeps one step per tool call.
/// </summary>
public interface IRecordedToolPolicy : IToolPolicy;

/// <summary>
/// Decides, before every tool call and on the worker, whether the call may run. The decision is recorded with the step, so changing
/// a policy never disturbs a run that is already in flight. Commercial packages plug richer policies in here.
/// </summary>
public interface IToolPolicy
{
    ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The free baseline: read-only tools run, anything that changes the outside world is held for a person to approve unless the
/// application's owner explicitly allows it. A denial can never be overridden by an approval.
/// </summary>
public sealed class BaselineToolPolicy : IToolPolicy
{
    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApprovalRequirement> _approval = new(StringComparer.Ordinal);

    /// <summary>The requirement applied to side-effecting tools that need approval and have no entry of their own.</summary>
    public ApprovalRequirement DefaultApproval { get; set; } = new();

    /// <summary>Lets the named side-effecting tools run without asking. This is a deliberate decision by the application's owner.</summary>
    public BaselineToolPolicy Allow(params string[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (string t in tools)
        {
            _allowed.Add(t);
        }

        return this;
    }

    public BaselineToolPolicy Deny(params string[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (string t in tools)
        {
            _denied.Add(t);
        }

        return this;
    }

    /// <summary>Requires approval for the tool, by the given people.</summary>
    public BaselineToolPolicy RequireApproval(string tool, ApprovalRequirement requirement)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        ArgumentNullException.ThrowIfNull(requirement);
        _approval[tool] = requirement;
        return this;
    }

    public ValueTask<ToolDecision> EvaluateAsync(ToolPolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AgentTool tool = context.Tool;
        if (_denied.Contains(tool.Name))
        {
            return ValueTask.FromResult(ToolDecision.Refuse($"tool '{tool.Name}' is denied by policy"));
        }

        if (tool.Access == ToolAccess.ReadOnly || _allowed.Contains(tool.Name) || context.Approval is { IsApproved: true })
        {
            return ValueTask.FromResult(ToolDecision.Allow());
        }

        ApprovalRequirement requirement = _approval.GetValueOrDefault(tool.Name) ?? DefaultApproval;
        return ValueTask.FromResult(ToolDecision.NeedsApproval($"tool '{tool.Name}' changes something outside Kairos", requirement));
    }
}
