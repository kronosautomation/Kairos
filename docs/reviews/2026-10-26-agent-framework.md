# Kairos.Agents.AgentFramework: what was built, what it showed, what is open (Milestone 8, step 1)

Written 26 Oct 2026. Internal. Design: `docs/AGENT-FRAMEWORK-DESIGN.md` (section 12 records the decisions and what building changed). Placement decided by Aaron: Apache, in this repository.

## What exists

- `src/Kairos.Agents.AgentFramework` (Apache). `services.AddKairos(...).AddAgentFramework(af => af.AddAgent(agent))`; `DurableAgents` / keyed `AIAgent` / `agent.AsDurable(durableAgents)` give a real `AIAgent` whose `RunAsync` is the agent's Kairos conversation.
- One model call per recorded step, run by the real agent inside an activity; a Kairos workflow (`kairos.af.agent/<agent>`) runs the loop; tools are ordinary Kairos tool steps, run concurrently when one answer holds several. Agent Framework code never runs in replay.
- Classification at registration (`ReadOnly`, `AllowWithoutApproval`, `RequireApproval`), the author's `ApprovalRequiredAIFunction` honoured as a floor, unclassified tools logged by name at startup.
- Budgets/policy/redaction/credentials/approver verification come from the same `AgentsBuilder` the native agents use (`af.ConfigureAgents(...)`); a refused model call pauses for a raise or ends the run.
- Definition pin with an approval-based override (`OnDefinitionChange`), audited (`AgentFrameworkAudit.Read` returns a `DefinitionOverrideAuditRecord` naming the verified approver).
- Nested agents as durable child runs (`DurableAgentFunction.For`), opaque `AsAIFunction()` refused unless `AllowOpaqueNesting`; turn-level recording only with `AllowTurnLevel()`.
- Small additions to Apache code: `AgentsRuntime.GuardedModelCallAsync` (internal; the same guard/timeout/usage/settle/release as a native model step), `AgentsBuilder.DecoratePolicy`, `AgentActivityNames.FrameworkStep/Turn` (counted by `AgentHistory.TotalUsage`), `AgentTelemetry` spans for framework steps, `KairosClient.GetCurrentRunAsync`.
- Documents changed in the same change: `LICENSING.md`, `REPO-LAYOUT.md`, both `CLAUDE.md`s, `POSITIONING.md`, `MULTI-CLOUD-AND-AGENTS.md`. Public: `docs-site/concepts/agent-framework.md`, API reference entry, `samples/AgentFramework` (+ tests).

## A bug in the engine, found by the adapter

`IWorkflowContext.HistoryLength` (and so `ShouldContinueAsNew`) was one more on replay than when the code first ran: the `WorkflowTaskCompleted` that ends a batch was counted before the batch ran on replay, and does not exist yet when the code first runs. A decision taken exactly at the threshold diverged and the replay failed as non-determinism. It was latent (the real threshold is 40,000 events); the adapter's hand-over test sets the threshold to 8 and hit it. Fixed in `WorkflowExecution.Apply` with `HistoryLengthTests` (the test crashes the host without the fix, passes with it).

## How each requirement is proved

