# Agent Framework workflows on Kairos (Milestone 8, step 2)

Written 27 Oct 2026. Internal. Design: `docs/AGENT-FRAMEWORK-WORKFLOWS-DESIGN.md` (section 11 has the decisions of 27 Oct and what the build made of them). Code: `src/Kairos.Agents.AgentFramework/{WorkflowRegistration,WorkflowCheckpoint,WorkflowStepActivities,WorkflowDriver,DurableWorkflows,WorkflowStepScope,WorkflowWire}.cs`
and changes to `SnapshotCodec.cs`, `SessionChain.cs`, `DurableAIAgent.cs`, `DurableAgents.cs`, the runtime, the builder and the audit. Docs: `docs-site/concepts/agent-framework-workflows.md`, sample `samples/AgentFramework/WorkflowSample.cs`.
Also in this step: `FullSnapshotEvery` now defaults to 16 (as asked; the figures are in `2026-10-26-af-session-size.md`).

## What was built

A registered Agent Framework `Workflow` runs as a Kairos run (`kairos.af.workflow/<name>`, workflow id `afw/<name>/<instance>`). The Kairos workflow is ordinary replayed code that never runs Agent Framework: **an activity runs `SuperstepsPerStep` supersteps** (default 1) by restoring the
checkpoint it is given into a workflow built by the factory, pulling the framework's lockstep stream until that many `SuperStepCompletedEvent`s, and returning the new checkpoint, the outputs and the outstanding requests. The checkpoint travels as the session did: a step names it by hash
(workers keep a byte-bounded cache, `need-snapshot` when they do not hold it) and a result records it whole every `FullSnapshotEvery` steps and as a change otherwise.

| Decision (27 Oct) | Built as |
|---|---|
| Kairos runs the workflow | `DurableWorkflows` / `DurableWorkflow` / `DurableWorkflowRun`; no public checkpoint store |
| `SuperstepsPerStep`, default 1 | an option read when a run starts and carried in its input |
| Agents in executors: A, with a limit, a metric, C documented | durable agent calls from an executor are named by the step and the content (a rerun finds its turn); `WorkflowAgentWait` (5 min) fails the step with `WorkflowAgentWaitException` (not retried; names the conversation, says to use a request port); `kairos.af.workflow.conversation_waits`; the wait also heartbeats the step |
| Requests: approval or signal | `RequestsAsApprovals(port, requirement, describe)` (response type must be `bool`); every other port by signal `af.workflow.response/<id>` through `RespondAsync`; the default text is the port and the redacted request |
| Redaction: a registration error | startup refuses protocol types the redactor does not know (`AllowStateType<T>`, `RedactState<T>`); a type that is only in executor state is refused when its checkpoint is recorded |
| Factory per step | built twice at registration; two different builds are a registration error |
| Terminate (audited), no output streaming | `TerminateAsync(principal, reason)` asks the owned conversations to stop first; outputs are in the view |
| Pin; approval to continue after a change | structure hash + package version; `OnStructureChange`; `WorkflowOverrideAuditRecord` (see "What the build found") |
| Hand-over | the checkpoint travels whole once with the outputs, the open signal requests and the owned conversations; **waits for open approvals** |

Conversations an executor starts are named by the step (`wf.<hash of step key, agent, content, occurrence>`): a rerun after a crash sends the same message id to the same session and is answered from the conversation. They are the workflow's: a cancel, a terminate (from the handle) or a failure asks them to stop
(`kairos.af.workflow.cancel`, the cancel signal with its fixed id, so asking twice is one request).

## What the build found (and decided)

