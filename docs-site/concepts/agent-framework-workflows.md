# Microsoft Agent Framework workflows, made durable

[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) workflows are graphs of executors that run in *supersteps*: every executor with a message to handle runs, and what they send is the next superstep's input.
`Kairos.Agents.AgentFramework` makes such a workflow durable. Each superstep (or a few) is a recorded Kairos step that carries the workflow's checkpoint, so the workflow survives a crash or a redeploy, a request to a person
waits **without holding a worker**, and the workflow can be cancelled and terminated like any other Kairos run. The workflow itself is written the usual way; registering it is the only thing that mentions Kairos.

This page is about workflows. [Durable agents](agent-framework.md) are the other half; the two work together.

## An example

A refund is summarised by an agent, a person in finance decides, and the refund is made. The executors, with explicit ids (a checkpoint names them):

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-executors)]

The graph, built from them. It is built again for every step, so it must be the same graph every time:

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-build)]

And the registration:

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-register)]

Starting a run and waiting for it:

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-use)]

> [!NOTE]
> Inside the `Kairos` namespaces, `IWorkflowContext` means Kairos' own workflow context. An executor's context is Agent Framework's: use an alias, as the example does
> (`using AgentFrameworkContext = Microsoft.Agents.AI.Workflows.IWorkflowContext;`).

## What is recorded

| Agent Framework does | Kairos records | After a crash |
|---|---|---|
| Runs a superstep (every executor with a message) | A step: the workflow's checkpoint after it, what it yielded, the requests it is waiting for | A recorded superstep is **never run again**. The superstep that was running when the worker died is run again from the previous checkpoint. |
| An executor raises a request (a `RequestPort`) | The request, in the step that raised it; then an approval or a signal | Waits, with nothing running, for as long as it takes. |
| Yields an output | The output, in the step that yielded it | Kept. |

A step runs on any worker that has the workflow registered: the checkpoint is restored into a freshly built workflow, one or more supersteps run, and the next checkpoint is recorded. Replaying a run never runs an executor.
`AgentFrameworkOptions.SuperstepsPerStep` (default 1) says how many supersteps one step runs: 1 records every superstep and a crash costs at most the one that was running; a larger number records less, and a crash costs more.

## Executors must be safe to run twice

If the worker dies in the middle of a superstep, nothing inside it was recorded, and the next attempt runs the whole superstep again. In a superstep with several executors, the ones that had finished are run again too.
The contract for an executor is therefore the same as for a tool that Kairos retries: **safe to run twice**, or reach the world through something that is.

* A service that honours an idempotency key is safe. `AgentFrameworkWorkflowContext.IdempotencyKey(Id)` is a key that is the same every time the step runs again, and different for every executor and every step: the `Settle` executor above passes it to the refund service, which refunds once.
* A call to a durable agent from an executor is safe (see below).
* Executor ids must be explicit and stable. Registration builds the workflow twice and refuses it when the two builds differ.

## Requests: approvals and signals

An executor that needs a person raises a request through a `RequestPort`. What happens next depends on the registration:

* A port registered with `RequestsAsApprovals` becomes an **approval**. It appears in the approvals inbox with the text you give (or, by default, the port's name and the request, redacted), and a verified approver decides it. The port's response
  type must be `bool`. The approver is recorded in the run's history, as for an agent's approvals.
* Any other port is answered by **signal**: `DurableWorkflowRun.RespondAsync(requestId, response)` with the port's response type. Answering twice is one answer.

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-inbox)]

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-signal)]

Where the request is raised:

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-ask-executors)]

`WorkflowView.Pending` lists the open requests with `IsApproval` set for the ones that are approvals. An approval cannot be answered with `RespondAsync`: a signal does not say who decided, and an approval must.

## Agents inside executors

An executor may call a durable agent (`context.Agents.Get("summarizer")` in the factory's `WorkflowBuildContext`). The conversation is named by the step and what is said, so if the superstep runs again after a crash the call
**finds the answer the conversation already gave**: the model is asked once, however often the executor runs. Conversations an executor starts belong to the workflow and are asked to stop when it is cancelled, terminated or fails.

A worker is held for as long as an executor waits for a conversation, and the executor does not know whether the conversation is about to answer or is waiting for a person for three days. So the wait has a limit:
`AgentFrameworkOptions.WorkflowAgentWait` (default five minutes). Past it the step fails, without being retried, with an error that names the conversation and says to use a request port instead.
The metric `kairos.af.workflow.conversation_waits` counts the activities that are waiting on a conversation right now; a value that stays above zero is an executor that should be a request.

**The right pattern for something that may wait for a person is a request, not a call.** An agent that is quick and unattended (a summary, a draft, a classification) is fine inside an executor. An agent that may ask for approval,
or wait for an answer from a human, belongs behind a `RequestPort`: the workflow raises the request, holds nothing while it waits, and goes on when it is answered.

## Stopping a workflow

[!code-csharp[](../../samples/AgentFramework/WorkflowSample.cs#workflow-stop)]

A cancelled run ends as failed with the cancellation, and its view says `Cancelled`. A terminated run says `Terminated`. Terminating needs a verified principal that may terminate runs; the audit trail names it.

## Redaction

With a redactor configured, every piece of text in the workflow's input, messages, executor state, requests and outputs is redacted before it is recorded. The checkpoint holds **your** types in Agent Framework's JSON, so the redactor can only
be as complete as the types it is told about. Strings, chat messages and lists of those are built in; every other type that the workflow passes between executors must be declared in the registration:

* `AllowStateType<T>()` says values of `T` hold no free text.
* `RedactState<T>(value => ...)` gives the redactor a way into the text that `T` holds.

Registration fails at startup with a message that names the type and these options when a type the protocol shows is not declared. A type that is only visible in executor state is refused when the checkpoint that holds it is recorded
(the step fails with a message that names the type), never skipped.

## When the workflow changes under a run

A run is pinned to the structure of the workflow it started with (its executors, edges and ports) and to the Agent Framework package version, whose checkpoint format is the package's. If a worker has a different structure or version,
a person decides, as an approval whose approver is recorded (`OnStructureChange` says who may), before the run goes on. Agent Framework restores a checkpoint only into a workflow with the same executors, edges and ports, so a change to the graph
cannot be approved: the run ends with an error that says so. **Let the runs in flight finish, or cancel them, before deploying a change to the graph, or register the changed graph under a new name.** A change to what an executor does without
changing the graph is not visible to Kairos; the runs in flight go on with the new code.

## Long workflows

A run hands over to a fresh run before its history gets too long, carrying the checkpoint and the outputs so far; open requests answered by signal are waited for again by the new run. A hand-over waits until open approvals are decided.
Callers do not notice: `DurableWorkflowRun` follows the workflow.

## What to know

* The workflow, its messages, state, requests and outputs are serialized by Agent Framework with the options you give (`JsonOptions`); a source-generated context keeps that free of reflection.
* A checkpoint larger than `MaxCheckpointBytes` (2 MiB by default) fails the step with a clear error: keep large values out of executor state and messages, and pass references.
* History holds a checkpoint whole every `FullSnapshotEvery` steps and otherwise only what changed, like a session. The part of a checkpoint that describes the workflow is the same in every checkpoint and is never repeated.
* Outputs are available when the workflow finishes and as it goes (`WorkflowView.Outputs`); output streaming is not offered.
* The factory is called once for every step. Keep it cheap, and keep the executors it creates free of state of their own: a workflow's state is in the checkpoint.
* The package is pinned to the Agent Framework version it was verified against. A different version is a reason to run the package's contract tests before moving the pin.
