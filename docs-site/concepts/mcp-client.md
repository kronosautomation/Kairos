# Calling MCP tools from an agent

An agent often needs tools that live behind a Model Context Protocol server. `Kairos.Agents.McpClient` turns each call into a durable step:
`ctx.CallMcpToolAsync(server, tool, arguments)`.

## Setting up

[!code-csharp[](../../samples/AgentSamples/McpExamples.cs#mcp-client-setup)]

Register `runtime.Activities` on every worker that runs the workflow (in embedded mode, `AddActivities`). You own the connection: create the
SDK's `McpClient` over stdio or HTTP and hand it over, or give a function that connects per run (for example to a different server per
tenant).

## Calling

[!code-csharp[](../../samples/AgentSamples/McpExamples.cs#mcp-client-call)]

- **Recorded once.** The result goes into the history. When the workflow is replayed, the recorded result is returned and the server is not
  called again.
- **Idempotent.** Every attempt of one step carries the same idempotency key (run and step number) in the request metadata
  (`kairos/idempotencyKey`). A server that honours it does the work once even if the answer was lost and the step ran again. A server that
  does not may repeat the work after a crash in that one window, the same limit as any other [activity](retries.md).
- **A tool that reports an error is a result.** `IsError` is set and the agent reads the text; nothing is thrown. Network errors and timeouts
  are retried (three attempts by default, `McpCallOptions.Retry`). An unregistered server name fails at once.

## Long calls

[!code-csharp[](../../samples/AgentSamples/McpExamples.cs#mcp-client-long)]

When the server supports tasks, the call runs as a remote task. The remote task id is recorded, and the step then polls with
[durable timers](timers-and-signals.md), so the wait costs no thread and a restart resumes polling instead of starting the call again.
Polls follow the interval the server suggests, kept between `MinimumPollInterval` and `MaximumPollInterval`. When `Timeout` (workflow time)
passes, the remote task is cancelled and the call fails with `TimeoutException`. A remote task that fails, is cancelled by someone else, or
asks for input an agent cannot answer ends the call with `McpToolFailedException`, `McpToolCancelledException` or
`McpInputRequiredException`.

## Safety

A remote tool is code you do not control, so the baseline from [Tool policy and credentials](tool-policy-and-credentials.md) applies:

- **Only tools you declare read-only run without asking.** `ReadOnlyTools` is your statement; the server's own "read only" hint is not
  believed. Every other tool waits for an [approval](approvals.md), then runs with the approval attached. A rejected or expired approval
  throws `ApprovalRejectedException`, which rolls a [saga](sagas.md) back.
- **Policy decides first.** A tool your policy denies is refused with `McpToolDeniedException` and never reaches the server. Tools are named
  `server.tool` in policy rules, and `UsePolicy` takes the same policy object you gave the agent runtime.
- **Redaction** (`UseRedactor`) applies to recorded results and errors.
- Whatever credentials the connection uses belong to the connection (the transport), not to the agent. Give it only what the declared tools need.
