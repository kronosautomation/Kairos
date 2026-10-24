# Group-commit experiment (RESEARCH.md R3)

Question: does committing several workflow-task completions in one Postgres transaction beat one transaction per completion?

Harness: `dotnet run -c Release --project benchmarks/Kairos.Benchmarks -- groupcommit` (Testcontainers `postgres:17`, or point
`KAIROS_BENCH_PG` at any server). Closed loop: N workers each claim a task and complete it (boundary + one scheduled activity =
3 history rows, a run update, a task delete, an activity-task insert). Group commit = `WorkflowTaskGroupCommitter`
(4 concurrent writers, `CompleteWorkflowTasksAsync`: one round trip, one commit per batch).

Run of 24 Oct 2026: Postgres 17.11 in Docker Desktop on a Windows laptop (16 client cores, default fsync, local disk).
Indicative only: absolute numbers are low because of the Docker Desktop VM, so read the ratios.

| mode | workers | completions/s | avg batch | p50 ms | p99 ms |
|---|---:|---:|---:|---:|---:|
| single commit | 4 | 240 | 1.0 | 5.52 | 9.64 |
| group 8 / 1ms | 4 | 139 | 2.1 | 18.50 | 30.11 |
| group 32 / 2ms | 4 | 137 | 2.2 | 19.00 | 30.46 |
| single commit | 16 | 438 | 1.0 | 11.74 | 32.08 |
| group 8 / 1ms | 16 | 347 | 3.1 | 28.33 | 68.31 |
| group 32 / 2ms | 16 | 348 | 3.4 | 28.46 | 63.55 |
| single commit | 64 | 447 | 1.0 | 66.90 | 178.48 |
| group 8 / 1ms | 64 | 419 | 8.0 | 115.18 | 187.27 |
| group 32 / 2ms | 64 | 384 | 8.8 | 123.98 | 257.69 |

An earlier run with a single writer loop (one batch in flight) reached 709/s at 64 workers with batch 32 against 405/s for
single commits, but did not reproduce once four writers were allowed, so treat it as noise or an artefact of serialising writes.

## Reading

- Here group commit **does not pay**: it adds linger and queueing latency and the server still executes one statement per task,
  so the only saving (fewer commits) is small when many connections already commit in parallel (Postgres group-commits WAL
  flushes itself).
- The bottleneck at 64 workers is the database/VM, not the client: single commit also stops scaling past ~16 workers.
- Decision: keep one transaction per workflow task (the default; `AddKairos` does not use the group committer). Keep the
  committer and the batch API as an experiment.
- Revisit before Milestone 7 on production-like storage (fsync-bound NVMe or network disks, where commit latency dominates),
  with a larger run count and several repetitions. A fairer test also needs realistic task payload sizes.

Not yet done from R3: the Halfmoon-style reduced-logging experiment for read-only steps (it must keep I2-I4 green in the
simulator and the TLA+ model first), and comparison with published Netherite/Halfmoon numbers.
