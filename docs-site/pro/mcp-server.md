# Publishing agents and workflows as an MCP server

`Kairos.Agents.Mcp` lets MCP clients, such as an assistant or another agent, start your Kairos workflows as tools, follow their runs as tasks, and answer approvals.
It decides who is calling from the authenticated connection, never from what a message says, and applies the same role rules as the rest of Kairos. It is a
library for your own ASP.NET Core host. The [standalone server](standalone-server.md) does not serve MCP. This page covers what is exposed, how callers are
identified, what each role may do and the limits. It is a commercial feature.

To call MCP tools from an agent, see [Calling MCP tools from an agent](../concepts/mcp-client.md). The open-source task store that keeps MCP tasks across restarts
is described in [Durable tasks for MCP servers](../concepts/mcp-tasks.md).

## What is exposed

**Workflows as tools.** You register each workflow you want to offer with a tool name, a description, a JSON Schema for the arguments and the workflow type. A call to
that tool starts a run with the arguments as its input.

- A client that supports the MCP tasks extension gets the run as the task. Any other client gets the run id.
- A client can send an idempotency key in the call's `_meta` (`kairos/idempotencyKey`). A retried call with the same key returns the same run.

**Operator tools.** These seven are always the same:

| Tool | What it does |
|---|---|
| `start_workflow` | Starts one of the registered workflows by type. |
| `get_run` | Reports a run's state and the types and times of its events. Never payloads. |
| `list_runs` | Lists runs started through this server, newest first (1 to 200, 50 by default). |
| `signal_run` | Sends a signal. A signal cannot carry an approval decision. |
| `approve`, `reject` | Decide a pending approval as the authenticated caller. |
| `cancel_run` | Asks a run to stop and roll back. |

A workflow tool may not use one of these names. Payloads are opaque to the server, so no tool returns payload plaintext.

**Approvals as questions.** With the task store options the library provides, a run's pending approvals appear to the client as input requests, one per approval. Accepting
approves and declining rejects. Cancelling the question leaves the approval open.

## How callers are identified

The MCP endpoint trusts the authenticated user of the connection and nothing else. The library reads the caller from these claims, and anything missing or malformed means no
caller:

| Claim | Meaning |
|---|---|
| `kairos_tenant`, `kairos_namespace` | The tenant and namespace the caller acts in. |
| `kairos_role` | A Kairos role name. |
| `sub` | The caller's identity, recorded as the approver of any approval they decide. |
| `role` | Business roles, such as `finance`, checked against an approval's required roles. |

Whatever issues these claims (your identity provider or the authentication in front of the endpoint) is the trust boundary, so configure it so that a client cannot choose them.
For a single-user stdio server, a fixed caller can be given, and the connection must still be authenticated. Nothing in a tool argument or an answer is read for identity: a
forged approver in a signal or an answer is ignored or refused, and tests cover that.

## Authorization

The caller's role in the server's own tenant and namespace decides, using the same rules as the [standalone server](standalone-server.md#roles).

| Role | Sees and may use |
|---|---|
| `Operator`, `Admin` | The workflow tools, `start_workflow`, `get_run`, `list_runs`, `signal_run`, `cancel_run`. |
| `Approver` | `get_run`, `list_runs`, `approve`, `reject`. |
| `Reader` | `get_run`, `list_runs`. |
| `Worker`, no authentication, or another namespace | Nothing. |

`tools/list` shows a caller only the tools its role allows, and `tools/call` checks again, so naming a hidden tool is still refused. An approval decision is signed with the
key the workers verify with, so a worker can prove who decided. A caller without the approval's required business role is recorded and **not accepted**.

## Limits

- `list_runs` lists runs started through this server in this process. The default list is kept in memory, is lost on restart and holds only runs this process started. Supply
  an index of your own for anything else.
- Cancelling asks the run to stop. It stops where it waits in a step that can be cancelled.
- The MCP SDK it builds on marks the tasks feature as experimental. Pin the SDK version and test before you upgrade it.
- Rate limiting is not provided here. Put it in front of the endpoint.

## Enterprise-managed sign-in

The library includes a validator for identity assertion grants, which lets a person sign in once at the company identity provider and use an MCP client without a second
login. It checks the trusted issuer, the token type, that the audience is exactly this authorization server, the client, the resource, the subject and token id, a lifetime
of at most five minutes, and replay. Any doubt refuses. It is a building block for your own authorization server: **the token endpoint is not provided**, and neither is key
retrieval.
