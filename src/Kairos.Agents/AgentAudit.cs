namespace Kairos.Agents;

/// <summary>How an approval stood when the history was read.</summary>
public enum ApprovalAuditOutcome
{
    /// <summary>A person's decision was accepted: approved.</summary>
    Approved,

    /// <summary>A person's decision was accepted: rejected.</summary>
    Rejected,

    /// <summary>No decision was accepted by the time of the last event: still waiting, timed out, or the run ended or was cancelled first.</summary>
    Unresolved,

    /// <summary>A decision arrived, and what became of it is in a sealed step the reader did not open.</summary>
    Sealed,
}

/// <summary>One thing an agent run did that an auditor asks about. Built from the run's history by <see cref="AgentAudit"/>; it never holds a payload, a token or the text of a request.</summary>
/// <param name="At">When it was recorded (the time of the history event that records it).</param>
public abstract record AgentAuditRecord(DateTimeOffset At);

/// <summary>An approval: asked for, and (when opened) how it ended and who decided.</summary>
/// <param name="At">The time of the accepted decision, or of the request when there is none.</param>
/// <param name="ApprovalId">The approval's id (readable: it is in the name of the decision signal).</param>
/// <param name="Tool">The tool the approval was for, or null (not opened, or not for a tool).</param>
/// <param name="RequestedAt">When it was requested, or null when the request is sealed and was not opened.</param>
/// <param name="DecidedAt">When the accepted decision was recorded.</param>
/// <param name="Outcome">How it stood.</param>
/// <param name="Approver">The subject the approver verifier established (never what the decision said about itself), or null.</param>
/// <param name="Roles">The roles the verifier established.</param>
/// <param name="Refused">How many decisions for this approval were refused by the verifier (not allowed, bad evidence, replayed).</param>
public sealed record ApprovalAuditRecord(
    DateTimeOffset At, string ApprovalId, string? Tool, DateTimeOffset? RequestedAt, DateTimeOffset? DecidedAt, ApprovalAuditOutcome Outcome,
    string? Approver, IReadOnlyList<string> Roles, int Refused) : AgentAuditRecord(At);

/// <summary>What the policy decided about one tool call, as recorded with the step.</summary>
/// <param name="At">When the step completed.</param>
/// <param name="Step">The activity number of the tool step.</param>
/// <param name="Tool">The tool.</param>
/// <param name="Verdict">Run it, hold it for a person, or refuse it.</param>
/// <param name="Reason">The policy's reason, when it gave one. Written by the policy: do not make it carry data.</param>
public sealed record PolicyDecisionAuditRecord(DateTimeOffset At, int Step, string Tool, ToolVerdict Verdict, string? Reason) : AgentAuditRecord(At)
{
    /// <summary>The rule that decided, when the policy records its decisions (<see cref="IRecordedToolPolicy"/>), else null.</summary>
    public string? Rule { get; init; }

    /// <summary>The version of the policy that decided, or 0.</summary>
    public long PolicyVersion { get; init; }

    /// <summary>The hash of that version, or null.</summary>
    public string? PolicyHash { get; init; }
}

/// <summary>A credential issued to a tool for one call: what it allowed and until when. The token itself is never recorded.</summary>
/// <param name="At">When the step completed.</param>
/// <param name="Step">The activity number of the tool step.</param>
/// <param name="Tool">The tool it was issued to.</param>
/// <param name="Scopes">What it allowed.</param>
/// <param name="ExpiresAt">When it stopped working.</param>
/// <param name="OnBehalfOf">Who the agent was acting for, or null.</param>
public sealed record CredentialAuditRecord(
    DateTimeOffset At, int Step, string Tool, IReadOnlyList<string> Scopes, DateTimeOffset ExpiresAt, string? OnBehalfOf) : AgentAuditRecord(At);

