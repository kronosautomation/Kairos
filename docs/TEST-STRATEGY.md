# Kairos — Deep Test Strategy

A durable execution engine is only worth paying for if it never loses, duplicates or corrupts work.
Ordinary unit tests do not prove that. This plan layers tests from fast and exhaustive to slow and realistic.
Every layer runs in CI; the slow ones run nightly. A milestone is not done until its rows here are green.

## Guarantees under test (the invariants)

Every deep test below checks some of these. Name them in test code as `Invariant.*`.

| # | Invariant |
|---|---|
| I1 | **No lost work:** every accepted start eventually completes, fails, or is cancelled. Nothing silently stalls. |
| I2 | **Exactly-once state:** each history event is appended once, in order, with a gap-free sequence. |
| I3 | **At-most-once side effects per idempotency key:** an activity/tool with key K takes effect once, whatever the retries, crashes or lease losses. |
| I4 | **Deterministic replay:** replaying a history with the same code yields the same commands. Any divergence is detected, never silently applied. |
| I5 | **Tenant isolation:** no query, API, cache, log, metric or error message exposes another tenant's data. Fails closed. |
| I6 | **Integrity:** the hash chain verifies for every run; any tamper or reorder is detected. |
| I7 | **Confidentiality:** the server never stores or logs plaintext payloads when client-side encryption is on. |
| I8 | **Fencing:** a node that lost its lease cannot write. |
| I9 | **Bounded resources:** memory, history size, cache bytes and queue depth stay within configured limits under load. |
| I10 | **Version safety:** in-flight runs keep running correctly across deploys (pinning + patch API). |
| I11 | **Compensation completes:** every compensation started for a failed or cancelled run eventually completes, exactly once. |

## Layer 1 — Unit and property-based tests (every commit)

- xUnit for behaviour. **Property-based tests (FsCheck or CsCheck)** generate random workflows (random mixes of activities, timers,
  signals, child calls, failures) and assert I2, I4, I6 for every generated case. Shrinking gives minimal failing workflows.
- Replay round-trip property: run → record history → replay N times → identical commands (I4).
- Hash chain property: any single-byte change, insertion, deletion or swap in history is detected (I6).
- Serialization round-trip for every event type; source-generated System.Text.Json only.

## Layer 2 — Deterministic simulation testing (every commit, short; nightly, long)

The core of the "deep" suite. Run the whole engine (scheduler, store, workers, timers, clock, network) inside one process
with **all non-determinism injected from a single seed**: virtual clock, seeded random, simulated store and transport.

- A simulator drives thousands of workflows and injects faults at random points: worker crash, server crash, lost or
  duplicated messages, delayed responses, clock jumps, lease expiry, store write failures, partial commits.
- After every run it checks I1–I4, I8 and I9.
- **Any failure prints its seed; the seed reproduces the exact failure bit-for-bit.** Failing seeds are added to a
  regression corpus that runs on every commit.
- CI: 1,000 seeds per commit; nightly: millions of seeds for hours.
- Design rule this forces: Core takes `TimeProvider`, a seeded RNG, and abstract transport/store. No hidden `DateTime.Now`,
  `Task.Delay`, thread pool timing, or static state in Core.

## Layer 2b — Formal model (TLA+)

- `spec/Kairos.tla` models runs, history append, leases with fencing, crashes, replay, signals, cancel and compensation.
- TLC model-checks I1, I2, I3, I8, I11 at small bounds in CI (nightly). Every model invariant has a matching simulator check.
- Basis: Microsoft's formal Durable Functions semantics (OOPSLA 2021). See docs/RESEARCH.md (R2).

## Layer 3 — Systematic concurrency testing

- Explore thread interleavings of the dispatcher, lease manager, sticky cache and append path with a systematic
  concurrency tester (evaluate Microsoft Coyote for .NET; verify current maintenance status before adopting).
- Targets: two workers completing the same task, lease handoff during completion, cache eviction during replay,
  concurrent signals and cancel on one run.

## Layer 4 — Crash-at-every-step tests (every commit for small cases, nightly for all)

- For a set of reference workflows, kill the worker (and separately the server) **at every possible step boundary**,
  including between "activity succeeded" and "completion recorded", then restart and assert I1–I3.
- Tool/agent variant: a fake external system records every call with its idempotency key; assert each key takes
  effect once (I3).

## Layer 5 — Real Postgres integration and fault injection (every commit + nightly)

- **Testcontainers** for PostgreSQL across supported versions (matrix: 15, 16, 17, and the latest release).
- **Cross-tenant leak tests (I5):** for every repository method and API endpoint, create data as tenant A and assert
  tenant B gets nothing, including through RLS with a forgotten `WHERE`, list endpoints, search, error messages, metrics labels.
- **Role tests:** dataplane role cannot run DDL or read control-plane tables; control plane cannot read payload tables.
- **Network faults with Toxiproxy:** latency, packet loss, connection resets, half-open connections between workers,
  server and Postgres. Assert I1–I3, I8.
