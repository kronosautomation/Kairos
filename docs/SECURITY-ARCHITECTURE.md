# Kairos — Security & Separation Architecture

Kairos is a durable workflow engine for .NET: workflows are plain C# (async/await),
every step is checkpointed to an event history, and runs survive crashes and deploys.
Security and tenant isolation are design constraints from day one, not add-ons.

## 1. Core principle: the engine never runs customer code

- Customer workflow and activity code runs only in **customer-hosted workers** (their VMs, containers, functions).
- Workers connect **outbound** to Kairos, poll for tasks, and report results. Kairos never calls into customer networks.
- The server stores and orders events. It does not execute or deserialize customer types.
- Result: a compromise of the Kairos service cannot run code in a customer environment, and customer code cannot reach other tenants.

## 2. Plane separation

| Plane | Responsibility | Exposure |
|---|---|---|
| **Data plane** (`Kairos.Server`) | Start/signal/cancel runs, task polling, history | Public, mTLS or token auth, per-namespace scoped |
| **Control plane** (`Kairos.Admin`) | Tenants, namespaces, keys, quotas, billing | Private network only, separate deployment, separate identity, separate DB role |
| **Persistence** | Postgres | Private endpoint only, no public IP |

The data plane cannot create tenants or keys. The control plane cannot read workflow payloads.

## 3. Tenant isolation (defense in depth)

1. **Namespace = isolation boundary.** Every row carries `tenant_id` + `namespace_id`.
2. **Postgres Row-Level Security** with `FORCE ROW LEVEL SECURITY`. The server connects as a non-owner role and sets
   `app.tenant_id` per transaction (`SET LOCAL`), so a missing `WHERE` clause cannot leak rows.
3. **Separate database roles:**
   - `kairos_migrator` — DDL only, used by migrations in CI/CD, never by running services.
   - `kairos_dataplane` — DML on workflow tables, bound by RLS, no DDL.
   - `kairos_controlplane` — DML on tenant/key/quota tables only, no access to payload tables.
   - `kairos_readonly` — reporting/support, RLS-bound.
4. **Enterprise tier:** dedicated database (or dedicated cluster) per tenant, same code path.
5. **Noisy-neighbor limits** per namespace: request rate, concurrent runs, payload size, history length, timer count.

## 4. Data protection

