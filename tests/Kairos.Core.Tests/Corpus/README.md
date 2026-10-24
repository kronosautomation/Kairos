# Golden history corpus

Histories recorded by a released build, replayed by every later build (docs/TEST-STRATEGY.md, layer 6).

- `v1/` was recorded by the Milestone 2 build. Each file says which workflow code replays it (`replay-with`) and how the run ended (`outcome`).
- A replay failure is a compatibility break for runs in flight. Fix the engine, or gate the intentional change with the patch API
  (`IsPatched`) and keep the old history replaying. **Never re-record or edit a file to make a failure go away.**
- Record the next release into a new directory: `KAIROS_CORPUS_RECORD=<dir> dotnet test --filter Record_the_corpus`, then add the directory.
- Any new `HistoryEventType` must appear in at least one corpus file (`The_corpus_exists_and_covers_every_event_type`).
- The workflow code the corpus replays with (`GoldenCorpus.Resolve`, `SimPrograms`, `ReferenceWorkflows`) is frozen for the same reason.
- `m3/` holds histories recorded by the Milestone 3 build whose format differs from `v1/` (signals carrying ids). `v1/` histories without ids
  must keep replaying byte for byte; the unsignalled `v1/` files are identical to what the M3 build records.