/// <summary>
/// Reads what an auditor asks of an agent run (its approvals, the policy's decisions and the credentials it issued) back out of the run's history. Pass the history that
/// <see cref="KairosClient.GetOpenedHistoryAsync"/> returns, or use <see cref="ApprovalSignals"/> on a history whose payloads are still sealed.
/// </summary>
public static class AgentAudit
{
    /// <summary>
    /// Approvals, policy decisions and credentials in a history whose payloads have been opened, in the order the history recorded them.
    /// </summary>
    /// <exception cref="FormatException">An agent step's payload is not what an agent step records (for example it is still sealed).</exception>
    public static IReadOnlyList<AgentAuditRecord> Read(IReadOnlyList<HistoryEvent> openedHistory)
    {
        ArgumentNullException.ThrowIfNull(openedHistory);
        try
        {
            return ReadCore(openedHistory);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FormatException("An agent step in this history is not readable. Was it opened with the right key?", ex);
        }
    }

    private sealed class ApprovalState
    {
        public DateTimeOffset? DecidedAt { get; set; }

        public long DecidedSequence { get; set; }

        public bool Approved { get; set; }

        public string? Approver { get; set; }

        public string[] Roles { get; set; } = [];

        public List<string> Accepted { get; } = [];

        public int Refused { get; set; }
    }

    private static void AddToolDecision(List<(long Sequence, AgentAuditRecord Record)> records, ActivityCompleted c, ToolStepRequest asked, ToolStepResponse response)
    {
        ToolVerdict verdict = response.Outcome switch
        {
            ToolOutcomes.Denied => ToolVerdict.Deny,
            ToolOutcomes.ApprovalRequired => ToolVerdict.RequireApproval,
            _ => ToolVerdict.Auto,
        };
        records.Add((c.Sequence, new PolicyDecisionAuditRecord(c.Timestamp, c.ActivityId, asked.Name, verdict, verdict == ToolVerdict.Auto ? null : response.Reason)));
    }