- **Failover drills:** promote a replica mid-load; kill the primary; assert no lost or duplicated events.
- `LISTEN/NOTIFY` loss: drop notifications and confirm workers still make progress (fallback polling).

## Layer 6 — History compatibility and versioning (every commit)

- A **golden history corpus**: real histories recorded from every released version, stored in the repo.
- Every new build must replay every corpus history without non-determinism (I4), or, where a change is intentional,
  the patch API must cover it (I10).
- Deploy simulation: start runs on build N, deploy build N+1 mid-flight, assert pinned runs finish on N and patched
  changes apply correctly.
- Runtime matrix: .NET 10 and .NET 11 with Runtime Async on; Native AOT build of Server; JIT and AOT must produce identical replay results.

## Layer 7 — Security tests (every commit + release)

- AuthZ matrix test: every role × every API operation × own/other namespace → expected allow/deny, generated from one table.
- Confidentiality (I7): with encryption on, scan Postgres tables, blobs, logs and traces for known plaintext canaries; must find none.
- Tamper tests: edit history rows directly in Postgres; replay and verification must reject them (I6).
- API keys: stored only as HMAC; constant-time compare; expired and revoked keys rejected.
- Fuzzing (SharpFuzz or equivalent) of every parser that reads untrusted input: gRPC/HTTP payload decoding, history
  deserialization, MCP and A2A message handling, CloudEvents parsing.
- Static and supply chain: analyzers as errors, NuGetAudit, container image scan, secret scanning; DAST against a staging cell before release.

## Layer 8 — Provider conformance (every adapter, every commit)

- `Kairos.Conformance`: one test suite parameterized by provider. Every store, key, identity, blob and event adapter
  must pass the same tests on its real service (cloud tests nightly against sandbox accounts; local emulators per commit
  where they exist: Azurite, LocalStack or equivalents, MinIO).
- Includes failure semantics: throttling, eventual consistency, key rotation, credential expiry.

## Layer 9 — Agent and protocol tests

- **Recorded model fixtures:** a fake `IChatClient` that replays recorded responses, including tool calls, refusals,
  malformed JSON, timeouts and rate-limit errors. Assert durability, budgets and policies without calling a real model.
- Replay never calls the model: count model invocations across crash/replay cycles (must equal the number of unique steps).
- Approval tests: approvals that arrive late, twice, after cancel, from the wrong identity.
- Budget tests: token, cost, step and time budgets stop or pause runs exactly at the limit.
- **MCP conformance** against the 2026-07-28 spec (stateless requests, Tasks lifecycle, auth) and **A2A** task-state
  mapping, using official conformance tools where they exist, plus our own contract tests.
- A small live-model smoke suite (nightly, low budget) to catch provider API drift.

## Layer 10 — Performance, soak and resource tests (nightly + release)

- BenchmarkDotNet gates (throughput and allocations) on replay, append, dispatch; regressions fail CI.
- Load tests (NBomber or k6): steps/second per vCPU, p50/p99 latency for start and step completion, Postgres CPU/IO per million steps.
- **Soak:** 24-hour (nightly) and 72-hour (release) runs with mixed workflows and fault injection; assert I1, I9: flat memory,
  no handle/connection leaks, bounded queue depth.
- Noisy neighbour: one tenant floods; others' p99 stays within target.

## Layer 11 — Mutation testing (weekly)

- Stryker.NET on Core and Persistence. Target a high mutation score on replay, hash chain, leasing and RLS code;
  surviving mutants become new tests.

## Layer 12 — Release and disaster-recovery drills (each release)

- Backup and point-in-time restore of a cell; runs resume correctly after restore.
- Region / cross-cloud recovery drill once the DR milestone lands; measure actual recovery time.
- Upgrade and rollback of the server with live traffic.

## Milestone mapping

| Milestone | Test layers that must be green |
|---|---|
| M2 Postgres + embedded | 1, 2 (Core), 4, 5, 6, 7 (isolation, tamper, roles), 10 (benchmarks) |
| M3 Providers | 8, 7 (confidentiality), fuzzing of event parsing |
| M4 Agents | 9 (fixtures, budgets, approvals, replay call counts), 4 (tool idempotency) |
| M5 MCP + A2A | 9 (protocol conformance), 7 (authz matrix for MCP), fuzzing of MCP/A2A |
| M6 Azure + AWS | 8 against real cloud sandboxes, 12 (backup/restore) |
| M7 Server | 3, 7 (full authz matrix, DAST), 10 (load, soak, noisy neighbour) |
| M8 Reach | 12 (cross-cloud DR drill) |

## Tooling summary

xUnit · FsCheck/CsCheck · in-house deterministic simulator · Coyote (evaluate) · Testcontainers · Toxiproxy ·
SharpFuzz · Stryker.NET · BenchmarkDotNet · NBomber or k6 · Azurite / LocalStack / MinIO emulators · golden history corpus.
