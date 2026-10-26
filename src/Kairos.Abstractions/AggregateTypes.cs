namespace Kairos;

/// <summary>Model usage of one workflow type (an "agent") on one UTC day for one model, summed over the runs that recorded it.</summary>
/// <param name="WorkflowType">The agent: the workflow type of the runs.</param>
/// <param name="Day">The UTC day the attempts were recorded on.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="Steps">Model steps that completed.</param>
/// <param name="FailedAttempts">Attempts that were paid for and then failed.</param>
/// <param name="InputTokens">Input tokens of both, cached ones included.</param>
/// <param name="OutputTokens">Output tokens of both.</param>
/// <param name="CachedInputTokens">The part of <paramref name="InputTokens"/> served from the provider's cache.</param>
/// <param name="SavedInputTokens">Input tokens of completed steps that had completed before a later failure of their run: not paid for again when it carried on (an estimate of what durability saved).</param>
/// <param name="SavedOutputTokens">The same for output tokens.</param>
/// <param name="SavedCachedInputTokens">The same for cached input tokens.</param>
public sealed record UsageBucket(
    string WorkflowType, DateOnly Day, string Model, long Steps, long FailedAttempts, long InputTokens, long OutputTokens, long CachedInputTokens,
    long SavedInputTokens, long SavedOutputTokens, long SavedCachedInputTokens);

/// <summary>How many runs of a workflow type recorded usage in the window.</summary>
public sealed record WorkflowTypeRuns(string WorkflowType, long Runs);

/// <summary>What <see cref="IKairosStore.GetUsageSummaryAsync"/> returns.</summary>
/// <param name="Buckets">Usage by workflow type, day and model, ordered by workflow type, day, model.</param>
/// <param name="Runs">Runs that recorded usage in the window, by workflow type.</param>
/// <param name="Behind">How many runs have events the aggregate has not read yet (a store that projects catches up before answering, within a budget). Zero means the answer is complete.</param>
public sealed record UsageSummary(IReadOnlyList<UsageBucket> Buckets, IReadOnlyList<WorkflowTypeRuns> Runs, int Behind);

/// <summary>A position in the approvals waiting for a person, oldest first.</summary>
public readonly record struct PendingApprovalCursor(DateTimeOffset RequestedAt, string ApprovalId);

/// <summary>An approval a run is waiting for. Metadata only: what it is about is sealed, and a browser that may open it fetches the run.</summary>
public sealed record PendingApprovalItem(RunKey Run, string WorkflowType, string ApprovalId, DateTimeOffset RequestedAt)
{
    public PendingApprovalCursor Cursor => new(RequestedAt, ApprovalId);
}

/// <summary>What <see cref="IKairosStore.GetPendingApprovalsAsync"/> returns.</summary>
/// <param name="Items">Up to the requested number, oldest first.</param>
/// <param name="Total">How many approvals are waiting in all.</param>
/// <param name="Behind">As in <see cref="UsageSummary.Behind"/>.</param>
public sealed record PendingApprovalPage(IReadOnlyList<PendingApprovalItem> Items, int Total, int Behind);
