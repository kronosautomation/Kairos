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

    /// <summary>Change the namespace's tool policy, price table or budgets. Administrators only; every change is audited with both hashes.</summary>
    ManagePolicy,

    /// <summary>Read the namespace's current tool policy and price table (the rules themselves, not the budgets' status).</summary>
    ReadPolicy,

    /// <summary>
    /// What a worker that has no database of its own asks the data plane while it runs agent steps: the namespace's current policy, rate-limit counters, and the budget ledger (reserve, settle, release,
    /// count a tool call). Workers only: it reads and writes counters on behalf of runs, and cannot read or change a policy document.
    /// </summary>
    EvaluatePolicy,

    /// <summary>
    /// Start the conversation of a durable agent (a run whose workflow type is <c>kairos.af.agent/{agent}</c> and whose workflow id is <c>af/{agent}/...</c>) on behalf of a running activity. Held by <see cref="Role.AgentWorker"/> only, and
    /// only for such runs: the data plane's conversation gate decides which runs those are.
    /// </summary>
    StartConversation,

    /// <summary>Send a conversation a message, a nested agent's answer, or the request to close. Not the reserved cancel signal (<see cref="CancelConversation"/>) and not an approval.</summary>
    SignalConversation,

    /// <summary>Ask a conversation to stop: the reserved cancel signal with its fixed id. Not <see cref="CancelRun"/>, which an operator uses and which is audited with a reason.</summary>
    CancelConversation,

    /// <summary>Read a conversation: its state, its current run, a part of its history, and wait for it to grow.</summary>
    ReadConversation,
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
        [Role.Worker] = [Operation.PollWorkflowTask, Operation.PollActivityTask, Operation.CompleteTask, Operation.HeartbeatTask, Operation.EvaluatePolicy],
        [Role.AgentWorker] =
        [
            Operation.PollWorkflowTask, Operation.PollActivityTask, Operation.CompleteTask, Operation.HeartbeatTask, Operation.EvaluatePolicy,
            Operation.StartConversation, Operation.SignalConversation, Operation.CancelConversation, Operation.ReadConversation,
        ],
        [Role.Operator] = [Operation.StartRun, Operation.SignalRun, Operation.CancelRun, Operation.TerminateRun, Operation.ReadRun, Operation.ExportHistory, Operation.ReadPolicy],
        [Role.Admin] =
        [
            Operation.StartRun, Operation.SignalRun, Operation.CancelRun, Operation.TerminateRun, Operation.ReadRun, Operation.ReadAuditLog,
            Operation.ResumeStuckTask, Operation.FailRun, Operation.ExportHistory, Operation.ManagePolicy, Operation.ReadPolicy,
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
