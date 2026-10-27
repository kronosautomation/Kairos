# Kairos-backed checkpoints for Agent Framework workflows (Milestone 8, step 2) — design

Written 26 Oct 2026. Internal. Nothing here is built; section 11 lists the decisions I need before building. Follows `docs/AGENT-FRAMEWORK-DESIGN.md` (section 5.6 is the short version of this) and the follow-ups recorded in
`docs/reviews/2026-10-26-af-*.md`. Placement: the same package, `Kairos.Agents.AgentFramework` (Apache), as decided for step 1.

## 1. What is being asked

Microsoft Agent Framework has a second half besides agents: **workflows**, a graph of executors that run in *supersteps* (every executor with a message to handle runs; their output messages are the next superstep's input), with an
optional `CheckpointManager` that writes a checkpoint at the end of each superstep and can resume from one. Step 1 made *agents* durable. Step 2 asks: can Kairos be where the checkpoints live, so that a workflow survives a crash or a redeploy,
waits for a person without holding compute, and is recorded, encrypted, audited and cancellable like everything else in Kairos?

## 2. What I measured (pinned `Microsoft.Agents.AI.Workflows` 1.24.0; throw-away programs, not in the repository)

| # | Question | Result |
|---|---|---|
| X1 | What does a run do, and when are checkpoints written? | `InProcessExecution.Lockstep.WithCheckpointing(manager).RunAsync` runs **every superstep until the graph is idle** in one call and writes one checkpoint per superstep, each naming its parent. A chain A→B→C gave `cp1` (after A), `cp2` (after B, with C's input queued), `cp3`. |
| X2 | Can I run exactly one superstep per call? | **Yes, with the streaming run in lockstep mode.** `Lockstep.RunStreamingAsync` yields events superstep by superstep and does not start the next one until the consumer pulls: breaking after `SuperStepCompletedEvent` left `A` run and one checkpoint written (`B` and `C` had not run), and disposing the run did not run more. This relies on the pull behaviour of the stream, which is not documented; step 2 would pin it with a contract test, as step 1 does for the session shape. |
| X3 | Does a checkpoint restore into a new process? | **Yes.** A new `Workflow` object (built by the same code) and a new `CheckpointManager` over a store that holds the checkpoints resumed from `cp1` and ran `B`, `C` only, with the same output. |
| X4 | What does a failing executor do? | **It does not throw.** The run goes `Idle` with `ExecutorFailedEvent` and `WorkflowErrorEvent` in its events; the checkpoint written before the failing superstep is the last one. The step has to turn those events into an activity failure so that Kairos retries the superstep. |
| X5 | External requests (a person decides) | A `RequestPort` bound as an executor makes the run stop with `RunStatus.PendingRequests` and a `RequestInfoEvent`; the last checkpoint holds the outstanding request. A **new process** restored from that checkpoint, was handed the response with `SendResponseAsync(request.CreateResponse(...))`, and finished. The request id is stable across the restore. (`WatchStreamAsync` must be called with `blockOnPendingRequest: false`, or the stream waits for the response and never ends.) |
| X6 | What does one superstep per call cost? | 6 ms per call on a 30-executor chain, all of it restoring and re-binding; 0.8 ms per superstep when the same 30 supersteps run in one call. Immaterial next to an activity's own cost. |
| X7 | What is in a checkpoint? | Six sections: the step number, **the whole workflow structure** (executors with their .NET type names, edges, request ports), the runner data (instantiated executors, the queued messages for the next superstep, outstanding requests), executor state, edge state and the parent. 1.2 KB for three executors, 7.5 KB for thirty; most of that is the structure, which is the same in every checkpoint. |

What this means:

- A superstep is a real unit that Agent Framework itself can stop at and resume from. It is the unit Kairos can record.
- **Nothing inside a superstep is individually recorded.** A superstep runs inside one activity; if the worker dies in the middle, the next attempt runs the *whole superstep again* from the previous checkpoint. Step 1's promise ("a recorded step is never run
  again; at most one extra execution of the step that was running") becomes "a recorded **superstep** is never run again; at most one extra execution of the superstep that was running", and a superstep may contain several executors running in parallel and several model and tool calls.
- The checkpoints already contain everything: the graph state, the messages in flight, the pending requests. Kairos needs no model of the graph.

## 3. The contract the documentation will have to state

1. A workflow's progress is recorded superstep by superstep. A crash costs, at most, the superstep that was running.
2. **Executors must be safe to run twice**, or must reach the world through something that is (a durable agent, a tool called with an idempotency key). In a superstep with several executors, the ones that had finished are run again too.
3. Executor ids must be stable (explicit ids, not generated) and a registered workflow is pinned by the hash of its structure, as agents are by their definition.
4. External requests wait without holding compute; they are answered through Kairos (approvals or signals).
5. A model or tool call inside an executor is **not** a separate recorded step unless it goes through a durable agent (section 6).

## 4. The shape

```
caller ──start/respond/cancel──▶  Kairos run  "kairos.af.workflow/<name>"      (workflow code: ours, deterministic)
                                     │  loop
                                     ├─▶ activity  kairos.af.workflow.step     (restores a checkpoint, runs ONE superstep, returns the new checkpoint,
                                     │                                          the workflow's outputs, and any requests it is now waiting for)
                                     ├─▶ requests  → Kairos approvals / signals (no compute while they wait)
                                     └─▶ done  → the run ends with the workflow's outputs
```

- **One activity per superstep** (X2). The activity builds the registered `Workflow` (a factory the application gives, called once per activity: executors are not shared between steps), makes a `CheckpointManager` over an in-memory store holding only the checkpoint it was
  given, resumes from it (or starts, for the first step), runs until `SuperStepCompletedEvent`, `PendingRequests` or idle, and returns the new checkpoint. The in-memory store is a *carrier*: Kairos history is the store.
- **The checkpoint travels like the session did in step 1.** Same references, same changes: a step names the checkpoint that an earlier step recorded by hash; a result carries the new one whole every N steps and otherwise as a change against the one before
  (`SnapshotCodec`, `SnapshotCache`, `SessionChain` are written for any JSON). The static part of a checkpoint (X7) is the first thing a change leaves out, so the cost of a superstep in history is the messages that moved and the state that changed.
- **Requests.** `RunStatus.PendingRequests` returns the `ExternalRequest`s. The Kairos workflow turns each into an approval (when the registration says it is one: a port whose request type is an approval, or a `Describe` hook that returns the text and the roles) or a
  signal `af.workflow.response/<requestId>` (anything else), waits with no compute, and delivers the answer in the next step's input: `SendResponseAsync(request.CreateResponse(answer))` on the restored run. A request restored from a checkpoint is announced again; the step
  de-duplicates by request id.
- **Cancel and terminate** are the saga of step 1: a cancel stops the run at the next superstep boundary (a superstep in flight finishes and is recorded), waiting requests end at once, and the durable agents the workflow started are cancelled with it.
- **The handle** is `DurableWorkflows.Get("name")` with `StartAsync(input)`, `WaitAsync` (the cheap wait of step 1: only what the run wrote since the last look), `Respond`, `Cancel`, `Terminate`, and `Outputs`/`Pending` read from history.

## 5. Why not the alternatives

| Alternative | Why not |
|---|---|
| **Run to idle in one activity** (design 5.6 as first written) | Simpler and cheaper in events, but a crash re-runs everything since the last request, however many supersteps that was. X2 shows that one superstep per activity is possible at a cost that does not matter. Kept as an option (`SuperstepsPerStep`, default 1) if someone's workflow has thousands of tiny supersteps. |
| **Only the store**: a public `KairosCheckpointStore : JsonCheckpointStore`, with the application running its own workflow and resuming it | Section 11, question 1. Much smaller, and useful to people who already run workflows in their own process, but then Kairos neither resumes nor schedules anything: there is no run, no waiting without compute, no approvals, no cancel. |
| **Run the workflow as an agent** (`workflow.AsAIAgent()`) and register that agent turn-level (`AllowTurnLevel()`) | Works today with the step 1 contract (a recorded turn is never run again; a crash re-runs the whole turn), and is the right answer for short workflows. It gives no superstep recording and a long workflow with a person in the middle holds an activity for as long as the person takes. Documented as the alternative; not a substitute. (Not yet verified; I will check before the documentation says it.) |
| **Rebuild the graph as a Kairos workflow** | Executors are arbitrary code and Agent Framework owns the routing, fan-out and fan-in semantics. A reimplementation would be a different product, and it would drift. |

## 6. Agents inside executors (the part with a real choice)

The common executor is an agent: `AIAgent` as an executor, or an executor that calls `agent.RunAsync`. Three ways to treat it:

- **A. As is.** The agent runs inside the superstep's activity. A `ChatClientAgent` is called normally; a `DurableAIAgent` (step 1) starts or continues a Kairos conversation and the activity waits for its answer. A crash re-runs the superstep, and the durable agent
  call is then *answered from the conversation* if the conversation can recognise it: the activity gives `DurableAIAgent` a message id derived from the step and the executor (an ambient context, like `AgentFrameworkToolContext`), so the repeated call finds the turn it already
  sent. Net: an agent call through a durable agent is exactly-once-recorded even though its superstep ran twice. The cost: **the activity is occupied while the conversation is waiting**, and if that conversation waits for a person (a refund approval) it holds a worker slot for days. Activities have no timeout in the engine and a heartbeat keeps the lease; it works, it is not free.
- **B. Suspend.** The ambient context makes `DurableAIAgent.RunAsync` throw a suspension instead of waiting when the answer is not there yet. The step catches it, returns "waiting for conversation X", the Kairos workflow waits for that conversation's answer by signal (as nested agents do), and
  re-runs the superstep, in which the call now finds its answer and returns at once. No compute while waiting, but the executor's code *before* the call runs again, which is a harder contract than "safe to run twice": it must also reach the same call with the same arguments (deterministic up to the call).
- **C. Only through requests.** Executors that need a person raise an `ExternalRequest` (a port). Agents inside executors are called as in A, and the documentation says an agent that may wait for a person should be a request, not a call.

Recommendation: **A with C's advice in the documentation, B later if the experience asks for it.** A reuses everything step 1 has and states a contract anyone can read; B changes what executors may do.

## 7. Size, waiting, cancel

All three reuse step 1's machinery unchanged: references and changes for the checkpoint, the wait and cursor reads for the handle, the saga and the watch for cancellation (a durable agent started from an executor is a nested run of the workflow's run, so a cancelled workflow cancels it, and a terminated one is found by the watch).
The same measurements will be taken for a workflow of a hundred supersteps with a chat history in its state, and reported.

## 8. Security (SECURITY-ARCHITECTURE.md is binding)

- The server never sees a checkpoint in clear: it is a step input/result like any other, sealed by the client's payload protector. The server does not deserialize customer types; the checkpoint's .NET type names are inside the sealed payload.
- No new table. The store is Kairos history, so row-level security, the hash chain, audit and retention apply unchanged; the leak tests for history cover it. (The public-store variant of question 1 would add a run type, not a table.)
- Checkpoints deserialize **customer types in the worker** (that is what Agent Framework's `JsonSerializerOptions` is for). The registration takes the options; with a source-generated context the Server-side AOT rule holds because workers are customer-hosted.
- Redaction applies to the text in a checkpoint the way it does to a session: a walker over the checkpoint JSON that refuses shapes it does not know, with a registration to declare executor state free of text. The checkpoint is Agent Framework's JSON with the *customer's* types inside it, so a walker can only be as complete as the types it is told about: a registered workflow with a redactor
  must list its state types or declare them free of text (question 5).

## 9. How it will be proved

- **Crash at every store operation** of a workflow with a fan-out, a person's decision in the middle, and a durable agent in an executor: the outcome equals the fault-free one, every superstep recorded once, every request answered once, the agent called once in the world.
- A reference run per shape in a **corpus** (v6): plain, with a request, restored by another worker, with a durable agent, cancelled; replayed by every build.
- **Contract tests** that pin what we rely on in 1.24.0: the pull behaviour of the streaming run (X2), the content of a checkpoint, that a failure arrives as events, that a request survives a restore with its id, that the workflow section is the same for the same graph.
- Executors run twice on a crash: a test counts the executions per superstep across the crash matrix and asserts "at most one extra".
- Mutation checks of the new code, the size and wait measurements, an authorization matrix entry for the new run type, and a review note.

## 10. Risks

- **`Microsoft.Agents.AI.Workflows` is young** and the pinned version is the only one tested. The checkpoint format is Agent Framework's: an upgrade can change it, so a run in flight across an upgrade needs the same pin-and-approve mechanism agents have (the structure hash plus the package version).
- The X2 behaviour is an implementation detail of the lockstep stream; if a later version runs ahead, the fallback is run-to-idle per activity (section 5), with its weaker recovery.
- Executors with side effects that are not idempotent will run twice after a crash. We can state it; we cannot stop it.
- A very wide superstep (hundreds of parallel executors) is one activity; its crash cost is the whole superstep.

## 11. Decisions

**Decided 27 Oct 2026** (the questions are kept below as they were asked; this is the answer to each and what it became in the build; the review note is `docs/reviews/2026-10-27-af-workflows.md`).

| # | Decision | In the build |
|---|---|---|
| 1 | (a) Kairos runs the workflow. A public checkpoint store only if someone asks. | `DurableWorkflows` / `DurableWorkflow` / `DurableWorkflowRun`; no public `JsonCheckpointStore`. |
| 2 | A setting, `SuperstepsPerStep`, default 1. | `AgentFrameworkOptions.SuperstepsPerStep`; carried in the run's input, like the other per-run settings. |
| 3 | A, with a limit: a wait inside a superstep has a configurable maximum (default 5 minutes), past it the step fails with an error naming the conversation and saying to use a request port; a metric for activities waiting on a conversation; C documented as the right pattern; B later. | `WorkflowAgentWait`, `WorkflowAgentWaitException` (not retried), `kairos.af.workflow.conversation_waits`; the wait loop also heartbeats the step. |
| 4 | Approval when the port says so, signal otherwise; the default `Describe` shows the port name and the redacted request. | `RequestsAsApprovals(port, requirement, describe)`; the port's response type must be `bool` (a registration error otherwise). Signals: `af.workflow.response/<requestId>`, `DurableWorkflowRun.RespondAsync`. |
| 5 | A registration error: with a redactor, unknown state types fail at startup naming the type and the option, the same fail-safe rule as step 1. | `AllowStateType<T>()`, `RedactState<T>(...)`. Startup sees the protocol (executor input/output types, ports, outputs); a type that only appears in executor *state* cannot be seen at startup and is refused when the checkpoint that holds it is recorded (`SnapshotShapeException`, with the same words). |
| 6 | The factory per step. | `AddWorkflow<TInput>(name, Func<WorkflowBuildContext, Workflow>, configure)`; registration builds it twice and refuses it when the two builds differ. |
| 7 | Include terminate (audited with the verified principal); leave out output streaming. | `TerminateAsync(principal, reason)` (the conversations are asked to stop first); outputs are in the view, not streamed. |
| 8 | Accept the pin; continuing after a structure-hash or version change needs an approval, audited with the verified principal. | `OnStructureChange(requirement)`, `WorkflowOverrideAuditRecord`. **What the build found:** Agent Framework restores a checkpoint only into a workflow with the same executors, edges and ports (X8 below), so a *structure* change can never be approved into a working run. The step tries the restore first: if the framework refuses, the run ends with `WorkflowIncompatibleException` and the reason, and nobody is asked to approve the impossible. The approval is real for what the framework accepts: a different package version (a newer checkpoint format). |
| 9 | Include hand-over. | The checkpoint travels whole once, the outputs so far and the open signal requests go with it. **A hand-over waits for open approvals to be decided** (an approval belongs to the history of the run that asked it). |

Also decided: `FullSnapshotEvery` default 16.

Experiment X8 (27 Oct, while building decision 8): a checkpoint written by one graph was restored into (a) the same graph: works; (b) a graph with one executor's type changed: `InvalidDataException: The specified checkpoint is not compatible with the workflow associated with this runner`; (c) a graph with an added executor and edges: the same exception; (d) an added unreachable executor: refused by the builder itself.

### The questions as asked

1. **What is wanted: orchestration or storage?** (a) Kairos runs the workflow: a Kairos run, an activity per superstep, requests as approvals, resumption on any worker (this document). (b) Only a `KairosCheckpointStore` that an application plugs into its own `CheckpointManager`: much smaller, no recovery or waiting. (c) (a), with (b) falling out as a public type later. I recommend (a), and (b) only if someone asks for it separately.
2. **Granularity.** One superstep per activity (recommended) or run to idle per activity, or the setting with a default of 1?
3. **Agents inside executors.** A (recommended), B, or C only. If A: is it acceptable that a durable agent call that waits for a person holds the superstep's activity for as long as it takes, with the documentation saying to use a request instead?
4. **Requests.** An `ExternalRequest` becomes a Kairos approval when its port says so and a signal otherwise: is that the split you want, and who writes the approval text (a `Describe` hook with a default, as proposed)?
5. **Redaction.** With a redactor configured, a workflow must declare its state types (the walker refuses what it does not know), as agents do for session state. Accept, or is a registered workflow with a redactor and unknown state types a registration error?
6. **Where the workflow comes from.** A factory delegate in the registration (`AddWorkflow("name", () => workflow)`), called once per step. A factory per step costs a build of the graph per superstep (X6: ms). Accept?
7. **Public surface.** `DurableWorkflows`, `DurableWorkflowRun` (start, wait, respond, cancel, terminate, outputs). Anything you would rather it not offer in the first version (terminate? outputs streaming)?
8. **Version pin.** The checkpoint format is the framework's. Pin to 1.24.0 as step 1 does and require an approval to continue a run after the structure hash or the package version changes?
9. **Scope of the first cut.** Include the hand-over (continue-as-new) of a long workflow's run, or leave it out as step 1 left out streaming? A workflow of thousands of supersteps needs it; most do not.

## 12. Plan once decided

Design review (this document) → wire types and the contract tests → the step activity and the workflow → requests → handle → cancel → corpus and crash matrix → measurements → mutation → docs page and sample → review note. About the size of step 1's agent loop; the
checkpoint codec, cache, chain, cursor reads, waits and saga are reused as they are.
