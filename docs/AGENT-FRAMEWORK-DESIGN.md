# Kairos.Agents.AgentFramework: durable Microsoft Agent Framework agents (Milestone 8, step 1) — design

Written 26 Oct 2026, before any code. Internal. Sources: the packages themselves (API read by reflection from the pinned assemblies, not from memory), `docs/MULTI-CLOUD-AND-AGENTS.md`, `docs/LICENSING.md`, `docs/REPO-LAYOUT.md`,
`docs/SECURITY-ARCHITECTURE.md`, the M4 agents review, and four small experiments (section 3). Nothing here is built. Section 10 lists what needs a decision.

## 1. What is being asked

Make Agent Framework agents durable on Kairos "without changing how people write them": the agent is a `ChatClientAgent` (or any `AIAgent`) with instructions, tools and `IChatClient`; the durability comes from
`services.AddKairos(...).AddAgentFramework()` and one line per agent. After a crash, a restart or a redeploy, a resumed run never calls the model or a tool again for a step already recorded; approvals pause the run with no compute held;
policy, budgets, redaction, encryption and usage recording behave as for native Kairos agents.

## 2. The packages (checked 26 Oct 2026)

| Package | Version | Released | Notes |
|---|---|---|---|
| `Microsoft.Agents.AI.Abstractions` | **1.24.0** | 2026-10-07 | `AIAgent`, `AgentSession`, `AgentSessionStateBag`, `AgentSessionStore`, `ChatHistoryProvider`, `AIContextProvider`, `AgentResponse`. Depends on `Microsoft.Extensions.AI.Abstractions` 10.10.1. MIT. |
| `Microsoft.Agents.AI` | **1.24.0** | 2026-10-07 | `ChatClientAgent`, `ChatClientAgentOptions`, `ToolApprovalAgent`, `LoopAgent`, `AIAgentBuilder`, compaction, skills, memory providers. Depends on `Microsoft.Extensions.AI` 10.10.0 and (transitively) `.Evaluation`, `Compliance.Abstractions`, `VectorData.Abstractions`, `ML.Tokenizers`. MIT. |
| `Microsoft.Agents.AI.Workflows` | **1.24.0** | 2026-10-07 | the workflow engine: executors, edges, supersteps, `CheckpointManager`, `ICheckpointStore<T>`, `JsonCheckpointStore`, `InProcessExecution` (`Default`, `OffThread`, `Concurrent`, `Lockstep`), `Run`, `ExternalRequest`/`ExternalResponse`. |
| `Microsoft.Extensions.AI` | 10.10.0 (the repository pins `.Abstractions` 10.11.0) | | `FunctionInvokingChatClient`, `ApprovalRequiredAIFunction`, `ToolApprovalRequestContent`/`ToolApprovalResponseContent`. |

**Pin: 1.24.0**, not the newer 1.25.0 (12 days old) or 1.26.0 (3 days old): the release-tooling rule is "at least two weeks old". All three are stable (1.x, not preview). Not used, and not to be referenced: `Microsoft.Agents.AI.OpenAI`
and any other provider package (they pull a vendor SDK), `Microsoft.Agents.AI.Hosting*` and `.DurableTask` (both still preview; the latter is the Azure Durable Task Scheduler route this package is the alternative to), `.Declarative`.
Re-pin deliberately, with the test suite, as for every dependency.

What the experiments and the reflection dump established about the **1.x API** (it differs from preview material that circulates; the old "thread" is now `AgentSession`):

- `AIAgent.CreateSessionAsync()`, `SerializeSessionAsync(session)` → `JsonElement`, `DeserializeSessionAsync(JsonElement)`; `RunAsync(messages, session, options)` (non-streaming) and `RunStreamingAsync`.
- `AgentSession` is an opaque base class with a `StateBag` (`AgentSessionStateBag`, JSON-serializable); a `ChatClientAgentSession` also carries a `ConversationId` when the service stores the history. The built-in `InMemoryChatHistoryProvider` keeps messages in the state bag.
  Experiment: a session after one user message and one assistant reply serializes to ~210 characters and grows ~150 characters per turn.
