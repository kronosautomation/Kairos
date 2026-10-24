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