- **Client-side payload encryption (default on).** The worker SDK encrypts inputs, outputs, and signal payloads with
  envelope encryption (AES-256-GCM data keys, wrapped by the customer's KMS / Key Vault key). Kairos stores ciphertext
  only and never holds the customer's key. Search attributes stay plaintext and are opt-in per field.
- **Server-side at-rest encryption** on storage and backups as a second layer.
- **TLS 1.3 everywhere**, including server ↔ Postgres.
- **No secrets in history.** SDK analyzers flag workflow inputs typed as credentials; secrets come from the worker's own vault.

## 5. Identity & access

- **Workers:** mTLS client certificates or short-lived OIDC client-credential tokens (≤ 1 hour). No long-lived shared secrets.
- **API keys** (for humans/CI): shown once, stored as HMAC-SHA256 with a server-side pepper held in Key Vault, scoped to one
  namespace and one role, expiring by default.
- **Roles per namespace:** `admin`, `operator` (start/signal/cancel/terminate), `worker` (poll/complete only), `reader`.
  Least privilege: a worker credential cannot start or read other workflows.
- **Control plane** uses workforce SSO (Entra ID / OIDC) with MFA, never API keys.

## 6. Integrity & audit

- **Hash-chained history:** each event stores `SHA-256(prev_hash || event)`, so tampering or reordering is detectable.
- **Append-only audit log** of every control-plane action and every privileged data-plane action (terminate, reset, key use).
- Optimistic concurrency on run state (version column), so no lost updates under concurrent workers.

## 7. Workflow correctness as a security property

- Roslyn analyzers block non-deterministic APIs inside workflow code (`DateTime.Now`, `Guid.NewGuid`, `Random`,
  direct I/O, `Task.Run`, static mutable state) and steer to `ctx.Now`, `ctx.NewGuid`, activities.
- Replay verification: a worker that produces a command sequence differing from history fails the task rather than
  silently diverging.

## 8. Platform & supply chain

- Central package management, NuGet lock files, `NuGetAudit` set to fail on high/critical vulnerabilities.
- `TreatWarningsAsErrors`, nullable enabled, analyzers on, no reflection-based serialization (System.Text.Json source generators).
- Native AOT, non-root distroless containers, read-only root filesystem, no shell in the image.
- Secrets via managed identity and Key Vault; nothing in config files or env files committed to git.
- Signed container images and SBOM generation in CI.

## 9. Solution layout (separation in code)

```
src/
  Kairos.Abstractions     contracts only, zero dependencies
  Kairos.Core             history model, replay, state machine (no I/O)
  Kairos.Persistence.Postgres   RLS-aware storage, migrations
  Kairos.Server           data-plane API (gRPC + HTTP)
  Kairos.Admin            control-plane API (separate host)
  Kairos.Worker           customer SDK: workflows, activities, payload encryption
  Kairos.Analyzers        determinism + secret-handling analyzers
tests/
  Kairos.Core.Tests
  Kairos.Persistence.Tests   includes cross-tenant leak tests that must fail closed
  Kairos.Worker.Tests
```

Dependency rule: Core depends only on Abstractions; Server/Admin never reference Worker; Worker never references Persistence.

## 10. Performance, redundancy & memory (top-priority design goals)

### Speed
- **One transaction per workflow task:** append new events, update run state, and enqueue follow-up tasks/timers in a
  single Postgres round-trip (batched statements). No read-modify-write chatter.
- **Push, not poll:** workers long-poll; the server wakes them via Postgres `LISTEN/NOTIFY`, with `FOR UPDATE SKIP LOCKED`
  task claims so many workers never contend on the same row.
- **Sticky execution:** a worker keeps hot workflow state cached, so a new task only replays the *new* events.
  Full replay happens only on cache miss or failover.
- gRPC over HTTP/2 between workers and server, binary payloads, prepared statements, connection pooling.
- Tables partitioned (history by run-id hash, timers by due time) so indexes stay small and hot.
- **Benchmarks are gates:** BenchmarkDotNet suites for replay, append, and task dispatch run in CI and fail on regression.

### Redundant operation (high availability, no duplicate side effects)
- **Stateless server nodes**, N+1 behind a load balancer, any node can serve any tenant. Rolling deploys with graceful drain.
- **Postgres HA:** zone-redundant primary with a synchronous standby, point-in-time restore, cross-region async replica for DR.
- **No single leader:** timer and task-dispatch work is split into shards owned by time-limited leases with
  **fencing tokens**, so a paused or partitioned node cannot write after losing its lease.
- **Exactly-once state, at-least-once delivery:** every activity attempt carries an idempotency key
  (`run_id + activity_seq`); completions for a stale attempt or stale lease are rejected. Starting a workflow with an
  existing workflow id is deduplicated, not re-run.
- Optimistic concurrency (`version` column) on runs prevents two workers advancing the same run.
- Heartbeats + timeouts detect dead workers and reschedule tasks automatically.

### Memory
- Hot paths allocation-free where practical: `ArrayPool<byte>`, `Span<T>`, `Utf8JsonWriter`/source-generated
  serializers, no LINQ or closures in dispatch/replay loops.
- Payloads stored separately from event metadata and loaded lazily; replay touches metadata only unless a step needs
  its result.
- Sticky cache is a bounded LRU with a **byte budget**, not an entry count, and evicts under memory pressure.
- History size caps with **continue-as-new** for long-lived workflows, so no run grows without bound.
- Streaming reads of history pages instead of materializing full histories.
- Allocation budgets asserted in benchmarks (`MemoryDiagnoser`), Server GC tuned, `DOTNET_GCHeapHardLimit` set in containers.

## 11. Lessons from October 2026 incidents

Three incidents in October 2026 share one root: credentials. Each maps to a Kairos rule.

| Incident | What happened | Kairos rule |
|---|---|---|
| Railway (20–22 Oct) | Attacker reached build infrastructure, took CI credentials, and decrypted a subset of customers' envelope-encrypted environment variables (at most 1.85% of workspaces). Everyone told to rotate keys. | **The platform must not be able to decrypt.** Payload keys are unwrapped only by the customer's worker via the customer's KMS/Vault. The Kairos server, its CI and its operators hold no unwrap rights. |
| US banks (Oct) | Customers gave AI agents their bank passwords; banks found unvetted agents acting inside accounts. | **Agents never hold a user's password.** Each agent run gets short-lived, scoped credentials; tools are read-only by default; writes need a policy or an approval. |
| South Korean financial firms (Sept–Oct) | Credential stuffing, accelerated by the open-source ARTEX AI agent, against weakly authenticated side systems (loan-agent portals, staff support apps); about 68,000 records exposed. | **No passwords anywhere.** Every Kairos endpoint uses SSO, mTLS or short-lived tokens; rate limiting and lockout on anything that accepts credentials; side systems (dashboards, admin, MCP) held to the same bar as the core. |

Requirements derived from these:
1. **Decryption-boundary test (Milestone 3):** with client-side encryption on, a test acting as a fully compromised server
   (database, server process, CI identity) must fail to decrypt any payload. Canary scans already cover storage, logs and traces.
2. **No long-lived CI secrets:** pipelines authenticate to clouds and registries with workload identity / OIDC federation
   only. Hosted Kairos Cloud builds are single-tenant and rebuilt from verified source.
3. **Bulk revocation:** an operator can revoke every worker credential and API key for a namespace (or all) in one action,
   and every credential has an expiry.
4. **Agent credentials:** `Kairos.Agents` issues per-run, per-tool scoped credentials with expiry; default tool policy is
   read-only; side-effecting tools require an explicit allow policy or a human approval. This baseline is free (see
   docs/LICENSING.md); budgets and the policy console are commercial.
5. **Authentication everywhere:** no password login on any Kairos surface; rate limiting and lockout on token endpoints;
   MCP endpoints use enterprise-managed authorization.
6. **Pinned and checked:** NuGet lock files, checksum-verified tool downloads, and every container image pinned to a digest
   (`docker/pin-images.ps1`; CI fails on unpinned images). Upgrades are deliberate: re-pin, re-run the full test suite.

Sources: Railway preliminary report (blog.railway.com, 22 Oct 2026); TechCrunch, 22 Oct 2026; CBS News, 23 Oct 2026;
Seoul Economic Daily, 7 Oct 2026; Tech Times, 5 Oct 2026.

## 12. What client-side payload encryption protects, and what it does not

Payload encryption (section 4) protects **content**. A server that is fully compromised (database, process, CI identity) cannot read any
payload and cannot forge a sealed one; this is tested (`DecryptionBoundaryTests`, `ConfidentialityTests`).

**Protected (sealed with AES-256-GCM, bound to tenant, namespace, run, field and id):** workflow input and result, activity input and
result, signal payloads, failure messages (the only unsealed failure text accepted is the store's own fixed lease-expiry record), and
the carried input of continue-as-new. Each signal also has a client-generated id, so two signals of one name are not interchangeable and
a redelivered id is ignored.

**Not protected, by design (readable metadata):** workflow, activity and signal *names*, error *types*, event types and sequence,
timer durations, timestamps, run and workflow ids, sizes of payloads, and the tenant structure. Do not put secrets or personal data in
names or ids; put them in payloads.

**What a compromised server can still do** (integrity of content holds, integrity of *scheduling* does not):
- withhold, delay or reorder events, or stop answering (denial of service);
- fire timers early or late, or refuse to fire them;
- fabricate events that carry no sealed payload (for example a timer firing, an activity failure of the store-written lease-expiry kind,
  or a termination), and drop events from the tail of a run (the hash chain proves order and integrity of what is present, not completeness);
- observe traffic patterns: which workflows run, when, how often, and how large their payloads are.
It cannot make a workflow read data it did not write, and replaying a sealed value into another place fails closed.

**Revocation window.** After a customer revokes or rotates a master key in their KMS:
- a process started afterwards can open nothing and seal nothing;
- a running worker stops *opening* previously cached keys after `UnwrappedKeyTtl` (default 15 minutes);
- a running worker stops *sealing* with its current data key after `DataKeyLifetime` (default 1 hour), then fails closed;
- keys are wiped from memory when replaced or evicted. Set shorter values for stricter response; the cost is more key-provider calls.

If event authenticity against a malicious server matters (regulated settings), the standalone server (Milestone 7) gets an
event-authenticity design: commands signed by the worker with a customer-held key, verified before replay.