- Function tools are `AIFunction`s in `ChatOptions.Tools`; `ChatClientAgent` wraps its client in `FunctionInvokingChatClient` and runs the call–invoke–call loop **inside one `RunAsync`**.
- Approvals are MEAI content: an `ApprovalRequiredAIFunction` makes `RunAsync` return a `ToolApprovalRequestContent` (id + the tool call) instead of invoking the tool; the caller sends back `request.CreateResponse(approved, reason)`
  in the next `RunAsync` on the same session. `ToolApprovalAgent` is a wrapper with auto-approval rules.
- The workflow engine runs supersteps; with a `CheckpointManager` it writes one JSON checkpoint per superstep (experiment: 3 checkpoints of 6–8 KB for a two-agent sequential workflow) and `ResumeAsync(workflow, checkpoint)` continues
  from a checkpoint without re-running the executors that finished before it (experiment: the first agent ran 0 times on resume, the second once). Executor ids come from agent ids, which are random per instance unless set:
  resuming with freshly constructed agents without explicit `Id`s fails with "the specified checkpoint is not compatible with the workflow".

## 3. The experiments, and the one that changes the design

I wrote throw-away tests (scratch directory, not in the repository) against the pinned packages and the real Kairos test harness.

**E1. Running Agent Framework code inside the replayed workflow does not work.** The obvious design is a `KairosChatClient : IChatClient` (each `GetResponseAsync` = a recorded model step) and function wrappers (each tool call = a
recorded tool step), then `await agent.RunAsync(...)` directly in workflow code. It appears to work for the first steps and then silently stalls: after a Kairos await *inside a tool*, the run goes idle with no error, no fault, no
exception (even first-chance), and nothing scheduled. Cause, confirmed with a 20-line experiment with no Kairos at all: the library awaits with `ConfigureAwait(false)` (`ChatClientAgent` has 20 of them and `FunctionInvokingChatClient` one, on the
function-call path, in the current sources of both repositories; the reflection of the 1.24.0 binaries shows the same shape). When the awaited task completes on a thread whose `SynchronizationContext` is not the default one — which is exactly the Kairos replay scheduler, a FIFO context that keeps continuations in history order — .NET queues
that continuation to the **thread pool**. The code then runs off the scheduler, after the replay has ended, and its next call is never collected. Kairos' own determinism rules forbid `ConfigureAwait(false)` in workflow code for this reason.
The reproduction passes some of the time (steps that happen to be complete when awaited resume synchronously), which makes it worse, not better. Library code we do not own cannot be made to obey a rule it does not know.

Consequences: Agent Framework code **must never run inside a replayed workflow**. Nor can it be hosted as a coroutine on pool threads without a way to know when it is quiet (parallel tool calls, parallel executors make the order of its requests
racy). Section 4 is the design that follows.

**E2. With declaration-only tools, one `RunAsync` is exactly one model call.** If the agent is given its tools as `AIFunctionDeclaration`s (`function.AsDeclarationOnly()`), `FunctionInvokingChatClient` cannot invoke them: `RunAsync` returns the
response containing the `FunctionCallContent` and stops. A second `RunAsync([tool-result message], session)` continues the conversation. Verified: calls=1 after the first run; after serializing the session, deserializing it into a fresh session object and
running with the tool result, calls=2 and the answer arrives. The whole agent (instructions, middleware, context providers, compaction, memory, telemetry) ran normally in each call.

**E3. `ApprovalRequiredAIFunction` pauses `RunAsync` with a `ToolApprovalRequestContent`; `CreateResponse(true)` resumes it** (calls=1, then 2). In that native path the framework itself invokes the tool after approval; the adapter does not use that path for
its own tools (section 5.3) but must handle the content when something else produces it (hosted MCP tools, `ToolApprovalAgent`).

**E4. Workflow checkpoints are real superstep boundaries and can be stored anywhere** (section 5.6).

## 4. Architecture: Agent Framework runs a step, Kairos runs the loop

