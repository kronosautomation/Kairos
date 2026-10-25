# Agents and sagas: design decisions and open items (Milestone 4)

Written 24 Oct 2026 with the first `Kairos.Agents` implementation. Internal: decisions, trade-offs and what is still open. Not for `docs-site/`.

Sources: `docs/MULTI-CLOUD-AND-AGENTS.md` section 3, `docs/RESEARCH.md` R1, `docs/LICENSING.md`, `docs/SECURITY-ARCHITECTURE.md` section 11.

## What was built

| Requirement | Where | Pinned by |
|---|---|---|
| Model step recorded, never re-billed on replay | `AgentContext.ChatAsync` is an activity; the reply and usage are its result | `ModelStepTests` (call counts), `CrashAtEveryStepTests.Model_calls_under_crashes...` |
| Idempotent tool step (`run id + step`) | `kairos.agent.tool` activity; `ToolInvocation.IdempotencyKey` is the activity key | `ToolAndApprovalTests.A_tool_that_fails_after_its_effect...` |
| Approvals with approver identity | signal `kairos.approval.<id>` carrying `ApproverIdentity`; rules in `ApprovalRules` | `ToolAndApprovalTests` (wrong identity, self-approval, twice, late, after cancel) |
| Read-only baseline | `BaselineToolPolicy`: read-only runs, side-effecting waits for approval, deny always wins | `ToolAndApprovalTests` |
| Per-run scoped credentials | `IToolCredentialIssuer`, `HmacToolCredentialIssuer` (run + tool + scope + expiry, HMAC-SHA256) | `CredentialTests` (including the canary scan of history) |
| Sagas, I11 | `RunSagaAsync`, `ISaga.Compensate`, `SagaHistory.CheckCompensationsComplete` | `SagaTests`, crash matrix, `SagaSimulationTests`, `spec/KairosSaga.tla` |
| Recorded model fixtures | `Kairos.Testing.Agents` (`ModelFixture`, `FixtureChatClient`, `RecordingChatClient`, `ScriptedChatClient`) | `FixtureTests`, `samples/AgentSamples.Tests` |

No engine change was needed: everything is built from activities, signals and timers. History format, stores and the golden corpus are untouched.

## Decisions worth knowing

1. **The policy is evaluated inside the tool activity, not in workflow code.** The decision is then part of the recorded result, so a
   policy change cannot make an in-flight run diverge (workflow code would have re-evaluated on replay and thrown non-determinism),
   and a workflow bug cannot skip the check. It also costs no extra round trip on the common path. The price: a tool that needs
   approval costs two tool activities (ask, then run with the approval attached).
2. **Rollback progress is readable from history through marker activities** (`kairos.saga.mark`: plan, step begin/end, end) instead of
   a new history event type. A new event type would have touched the codec, both stores, the schema constraints and the golden corpus.
   The cost is four extra activities per rollback and one more activity that every agent worker must register (it comes with
   `AgentsRuntime.Activities`). A marker that fails is skipped: the rollback itself must never depend on bookkeeping.
3. **Compensations are retried 100 times (waits up to five minutes), not forever.** Unbounded retry grows the history without limit and
   hides a permanently broken undo. After the last attempt the run fails with `SagaCompensationException` naming what was not undone,
   and the remaining undo steps still run. I11 is read as "every started compensation reaches its end marker", with failure being a
   recorded end, not a silent one.
4. **Cancel is cooperative.** Nothing in flight is abandoned, because abandoning an activity whose effect is under way could leave the
   effect in place after its undo ran. Approval waits end at once; other steps notice at the next agent step or `ThrowIfCancelled`.
   An operator-terminated run (`RunTerminated`) is not rolled back, since its code is not replayed; I11 exempts it. Document the difference.
5. **Model calls are at-least-once like any activity.** If the process stops after the provider answered and before the answer is
   recorded, the model is called again (and billed again). The idempotency key is passed in `ChatOptions.AdditionalProperties`, but no
   mainstream provider client uses it today. This is the honest reading of "never re-billed on replay": replay is free; a crash in that
   one window is not. The tests bound it (at most one extra call per crash).
6. **Replies keep only text, reasoning, calls and results.** Other content is refused on the way in and becomes a visible placeholder on
   the way out. The wire format is Kairos' own, so a Microsoft.Extensions.AI upgrade cannot change how old histories deserialize.
7. **The analyzers treat `IAgentContext` like `IWorkflowContext`.** Agent loops are workflow code.

## Open items and risks

- **Approver identity is asserted, not authenticated.** The engine checks it against the requirement and records it; whoever can call
  `AgentClient.ApproveAsync` (or write signals to the database) can name any identity. In embedded mode the host application is the
  boundary. For the standalone server (Milestone 7) the identity has to come from the authenticated principal, and for regulated
  settings the event-authenticity design in `SECURITY-ARCHITECTURE.md` section 12 applies. The public page says this plainly.
- **Budgets (tokens, cost, steps, wall-clock) and argument-level policy rules are not implemented here.** `docs/LICENSING.md` lists them
  as a commercial package (`Kairos.Agents.Policies`); section 3 of the engineering spec still lists them as `Kairos.Agents` requirements.
  The two documents disagree and should be reconciled. What this repository provides for them: the `IToolPolicy` seam, usage recorded
  in history (`AgentHistory.TotalUsage`), and `ApprovalRequirement`. Aaron to confirm the boundary.
- **Tool policy is per runtime instance, not per namespace.** Embedded mode has one scope, so this is equivalent; the standalone server
  needs a per-namespace policy source.
- **R1 benchmark not done:** saga versus a single database transaction for short, low-contention workflows, and the "transactional step
  group" fast path when every step touches only the Kairos database. Needs the group-commit work to be measured first.
- **Large conversations:** `ShouldContinueAsNew` exists; there is no helper that summarises a conversation and continues. Agents with
  long conversations have to do it by hand. A saga cannot continue as new while it holds compensations (refused, rolls back).
- **No MCP, no streaming of tool progress.** Milestone 5.
- The TLA+ saga model abstracts the forward phase (any prefix of registrations, then failure or success) and the compensation body (one
  idempotent activity). Multi-activity undo steps are covered by the simulator, not by the model.
- Disclosure review (Aaron) is still required before the five new pages in `docs-site/concepts` are published.
