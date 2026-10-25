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

A decision from someone who does not qualify, or one that is malformed or names another approval, is **ignored**: the run keeps waiting
and the stray decision stays in the history. An approval can only be given once. After the first valid decision further decisions change
nothing, and a decision for a run that has ended is refused (`false`).

## Deciding

Your application shows the pending approvals, authenticates the person who decides, and passes their identity:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#approval-inbox)]

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#approval-decide)]

`RejectAsync` is the same call with the opposite answer. Sending the same decision twice is harmless (the delivery is identified by
approval, person and answer), so a request that is retried after a timeout cannot decide twice.

`AgentHistory.PendingApprovals(history)` lists the requests nobody has validly decided; it is what an approvals page or an inbox is
built from. Pass it the history from `GetOpenedHistoryAsync`, which opens payloads when encryption is on.

## What this does and does not guarantee

- **The identity in the history is the one your application asserted.** Kairos checks it against the requirement and keeps it, but it
  cannot authenticate a person itself. Whoever can call `ApproveAsync` can name any identity, so put the call behind your own
  authentication and authorization, as you would any endpoint that changes state. `ApproverIdentity.Evidence` is for whatever proof you
  want to keep alongside (a session or assertion id); never put a password in it.
- **Anyone who can write signals to the run's database can forge a decision.** The database role your application uses is the trust
  boundary; keep it with the application.
- **An approval covers one call.** Approving a tool call does not approve the next one, even with the same arguments.
- **Timeouts follow workflow time**, so a run that is waiting is not woken early or late by clock differences between servers.
- With [payload encryption](history.md) on, the request details, the decision and the approver's identity are sealed like any other signal.