```
Kairos workflow  "kairos.af.agent"   (deterministic, written by us; Agent Framework never runs here)
  loop:
    r = activity "kairos.af.step"( agent name, session snapshot, new messages )        <- ONE model call, run by the real AIAgent
    if r has no tool calls and no approval requests: return r.reply
    for each tool call of r:   ctx.InvokeToolAsync(call)       <- existing Kairos tool step: policy, approval, credentials, idempotency, budgets
    for each approval request of r (not ours): ctx.RequestApprovalAsync(...)             <- existing exact-call approval, no compute held
    new messages = tool results (and approval responses)
```

- The **step activity** deserializes the session snapshot, calls `agent.RunAsync(newMessages, session)` on the real agent, whose tools were handed to it as declarations only, serializes the session, and returns: the response messages, the new
  snapshot, usage, finish reason and the model id. Everything non-deterministic about Agent Framework (its threading, ids, timestamps, providers, middleware) happens inside an activity, which Kairos already treats as non-deterministic and
  records the result of. On replay the activity is not executed; the recorded result is returned. **The framework code is never replayed.**
- The **tool steps** are the existing Kairos tool steps: the adapter registers each function the agent offered as an `AgentTool` whose handler is `function.InvokeAsync(arguments)` (so the tool code is the user's, unchanged) and whose declaration is
  the function's schema. Calls go through `IAgentContext.InvokeToolAsync` like a native agent: the recorded policy decision, rate limits, `steps` limits, approvals, scoped credentials, the idempotency key `run + step`, redaction.
- The **driver workflow** is our code, small, and versioned like any workflow (patches, golden corpus). It is deterministic by construction: it only awaits context calls.
- **Opaque fallback.** An `AIAgent` that is not a `ChatClientAgent` (an A2A agent, a custom agent), or is wrapped in something that loops by itself (`LoopAgent`, `ToolApprovalAgent` with auto-approval iterations), cannot be stepped. It is
  registered as **one activity per `RunAsync`** (`kairos.af.turn`): recorded and never re-run once recorded, but at-least-once inside the turn, and said so (section 6).
- **Transparent wrappers** (`OpenTelemetryAgent`, `LoggingAgent`, `DelegatingAIAgent` subclasses that do not loop, `AIAgentBuilder.Use` middleware) are kept: they wrap the real agent inside the step activity and run normally.

### 4.1 The one-line API

```csharp
// Hosting: the agent you already have becomes a durable one.
services.AddKairos(o => o.UsePostgres(connectionString))
        .AddAgentFramework(af => af.AddAgent(supportAgent));          // supportAgent is an AIAgent: ChatClientAgent, built the usual way

// Use it anywhere an AIAgent is accepted, including inside other Agent Framework code:
AIAgent durable = durableAgents.Get("support");                       // a real AIAgent whose RunAsync starts/continues a Kairos run and returns its answer
AgentResponse answer = await durable.RunAsync("where is order A-1042?", session);
```

`DurableAIAgent : AIAgent` is the user-facing handle; its sessions (`DurableAgentSession`) carry the session id, which is the Kairos workflow id of the conversation. Consequently the existing Agent Framework consumers of `AIAgent` (workflows, hosting
layers, `AsAIFunction()`, A2A) work with it unchanged, which is the "without changing how people write them" requirement made concrete. Also `agent.AsDurable(durableAgents)` for the one-liner on an agent instance. Registration needs a stable
**agent id**: the adapter requires `AIAgent.Id`/`Name` to be set (it refuses a random one at registration with a message, because the workflow type, the checkpoint compatibility check and the audit trail all key on it).

Embedded mode: `AddAgentFramework` registers the driver workflow and the activities into the same registry and runtime `AddKairos` builds; the standalone server needs nothing new (workers register the same way; the agent definition lives in the customer's
worker, never in the server, per "the server never executes customer code").

## 5. The seven questions

### 5.1 Which parts become recorded Kairos steps

| Agent Framework event | Kairos step | Recorded | Re-run after a crash |
|---|---|---|---|
| One model call (one `RunAsync` of the stepped agent) | `kairos.af.step` activity | request snapshot, response, new snapshot, usage, finish reason, model id | only if the crash came before its result was recorded: at most once more, like a native model step (the provider may bill the call again in that window; the idempotency key `run:activityId` is passed in the run options) |
| Each tool call | `kairos.agent.policy` (when a recording policy is configured) + `kairos.agent.tool` | decision, result | the tool is not re-invoked once recorded; before that, retried under the tool retry policy with its idempotency key |
| Approval | signal `kairos.approval.<id>` (existing) | who decided, evidence | never |
| Everything inside the step: context providers, memory search, compaction, middleware, telemetry | none of its own | only through the step's result | **again, with the step**, if the step is re-run (never on replay) |
| The driver loop | workflow code | — | replayed from history, deterministic |

Not recorded step by step, and said so: the I/O of context providers and middleware inside a step. A memory provider that searches a vector store runs once per step execution; it must be safe to run twice (it normally is: it reads).

### 5.2 How the thread (session) state is saved and restored

The session snapshot (`SerializeSessionAsync`) **is the durable state**. It travels as the input and result of the step activity, so it is in history, sealed by the worker's payload protection like every payload; nothing else is stored, and no
`AgentSessionStore` implementation is needed. A crash resumes from the last recorded snapshot because replay hands it back.

Across turns of a conversation: one Kairos run per conversation (workflow id = session id); a user message is a signal; the driver runs steps until the model answers, publishes the answer, and waits for the next message (a durable wait, no compute).
When the history nears its cap, the driver **continues as new** carrying the snapshot (existing `ShouldContinueAsNew`), so a conversation can last indefinitely. For one-shot calls a run per request is available; a session can be continued by
passing the previous snapshot. Conversations whose history lives in the service (`ConversationId`) carry only the id in the snapshot.

Cost: every step ships the whole snapshot (input and result), so a conversation of n steps moves O(n²) bytes through history until it continues as new or compacts. Measured growth is small (150 characters per short turn), but real conversations
with tool output are not. Mitigations, in order: Agent Framework's own compaction strategies run inside the step (they shrink the snapshot); a size guard fails loudly beyond a configured limit; deltas against a content-addressed snapshot are possible later
without changing the contract. Open question 4.

### 5.3 How Agent Framework approvals map to Kairos exact-call approvals

- **Function tools.** `ApprovalRequiredAIFunction` is read when the agent is registered: that tool's Kairos requirement is "approval required" (a side-effecting tool under the baseline policy, or the policy engine's rule if one matches). At call time the
  existing exact-call approval applies: the approval is bound to the tool name and the digest of the arguments, shows the (redacted, sealed) arguments in the dashboard, needs the roles and count the requirement names, rejects self-approval, and records the
  authenticated approver (with the verified evidence the M7 approver design added). The agent sees the tool message "a person rejected the call by <subject>" or the result, as a native Kairos agent does. The run waits on the approval signal and holds no
  compute.
