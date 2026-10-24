namespace Kairos;

/// <summary>Outcome of replaying a history: the commands the workflow wants executed next.</summary>
public sealed record ReplayResult(IReadOnlyList<WorkflowCommand> NewCommands, bool IsClosed);

public static class WorkflowReplayer
{
    /// <summary>
    /// Runs <paramref name="workflow"/> against an existing history. Commands that history already confirms are checked
    /// (<see cref="NonDeterminismException"/> on mismatch); commands beyond the end of history are returned as new.
    /// </summary>
    /// <param name="workflow">The workflow code to run.</param>
    /// <param name="history">The run's history, starting with its start event.</param>
    /// <param name="verifyHashChain">Verify the SHA-256 chain first; disable only when the caller verified it already.</param>
    /// <param name="continueAsNewSuggestedAt">History length at which <see cref="IWorkflowContext.ShouldContinueAsNew"/> turns true.</param>
    public static ReplayResult Replay(
        WorkflowFunc workflow, IReadOnlyList<HistoryEvent> history, bool verifyHashChain = true, int continueAsNewSuggestedAt = HistoryLimits.ContinueAsNewSuggestedAt)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0 || history[0] is not WorkflowStarted)
        {
            throw new ArgumentException("History must begin with WorkflowStarted.", nameof(history));
        }

        if (verifyHashChain)
        {
            HashChain.Verify(history);
        }

        HistoryEvent first = history[0];
        var execution = new WorkflowExecution(workflow, first.TenantId, first.NamespaceId, first.RunId) { ContinueAsNewSuggestedAt = continueAsNewSuggestedAt };
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] is PatchMarker marker)
            {
                execution.AddRecordedPatch(marker.PatchId);
            }
        }

        for (int i = 0; i < history.Count; i++)
        {
            execution.Apply(history[i]);
        }

        execution.FinishHistory();

        var commands = new List<WorkflowCommand>(execution.PendingCommandCount);
        execution.DrainCommands(commands);
        return new ReplayResult(commands, execution.IsClosed);
    }
}
