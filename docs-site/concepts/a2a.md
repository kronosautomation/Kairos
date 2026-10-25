# Calling other agents with A2A

The Agent2Agent (A2A) protocol lets agents built by different teams talk to each other: one sends a message, the other answers directly or starts
a task that finishes later. `Kairos.Agents.A2aClient` makes each call a durable step, and `Kairos.A2A` adds the two pieces that make calling an
agent you did not write safe to automate: signed Agent Cards, and a Kairos run that can be reported as an A2A task.

Both packages build on the official `A2A` SDK, which is a prerelease at the time of writing, so these packages are published as prereleases too.

## Calling a remote agent

[!code-csharp[](../../samples/AgentSamples/A2aExamples.cs#a2a-client-setup)]

Register `runtime.Activities` on every worker that runs the workflow (in embedded mode, `AddActivities`). Then, in the workflow:

[!code-csharp[](../../samples/AgentSamples/A2aExamples.cs#a2a-call)]

- **Recorded once.** The answer goes into the history. When the workflow is replayed, the recorded answer is returned and the agent is not
  contacted again.
- **Idempotent.** The message id is the step's idempotency key (run and step number), the same on every attempt. A2A servers de-duplicate on
  the message id, so a retry after a lost answer is the same message, not a second request. An agent that does not de-duplicate may repeat the
  work after a crash in that one window, the same limit as any other [activity](retries.md).
- **Long tasks.** When the agent answers with a task, the task id is recorded and polled with [durable timers](timers-and-signals.md). A restart
  resumes polling and never sends the message again. After `A2aCallOptions.Timeout` (workflow time) the task is cancelled remotely and the call
  fails with `TimeoutException`.
- **Questions.** A task that needs input throws `A2aInputRequiredException` with the agent's question and the task id. Answer by calling again with
  `A2aCallOptions.TaskId` set. `failed`, `rejected` and `canceled` tasks throw `A2aAgentFailedException`, `A2aAgentRejectedException` and
  `A2aAgentCancelledException`; a task that needs authentication throws `A2aAuthRequiredException`.
- **Cancelling.** Inside a [saga](sagas.md) the call is cancel-aware: a cancel request ends the wait between polls at once, and rolling the saga back
  (for a cancel or for any later failure) cancels the agent's task as one of its undo steps, retried until it works. If the task has already ended by itself
  there is nothing to cancel and the undo is dropped; a task that stopped to ask a question is still alive, so its undo stays. Cancelling a task that is
  already over is not an error. A message whose answer was lost before its task id was known cannot be cancelled remotely, because nothing recorded the id.
- **Safety.** An agent acts in the world, so it is treated like any other [tool](tool-policy-and-credentials.md): only agents you declare read-only
  run without asking; the others wait for an [approval](approvals.md) and run with it attached, and a rejected approval throws
  `ApprovalRejectedException`. Policy sees the agent as `a2a.<name>`. `UseRedactor` applies to what is recorded.

## Signed Agent Cards

**A signed card is required by default.** `AddAgent(name, address, http, verifier)` takes the keys you trust and refuses a card that is missing,
unsigned or signed by anyone else. An agent whose publisher does not sign is an exception you make in code, one agent at a time:

[!code-csharp[](../../samples/AgentSamples/A2aExamples.cs#a2a-unsigned)]

`AllowUnsignedCard()` accepts a card with no signature at all. It does not excuse a bad one: a card that carries a signature that does not
verify is still refused, because an unsigned card is merely unvouched for while a failed signature is a forgery. A connector with neither a
verifier nor the opt-out refuses to connect, before anything is fetched.

An Agent Card says where an agent is and what it can do. Whoever can change it can send your messages somewhere else, so a card can carry a
signature. Kairos signs and verifies them as the A2A specification describes: a JWS (RFC 7515) over the card's canonical JSON (RFC 8785), with
the `signatures` field itself left out, using ECDSA P-256 (`ES256`).

[!code-csharp[](../../samples/AgentSamples/A2aExamples.cs#a2a-sign)]

[!code-csharp[](../../samples/AgentSamples/A2aExamples.cs#a2a-verify)]

What the verifier insists on:

- **You name the keys.** A card is accepted only if one of its signatures verifies with a key you listed. The key a card names for itself is never
  fetched or believed, and a header's `jku` is ignored.
- **Exactly ES256.** `none`, `HS256` and every other algorithm are refused, as is any header field it does not know (including `crit`).
- **The whole card.** Changing the name, an address, a skill, a capability or anything else invalidates the signature, and a signature cannot be
  moved to another card.
- **The card as received.** `AgentCardVerifier.VerifyJson` checks the bytes that arrived, so fields the SDK does not model are covered by the
  signature instead of being lost before the check. The connector does this for you: with a verifier it refuses a card that is missing,
  unsigned or untrusted with `UntrustedAgentCardException`, before anything is sent.

A valid signature says who published the card, not that the agent is safe or its answers are right. Keep the policy and approval rules for what an agent may be asked to do.

## A run as an A2A task

`KairosA2aTasks` reports a Kairos run as an A2A task, so an agent you publish with Kairos has tasks that survive restarts and deployments: an open run
is `working`, a completed run is `completed` with its result as an artifact, a run a saga cancelled is `canceled`, a run that ended because an
approval was refused is `rejected`, anything else that went wrong (or an operator terminated) is `failed`, and a run waiting on people is
`input-required` when you supply an `IA2aInputProjection`. A run that continues as new stays the same task. Task ids belong to one tenant and
namespace, with an optional signature, and an id from elsewhere finds nothing.

Who answers a question a task asked is never taken from the message that answers it; whatever serves the task decides that from the
authenticated caller, the same rule as for [approvals](approvals.md).