- **Requests the framework produces itself** (a hosted MCP tool with approval, `ToolApprovalAgent`): when a step's response contains `ToolApprovalRequestContent`, the driver turns each request into a Kairos approval for that exact tool call and, when it is decided,
  sends `ToolApprovalResponseContent` in the next step. The approver sees the same thing. Tested separately because the origin is different.
- **Workflow `ExternalRequest`s** (section 5.6) map to the same primitive when they are approvals and to signals otherwise.
- A tool with **no** approval wrapper: open question 3 (preserve the author's intent, or apply the Kairos baseline).

### 5.4 Tool policy, the policy engine and budgets

Nothing new is invented; the adapter feeds the existing seams:

- **Tools** run through `InvokeToolAsync`, so `IToolPolicy` (baseline), `IRecordedToolPolicy` (the Pro policy engine, whose decision is recorded before the tool runs), rate limits, `steps` limits and the Policy service for remote workers apply exactly as for native agents. The Apache
  adapter depends only on the Apache hooks (`IToolPolicy`, `IRecordedToolPolicy`, `IModelCallGuard`); the engine, ledger and `UseRemotePolicies` are plugged into the same `AgentsBuilder` by their own packages.
- **Budgets.** The step activity wraps `agent.RunAsync` in `IModelCallGuard` exactly as `kairos.agent.chat` does: reserve at the model's maximum before the call, settle with the usage the response reports, release if the step fails for good; a refusal is recorded
  and the driver pauses for a budget approval or fails (`onExceed`). Because one step is one model call the reservation is exact. The reservation key is the step's idempotency key, so a retried step reuses it.
- **Usage.** `AgentResponse.Usage` is reported in the activity result's `Usage`, the field `GetUsageAsync`, `AgentHistory.TotalUsage` and the cost reports already read. The model id comes from the underlying `ChatResponse.ModelId`; it must resolve through the price table
  (aliases), or the money limit fails closed as for native agents.
- **Both ways.** The same tests run with the in-process engine and with a worker that has only the Policy service (Kairos.Commercial's remote test rig), as the user asked.

### 5.5 What stays non-deterministic, and how replay avoids re-running it

Everything Agent Framework does — model sampling, message ids, timestamps, provider lookups, tool code — happens in activities. Replay never executes an activity; it returns the recorded result to the driver, which is deterministic code of ours that only reads
those results. There is nothing for replay to "avoid": the framework is not in the replayed path (E1 is why). What remains non-deterministic *across crashes* is only the window the engine already documents: an activity that ran but whose result was not yet recorded
runs again. Hence the stated semantics: **exactly once recorded, at most one extra execution per step per crash, never after the result is recorded.** The crash tests assert both halves.

One hazard remains and is tested: a **changed agent definition between crash and resume** (different tools, instructions) changes what a retried step does. The registration records a hash of the definition (name, instructions, tool names and schemas) in the step request; a resumed
run whose registered agent differs fails the step with a clear error rather than mixing definitions (a patch/version mechanism like workflows').

### 5.6 A Kairos-backed checkpoint store for Agent Framework workflows

Short answer: **yes, as the next step, not this one, and with an honest contract.** What is possible (experiment E4): the Agent Framework workflow engine writes a JSON checkpoint at each superstep boundary and resumes from one. A `JsonCheckpointStore` whose
storage is Kairos history works like this: a Kairos workflow `kairos.af.workflow` repeats an activity `kairos.af.workflow.advance` that restores the latest checkpoint into a `CheckpointManager` held for that activity, runs the Agent Framework workflow in **lockstep** until it
is idle, waiting for an external response or finished, and returns the new checkpoints (and outputs, and the list of `ExternalRequest`s) as its result. The checkpoints are sealed history payloads, so a crash resumes from the last recorded superstep, on any worker, with no
database of its own. External requests become Kairos approvals or signals and hold no compute.

What it does **not** give, and the contract must say so: model and tool calls inside the executors of a superstep are not individually recorded (the activity cannot start Kairos steps), so after a crash the whole superstep may run again. Executors must be safe to
run twice, or call durable agents. (`DurableAIAgent` from section 4.1 is usable as an agent inside an executor, which gives that executor a durable, exactly-once-recorded step per call, at the cost of one run per call.) Executor ids must be stable (explicit
agent ids; E4 shows the failure when they are not). Parallel executors within a superstep may interleave; the checkpoint is taken after all have finished.
This is also why it is a separate step: it is a different contract (superstep, not call), a different store, and it needs its own crash tests.

## 6. Semantics the documentation must state (public page, once built)

- A step that has a recorded result is never run again, by any worker, ever. A step without one may run again once per crash.
- Context providers, memory, compaction and middleware run inside the step, on live execution only, at least once per step execution.
- Streaming (`RunStreamingAsync`) is not durable in this step: the durable handle returns the final response; streaming progress is a later feature.
- Concurrent calls on one session are serialised by the run (one driver per conversation).
- Agents that cannot be stepped run turn-by-turn (`kairos.af.turn`) with at-least-once inside the turn.
- The model's maximum output tokens and the model alias used for pricing must be configured for budgets to apply (as for native agents).

## 7. Redaction, encryption, token usage

- **Encryption.** The step request and result (including the session snapshot) are activity payloads, sealed by the worker SDK like every payload; the server and database see ciphertext. The canary test sends a canary through the user message, a tool result and the model
  reply and scans history, audit export, logs, metrics and traces for plaintext and base64 (the M7 canary harness).
- **Redaction.** `UseRedactor` applies "to every piece of text before it is recorded". For this package that means the new messages going into a step, the tool arguments and results (existing), the approval details (existing) **and the text inside the stored snapshot**.
  Redacting the snapshot is the delicate part: the snapshot is Agent Framework's JSON, not ours. Plan: redact string leaves of message `text`, `arguments`, and `result` members by walking the JSON structure, never keys, roles or types, and refuse (fail the registration) if the
  walker meets a snapshot shape it does not know. Open question 9.
- **Token usage.** As in 5.4. The experiment shows the numbers survive the round trip (15 tokens reported for the scripted call).

## 8. Licence and placement

**The documents currently say something different from the expectation in the request, and I have not changed them.** `docs/LICENSING.md` lists `Kairos.Agents.AgentFramework` as **Commercial, Business tier**; `docs/REPO-LAYOUT.md` places it in `Kairos.Commercial` ("Future");
`CLAUDE.md` (both) lists "Agent Framework provider" among the commercial-tier items that must never be committed to the Apache repository; `docs/POSITIONING.md` calls it "an adoption hook, not core revenue" and puts it under "Pro: ... Agent Framework package".
Three tiers in three documents.

Recommendation, which matches the expectation in the request: **`Kairos.Agents.AgentFramework` is Apache 2.0, in this repository.** Reasons: (1) the licensing rule is that correctness and safety are never paid, and the adapter's whole job is exactly-once-recorded steps and approvals; (2) it needs no commercial code:
every seam it uses is an Apache hook already (`IToolPolicy`, `IRecordedToolPolicy`, `IModelCallGuard`, approvals, recorded steps), so it works with the baseline policy alone; (3) the positioning calls it the adoption hook; and (4) the Pro value is untouched: the policy
engine, price tables and budgets, the Policy service for remote workers, the dashboard's approval inbox and cost views all plug into the same `AgentsBuilder` and apply to Agent Framework agents without a line of adapter code. A Pro-only add-on package is not needed;
if one is wanted later it would be a thin `Kairos.Agents.AgentFramework.Policies` that wires `UseKairosPolicies` / `UseRemotePolicies` for Agent Framework registrations in one call, but `AddAgentFramework(...).UsePolicies(...)` on the builder already does that.
Applying this means editing `LICENSING.md`, `REPO-LAYOUT.md`, both `CLAUDE.md`s and `POSITIONING.md`, and moving the "never add here" line; that is the owner's call (question 1). The Kairos-backed workflow checkpoint store (5.6) would sit in the same package.

## 9. How the requirements will be proved

| Requirement | Proof |
|---|---|
| A resumed agent never calls the model or a tool again for a recorded step | crash-at-every-step test (the M4 `CrashAtEveryStepTests` pattern): a run with two tool calls, one needing approval; the worker is killed at every store call; call counters of the scripted model and the tools must equal the number of steps recorded, plus at most one extra for the step in flight; run for seeds 1–N with fault injection |
| Approval pauses with no compute and resumes with the approver recorded | test: the run is idle (no task leased, no timer) while pending; approving as a named, verified principal resumes it; the history names the approver; wrong role / self-approval / twice / late behave as native |
| Policy and budgets as native, also through the Policy service | the same test class run twice: in-process engine; and a remote worker using `UseRemotePolicies` against `Kairos.Server` (in the Commercial rig) |
| Redaction, encryption, usage | canary test; redactor test on snapshot; usage totals equal `AgentHistory.TotalUsage` |
| Golden corpus | new `.khist` entries recorded through the adapter (plain run, tool, approval, rejection, budget pause, continue-as-new) that the driver workflow must replay forever |
| No model-vendor SDK | architecture test: the assembly references nothing matching `OpenAI`, `Azure.AI`, `Anthropic`, `Google.*`, `Mistral`, `Cohere`, `OllamaSharp`; only `Microsoft.Extensions.AI*`, `Microsoft.Agents.AI*` |
| Mutation checks | driver loop (step counting, approval mapping, snapshot threading), the step activity (guard calls, usage), definition-hash check, redaction walker, registration refusals |
| Sample | `samples/AgentFramework`: a support agent with one read-only tool, one tool that needs approval, and a budget, in the GettingStarted style, run by a sample test with a scripted model |
| Determinism hazard stays fixed | a test that runs an agent whose tool awaits a Kairos timer-like delay after the frontier (E1's shape) and must complete; it would stall under the rejected design |

## 10. Open questions (decisions I need before building)

1. **Licence tier and repository.** The documents disagree with each other and with the request (section 8). Recommendation: Apache, in `Kairos`. Confirm, and I will update the three-plus documents in the same change that adds the project. If the answer is Commercial, the project goes to
   `Kairos.Commercial` and nothing else here changes.
2. **The execution model.** Section 3's E1 rules out running Agent Framework inside the replayed workflow. The design is therefore "the framework runs a step, Kairos runs the loop". The price: the framework's own run loop (parallel tool calls inside one `RunAsync`,
   `LoopAgent`, streaming, auto-approval iterations) is not preserved for stepped agents; those wrappers fall back to turn-level recording. Is that acceptable as the first release?
3. **Tools without an approval wrapper.** Agent Framework runs them automatically; Kairos' baseline says a side-effecting tool needs an allow policy or approval. "Without changing how people write them" argues for keeping the author's meaning (no wrapper = no approval) by registering such tools as
   explicitly pre-allowed, with the policy engine still able to deny or require approval; safety argues for the baseline. Recommendation: keep the author's meaning by default, log it once at registration, and provide `.RequireApproval("tool")` / `.ReadOnly("tool")` overrides, so an operator can tighten without touching the agent. Decide.
4. **Snapshot size.** Full snapshot per step (O(n²) until continue-as-new) with a configurable size guard, versus building deltas now. Recommendation: full snapshots plus guard in this step, measure with realistic tool output, then decide.
5. **Workflows and the checkpoint store.** Recommend: not in this step; a second step with the superstep contract of 5.6. Or include it now and the step grows by roughly half.
6. **Conversation model.** One long-lived run per conversation with a signal per user message (recommended) versus one run per request with the snapshot passed along. How should a caller receive an answer to a signalled message: the durable handle waits for a "turn completed" record in history (poll or long-poll), or a result query? I would
   start with the handle polling the run's history for the turn record, since there is no outbound channel in the engine today.
7. **Opaque agents.** Is turn-level recording (at-least-once inside the turn) acceptable for agents that cannot be stepped, or should registering them be refused unless the user opts in explicitly?
8. **Dependency footprint.** The adapter must reference `Microsoft.Agents.AI` (for `ChatClientAgent`), which brings `Microsoft.Extensions.AI` 10.10.0 and four more Microsoft packages, against the "minimal dependencies" rule. `Abstractions` alone would not let us read tools and options
   from an existing `ChatClientAgent`. Accept, or limit the first release to agents registered through a factory (`af.AddAgent("support", sp => ...)`), which would need only the abstractions in the adapter? Recommendation: accept the dependency; the user has it already.
9. **Redaction inside the snapshot.** The plan in section 7 walks the JSON and refuses unknown shapes. If that is too fragile, the alternative is to apply the redactor only to what enters and leaves the step and document that the snapshot may contain text the redactor would have changed. Which?
10. **Agents as tools (`AsAIFunction`).** A stepped agent calling another agent as a tool: inside the tool activity the nested agent would run as one opaque turn. Making it a child Kairos run is straightforward with `DurableAIAgent` but adds a run per call. Do you want nested agents durable by default?

## 11. Risks

- **A new Agent Framework release changes the session JSON, the declaration-only behaviour or the approval content types.** Pin, run the experiments as permanent contract tests (E2, E3 and the snapshot round trip are written as tests of the adapter), and re-pin deliberately. 1.x is stable but young.
- **Snapshot compatibility across versions:** a run in flight during an Agent Framework upgrade resumes with a snapshot the new version must read. The framework's own `Deserialize` is the contract; tests include a snapshot recorded with 1.24.0 in the golden corpus.
- **The definition-hash check** will trip on harmless changes (a reworded instruction); it fails the step with a clear message and an override.
- **The driver workflow is code in a package**: changing it can break in-flight runs. The golden corpus and patch markers are the guard, as for `Kairos.Agents`.
