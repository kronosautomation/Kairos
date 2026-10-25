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

public readonly record struct ToolDecision(ToolVerdict Verdict, string? Reason = null, ApprovalRequirement? Approval = null)
{
    public static ToolDecision Allow() => new(ToolVerdict.Auto);

    public static ToolDecision Refuse(string reason) => new(ToolVerdict.Deny, reason);

    public static ToolDecision NeedsApproval(string reason, ApprovalRequirement? approval = null) => new(ToolVerdict.RequireApproval, reason, approval);
}

/// <param name="Run">The run making the call.</param>
/// <param name="Tool">The tool being called.</param>
/// <param name="ArgumentsJson">The model's arguments.</param>
/// <param name="Approval">The approval that already covers this call, if it was approved; null on the first look.</param>
public sealed record ToolPolicyContext(RunKey Run, AgentTool Tool, string ArgumentsJson, ApprovalDecision? Approval);

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
