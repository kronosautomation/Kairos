namespace Kairos.Agents;

/// <summary>How a person's decision on an approval turned out.</summary>
public enum ApprovalOutcome
{
    Approved = 0,
    Rejected = 1,

    /// <summary>Nobody decided before the timeout. Treated as a refusal.</summary>
    Expired = 2,
}

/// <summary>Who may decide an approval, and for how long it stays open.</summary>
/// <param name="Approvers">Subjects allowed to decide. With no roles either, anyone the host lets through may decide.</param>
/// <param name="Roles">Roles allowed to decide. A person qualifies by subject or by role.</param>
/// <param name="Timeout">Workflow time after which the approval expires. Null waits indefinitely.</param>
/// <param name="ForbidSelfApproval">The person the agent acts for may not approve their own request.</param>
/// <param name="Count">How many different people must approve (1 or 2). One rejection by a verified person ends it. The same person's second decision does not count.</param>
public sealed record ApprovalRequirement(
    IReadOnlyList<string>? Approvers = null, IReadOnlyList<string>? Roles = null, TimeSpan? Timeout = null, bool ForbidSelfApproval = true, int Count = 1)
{
    /// <summary>The most people one approval may require.</summary>
    public const int MaxCount = 2;
}

/// <summary>A request for a person to decide. Recorded in history, so an approvals inbox can find it.</summary>
public sealed class ApprovalRequest
{
    /// <summary>One line a person can act on, for example "Refund 250 EUR to order 8841".</summary>
    public required string Action { get; init; }

    /// <summary>The detail behind it (the tool arguments, a draft message). Redacted like any other recorded text.</summary>
    public string? Details { get; init; }

    public string? Tool { get; init; }

    public ApprovalRequirement Requirement { get; init; } = new();
}

/// <summary>The recorded answer to an approval request, including who gave it.</summary>
public sealed record ApprovalDecision(
    string ApprovalId, ApprovalOutcome Outcome, ApproverIdentity? Approver, string? Comment, DateTimeOffset? DecidedAt)
{
    /// <summary>Everyone whose approval counted, in the order they approved (more than one when <see cref="ApprovalRequirement.Count"/> asked for it). <see cref="Approver"/> is the first.</summary>
    public IReadOnlyList<ApproverIdentity> Approvers { get; init; } = [];

    public bool IsApproved => Outcome == ApprovalOutcome.Approved;

    /// <exception cref="ApprovalRejectedException">The request was rejected or expired. Inside a saga this rolls the work back.</exception>
    public void ThrowIfNotApproved()
    {
        if (!IsApproved)
        {
            throw new ApprovalRejectedException(this);
        }
    }
}

/// <summary>An approval was refused or timed out.</summary>
public sealed class ApprovalRejectedException : Exception
{
    public ApprovalRejectedException(ApprovalDecision decision)
        : base(Describe(decision))
    {
        Decision = decision;
    }

    public ApprovalRejectedException()
        : this("An approval was refused.")
    {
    }

    public ApprovalRejectedException(string message)
        : base(message)
    {
        Decision = new ApprovalDecision("", ApprovalOutcome.Rejected, null, null, null);
    }

    public ApprovalRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Decision = new ApprovalDecision("", ApprovalOutcome.Rejected, null, null, null);
    }

    public ApprovalDecision Decision { get; }

    private static string Describe(ApprovalDecision decision) =>
        $"Approval {decision.ApprovalId} {decision.Outcome.ToString().ToLowerInvariant()}" +
        (decision.Approver is { } a ? $" by {a.Subject}" : "") + ".";
}

/// <summary>Told when an approval is requested, so the host can notify the right people. Runs as a durable step, once per request.</summary>
public interface IApprovalNotifier
{
    ValueTask NotifyAsync(ApprovalNotice notice, CancellationToken cancellationToken);
}

/// <param name="Run">The run waiting.</param>
/// <param name="ApprovalId">Pass this to <see cref="AgentClient.ApproveAsync"/> or <see cref="AgentClient.RejectAsync"/>.</param>
/// <param name="Request">What is being asked.</param>
/// <param name="RequestedBy">Who the agent acts for.</param>
/// <param name="RequestedAt">Workflow time of the request.</param>
public sealed record ApprovalNotice(RunKey Run, string ApprovalId, ApprovalRequest Request, string? RequestedBy, DateTimeOffset RequestedAt);