1. **A graph change can never be approved into a working run.** Agent Framework restores a checkpoint only into a workflow with the same executors, edges and ports (design X8: type changed, executor and edges added: `InvalidDataException`). So the approval the decision asked for
   ("continuing after a structure-hash or version change needs an approval") is real only for what the framework *accepts*: a different package version. The step tries the restore first; if the framework refuses, the run ends with `WorkflowIncompatibleException` and the reason ("finish or cancel the runs
   in flight before deploying a change to the graph, or register the changed graph under a new name"), and **nobody is asked to approve the impossible**. The structure pin is therefore redundant with the framework's own refusal (a mutant that ignores it is equivalent), and is kept for the early, clear answer.
2. **The delta codec did not fit messages that move.** The first measurement of a workflow whose chat history is passed from executor to executor recorded 80 of 100 checkpoints whole: the history sits under a different key (the next executor's queue) in every checkpoint, so a change by position was as large
   as the checkpoint. I added a reference operation to the change format, **for workflows only** (a session's changes are written exactly as before, so v4 and v5 are untouched): `{"m": path, "c": change}` is "the value at that path of the checkpoint this change applies to, with this change applied to it" (a list that moved and grew by a
   message), with `{"w": ...}` and `{"a": ...}` for a new object or array some of whose members are such references. References are found by content (the containers of the old checkpoint of 256 bytes or more, by hash), including a list whose first n-k elements are an old list. Applying checks every path and the
   result is checked against the recorded hash, as before. Effect, below.
3. **A hand-over cannot carry an open approval** (an approval belongs to the run that asked it, and the saga cannot continue as new with compensations registered). It waits until open approvals are decided; open signal requests are waited for again by the new run. A workflow that runs for a very long time *around* a permanently open approval can therefore
   reach the engine's history limit; the answer is a signal request for anything that waits that long.
4. **Redaction cannot see executor state at startup.** The protocol shows the types that executors accept, send and yield and the ports' types; state is written under dynamic keys. A state type the redactor does not know is refused at the first checkpoint that holds it, with the same message (type and option), never skipped. The startup check covers what it can.
5. **An executor that waits for a conversation cannot be told apart from one that waits three days**; hence the limit. The wait inside the activity renews the step's lease each time round (production workers also heartbeat by themselves).
6. A failing executor arrives as events, not an exception (X4). The step turns them into `WorkflowExecutorFailedException` carrying the executor id and the **type** of the exception, never its message (history must not hold what an executor chose to say). An exception of ours (`WorkflowAgentWaitException`) is passed through, because its message is ours.
7. In Kairos namespaces `IWorkflowContext` means Kairos' own; an executor's context is Agent Framework's, so the docs and the sample alias it.

## The figures

100 supersteps, a chat history that grows by a reply of about 3 KB per executor (largest checkpoint about 390 KB), history sealed or not (sizes are of the payloads), `MaxCheckpointBytes` raised:

| | before the reference operation | after |
|---|---|---|
| history in the messages passed on, `FullSnapshotEvery` 16, 1 superstep per step | 28,725,653 B (80 of 100 steps whole) | **2,699,127 B (7 whole, 93 changes)**, 27 KB per step |
| the same, in executor state (a scope every executor shares) | not measured | 2,715,380 B (7 whole, 93 changes) |
| every checkpoint whole (`FullSnapshotEvery` 1) | 29,415,086 B | 29,415,086 B / 30,522,902 B (state) |
| `SuperstepsPerStep` 5 (20 steps) | not measured | 1,090,143 B (messages), 1,058,627 B (state) |

Asking where a **waiting** workflow stands (the same run, 302 events, 2.70 MB of history):

| | calls | events | bytes |
|---|---|---|---|
| the first look (reads the run from the start, once) | 2 | 302 | 2,699,127 |
| a look when nothing was written | 1 | **0** | **0** |
| reading the whole history instead, per look | 1 | 302 | 2,699,127 |

A waiting workflow holds no worker and no timer (`NextWakeup` is null in the test); an approval is a signal wait like any other.

## How it is proved

- **Contract tests against the pinned package** (`WorkflowContractTests`, 14): the lockstep stream does not start the next superstep until pulled and writes one checkpoint for the one it ran; a failing executor is events and writes no checkpoint for its superstep; a checkpoint restores into a new workflow object with the request and its id; the checkpoint sections and the shapes the walker reads; the structure
  hash; a different graph is refused; the walker (strings, messages, lists redacted; scalars left; unknown members and types refused, five cases). The package version is pinned by a test that fails on 1.25.
- **Crash at every store operation** (`WorkflowCrashTests`): a fan-out with a decision in the middle (68 store operations, **136 crashes injected and survived**), a signal request (44 operations, 88 crashes), and a durable agent in an executor (45 operations, 90 crashes). Outcomes equal the fault-free run's; every executor ran once or twice (the superstep that was running), never more;
  the model was called once or (when a model call's result died with the worker) twice, never more. The third test runs the conversation's work from the waiting executor (a test seam, `WhileWaitingForConversation`) so that the order of store operations is the same each time; with parallel workers the operation numbering is not stable and the first attempt at this matrix took an hour and proved little.
- **Corpus v6** (11 histories): plain, approval, signal, restored by another worker (`need-snapshot`), agent in an executor, cancelled, hand-over (two runs), a changed package version with its approval, and a hand-over whose checkpoint is put together from references. Replayed by every build.
- **Behaviour** (`WorkflowTests`, `WorkflowRequestTests`, `WorkflowAgentTests`, `WorkflowSafetyTests`, 34 + 7 + 5 + 18): one step per superstep, several per step, a fan-out in one superstep, a failing executor retried as a superstep (what it said is not recorded), approvals decided by verified approvers (a refused approver is counted and the workflow keeps waiting),
  a signal answered twice is one answer, a waiting workflow costs 0 events to ask about, another worker takes over from the checkpoint; a durable agent called from a rerun superstep is asked once; the wait limit (error, metric); cancel and terminate (including the conversations; a reader may not terminate); a cancel in the middle of a superstep stops at the boundary after it; hand-over with and without an open request;
  a changed package version needs an approval and a refused one ends the run; a changed graph ends the run with the reason; redaction of input, state, requests and outputs; refusal of state it does not know; client-side encryption (the store holds ciphertext); another tenant's handle finds, reads, answers and cancels nothing; registration errors (unstable ids, approval port missing or not `bool`, names, an unknown type under a redactor).
- Authorization for the new run type: the operations on a run are the engine's (`StartRun`, `SignalRun`, `CancelRun`, `TerminateRun`, `DecideApproval`); `WorkflowSafetyTests` takes the role matrix for a workflow run (reader refused terminate; operator allowed; an approver without the required role is refused and counted).
- Mutation checks: below.

## Mutation checks

(see the end of this note)

## Open items

- **Remote workers** (outbound-only, `RemoteWorkerStore`) cannot run executors that call durable agents, nested agents, or the parent watch: they have no client for start, signal, read and wait. This is the first item of the Commercial work that follows (`Kairos.Commercial/docs/WORKER-CONVERSATIONS-DESIGN.md`).
- **Output streaming** is not offered (decided).
- Executor state under a redactor is checked at record time, not at startup (finding 4).
- A change inside a moved value other than an append (an element edited in place) is written out whole.
- A very wide superstep (hundreds of executors) is one activity; its crash cost is the whole superstep.
- Accepting the framework's behaviour that a checkpoint restores only into the same graph means a graph change needs a drain or a new name; a rolling deploy of a changed graph with runs in flight fails those runs' next step. The documentation says so.
