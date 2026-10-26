using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kairos.Agents;

/// <summary>
/// The caller's side of an agent run: deciding approvals and cancelling. Your application authenticates the person first and passes only an
/// identity it authenticated. With <paramref name="evidenceSigner"/> the client signs each decision for that identity (ES256, through Key Vault or KMS in production), which is what lets
/// the worker prove who decided with the public key alone (see <see cref="AgentsBuilder.UseApproverVerifier"/> and <see cref="EsApproverVerifier"/>); give a signer only to the code that authenticates people.
/// </summary>
public sealed class AgentClient(KairosClient client, TimeProvider time, IApprovalEvidenceSigner? evidenceSigner = null)
{
    public const string CancelSignalName = ReservedSignals.Cancel;

    public static string ApprovalSignalName(string approvalId) => ReservedSignals.ApprovalPrefix + approvalId;

    public ValueTask<bool> ApproveAsync(RunKey run, string approvalId, ApproverIdentity approver, string? comment = null, CancellationToken cancellationToken = default) =>
        DecideAsync(run, approvalId, approved: true, approver, comment, cancellationToken);

    public ValueTask<bool> RejectAsync(RunKey run, string approvalId, ApproverIdentity approver, string? comment = null, CancellationToken cancellationToken = default) =>
        DecideAsync(run, approvalId, approved: false, approver, comment, cancellationToken);

    /// <summary>Asks a run that is inside a saga to stop and roll back. It stops at its next step boundary or approval wait.</summary>
    public ValueTask<bool> RequestCancelAsync(RunKey run, string reason, CancellationToken cancellationToken = default) =>
        client.SignalAsync(run, CancelSignalName, Encoding.UTF8.GetBytes(reason ?? ""), signalId: ReservedSignals.CancelSignalId, cancellationToken);

    private async ValueTask<bool> DecideAsync(
        RunKey run, string approvalId, bool approved, ApproverIdentity approver, string? comment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver.Subject);
        var wire = new WireApprovalDecision
        {
            ApprovalId = approvalId,
            Approved = approved,
            Approver = new WireApprover
            {
                Subject = approver.Subject,
                Name = approver.Name,
                Roles = [.. approver.Roles ?? []],
                Evidence = approver.Evidence ?? await MintAsync(run, approvalId, approved, approver, cancellationToken).ConfigureAwait(false),
            },
            Comment = comment,
            DecidedAt = time.GetUtcNow(),
        };

