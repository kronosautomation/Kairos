# Exactly-once checklist (R4)

Reviewed 24 Oct 2026, Milestone 3. Sources: Zhang et al., *Fault-tolerant and Transactional Stateful Serverless Workflows*
(Beldi, OSDI 2020) and *Halfmoon* (SOSP 2023), as listed in `docs/RESEARCH.md`. Internal document: not for `docs-site/`.

## Method

For each failure case those designs guard against, find the Kairos mechanism and the test that pins it. Where there is no test,
add one (both stores run the same contract tests) or record the gap. The definitions being mapped:

- **I2 exactly-once state:** every history event is appended once, in order, without gaps.
- **I3 at-most-once effect per idempotency key:** an activity with key K takes effect once, whatever the retries, crashes or
  lease losses.

## Beldi: failure cases

| # | Failure case | Kairos mechanism | Pinned by | Result |
|---|---|---|---|---|
| B1 | Instance crashes at any point in a step, including between the effect and its log record | Result recorded by a separate completion; the activity key is stable, so a rerun re-applies the same key | Crash-at-every-step matrix; simulation I3 | Covered |
| B2 | A restarted instance runs while the old one is still alive (the intent collector's duplicate) | Lease + fencing token; every completion carries its token and is rejected as `StaleLease` when it no longer matches | `Expired_workflow_lease_is_reclaimed_...`, `Stale_activity_completion_...`, TLA+ `ActFencing` mutation, simulation zombies | Covered |
| B3 | The caller retries the invocation after an unknown outcome | Workflow id is the start's idempotency key: starting an id that already has a run returns that run, also after it finished | `Starting_an_existing_workflow_id_...`; **new** `Starting_a_workflow_id_again_after_the_run_finished_...`, `Many_callers_starting_the_same_workflow_id_at_once_...` | **Gap closed** (finished runs and races were untested) |
| B4 | An accepted invocation is lost before it runs (the intent table's job) | Start appends `WorkflowStarted` and makes the first task runnable in one transaction | Contract + `FaultInjection` (lost acknowledgement) | Covered |
| B5 | Concurrent writers to the same log slot | Per-run sequence number checked on every append (`SequenceConflict`); one transaction per workflow task | `Completion_after_history_advanced_is_a_sequence_conflict_...`; **new** `Racing_completions_of_one_workflow_task_apply_exactly_one` | **Gap closed** (same-lease race was untested) |
| B6 | The completion's acknowledgement is lost and the worker repeats it | A completed task is retired, so the repeat finds no task and writes nothing | **new** `Repeating_a_workflow_task_completion_...` (workflow task); `Stale_activity_completion_...` (activity, repeat of the applied completion); Toxiproxy `Commits_whose_acknowledgement_is_lost_...` against real Postgres | **Gap closed** (workflow-task repeat was untested) |
| B7 | A message delivered twice (callbacks, async invocation) | Signal id de-duplicated per run, inbound events de-duplicated by event id (start by workflow id, signal by event id) | `A_redelivered_signal_id_...`; **new** `Many_deliveries_of_the_same_signal_id_at_once_...`; `The_processor_applies_each_message_once_...`; Toxiproxy `Signals_retried_after_an_unknown_outcome_...` | **Gap closed** (concurrent duplicates were untested) |
| B8 | Control flow diverges between executions, reusing step numbers for different operations | Replay compares each command with its recorded event; a difference faults the task and appends nothing | Determinism tests, property tests, golden corpus, analyzers KAI001-KAI005 | Covered |
| B9 | A straggler finishes after the instance was garbage-collected or ended | Completion for a closed or ended run is refused; ending a run removes its tasks and timers | `Failing_a_run_removes_...`; **new** `A_late_activity_result_for_a_run_that_was_ended_changes_nothing` | **Gap closed** (late result after an operator ended the run was untested) |
| B10 | Lock holder crashes while holding a lock (transactions) | Not applicable: Kairos has no cross-workflow locks. Revisit with sagas (Milestone 4) | n/a | Sagas landed (M4) without cross-workflow locks, so the case still does not exist; the saga rollback has its own crash matrix, simulation and TLA+ model (`KairosSaga.tla`) |
| B11 | Child instances' completion is delivered twice to the parent | Not applicable: no child workflows yet. Revisit with child workflows | n/a | Out of scope until children exist |

## Halfmoon: failure cases

Halfmoon shows that logging only reads, or only writes, is enough, provided every operation that is not logged is a deterministic
function of the log.

| # | Failure case | Kairos mechanism | Pinned by | Result |
|---|---|---|---|---|
| H1 | A value that is not logged differs on replay | `ctx.Now` is the timestamp of the event being processed, `ctx.NewGuid()` derives from the run id and a counter, `IsPatched` is recorded as a marker; everything else that can differ is an activity, which is logged | Replay round-trip property tests; analyzers KAI001, KAI002 | Covered |
| H2 | Read-only steps could skip the log | Every activity result is logged. Skipping it for pure reads is the open R3 experiment; it must keep I2-I4 in the simulator and the TLA+ model before it ships | `docs/RESEARCH.md` R3 | Deliberately not done |
| H3 | Order between a read and a later write is not preserved | One total order per run: sequence numbers, hash-chained | Hash-chain property tests; `AppendOnly` in TLA+ | Covered |
| H4 | The effect and its log record are not atomic | They cannot be for an external system; the activity key makes the repeat harmless. A step that touches only the Kairos database could share the transaction (R1 "transactional step group", Milestone 4) | Simulation I3; crash matrix | Covered for external effects |

## Failures the published designs do not cover but a database-backed engine has

| # | Failure case | Test |
|---|---|---|
| D1 | The connection is reset after commit, before the client reads the answer | Toxiproxy `Commits_whose_acknowledgement_is_lost_...` (workflow and activity completions retried across the fault) |
| D2 | A blackholed connection (half-open): no error, only silence | Toxiproxy `A_half_open_connection_...`: statements time out and work resumes |
| D3 | The database disappears and returns | Toxiproxy `A_database_that_disappears_and_returns_...` |
| D4 | The primary dies mid-load and a replica is promoted | `FailoverTests`: with a synchronous replica no event read before the kill is missing afterwards (hash for hash), and no run duplicates an event or an effect |
| D5 | A worker dies without bound (not a bounded count) | TLA+ `KairosReaper`: no crash budget; every run closes or is parked where an operator sees it |
| D6 | The LISTEN connection is cut, so notifications are lost | Toxiproxy `A_cut_LISTEN_connection_...`: workers fall back to polling, the notifier listens again |

## Results

Six contract tests added (they run against the in-memory store and PostgreSQL), and the network-fault and failover suites.
No defect was found in the stores: each new case passed on the first run against both. The review's value is the mapping
above; a future change that weakens one of these guarantees now fails a named test.

## Residual risk, tracked

- B10: sagas have landed without cross-workflow locks, so the case does not arise. Revisit if a lock or a cross-workflow transaction is ever added.
- B11: re-run this checklist when child workflows land.
- H2: the reduced-logging experiment (R3) must be checked against this table before any step skips its log record.
- Asynchronous replication can lose acknowledged commits at failover; the failover drill therefore uses a synchronous replica.
  Operators on asynchronous replication accept that a run may redo its last acknowledged step (the idempotency key keeps the
  effect single). Document this in the operations guide when that page is written.
