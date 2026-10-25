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

## Open items

- Failed attempts' tokens are not recorded; a budget based on this undercounts retried calls. A separate record for failed attempts would need its own event field.
- Usage across a continue-as-new chain is per run; a chain total needs the run listing joined on workflow id.
- Reasoning tokens are not recorded (the plan names input, output, cached).
- Export of very large histories is in memory.
- Audit detail now ends with the head hash; consumers that parse it must tolerate the suffix.
