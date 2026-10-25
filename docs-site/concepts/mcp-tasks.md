# Durable tasks for MCP servers

The Model Context Protocol lets a client start a long tool call as a **task** and ask about it later. The C# SDK keeps those tasks in an
`IMcpTaskStore`; its built-in store lives in memory, so a restart forgets every task. `Kairos.Mcp.TaskStore` is an `IMcpTaskStore` whose
tasks are Kairos runs, so they survive restarts, deployments and moving between servers. It works with any MCP server built on the SDK; the
tools do not have to be Kairos workflows.

## Plugging it in

[!code-csharp[](../../samples/AgentSamples/McpExamples.cs#task-store)]

[!code-csharp[](../../samples/AgentSamples/McpExamples.cs#task-workflow)]

The store reads and writes through a `KairosClient`, so it needs a Kairos database. The workflow registration is needed on whichever
Kairos workers you run; it is what closes the run behind each task when the task ends.

## What a task is

| MCP | In Kairos |
|---|---|
| a new task | a run of `McpTaskWorkflow`; every update your server makes is recorded in its history |
| `working` | the run is open and nothing is outstanding |
| `input_required` | the server asked for input and no answer has arrived; the requests and the answers are in the history |
| `completed` / `failed` | the server reported a result or an error; the first ending wins and later ones are ignored |
| `cancelled` | the task was cancelled before it ended |

A **task id** names one run in one tenant and namespace. An id from another namespace is indistinguishable from an id that does not exist.
Give the store an `IdKey` (32 bytes or more) and ids also carry a signature, so a guessed or altered id is refused before any lookup.
With [payload encryption](history.md) on, results, errors and input requests are sealed like any other payload.

The store behaves like the SDK's reference store in every case the protocol defines (unknown ids read as nothing, a second ending is
ignored, answering a request that was never made changes nothing); the same tests run against both.

## Tasks that are Kairos workflows

When the tool behind a task is itself a workflow, report the run as the task. Its status, result and failure then come from the run
itself, and the run's own durability does the rest:

[!code-csharp[](../../samples/AgentSamples/McpExamples.cs#task-attach)]

- `working` is a run that is open; `completed` carries the run's result (reported as the JSON it is, or as text content); `failed` carries
  the error type and message.
- **Cancelling** asks the run to stop and roll back its [saga](sagas.md); the task reports `cancelled` once the run has ended that way.
  The run notices at its next cancel-aware step, such as an approval wait.
- A run that continues as new stays the same task.
- To report `input_required` for a run (for example its pending [approvals](approvals.md)) supply an `IRunInputProjection`, and to let a
  client answer, an `IRunInputResponder`. The responder must decide who answered from the **authenticated caller** and never from the
  response, so a client cannot name its own approver.

## What it does and does not make durable

**The state of a task is durable. The work behind a plain task is not.** The SDK runs a task's work in the server process, so if that
process stops, the task is still there but nothing is left to finish it. Set `AbandonedAfter` to record such a task as failed once it has
been silent that long, and let the client start again. A tool that is a Kairos workflow does not have this problem: its run continues on
any worker.

- A task's retention follows the run's, not the `TimeToLive` the store reports.
- Reading a task replays its history; very chatty tasks are best kept to a few updates.
