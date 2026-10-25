# Observability plan: six features that set Kairos apart

Decided 25 Oct 2026 (Aaron: "do all 6"). Internal planning document (not for docs-site; the disclosure policy applies).

Every competitor ships a run dashboard (Temporal Web UI, Inngest traces, Trigger.dev realtime, DBOS Conductor, Hatchet). A
dashboard alone is not a differentiator. These six are, because each builds on something only Kairos has: client-side
encryption, deterministic replay with a hash-chained history, and a .NET-only focus.

Order of work: F4 and F3 now (Apache, independent of the server), then F2 and F5, then F1 and F6 with the dashboard in
Milestone 7. Each feature lands with tests in the existing categories and a review doc in docs/reviews/.

| # | Feature | Placement (REPO-LAYOUT.md) | Edition (LICENSING.md) |
|---|---|---|---|
| F1 | Zero-knowledge dashboard | Kairos.Dashboard (Kairos.Commercial); core metadata APIs in Kairos | Pro |
| F2 | Replay a production run in your debugger | Kairos.Testing + `kairos` CLI (Kairos); dashboard replay view stays Pro | Community (CLI, replayer), Pro (dashboard button) |
| F3 | Deploy safety gate | `kairos replay-check` CLI + test helper (Kairos) | Community |
| F4 | .NET Aspire and OpenTelemetry, zero setup | Kairos.Hosting + Kairos.Aspire.Hosting (Kairos); packaged dashboards in Kairos.Telemetry | Community (spans, metrics, Aspire), Pro (packaged dashboards) |
| F5 | Agent cost tracking | Token/usage recording in Kairos.Agents (Kairos); pricing tables, budgets, cost views in Kairos.Agents.Policies | Community (usage recorded), Pro (cost, budgets) |
| F6 | Signed audit evidence export | Export in Kairos.Dashboard.Enterprise (Commercial); offline verifier `kairos audit verify` (Kairos) | Business (export), Community (verifier) |

## F1 Zero-knowledge dashboard
The dashboard server and the Kairos server never see plaintext. It renders metadata (run ids, workflow type, status, step
names, timings, attempt counts, hash-chain status) and fetches payloads as ciphertext; the browser decrypts with WebCrypto
(AES-256-GCM, the same envelope format and AAD binding as the .NET client).
- Key path: the browser asks a **key broker the customer hosts** (a small endpoint in their environment, using their KMS/Key
  Vault/Vault identity) to unwrap the data key for one value. Kairos never runs the broker and never sees a key. Short-lived,
  audience-bound broker tokens; the broker logs every unwrap with the viewer's identity.
- Metadata hygiene: step, signal and activity names can themselves be sensitive. Option to store them as keyed HMAC labels with
  a name map decrypted client-side. Document clearly what metadata the server sees.
- CSP: no third-party script, `connect-src` limited to the API and the broker; Subresource Integrity on dashboard assets.
- Tests: a browser test proving no plaintext crosses the network (intercept every response); decrypt parity tests between
  .NET and WebCrypto implementations using the golden corpus.

## F2 Replay a production run in your debugger
`kairos history export <run>` writes one run's history (ciphertext, hash chain intact) to a file; `KairosReplayer.ReplayAsync`
in Kairos.Testing replays it against local workflow code under the debugger, decrypting locally with the developer's own key
identity. Breakpoints in workflow code hit in the original order; activities are not executed (their recorded results are used).
- Export requires `Operation.ExportHistory` (new; operator role and above), is written to the audit log with who and why
  (`--reason` required), and verifies the hash chain on export and on load.
- Optional redaction: export metadata only, or re-encrypt for a named recipient key.
- Nondeterminism found during replay is reported with the first diverging command (expected vs actual), the same output F3 uses.
- Later (Pro): "Replay locally" button in the dashboard that produces the export command.

## F3 Deploy safety gate
`kairos replay-check --build <path> --sample <n|all> --since <time>` replays a sample of recent histories against a candidate
build and fails (non-zero exit) on any nondeterminism, listing workflow type, run id and first diverging command.
- Runs inside the customer's environment (a pipeline job with unwrap rights), so production payloads never leave it. Output is
  pass/fail and command-level diffs only, never payload contents.
- Sampling covers every workflow type and every pinned build version currently in flight, plus the golden corpus.
- Azure DevOps and GitHub Actions templates in the docs; a test-helper equivalent for unit tests.

## F4 .NET Aspire and OpenTelemetry, zero setup
- `AddKairos` registers an ActivitySource and Meter (`Kairos`) automatically: spans for run, workflow task, activity attempt,
  timer, signal, approval; metrics for queue depth, lease expiries, retries, stuck runs, replay time, history size.
- Agent model and tool steps follow the OpenTelemetry GenAI semantic conventions (track the spec: still Development status).
  **Message content capture is off by default and cannot carry plaintext payloads**: spans hold ids, names, sizes and timings.
- Trace context propagates through workflows: a run started from an HTTP request is a child of it; activities continue the trace
  across retries and process restarts (context stored in history, not in memory).
- `Kairos.Aspire.Hosting` (Aspire hosting integration): `builder.AddKairos("kairos").WithReference(postgres)`, so runs show
  in the Aspire dashboard with no configuration. Health checks for store and workers.
- Pro (Kairos.Telemetry): packaged Grafana and Azure Monitor workbooks, alert rules for stuck runs and lease churn.

## F5 Agent cost tracking
- Every recorded model step stores usage from `Microsoft.Extensions.AI` (`UsageDetails`: input, output, cached tokens) in its
  history event. Recorded once, so a replay never double-counts.
- Pro: a pricing table per model (customer-editable, versioned, effective dates) turns usage into money per step, run,
  workflow type, agent and tenant. Budgets (tokens, money, steps) are enforced as deterministic policy decisions recorded in
  history; exceeding one pauses the run for approval or fails it, per policy.
- "Saved by durability": when a run resumes, the cost of completed model steps that were not re-run is reported.

## F6 Signed audit evidence export
- Business: export for a time range or a set of runs: who started each run, every approval with the verified approver, every
  policy decision, credential issuance, history exports (F2) and admin actions, plus the hash-chain heads of each run.
- Signed with the customer's KMS or Key Vault key (asymmetric), never a Kairos key. Format: JSON Lines plus a detached
  signature and a manifest.
- Community: `kairos audit verify <export>` verifies the signature and every chain offline, so an auditor needs no licence and
  no access to the system.

## Security rules that apply to all six
- No plaintext payload leaves the customer's processes: not in spans, logs, metrics, exports or the dashboard server.
- Every new read path is tenant-scoped under RLS and has a cross-tenant leak test.
- Every export is authorised, audited and hash-chain verified.
