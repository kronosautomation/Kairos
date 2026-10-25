# Observability F2 and F5 (Community): history export, local replay, recorded token usage (design decisions and open items)

Written 25 Oct 2026. Internal. Plan: `docs/OBSERVABILITY-PLAN.md`. Public page: `docs-site/concepts/replay-locally.md`.

## F2: history export and local replay

- `Operation.ExportHistory` (operator and admin). `HistoryExporter.ExportAsync` checks `AccessPolicy` first, requires a reason (non-blank, 500 characters), calls
  `IKairosStore.ExportHistoryAsync`, verifies the chain, and only then produces a file.
- **Read and audit are one transaction** (PostgreSQL: the same transaction as the history read; in-memory: one lock). No export without an audit entry, no audit
  entry for a read that failed. A run that does not exist (or is in another scope) returns null and records nothing. Cross-tenant leak case added.
- The audit detail carries the reason and `(head <hash of the last event>)`. That anchors a file to the system: a file can be recomputed by anyone with the whole
  algorithm (true of every hash chain), but not made to match the head the system recorded.
- **Role enforcement is at the caller.** The CLI acts as an operator in the scope it is given; the real gate is the database role (the read-only role cannot insert
  into the audit log, so it cannot export: tested end to end). A server that authenticates people (Milestone 7) enforces the role matrix on the principal.
- **Metadata-only exports** (changed after review): linkage alone is forgeable, so the file carries a SHA-256 digest of the one field removed from each event and a
  second chain `C[i] = SHA-256(C[i-1] || label || redacted event || stored prev hash || stored hash || digest)`. The original chain cannot be recomputed without the
  payloads (SHA-256 runs over bytes that contain them), hence the second chain. Editing a name, a digest or a stored hash, reordering, dropping or inserting events,
  and presenting five-field lines without the mode line, are all rejected (tests, including a forgery that re-chains the redacted events so that linkage passes, and a
  mutation check on the digest verification). Such a file is refused by `KairosReplayer` (no payloads) and must be treated by any future audit verifier as digest-chain
  verified, not content-verified.
- `KairosReplayer` (Kairos.Testing) wraps `ReplayChecker.ReplayAsync` (new: also returns the replay result), so divergence output is identical to `replay-check`.
  The workflow code runs once, in the caller's process. Sealed files open only with the protector the developer passes.
- CLI: `kairos history export` (refuses to overwrite without `--force`; refuses before reading or recording anything when the arguments are incomplete) and
  `kairos history verify`.

## F5 (Community): usage recorded in history

- `UsageRecord(InputTokens, OutputTokens, CachedInputTokens, Model)` is a trailing optional field of `ActivityCompleted`. Events without it encode, hash and
  decode exactly as before: the whole golden corpus (`v1`, `m3`) passes unchanged, and a new `v2/usage-model-steps.khist` entry (two model steps, one with a
  cached prompt) was recorded by this build. The decoder rejects impossible records (negative counts, cached above input, over-long model).
- `ActivityContext.ReportUsage` lets any activity report usage; the worker copies it into the completion event. The chat step reports the provider's figures and the
  model that answered (falling back to the alias). Only the completing attempt is recorded: failed attempts that burned tokens are not (open item).
- `KairosClient.GetUsageAsync(run)` and `RunUsage.From(history)` sum per run and per model. Counts are unsealed metadata, so they are readable without a key (tested
  with sealed payloads). A continued run reports its own history only.
- Pricing tables, budgets and cost views stay in Kairos.Agents.Policies (Pro); they apply a price table to these figures, so prices can change without touching history.

## Follow-up review fixes

- **Audit identity is not self-asserted.** `AuditEntry` gains `SessionUser` and `ClientAddress` (PostgreSQL `session_user` and `host(inet_client_addr())`, written in the INSERT,
  migration `0006`), `ClaimedActor` (the supplied name, labelled a claim) and `VerifiedPrincipal`. For exports, `Actor` is the verified principal when `KairosClient.ExportHistoryAsync`
  was given one that an authenticator verified, otherwise `db:<session_user>`; it is never the supplied name. The CLI uses `HistoryExporter.ExportClaimedAsync`, whose `--actor` only
  becomes `ClaimedActor`. Tested in both stores, in the CLI, and end to end against PostgreSQL (`--actor ceo@example.com` records `kairos_dataplane` and a client address).
  `session_user` is used, not `current_user`: `SET ROLE` does not change it. All other audit inserts (resume, fail) also record the database facts; their `Actor` is still the supplied
  name (open item below).
- **Usage on failed attempts.** `ActivityFailed` carries the same optional trailing `UsageRecord`; the worker records what the attempt reported before it failed. `RunUsage` keeps the
  completed-step figures as they were and adds `Failed` (attempts and tokens spent on failed attempts) and `Total`, overall and per model. New golden entry
  `v2/usage-failed-attempt.khist`; the existing corpus files are untouched.

## Open items

- `Principal` is a plain record, so "verified" is a contract (it must come from an authenticator), not something the type enforces. `ExportAsync(Principal)` documents it; a signed principal would enforce it.
- Resume and fail-run audit entries still use the caller-supplied name as `Actor` (their database facts are recorded beside it).
- A failed attempt that never reports usage (the client threw before a response arrived) cannot be counted; the provider may still have charged for it.

- Usage across a continue-as-new chain is per run; a chain total needs the run listing joined on workflow id.
- Reasoning tokens are not recorded (the plan names input, output, cached).
- Export of very large histories is in memory.
- Audit detail now ends with the head hash; consumers that parse it must tolerate the suffix.
