namespace Kairos;

/// <summary>Everything a caller can ask a Kairos data plane to do. Each one is classified in <see cref="AccessPolicy"/>.</summary>
public enum Operation
{
    StartRun,
    SignalRun,
    CancelRun,
    TerminateRun,
    ReadRun,
    ReadAuditLog,
    PollWorkflowTask,
    PollActivityTask,
    CompleteTask,
    HeartbeatTask,
    ResumeStuckTask,
    FailRun,

    /// <summary>Approve or reject a pending approval. Separate from <see cref="SignalRun"/>: a signal cannot carry an approval.</summary>
    DecideApproval,

    /// <summary>Copy one run's history out of the system (ciphertext as stored). Always audited with who and why.</summary>
    ExportHistory,
}

/// <summary>
/// The role matrix as code: least privilege per namespace. A principal acts only inside its
/// own tenant and namespace, and a worker credential can neither start nor read workflows.
/// </summary>
public static class AccessPolicy
{
    private static readonly Dictionary<Role, HashSet<Operation>> Allowed = new()
    {
        [Role.Reader] = [Operation.ReadRun],
        [Role.Approver] = [Operation.ReadRun, Operation.DecideApproval],
        [Role.Worker] = [Operation.PollWorkflowTask, Operation.PollActivityTask, Operation.CompleteTask, Operation.HeartbeatTask],
        [Role.Operator] = [Operation.StartRun, Operation.SignalRun, Operation.CancelRun, Operation.TerminateRun, Operation.ReadRun, Operation.ExportHistory],
        [Role.Admin] =
        [
            Operation.StartRun, Operation.SignalRun, Operation.CancelRun, Operation.TerminateRun, Operation.ReadRun, Operation.ReadAuditLog,
            Operation.ResumeStuckTask, Operation.FailRun, Operation.ExportHistory,
        ],
    };

    public static bool IsAllowed(Principal principal, Operation operation, TenantScope target)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Scope == target && Allowed.TryGetValue(principal.Role, out HashSet<Operation>? ops) && ops.Contains(operation);
    }

    /// <summary>The operations a role may perform (for generating documentation and the exhaustive matrix test).</summary>
    public static IReadOnlySet<Operation> For(Role role) => Allowed[role];
}
