# Kairos.Mcp.TaskStore and Kairos.Agents.McpClient: design decisions and open items (Milestone 5)

Written 25 Oct 2026 with the implementation. Internal: decisions, trade-offs, what is open. Not for `docs-site/`. The task-store and client
parts of the original MCP design note moved here from `Kairos.Commercial/docs/MCP-DESIGN.md`; that file keeps the server parts.

Placement: `docs/REPO-LAYOUT.md` (both packages Apache, the Kairos MCP server commercial).

## SDK facts this was built against (ModelContextProtocol 2.3.0, Extensions.Tasks 2.3.0, read from the packages and by running them)

- `IMcpTaskStore`: `CreateTaskAsync`, `GetTaskAsync`, `SetCompletedAsync`, `SetFailedAsync`, `SetCancelledAsync` (returns bool),
  `SetInputRequestsAsync`, `ResolveInputRequestsAsync`, event `InputResponseReceived`. No list operation.
- Reference `InMemoryMcpTaskStore` semantics (probed): unknown id reads as null; Set* on an unknown id throws `InvalidOperationException`
  (`SetCancelled` returns false); a second ending is ignored; `SetCancelled` on an ended task is false; `SetInputRequests` after the end is
  ignored; `ResolveInputRequests` raises the event for every key, including unknown ones, and removes only known ones; status returns to
  Working when no request is left. The same contract tests run against both stores.
- `WithTasks(IMcpServerBuilder, IMcpTaskStore)` takes an instance, not a factory. The SDK creates the task, runs the tool **in the server
  process** in the background, and writes the outcome to the store.
- Client: `CallToolAsTaskAsync` returns `ResultOrCreatedTask<CallToolResult>` (a result or a created task); `GetTaskAsync` returns a status
  subclass (`Working`, `Completed` with `Result`, `Failed` with `Error`, `Cancelled`, `InputRequired` with `InputRequests`); `RequestParams.Meta`
  is a `JsonObject` (used for the idempotency key).

## Decisions

1. **Two kinds of task.** Plain tasks (`CreateTaskAsync`) are runs of `kairos.mcp.task`, a workflow that only records updates and ends with
   the task. Attached tasks (`AttachRunAsync`) report an existing workflow run. The kind is read from the first history event, so nothing
   else needs storing. Attached tasks cannot be completed or failed from outside (the workflow decides); cancel asks the saga to roll back.
2. **State is derived by folding the history on every read.** No projection table, no second source of truth, nothing to repair after a
   crash. Cost: a read replays the task's updates. Fine for tasks with a handful of updates; revisit with a snapshot if tasks get chatty.
3. **The first ending wins.** Terminal updates are signals; the fold stops at the first. This matches the reference store and makes
   concurrent endings harmless. `SetCancelled` reports false when the fold is already terminal; a cancel racing a completion in the same
   instant can report true and lose, which the reference store avoids only because it is single-process.
4. **Ids** are `kmt1_` plus the run key (48 bytes), with an optional HMAC. The store is constructed for one tenant and namespace and
   treats any id from elsewhere as unknown; a leak test pins it. The HMAC is optional because the run key is already unguessable enough
   behind authentication, but it removes a class of enumeration attempts at no cost.
5. **Who answers is never taken from the answer.** For attached runs, `IRunInputResponder.RespondAsync` is told the run and the request key
   and nothing about the responder's claimed identity; the implementation reads the authenticated principal. This is the review A1 rule
   applied to MCP. The approvals projection and responder themselves belong to the commercial server.
6. **Abandoned tasks.** Because the SDK runs the work in the server process, a plain task whose server died stays `working` forever. The
   opt-in `AbandonedAfter` records a failure (`TaskAbandoned`) when the task has been silent too long, as a recorded update so a late
   completion cannot flip the answer. A read can therefore write. Default off: a silent but healthy long tool must not be failed by a guess.
7. **Client steps keep the policy in the call activity** (as the tool step does): the decision is recorded with the step. A remote tool is
   side-effecting unless the application declares it read-only; the server's own hint is not trusted. Approval is a second call carrying the
   approval, exactly as for local tools.
8. **Long remote calls are two kinds of step:** the call (returns a result or a remote task id, which lands in history) and polls separated
   by durable timers. A restart therefore resumes polling and never starts the remote call again. Timeout is in workflow time and cancels the
   remote task best-effort.
9. **Idempotency key in `_meta` (`kairos/idempotencyKey`)** is our convention, not part of the protocol. A server that ignores it can repeat
   the work after a crash between its answer and our record (same window as any activity). Documented.

## Open items and risks

- **No `tasks/list`** in the SDK's store contract. A server that needs listing keeps its own index.
- **The work behind a plain task is not durable** (SDK design). Only the commercial server, whose tools are workflows, closes that gap.
- **Cancelling a remote task when the local saga is cancelled** is not done: only the timeout path cancels remotely. A cancelled agent run
  leaves its remote task running until it ends. Needs a hook in the saga cancel path.
- **`input_required` from a remote server** ends the call (`McpInputRequiredException`); an agent cannot answer elicitation. A handler that
  routes it to a person (an approval) is a natural next step.
- **Connection lifetime** is the caller's: the runtime asks for an endpoint per step. A connection pool/refresh policy is not provided.
- **Read amplification** in `GetTaskAsync` (decision 2) and no TTL-based cleanup: retention is the run's.
- **AOT:** both packages use source-generated JSON for their own types and the SDK's `McpJsonUtilities` for protocol types; not yet built
  with the AOT analyzers enabled against the full SDK surface.
- **Conformance:** the 2026-07-28 protocol conformance suite is not run against a Kairos-backed server yet; the SDK round trips in the
  tests are ours.
- Disclosure review (Aaron) is needed for `mcp-tasks.md` and `mcp-client.md` before publishing.
