namespace Kairos.Agents;

/// <summary>What a guard is told about a model request before it is made.</summary>
/// <param name="Run">The run asking.</param>
/// <param name="ActivityId">The model step's activity number.</param>
/// <param name="Attempt">The attempt of the step (1 for the first). A retry of the same activity is the same call as far as a reservation is concerned.</param>
/// <param name="IdempotencyKey">Stable across retries of the step (<c>run:activity</c>): a guard keys its reservation on it, so a retry reuses what the first attempt reserved.</param>
/// <param name="Model">The model alias the workflow asked for.</param>
/// <param name="MaxOutputTokens">The cap the request puts on the reply, or null.</param>
/// <param name="AllowanceApprovalId">The approval that let this one call exceed a budget, when a person approved raising it. Null normally.</param>
public sealed record ModelCallInfo(RunKey Run, int ActivityId, int Attempt, string IdempotencyKey, string Model, int? MaxOutputTokens, string? AllowanceApprovalId);

/// <summary>What a guard answers.</summary>
public sealed record ModelCallPermit
{
    /// <summary>True when the call may be made.</summary>
    public bool Allowed { get; init; }

    /// <summary>Opaque to the runtime; handed back to <see cref="IModelCallGuard.SettleAsync"/>.</summary>
    public string? Reservation { get; init; }

    /// <summary>When refused: the name of the limit that was reached. A name from the configuration, never a figure about anyone's data.</summary>
    public string? Limit { get; init; }

    /// <summary>When refused: <c>pause</c> asks a person to approve raising it, <c>fail</c> ends the run.</summary>
    public string OnExceed { get; init; } = "fail";

    /// <summary>When refused and paused: who may approve.</summary>
    public ApprovalRequirement? Approval { get; init; }

    public static ModelCallPermit Allow(string? reservation = null) => new() { Allowed = true, Reservation = reservation };

    public static ModelCallPermit Refuse(string limit, string onExceed = "fail", ApprovalRequirement? approval = null) =>
        new() { Allowed = false, Limit = limit, OnExceed = onExceed, Approval = approval };
}

/// <summary>
/// Asked before every model request and told what it cost afterwards. A guard that enforces a budget reserves the most the call can cost <b>before</b> the model is called, so concurrent calls cannot
/// overspend; <c>Kairos.Agents.Policies</c> (Pro) provides one backed by PostgreSQL. A guard that cannot decide throws: the step is retried, and the call is not made unguarded.
/// </summary>
public interface IModelCallGuard
{
    /// <summary>Reserves what the call may cost, or refuses it. Idempotent for one <see cref="ModelCallInfo.IdempotencyKey"/>.</summary>
    ValueTask<ModelCallPermit> ReserveAsync(ModelCallInfo request, CancellationToken cancellationToken);

    /// <summary>The call returned; <paramref name="usage"/> is what it reported (recorded with the step). Failure to settle is not an error for the call: whoever reconciles settles from history.</summary>
    ValueTask SettleAsync(ModelCallInfo request, ModelCallPermit permit, UsageRecord? usage, CancellationToken cancellationToken);

    /// <summary>
    /// The call failed for good: this was the last attempt the step may make, or the error is one it never retries. Give back what was held. What earlier attempts that were paid for and failed consumed
    /// is in history, and whoever reconciles adds it. Not called for a failure that will be retried (the retry reuses the reservation), and failure to release is not an error for the step.
    /// </summary>
    ValueTask ReleaseAsync(ModelCallInfo request, ModelCallPermit permit, CancellationToken cancellationToken);
}

/// <summary>A budget refused a model call and nobody raised it: the run ends.</summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException()
        : this("A budget was reached.")
    {
    }

    public BudgetExceededException(string message)
        : base(message)
    {
    }

    public BudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The limit that was reached.</summary>
    public string? Limit { get; init; }
}