| Requirement | Proof |
|---|---|
| Never re-call the model or a tool for a recorded step; crash at every step of a run with tools and an approval | `CrashAtEveryStepTests`: 69 store operations x before/after = 138 crashes, each survives; outcome equals the fault-free run, hash chain verifies, model calls 3..4, tools 1..2 each, the refund seen once by the world under the step's key. Four more matrices (`CrashMatrixTests`: parallel tools, a rejected approval, a budget that pauses, a hand-over to a fresh run; 34 to 73 store operations each, killed before and after every one); `NestedAgentTests` crash matrix (parent and child); 8 seeds of random crashes, delays and duplicate deliveries |
| Approvals pause with no compute; dashboard approval resumes; authenticated approver recorded | `SupportAgentTests`, `HostingTests` (a real host: `RunAsync` waits, `AgentClient.ApproveAsync` resumes), `ApproverIdentity` verified by `EsApproverVerifier`; the audit names the verified subject; wrong role / self approval behave as native (they are the native code) |
| Policy and budgets as native agents, also via the Policy service, the same tests both ways | `Kairos.Commercial/tests/Kairos.Server.Tests/AgentFrameworkPolicyTests`: decisions with rule names, ledger figures (7 steps, 75 tokens, 0 left reserved), a step limit, a token budget that pauses for two named people, an engine rule that is not overridden - each for a worker with a database and for one with only the Policy service, and a test that compares the two. Apache side: `ToolPolicyTests`, `ParallelAndBudgetTests` with a counting guard |
| Redaction, encryption, token usage as native; canary leakage test | `RedactionTests` (messages, tool arguments and results, the session snapshot, approval text; a message from a client with no redactor), `Kairos.Observability.Tests/AgentFrameworkConfidentialityTests` (spans, metrics, logs, and with encryption on the stored history, session included, holds no canary in text, base64 or hex; the unencrypted run is the control). Usage is in `ActivityCompleted.Usage` so `GetUsageAsync` and `AgentHistory.TotalUsage` read it |
| Golden corpus | `Corpus/v4`: 10 histories (plain, tools + approval, rejected, budget pause, hand-over first and second run, nested parent and child, definition change, turn level), verified and replayed by every build |
| No model-vendor SDK; architecture test | `BoundaryTests`: the referenced-assembly closure and the dependency manifest hold no vendor SDK and none of the framework's provider/hosting/Durable Task packages |
| Sample | `samples/AgentFramework` (one read-only tool, one tool needing approval, a token budget), tests run every region the docs page quotes |
| Snapshot shape as a contract | `ContractTests` pins the 1.24.0 version, how a run is cut into steps, what can be read from an agent, the session JSON (two goldens), and that every content type in the library is known to the redactor or deliberately refused |
| Definition override audited with the verified principal | `DefinitionChangeTests` (the override is an approval; the audit record and the standard audit name `user:bob`; rejected change ends the run with nothing called; only the named roles may allow it; a flapping definition ends after three) |

## Mutation checks

88 mutants of the new code (driver loop, step, runtime, definition, policy, redactor, handle, children, audit, and the engine fix). First pass: 66 caught, 11 did not compile, 8 survived. The survivors were real gaps and each got a test: a rejected budget raise (the test guard now honours an allowance), a close arriving with a hand-over, a message from a client without a redactor, a tool result sent in the wrong role, calls the provider already made, a turn-level answer containing a call, a duplicated field in the definition hash (removed). The mutants that did not compile were rewritten and run again. All are caught except one equivalent mutant (`D26`: waiting for a child when the tool step failed - a failed step cannot return the marker the wait looks for). Along the way a run hung because a mutant spun forever in workflow code: that is a caught mutant, killed by the harness.

## What the numbers say: every step carries the whole session

Design question 4 accepted full snapshots plus a size guard "and measure with realistic tool output". Measured (`MeasurementTests`): a support conversation where each message makes two lookups (about 4 KB and 2 KB of JSON) and a short answer, 3 steps per message, 20 messages:

| message | session bytes | bytes recorded for this message | events in run |
|---|---|---|---|
| 1 | 11,750 | 52,110 | 17 |
| 5 | 58,514 | 332,795 | 85 |
| 10 | 116,976 | 683,566 | 170 |
| 20 | 233,956 | 1,385,446 | 340 |

Total for 20 messages: 14.4 MB recorded (about 720 KB per message on average). It is quadratic as the design said, and the constant is larger than I estimated: the session grows by about 12 KB a message, and each message's three steps each write it twice (request and result), plus the tool output. The default guard (512 KiB) is reached at about message 44; continue-as-new bounds the *history* but not the session, so a conversation that long needs the session to shrink, not only the run to hand over. Postgres and sealing cost grow with this. I would not call it acceptable for conversations beyond a few dozen messages with tool output of this size. Options, in the order I would take them: (1) make Agent Framework's compaction strategies a documented default for long conversations (they run inside the step and shrink the snapshot); (2) a lower default guard with a clear error; (3) deltas against a content-addressed previous snapshot (the contract does not change), worth building once real traffic shows which of the above is not enough. Nothing is decided here.