    private static List<AgentAuditRecord> ReadCore(IReadOnlyList<HistoryEvent> history)
    {
        var steps = new Dictionary<int, ActivityScheduled>();
        var requests = new Dictionary<string, (WireApprovalRequest Request, DateTimeOffset At, long Sequence)>(StringComparer.Ordinal);
        var verifies = new Dictionary<int, ApprovalVerifyRequest>();
        var approvals = new Dictionary<string, ApprovalState>(StringComparer.Ordinal);
        var records = new List<(long Sequence, AgentAuditRecord Record)>();

        foreach (HistoryEvent e in history)
        {
            switch (e)
            {
                case ActivityScheduled s when s.ActivityType is AgentActivityNames.Tool or AgentActivityNames.Policy or AgentActivityNames.ApprovalRequest or AgentActivityNames.ApprovalVerify:
                    steps[s.ActivityId] = s;
                    if (s.ActivityType == AgentActivityNames.ApprovalRequest)
                    {
                        WireApprovalRequest request = WireCodec.Read(s.Input, AgentJson.Default.WireApprovalRequest);
                        requests[request.ApprovalId] = (request, s.Timestamp, s.Sequence);
                    }
                    else if (s.ActivityType == AgentActivityNames.ApprovalVerify)
                    {
                        verifies[s.ActivityId] = WireCodec.Read(s.Input, AgentJson.Default.ApprovalVerifyRequest);
                    }

                    break;

                case ActivityCompleted c when steps.TryGetValue(c.ActivityId, out ActivityScheduled? step):
                    if (step.ActivityType == AgentActivityNames.ApprovalVerify)
                    {
                        ApprovalVerifyRequest asked = verifies[c.ActivityId];
                        ApprovalVerifyResult result = WireCodec.Read(c.Result, AgentJson.Default.ApprovalVerifyResult);
                        if (!approvals.TryGetValue(asked.Request.ApprovalId, out ApprovalState? state))
                        {
                            approvals[asked.Request.ApprovalId] = state = new ApprovalState();
                        }

                        if (!result.Ok)
                        {
                            state.Refused++;
                        }
                        else if (state.DecidedAt is null)
                        {
                            // One rejection decides; an approval decides when as many different people as the request asked for have approved.
                            if (asked.Decision.Approved)
                            {
                                if (!state.Accepted.Contains(result.Subject))
                                {
                                    state.Accepted.Add(result.Subject);
                                    state.Roles = [.. state.Roles.Union(result.Roles)];
                                }
                            }
                            else
                            {
                                state.Accepted.Clear();
                                state.Accepted.Add(result.Subject);
                                state.Roles = result.Roles;
                            }

                            if (!asked.Decision.Approved || state.Accepted.Count >= Math.Clamp(asked.Request.Requirement.Count, 1, ApprovalRequirement.MaxCount))
                            {
                                (state.DecidedAt, state.DecidedSequence, state.Approved, state.Approver) = (c.Timestamp, c.Sequence, asked.Decision.Approved, string.Join(", ", state.Accepted));
                            }
                        }
                    }
                    else if (step.ActivityType == AgentActivityNames.Policy)
                    {
                        ToolStepRequest asked = WireCodec.Read(step.Input, AgentJson.Default.ToolStepRequest);
                        PolicyStepResponse response = WireCodec.Read(c.Result, AgentJson.Default.PolicyStepResponse);
                        ToolVerdict verdict = response.Outcome switch
                        {
                            ToolOutcomes.Denied => ToolVerdict.Deny,
                            ToolOutcomes.ApprovalRequired => ToolVerdict.RequireApproval,
                            _ => ToolVerdict.Auto,
                        };
                        records.Add((c.Sequence, new PolicyDecisionAuditRecord(c.Timestamp, c.ActivityId, asked.Name, verdict, verdict == ToolVerdict.Auto ? null : response.Reason)
                        {
                            Rule = response.Decision?.RuleId,
                            PolicyVersion = response.Decision?.PolicyVersion ?? 0,
                            PolicyHash = response.Decision?.PolicyHash is { Length: > 0 } h ? h : null,
                        }));
                    }
                    else if (step.ActivityType == AgentActivityNames.Tool)
                    {
                        ToolStepRequest asked = WireCodec.Read(step.Input, AgentJson.Default.ToolStepRequest);
                        ToolStepResponse response = WireCodec.Read(c.Result, AgentJson.Default.ToolStepResponse);
                        if (asked.Decision is null)
                        {
                            AddToolDecision(records, c, asked, response);
                        }

                        if (response.Credential is { } grant)
                        {
                            records.Add((c.Sequence, new CredentialAuditRecord(c.Timestamp, c.ActivityId, asked.Name, [.. grant.Scopes], grant.ExpiresAt, grant.OnBehalfOf)));
                        }
                    }

                    break;
            }
        }

        // An approval is reported where its accepted decision was recorded or, when there is none, where it was requested.
        foreach ((string id, (WireApprovalRequest request, DateTimeOffset requestedAt, long requestedSequence)) in requests)
        {
            approvals.TryGetValue(id, out ApprovalState? state);
            bool decided = state?.DecidedAt is not null;
            ApprovalAuditOutcome outcome = !decided ? ApprovalAuditOutcome.Unresolved : state!.Approved ? ApprovalAuditOutcome.Approved : ApprovalAuditOutcome.Rejected;
            records.Add((decided ? state!.DecidedSequence : requestedSequence, new ApprovalAuditRecord(
                state?.DecidedAt ?? requestedAt, id, request.Tool, requestedAt, state?.DecidedAt, outcome, state?.Approver, state?.Roles ?? [], state?.Refused ?? 0)));
        }

        return [.. records.OrderBy(r => r.Sequence).Select(r => r.Record)];
    }

    /// <summary>
    /// The approvals a history shows <b>without opening anything</b>: one per approval id, taken from the names of the decision signals (which are readable), with the time the first decision
    /// arrived. Who decided and whether the decision was accepted are sealed, so every record is <see cref="ApprovalAuditOutcome.Sealed"/>.
    /// </summary>
    public static IReadOnlyList<ApprovalAuditRecord> ApprovalSignals(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ApprovalAuditRecord>();
        foreach (SignalReceived signal in history.OfType<SignalReceived>())
        {
            if (signal.SignalName.StartsWith(ReservedSignals.ApprovalPrefix, StringComparison.Ordinal) && seen.Add(signal.SignalName[ReservedSignals.ApprovalPrefix.Length..]))
            {
                result.Add(new ApprovalAuditRecord(signal.Timestamp, signal.SignalName[ReservedSignals.ApprovalPrefix.Length..], null, null, signal.Timestamp, ApprovalAuditOutcome.Sealed, null, [], 0));
            }
        }

        return result;
    }
}
