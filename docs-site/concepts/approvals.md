# Approvals

Some steps should not happen until a person says so: a refund, a message to a customer, a deployment. An approval is a durable wait for
a decision from someone you name. The run holds no thread while it waits, and the decision, including **who** made it, becomes part of
the run's history.

## Asking

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#approval-request)]

`RequestApprovalAsync` records the request, tells your `IApprovalNotifier` (if you registered one, to post to a chat channel or send an
email), and waits. It returns an `ApprovalDecision` with an outcome (`Approved`, `Rejected` or `Expired`), the approver's identity, the
comment and the time. `ThrowIfNotApproved()` turns anything but approval into an `ApprovalRejectedException`.

Tool calls that need approval (see [Tool policy and credentials](tool-policy-and-credentials.md)) use the same mechanism without any code
from you: `InvokeToolAsync` asks, waits, and then runs the tool with the approval attached, or tells the model the call was not made.

## Who may decide

An `ApprovalRequirement` says who counts:

| Setting | Meaning |
|---|---|
| `Approvers` | Subjects (user ids) allowed to decide. |
| `Roles` | Roles allowed to decide. A person qualifies by subject **or** by role. With neither set, any identity your application passes counts. |
| `ForbidSelfApproval` | On by default. The person the agent acts for (`ctx.OnBehalfOf`) may not approve their own request. |
| `Timeout` | Workflow time after which the approval expires. Nothing is waited on forever unless you leave it unset. |

A decision from someone who does not qualify, or one that is malformed, names another approval, or cannot be verified, is **ignored**: the
run keeps waiting, the approval stays in `PendingApprovals`, and the stray decision stays in the history. An approval can only be given
once. After the first accepted decision further decisions change nothing, and a decision for a run that has ended is refused (`false`).

## Proving who decided

A decision says who made it, but a claim is not proof. Kairos therefore treats decisions as untrusted until a **verifier** has checked the
evidence that came with them, and records the result as a step, so the check is never repeated when the run replays.

- **Approvals travel on a reserved signal name.** Names starting with `kairos.approval.` cannot be sent with `KairosClient.SignalAsync`
  (it throws), and a bus message with such a name is dropped. Only `KairosClient.DecideApprovalAsync` (which `AgentClient` uses) sends
  them. A server authorizes that call as its own operation, `DecideApproval`, granted to the `Approver` role and not to `Operator`, so being
  allowed to signal runs does not allow approving.
- **Evidence is signed with a key the workers do not have.** Approval evidence is an ES256 token (ECDSA on P-256) made by the code that
  authenticated the person, through an `IApprovalEvidenceSigner`. In production the private key lives in Key Vault, KMS or an HSM and the
  signer only asks it to sign; `LocalApprovalEvidenceSigner` holds a key in the process and is for tests and samples. Workers get the
  **public** key only (`EsApproverVerifier`), pinned by key id. A compromised worker, or anyone who holds the public key, can check evidence but
  cannot make it, and a key that is not an ECDSA P-256 public key (an HMAC secret, an RSA key) is refused when the verifier is made.
- **Evidence is bound to the decision.** One piece names the tenant, namespace, run, approval, answer and person it was made for, carries the
  roles the person holds, is issued at a moment, expires within five minutes at most, and has a random id. The verifier accepts it only if all
  of those match the decision, and it takes the subject and roles **from the evidence**, never from the decision's own fields. Evidence for
  another run, another approval, the opposite answer or another person is refused, as is expired, forged or altered evidence, a different algorithm
  (including a token keyed with the public key as if it were a secret), or a token that is not approval evidence. Expiry is judged when the decision
  reaches the run, not when a worker gets to it; a decision whose evidence expired before it arrived stays open for a fresh one.
- **Evidence is used once.** The verifier remembers the ids it has accepted (in memory, bounded) and refuses a second verification step that
  presents the same id. A step that is repeated after a crash, between checking and recording, is the same step and still finds the decision good, so a crash never changes how a run ends.
  The memory is per process: the short life and the binding to one decision are what limit a replay on another worker.
- **Register the verifier** with `UseApproverVerifier`. For an identity provider that issues ID tokens, implement `IApproverVerifier` over
  the provider's validation instead; it receives the run, approval, answer, claimed subject, the evidence and the key of the verification step.
- **Fail closed.** Without a verifier, a request that names approvers or roles refuses every decision, because nothing could prove the
  claim. A request that names nobody accepts any identity, and the person the agent acts for is still barred from approving their own request.

## Deciding

Your application shows the pending approvals, authenticates the person who decides, and passes only an identity it authenticated.
Give the `AgentClient` the evidence signer so that it signs the decision for that identity:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#approval-inbox)]

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#approval-decide)]

`RejectAsync` is the same call with the opposite answer. Re-sending a decision with the same evidence is harmless (it is the same delivery), and a second valid decision after the first changes nothing.

`AgentHistory.PendingApprovals(history)` lists the requests nobody has validly decided; it is what an approvals page or an inbox is
built from. Pass it the history from `GetOpenedHistoryAsync`, which opens payloads when encryption is on.

## What this does and does not guarantee

- **In embedded mode your application is the trust boundary.** The signing key belongs to the code that authenticates people (and, in
  production, to Key Vault or KMS, which signs on its behalf). `AgentClient.ApproveAsync` signs whatever identity it is given, so call it only
  for a person you have authenticated, and put the endpoint behind your own authentication and authorization. Nothing that can merely send
  signals, nothing that only reads the database, and no worker, can approve: workers hold the public key only.
- **Whoever can ask the signer to sign can approve as anyone.** Limit who may use the signing key in Key Vault or KMS to the application that
  authenticates people, and keep it out of the tool services and out of anything that handles prompts. Rotating the key makes every unused
  piece of evidence worthless; list both the old and the new public key in the verifier while that happens.
- **An approval covers one call.** Approving a tool call does not approve the next one, even with the same arguments.
- **Timeouts follow workflow time**, so a run that is waiting is not woken early or late by clock differences between servers.
- With [payload encryption](payload-encryption.md) on, the request details, the decision and the approver's identity are sealed like any other signal.