## Answers to the owner's instructions

- **Long poll or polling?** Polling with backoff. The server's long poll serves worker task polls only (`LongPollTests`); nothing serves run updates. `DurableAIAgent.RunAsync` reads the run's history, quickly at first (25 ms) and then less often (up to 1 s). Each look reads the run's whole history (and opens it when payloads are sealed), so a very long conversation makes each look dearer; `SendAsync`/`TryReadAsync` let a caller choose its own cadence. A "wait for run update" operation in the engine would remove this; not built.
- **Opaque agents** need `AllowTurnLevel()`; registration fails with a message naming it. Hosted tools (provider-run web search, hosted MCP) make an agent turn level too; there is no separate switch.
- **Nested agents** are child runs by default; the child answers its parent by a signal and the parent also looks in now and then (growing to a quarter of an hour) in case the child run ended without answering.
- **Dependency footprint** (measured): `Microsoft.Agents.AI` and `.Abstractions` 1.24.0, `Microsoft.Extensions.AI` and `.Abstractions`, `.Evaluation`, `Microsoft.Extensions.Compliance.Abstractions`, `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.ML.Tokenizers`. No vulnerable package in the closure (`dotnet list package --vulnerable`).

## Things that behave differently from what the design said, or that a reader may not expect

- `AIAgent.Id` is random per instance; the adapter keys on `Name`.
- `AsDeclarationOnly()` is not enough to stop the function-invoking client from asking for the author's approval itself; the step builds plain declarations. A contract test shows both.
- `AllowWithoutApproval` lifts only the free policy's default hold. Under the policy engine an unmatched side-effecting tool is *denied*, and a denial stands, so there it changes nothing; a rule that matches and asks for approval is not overridden either. This is on purpose (an application owner's mark must not outrank an operator's policy) and is tested both ways, but it will surprise someone who expects the mark to open the tool.
- An exception from workflow code fails the run; it does not release the task. So "this worker lacks the agent" cannot be handled in the driver. There is one workflow type per agent (`kairos.af.agent/<name>`): a worker that lacks the agent does not register it and the engine leaves the run for one that has it. The step *activity* is not per agent, so a step that lands on a worker without the agent fails retryably (three attempts) and may be picked up elsewhere.
- Registration is strict on purpose: unknown marks, conflicting marks, two different tools with one name, a budget without a model, a redactor with session state it does not know all fail at startup with the option that fixes them.

## Open items

- **Cancelling or terminating a parent does not stop its child runs.** The child keeps going and its answer finds a closed parent (the notify is ignored). Needs a decision: cancel children when the parent is cancelled (a signal the child's loop honours), or leave them to the operator.
- **The driver is package code in a long-lived workflow.** A later change to it must keep replaying runs in flight: the golden corpus (`Corpus/v4`) guards the shapes recorded today, and new behaviour needs patch markers. The workflow is registered under the default build id only.
- **Tools that a context provider adds at run time** cannot be found by a worker after a restart (unknown tool message to the model). Documented; a registration option to declare them is the fix if it matters.
- **Streaming is not durable.** `RunStreamingAsync` returns the recorded answer whole.
- **The workflow checkpoint store** (design 5.6) is the next step, with the superstep contract.
- **Not run here:** the .NET 11 compile check the pipeline makes non-blocking (`-p:KairosTargetFramework=net11.0`); the pinned SDK is 10. The release tooling's package lists did not need changes (they enumerate packable projects), but a release has not been cut from this commit.
- **Line endings.** Repository blobs were LF already; the working tree on this machine is CRLF through `core.autocrlf=true`, so files I wrote showed as CRLF. Both repositories now have `.gitattributes` (`* text=auto eol=lf`) and everything I touched is LF on disk.
