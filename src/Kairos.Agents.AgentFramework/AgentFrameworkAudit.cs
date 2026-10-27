namespace Kairos.Agents.AgentFramework;

/// <summary>A person let a run continue on a changed definition of its agent.</summary>
/// <param name="At">When the override was recorded.</param>
/// <param name="Agent">The agent.</param>
/// <param name="FromHash">The definition the run was pinned to.</param>
/// <param name="ToHash">The definition it continues on.</param>
/// <param name="ApprovalId">The approval that allowed it.</param>
/// <param name="Approver">The subject the approver verifier established (never what the decision said about itself).</param>
/// <param name="Roles">The roles the verifier established.</param>
public sealed record DefinitionOverrideAuditRecord(
    DateTimeOffset At, string Agent, string FromHash, string ToHash, string ApprovalId, string Approver, IReadOnlyList<string> Roles) : AgentAuditRecord(At);

/// <summary>
/// What an auditor asks of a durable-agent run: everything <see cref="AgentAudit"/> reads (approvals with their verified approvers, the policy's decisions including the rule that let a tool run without approval,
/// credentials) plus the overrides of changed agent definitions.
/// </summary>
public static class AgentFrameworkAudit
{
    /// <summary>Pass the history that <see cref="KairosClient.GetOpenedHistoryAsync"/> returns.</summary>
    public static IReadOnlyList<AgentAuditRecord> Read(IReadOnlyList<HistoryEvent> openedHistory)
    {
        ArgumentNullException.ThrowIfNull(openedHistory);
        var records = new List<AgentAuditRecord>(AgentAudit.Read(openedHistory));
        foreach (ActivityScheduled scheduled in openedHistory.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentFrameworkNames.Override))
        {
            AfOverrideMark mark = AfCodec.Read(scheduled.Input, AfJson.Default.AfOverrideMark);
            records.Add(new DefinitionOverrideAuditRecord(scheduled.Timestamp, mark.Agent, mark.FromHash, mark.ToHash, mark.ApprovalId, mark.Approver, mark.Roles));
        }

        return [.. records.OrderBy(r => r.At)];
    }
}
