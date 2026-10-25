# Kairos formal model (TLA+)

`Kairos.tla` models the engine protocol: history append, workflow tasks with leases, fencing tokens and an optimistic
sequence check, activity tasks with fencing, worker crashes, zombie (late) completions, and signals. It is the formal
counterpart of the deterministic simulator (`tests/Kairos.Testing/Simulation`); see `docs/RESEARCH.md` (R2) and
`docs/TEST-STRATEGY.md` (Layer 2b).

| Model | Simulator check | Meaning |
|---|---|---|
| `Liveness` | I1 "never closed" | no lost work |
| `I2`, `AppendOnly` | I2 structure + hash chain | each event once, history only grows |
| `I3` | I3 external effects | a recorded result implies its idempotency-keyed effect |
| `ActFencing` (mutation) | stale completions rejected | I8, proved by `KairosUnfenced.cfg` finding a violation |
| `I9` | I9 store drained | closed runs leave no work |

Run: `TLA2TOOLS=/path/to/tla2tools.jar ./check.sh` (Java 11+). `Kairos.cfg` must pass; `KairosUnfenced.cfg` must fail.

**Status:** first TLC run (2026-10-24) found a liveness counterexample caused by the model, not the engine: orphaned
leases (holder crashed) had no fair expiry, so TLC could stop time. Fixed by splitting expiry into fair `Recover*` (orphaned)
and bounded, unfair `Expire*` (premature, creates zombies). Earlier note: written alongside Milestone 2 but not yet run through TLC (the jar could not be fetched in the authoring
environment). Treat it as unverified until `check.sh` is green; fix the spec, not the invariants, if TLC disagrees.

Not yet modelled: cancel (not implemented in the engine), compensation / I11 (Milestone 4 sagas), continue-as-new, child workflows.

## Reaper model (`KairosReaper.tla`)

Models lease expiry and what the engine does about repeated failure, **with no bound on worker crashes**. The state space stays finite
because every expiry consumes an attempt: an activity gets `MaxActAttempts` before its failure is recorded as terminal and the
workflow is told; a workflow task gets `MaxWfAttempts` expiries before it is parked (kept and visible, never offered again until an
operator resumes it). Live-holder expiry (zombies) is unbounded; operator actions (resume, terminate) are bounded by `MaxOperatorOps`.

| Property | Meaning |
|---|---|
| `Resolved` (`<>[]`) | whatever the workers do, every run ends up closed (success, failure, terminated) or parked for an operator; nothing is silently dropped |
| `I2`, `AppendOnly` | well-formed append-only history, including the recorded attempt failures |
| `I3` | a recorded result implies its idempotent effect |
| `I9` | closed runs leave no runnable work; attempt counters never pass their caps, so the reaper cannot loop forever |
| `ParkedIsVisible` | a parked task belongs to an open, started run |

`KairosReaperUnrecordedQuick.cfg` is the mutation: exhausted tasks vanish without a record. TLC must find a run that never resolves.
Run with the other models: `TLC_MODE=quick|full ./check.sh` (quick: 273k distinct states, seconds; full: two runs and three attempts, nightly).
Backoff delays are abstracted away (a pending task is offered eventually).
