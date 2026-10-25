# Observability F3: `kairos replay-check` (design decisions and open items)

Written 25 Oct 2026. Internal. Plan: `docs/OBSERVABILITY-PLAN.md` (F3). Placement: Apache (`Kairos.Cli`, `ReplayChecker` in Core, `DeterminismCheck` in
Kairos.Testing). Public pages: `docs-site/concepts/deploy-safety.md`, templates in `samples/ci/`.

## What was built

- `ReplayChecker` (Core): samples runs from a store, or takes loose histories, replays each against a candidate `WorkflowRegistry`, and returns a
  `ReplayCheckReport` (verdict per history, coverage per workflow type and build). `ToText` / `ToJson` render it.
- `NonDeterminismException` now carries `Sequence`, `Recorded` and `Emitted`, filled at every place the engine detects a divergence. The text is
  built from event and command *descriptions* (type, ids, names, durations, byte counts), never from payload bytes.
- `IKairosStore.ListRunsAsync` and `GetRunGroupsAsync` (read-only, metadata only), implemented for the in-memory and PostgreSQL stores, with contract
  tests and a cross-tenant leak case each (the leak test enumerates the interface, so a method without one fails the suite).
- `HistoryFile` (Core): the `.khist` text form (previously private to the golden-corpus tests) is now public; the corpus tests use it unchanged.
- `Kairos.Cli`: the `kairos` .NET tool. `replay-check --build <assembly>` loads the build into its own `AssemblyLoadContext` (Kairos assemblies shared
  with the tool so types match), finds its `IReplayCheckSetup` (assembly attribute or `--setup`), and replays.
- `DeterminismCheck` (Kairos.Testing): the same check as a unit-test assertion.
- Azure DevOps and GitHub Actions templates (`samples/ci/`) and a docs-site page.

## Decisions

1. **Sampling is deterministic and explainable:** per workflow type and build, the newest N open runs and the newest N closed runs (closed ones
   within `--since`). Open runs ignore `--since`: they are what the new build takes over. No randomness, so a failing sample is reproducible.
2. **Coverage is part of the result.** A type and build the candidate lacks is listed as not covered; `--strict-coverage` fails on it. A check that
   replays nothing fails (exit 1): a pipeline pointed at the wrong database must not go green.
3. **Integrity before determinism.** The hash chain is verified on the stored form first; a broken chain is `IntegrityFailure`, never reported as
   nondeterminism.
4. **Nothing printed from a payload.** Verified by tests that put a canary in every payload and assert it is absent from text and JSON output,
   including the end-to-end test that runs the real executable against PostgreSQL. A different *input* is described by sizes only.
5. **Least privilege.** The tool needs `SELECT` on run and history data (the read-only role works, and the PostgreSQL end-to-end test uses it) and,
   with encryption on, the right to unwrap through the customer's key provider. It never writes. Connection strings are read from the environment by
   default so they stay out of command lines and logs.
6. **The candidate is asked, not started.** `IReplayCheckSetup` receives a `KairosOptions` so the same registration code serves the application and
   the check. Activities are not run. Sealed histories without a protector are `Unreadable` (detected from sealed failure text), with a warning
   printed when no protector is configured, because byte payloads carry no marker.
7. **Store listing fails closed.** The interface defaults throw `NotSupportedException` rather than return an empty list, and the tool exits 2.

## Open items

- Sealed *byte* payloads carry no marker, so a history without sealed failure text replayed with no protector is read as plaintext (the workflow
  then usually diverges or fails). The warning on stderr is the mitigation; a marker in the sealed form would fix it but changes stored data.
- Replay uses the full history of each sampled run in memory (bounded by `MaxEvents`, default 100,000). Very large runs should continue-as-new.
- `kairos history export` and `kairos audit verify` (F2, F6) will share `HistoryFile` and the loader.
- The Commercial dashboard/pipeline integration (a "replay check" status per deployment) is not built.
- Not run on the .NET 11 / Runtime Async build leg.
