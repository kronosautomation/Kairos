# Kairos — Research Reading List and What We Take From It

Papers and designs behind competing durable-execution systems, and the concrete actions they drive for Kairos.
Reviewed 24 Oct 2026.

| Source | Who it backs | Key idea | Kairos action |
|---|---|---|---|
| Li, Kraft, Stonebraker, Zhou. *Consistency and Correctness in Data-Oriented Workflow Systems.* CIDR 2026. https://rmarcus.info/dbscholar/papers/h7bc721b4ef485a35 | DBOS | AC/DC: ACID-style correctness for workflows. Steps run exactly once and compensations are guaranteed to complete ("durable sagas"). Transactions win at low contention; durable sagas win at high contention or with long steps. | **R1** Durable sagas + compensation primitive (M4) |
| Skiadopoulos et al. *DBOS: a DBMS-oriented Operating System.* PVLDB 15(1), 2021. Cafarella et al. *DBOS: A Proposal for a Data-Centric Operating System.* arXiv 2007.11112, 2020. | DBOS | Run durable state in the database; workflows as database-backed code. | Validates the Postgres-only, embedded design. |
| Burckhardt et al. *Durable Functions: Semantics for Stateful Serverless.* OOPSLA 2021. https://www.microsoft.com/en-us/research/publication/durable-functions-semantics-for-stateful-serverless/ | Microsoft Durable Functions / Durable Task | Formal semantics of replay-based orchestration, with proofs. | **R2** Formal TLA+ model of Kairos replay, leases and fencing (M2–M3) |
| Burckhardt et al. *Serverless Workflows with Durable Functions and Netherite.* PVLDB 15, 2022. https://www.vldb.org/pvldb/vol15/p1591-burckhardt.pdf | Microsoft (Netherite backend, support ends 2028) | High-throughput engine: partitioned, batched commits with speculative execution. | **R3** Batching / group-commit experiments in benchmarks (M2) |
| Zhang, Cardoza, Chen, Angel, Liu. *Fault-tolerant and Transactional Stateful Serverless Workflows* (Beldi). OSDI 2020; extended arXiv 2010.06706. | Academic | Exactly-once and cross-function transactions on Lambda + DynamoDB without a central coordinator. | **R4** Correctness checklist for invariants I2/I3 |
| *Halfmoon: Log-Optimal Fault-Tolerant Stateful Serverless Computing.* SOSP 2023. https://doi.org/10.1145/3600006.3613154 | Academic (successor to Boki, SOSP 2021) | Logging only reads or only writes is enough for exactly-once; 1.5–4× less logging than Boki. | **R3** Evaluate reduced logging for read-only steps; **R4** checklist |
| Restate: *The anatomy of a durable execution stack from first principles.* https://restate.dev/blog/the-anatomy-of-a-durable-execution-stack-from-first-principles/ | Restate | Log-first architecture (Flink StateFun lineage, Delos virtual consensus, LogDevice, Raft). | Contrast for positioning: Kairos is database-first. Revisit if Postgres write throughput becomes the ceiling. |

## Actions

### R1 — Durable sagas and compensation (Milestone 4, alongside agents)
- `ctx.Compensate(async () => ...)` registers an undo step after a successful step; on failure or cancel, compensations run
  in reverse order, each as a durable, idempotent, exactly-once step that is retried until it completes.
- Compensation progress is recorded in history like any step, so a crash mid-rollback resumes the rollback.
- Agent use: undo tool side effects (refund, cancel booking) when a run is rejected at approval.
- Tests: crash-at-every-step during rollback; simulation invariant "every started compensation completes" (new I11).
- Benchmark per the CIDR 2026 finding: saga vs single-transaction for short, low-contention workflows; offer a
  "transactional step group" fast path when all steps touch only the Kairos database.

### R2 — Formal model in TLA+ (start in M2, complete by M3)
- `spec/Kairos.tla`: runs, history append, task leases with fencing tokens, worker crash, replay, signals, cancel.
- Model-check with TLC for small bounds: invariants I1 (no lost work), I2 (gap-free history), I3 (at-most-once per key),
  I8 (fencing).
- Keep the model and the deterministic simulator in step: every invariant in the model has a matching simulator check.
- Publish the spec: a trust signal for regulated buyers.

### R3 — Throughput experiments (M2 benchmarks, decisions before M7)
- Group commit: batch several workflow-task completions into one Postgres transaction under load; measure p99 latency
  vs throughput.
- Read-only steps: evaluate skipping full result logging where replay can safely re-derive the value (Halfmoon-style),
  only where invariants I2–I4 still hold in the simulator and the TLA+ model.
- Compare against published Netherite and Halfmoon results where the workloads are comparable.

### R4 — Correctness checklist
- Map the exactly-once definitions in Beldi and Halfmoon onto invariants I2/I3 and add any failure case they cover that our
  crash-at-every-step and simulation suites do not.
