# Concepts

Read these in order the first time; each one builds on the one before.

| Page | In one line |
|---|---|
| [Workflows and activities](workflows-and-activities.md) | Decisions go in workflows, side effects go in activities. |
| [Durable execution and replay](durable-execution.md) | How a method survives a crash, and what is guaranteed. |
| [Determinism](determinism.md) | What workflow code must not do, and what to use instead. |
| [History and integrity](history.md) | The recorded events, the hash chain, and how large a history may grow. |
| [Timers and signals](timers-and-signals.md) | Waiting for time, and for people and other systems. |
| [Retries and idempotency](retries.md) | Failure handling per activity, and making side effects safe to repeat. |
| [Leases and heartbeats](leases-and-heartbeats.md) | How work is handed to workers, long-running activities, and runs that get stuck. |
| [Versioning](versioning.md) | Changing workflow code while runs are in flight. |
| [Long-running workflows](continue-as-new.md) | Keeping a workflow that runs for months bounded. |
| [AI agents](agents.md) | Model calls and tool calls as recorded steps that are never repeated on replay. |
| [Tool policy and credentials](tool-policy-and-credentials.md) | Read-only by default, approval for changes, and short-lived credentials limited to one run and one tool. |
| [Approvals](approvals.md) | Waiting durably for a named person, and recording who decided. |
| [Verifying audit evidence](audit-evidence.md) | Checking a signed bundle of audit evidence offline, and history files against its recorded chain heads. |
| [Sagas and compensation](sagas.md) | Undoing earlier steps in reverse when a later one fails, surviving crashes mid-rollback. |
| [Testing agents](testing-agents.md) | Recorded model fixtures, scripted models and what to assert. |
| [Durable tasks for MCP servers](mcp-tasks.md) | A task store for any C# MCP server whose tasks survive restarts, backed by Kairos runs. |
| [Calling MCP tools from an agent](mcp-client.md) | MCP tool calls as recorded, idempotent steps; long calls polled across restarts. |
| [Calling other agents with A2A](a2a.md) | Durable A2A calls, signed Agent Cards, and a Kairos run reported as an A2A task. |
| [Agent Framework agents, made durable](agent-framework.md) | Your Microsoft Agent Framework agent with recorded steps, approvals that wait, and the same policy and budgets as native agents. |
| [Agent Framework workflows, made durable](agent-framework-workflows.md) | Your Microsoft Agent Framework workflow with recorded supersteps, requests that wait for a person holding nothing, and cancel and terminate. |
| [Verifying a release](verify-releases.md) | Signatures, SBOMs, build provenance and how to rebuild a release. |