        // The id makes a retry harmless: the same person deciding the same way twice signals once.
        // A fresh decision carries fresh evidence, hence a fresh delivery; a retry that re-sends the same evidence is the same delivery.
        string evidenceDigest = wire.Approver.Evidence is null ? "" : ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(wire.Approver.Evidence)))[..16];
        string signalId = $"approval:{approvalId}:{approver.Subject}:{(approved ? "approve" : "reject")}{evidenceDigest}";
        return await client.DecideApprovalAsync(run, approvalId, WireCodec.Write(wire, AgentJson.Default.WireApprovalDecision), signalId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string?> MintAsync(RunKey run, string approvalId, bool approved, ApproverIdentity approver, CancellationToken cancellationToken)
    {
        if (evidenceSigner is null)
        {
            return null;
        }

        DateTimeOffset now = time.GetUtcNow();
        return await ApprovalEvidence.MintAsync(
            evidenceSigner, new ApprovalEvidenceClaims(run, approvalId, approved, approver.Subject, [.. approver.Roles ?? []], now, now + TimeSpan.FromMinutes(5), ApprovalEvidence.NewEvidenceId()), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An approval request that nobody has validly decided yet.</summary>
public sealed record PendingApproval(
    string ApprovalId, string Action, string? Details, string? Tool, string? RequestedBy, ApprovalRequirement Requirement, DateTimeOffset RequestedAt);

/// <summary>Reads agent facts back out of a run's history (pass the opened, plaintext copy that <see cref="KairosClient.GetOpenedHistoryAsync"/> returns).</summary>
public static class AgentHistory
{
    /// <summary>The approvals a run is waiting on: requested, and no accepted decision has arrived. This is what an approvals inbox shows.</summary>
    public static IReadOnlyList<PendingApproval> PendingApprovals(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var requests = new List<WireApprovalRequest>();
        foreach (ActivityScheduled scheduled in history.OfType<ActivityScheduled>())
        {
            if (scheduled.ActivityType == AgentActivityNames.ApprovalRequest)
            {
                requests.Add(WireCodec.Read(scheduled.Input, AgentJson.Default.WireApprovalRequest));
            }
        }

        // An approval is decided once a verification step accepted a decision for it.
        var verifyIds = new Dictionary<int, string>();
        foreach (ActivityScheduled scheduled in history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentActivityNames.ApprovalVerify))
        {
            verifyIds[scheduled.ActivityId] = WireCodec.Read(scheduled.Input, AgentJson.Default.ApprovalVerifyRequest).Request.ApprovalId;
        }

        var decided = new HashSet<string>(StringComparer.Ordinal);
        foreach (ActivityCompleted done in history.OfType<ActivityCompleted>())
        {
            if (verifyIds.TryGetValue(done.ActivityId, out string? approvalId) && WireCodec.Read(done.Result, AgentJson.Default.ApprovalVerifyResult).Ok)
            {
                decided.Add(approvalId);
            }
        }

        var pending = new List<PendingApproval>();
        foreach (WireApprovalRequest request in requests)
        {
            if (!decided.Contains(request.ApprovalId))
            {
                pending.Add(new PendingApproval(
                    request.ApprovalId, request.Action, request.Details, request.Tool, request.RequestedBy,
                    AgentsRuntime.FromWire(request.Requirement), request.RequestedAt));
            }
        }

        return pending;
    }

    /// <summary>Tokens the run's model calls reported, summed from history.</summary>
    public static AgentUsage TotalUsage(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        long input = 0, output = 0, total = 0;
        var chatIds = new HashSet<int>(history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentActivityNames.Chat).Select(a => a.ActivityId));
        foreach (ActivityCompleted done in history.OfType<ActivityCompleted>().Where(a => chatIds.Contains(a.ActivityId)))
        {
            ChatStepResponse response = WireCodec.Read(done.Result, AgentJson.Default.ChatStepResponse);
            input += response.Usage?.Input ?? 0;
            output += response.Usage?.Output ?? 0;
            total += response.Usage?.Total ?? 0;
        }

        return new AgentUsage(input, output, total);
    }
}

/// <summary>One rollback as history shows it.</summary>
/// <param name="Planned">Compensations in the order they were to run (the reverse of registration).</param>
/// <param name="Started">Those that began.</param>
/// <param name="Finished">Those that ended, successfully or not.</param>
/// <param name="Failed">Those that ended without being undone.</param>
/// <param name="Ended">True when the rollback recorded its end.</param>
public sealed record SagaRollback(
    IReadOnlyList<string> Planned, IReadOnlyList<string> Started, IReadOnlyList<string> Finished, IReadOnlyList<string> Failed, bool Ended);

/// <summary>Reads sagas back out of history and checks that a rollback that began always completes.</summary>
public static class SagaHistory
{
    public static IReadOnlyList<SagaRollback> Rollbacks(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var result = new List<SagaRollback>();
        List<string> planned = [], started = [], finished = [], failed = [];
        bool open = false;

        foreach (ActivityScheduled scheduled in history.OfType<ActivityScheduled>())
        {
            if (scheduled.ActivityType != AgentActivityNames.SagaMark)
            {
                continue;
            }

            SagaMark mark = WireCodec.Read(scheduled.Input, AgentJson.Default.SagaMark);
            switch (mark.Kind)
            {
                case SagaMarks.RollbackBegin:
                    (planned, started, finished, failed, open) = ([.. mark.Names], [], [], [], true);
                    break;
                case SagaMarks.StepBegin when open:
                    started.Add(mark.Name ?? "");
                    break;
                case SagaMarks.StepEnd when open:
                    finished.Add(mark.Name ?? "");
                    if (!mark.Succeeded)
                    {
                        failed.Add(mark.Name ?? "");
                    }

                    break;
                case SagaMarks.RollbackEnd when open:
                    result.Add(new SagaRollback(planned, started, finished, failed, Ended: true));
                    open = false;
                    break;
            }
        }

        if (open)
        {
            result.Add(new SagaRollback(planned, started, finished, failed, Ended: false));
        }

        return result;
    }

    /// <summary>
    /// Checks that every rollback that began has completed. Returns null when that holds, otherwise what is wrong. For a run that closed on its own (completed, failed, continued)
    /// every rollback must have ended, with every planned compensation begun and finished, in the planned order. A run an operator
    /// terminated is exempt: that closes it from outside.
    /// </summary>
    public static string? CheckCompensationsComplete(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[^1] is RunTerminated || history[^1] is not (WorkflowCompleted or WorkflowFailed or WorkflowContinued))
        {
            return null;
        }

        int index = 0;
        foreach (SagaRollback r in Rollbacks(history))
        {
            index++;
            if (!r.Ended)
            {
                return $"rollback {index} never recorded its end although the run closed";
            }

            if (!r.Started.SequenceEqual(r.Planned))
            {
                return $"rollback {index} began [{string.Join(", ", r.Started)}] but planned [{string.Join(", ", r.Planned)}]";
            }

            if (!r.Finished.SequenceEqual(r.Planned))
            {
                return $"rollback {index} finished [{string.Join(", ", r.Finished)}] but planned [{string.Join(", ", r.Planned)}]";
            }
        }

        return null;
    }
}

/// <summary>Serializes conversations the way agent steps record them. Used by the recorded-model fixtures in <c>Kairos.Testing</c>.</summary>
public static class AgentMessageCodec
{
    public static byte[] Serialize(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return JsonSerializer.SerializeToUtf8Bytes([.. messages.Select(m => WireCodec.ToWire(m, null))], AgentJson.Default.WireMessageArray);
    }

    public static IReadOnlyList<ChatMessage> Deserialize(ReadOnlySpan<byte> json)
    {
        WireMessage[] wire = JsonSerializer.Deserialize(json, AgentJson.Default.WireMessageArray) ?? [];
        return [.. wire.Select(WireCodec.FromWire)];
    }

    /// <summary>A stable fingerprint of a model call: the conversation it carries. Equal conversations have equal fingerprints.</summary>
    public static string Fingerprint(IEnumerable<ChatMessage> messages)
    {
        byte[] bytes = Serialize(messages);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
