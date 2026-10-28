# Publishing agents over A2A

`Kairos.Agents.A2a` publishes Kairos workflows as agents that other agents can call over the Agent2Agent (A2A) protocol. Each workflow becomes a skill on a signed Agent
Card, each message starts a run, and the run is the task that the caller follows. The caller is identified from the authenticated connection, never from the message.
It is a library for your own ASP.NET Core host. The [standalone server](standalone-server.md) does not serve A2A. This page covers what is published, the signed card, how
callers are identified and the limits. It is a commercial feature.

Calling a remote agent, and verifying the cards of remote agents, are part of the open-source client and are described in
[Calling other agents with A2A](../concepts/a2a.md).

## What is published

- **Skills.** You list the workflows to publish, each with a skill id, a name, a description and the workflow type. The text of a message is the workflow's input. When
  more than one skill is published, the message names one in its metadata (`skillId`).
- **The Agent Card**, at `/.well-known/agent-card.json`, is public so that callers can discover the agent. It states the endpoint, the skills, and (if you declare them) the
  security schemes callers must use. Streaming and push notifications are not supported, and the card says so.
- **The A2A endpoint**, at `/a2a` unless you choose another path, which must match the address in the card. Requests to it get `401` unless the connection is authenticated and
  resolves to a caller.
- **Runs as tasks.** A task reports the run's own state, so tasks survive restarts. Follow-up messages on a task that waits for input answer its approval with a data part such as
  `{"decision":"approve"}` or `{"decision":"reject"}`, plus an `approvalId` when more than one is open.
- **Operations.** Send a message, get a task, list tasks, cancel a task.

## Signed Agent Cards

The server will not start without a card signer. The card is signed with ECDSA P-256 (`ES256`) as the A2A specification describes, over the card's canonical JSON, so a caller
that holds your public key can tell that the card is yours and unchanged. Callers using the Kairos client require a signed card by default. Serving an unsigned card is an
explicit option for development or for endpoints whose callers do not verify cards. A valid signature says who published the card. It does not say the agent is safe.

## How callers are identified

The caller is read from the authenticated user's claims, the same as for [MCP](mcp-server.md#how-callers-are-identified): `kairos_tenant`, `kairos_namespace`, `kairos_role`, `sub`,
and `role` claims for business roles. Anything missing means no caller. A fixed caller can be configured when there is exactly one legitimate caller; it still insists that the
connection is authenticated.

- **Runs belong to a caller.** The workflow id is made from the skill, the caller and the message id. A resent message from the same caller is the same run, and the same message
  id from another caller is a different run, so a caller cannot read or take over another's task by choosing its message id.
- **The approver is the caller.** A message only says approve or reject. The approver recorded is the authenticated caller, with the business roles the identity provider vouches for.
- **Messages without a message id are refused.**

## Authorization

| Operation | Needs |
|---|---|
| Send a message that starts a run | A role that may start runs: `Operator` or `Admin`. |
| Answer an approval | A role that may decide approvals: `Approver`. |
| Get or list tasks | A role that may read runs: `Reader`, `Approver`, `Operator` or `Admin`. |
| Cancel a task | A role that may cancel runs: `Operator` or `Admin`. |

The caller acts only in the server's own tenant and namespace. A refusal says what the caller may not do. A caller who is not authenticated gets `401`.

## Limits

- `tasks/list` reads a catalog of tasks created through this server. The default catalog is in memory, keeps at most 10,000 tasks, forgets on restart and logs a warning outside a
  development environment. Supply a catalog backed by storage you keep.
- A page of `tasks/list` holds at most 100 tasks (50 by default).
- Streaming, push notifications and extended cards are not available.
- The endpoint's protection is whatever authentication you put in front of it. The library does not rate limit.
- The A2A SDK it builds on is a prerelease, so this package is too.
