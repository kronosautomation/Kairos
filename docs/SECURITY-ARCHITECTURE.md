# Kairos — Security & Separation Architecture

Kairos is a durable workflow engine for .NET: workflows are plain C# (async/await),
every step is checkpointed to an event history, and runs survive crashes and deploys.
Security and tenant isolation are design constraints from day one, not add-ons.

## How to read this document

This document is binding, and every claim in it carries a status, so that what is promised and what exists never drift apart. Checked against the code on 28 Oct 2026.

| Status | Meaning |
|---|---|
| **[Built, tested]** | The code does it, and a named test would fail if it did not. |
| **[Built, untested]** | The code or configuration does it, and nothing would fail if it stopped. A claim in this state is a debt: add the test, or change the status. |
| **[Planned]** | Not built. It is a direction, not a property. Nothing may rely on it, and no customer-facing text may state it as true. |

A new claim is added with its status, and a status changes in the same commit as the code or the test that changes it. Where the sentence is longer than the claim, the claim is the part in front of the tag.

## 1. Core principle: the engine never runs customer code

- Customer workflow and activity code runs only in **customer-hosted workers** (their VMs, containers, functions). **[Built, untested]** (the server has no reference to the worker, registries or runtime; no test asserts that it never loads customer types)
- Workers connect **outbound** to Kairos, poll for tasks, and report results. **[Built, tested]** (`WorkerProtocolTests`, `RemoteWorkerSimulationTests`)
- Kairos never calls into customer networks. **[Built, untested]** The server makes outbound calls only to destinations its operator configures: the event sink (a queue, topic or signed webhook) and the inbound source. No test asserts that list.
- The server stores and orders events. It does not execute or deserialize customer types: payloads are opaque bytes. **[Built, untested]**
- Result: a compromise of the Kairos service cannot run code in a customer environment, and customer code cannot reach other tenants. The first half follows from the claims above (untested); the second half is the tenant isolation of section 3 **[Built, tested]**.

## 2. Plane separation

| Plane | Responsibility | Exposure |
|---|---|---|
| **Data plane** (`Kairos.Server`) | Start/signal/cancel runs, task polling, history | Public, per-namespace scoped. Callers authenticate with short-lived tokens or cloud identities **[Built, tested]**. Mutual TLS for workers is **[Planned]**: the server explicitly asks for no client certificate. |
| **Control plane** (`Kairos.Admin`) | Tenants, namespaces, keys, quotas, billing | **[Planned]**, and not in the first version. There is no project and no host. Only its database role exists. Until it exists, tenants and namespaces are provisioned by the migrator and by configuration. |
| **Persistence** | Postgres | Private endpoint only, no public IP: **[Built, tested]** in the AWS, Azure and Google cell stacks (`Kairos.Infra.Tests`). |

- The data-plane role cannot create tenants (no grant on the tenant tables). **[Built, tested]** (`SecurityTests`). There is no key table, so "cannot create keys" has nothing to test.
- The control-plane role cannot read workflow tables. **[Built, tested]** (`SecurityTests.Control_plane_role_cannot_read_workflow_data`). This is a role-level property; no host uses the role yet.

## 3. Tenant isolation (defense in depth)

1. **Namespace = isolation boundary.** Every row carries `tenant_id` + `namespace_id`. **[Built, tested]** (`SecurityTests.Every_kairos_table_enforces_row_level_security_and_carries_tenant_columns`; the tenant table itself has no namespace column).
2. **Postgres Row-Level Security** with `FORCE ROW LEVEL SECURITY`. The server connects as a non-owner role and sets
   `app.tenant_id` and `app.namespace_id` per transaction (`SET LOCAL`), so a missing `WHERE` clause cannot leak rows. **[Built, tested]** (`SecurityTests`, including forced RLS for the table owner and invisibility without a scope).
3. **Separate database roles:**
   - `kairos_migrator` — DDL only, used by migrations in CI/CD, never by running services. **[Built, tested]** for the standalone server, which refuses to start with a role that owns the tables or could bypass RLS (`PostgresNodeTests`). `AddKairos` with Postgres does the same check at start and refuses a superuser, a `BYPASSRLS` role or a table owner unless the code says `AllowPrivilegedDatabaseRole()` (warned at every start, exposed to the dashboard as `IDatabaseRoleReport`). **[Built, tested]** (`PrivilegedRoleTests`).
     - Migrations run as one transaction behind a transaction-scoped advisory lock, so instances that migrate together serialize and a failed run changes nothing; a run that cannot get the lock in time fails with a named error. **[Built, tested]** (`MigrationTests.Many_runs_started_at_once...`, `A_run_that_cannot_get_the_lock_in_time...`).
     - `MigrateOnStart` and `kairos db migrate` use the migrator login only and refuse the application's credential, an administrator's, or the same login for both. **[Built, tested]** (`MigrationTests.MigrateOnStart_refuses...`, `The_cli_provisions_and_migrates...`). `MigrateOnStart` does not check what the application's own login may do beyond not being the migrator.
   - `kairos_dataplane` — DML on workflow tables, bound by RLS, no DDL. **[Built, tested]**
   - `kairos_controlplane` — DML on tenant/namespace tables only, no access to payload tables. **[Built, tested]** (role level; no key or quota tables exist).
   - `kairos_readonly` — reporting/support, RLS-bound. **[Built, tested]**
4. **Enterprise tier:** a dedicated database per tenant, same code path. **[Built, untested]** It holds because a cell (a stack with its own database) can be deployed per tenant; the server does not route tenants to databases.
5. **Noisy-neighbor limits** per namespace:
   - request rate: **[Built, tested]** (`LimitsTests`) — per tenant, per signed-in user and per address, per node.
   - payload size: **[Built, tested]** (server default 1 MiB).
   - history length per run: **[Built, tested]** (`StoreContractTests`, default 50,000 events).
   - open runs: **[Built, tested]** (`StoreLimits.MaxOpenRunsPerNamespace`, default 100,000; the PostgreSQL store checks a count it refreshes every few seconds, so several nodes can overshoot briefly).
   - pending timers: **[Built, tested]** (`StoreLimits.MaxPendingTimersPerNamespace`, default 100,000; a task that would exceed it ends its run with `TimerLimitExceeded`).
6. **`LISTEN/NOTIFY` is outside row-level security.** Any connection to the database can `LISTEN` on a channel and read every
   payload, whatever its role and whatever RLS says about tables. So a notification payload never carries an identifier or
   anything about the work: no tenant id, no namespace id, no queue or task name, no build name, no workflow type. It is exactly
   `{tag}:{count}`, where the tag is the first 16 bytes (hex) of HMAC-SHA256 over tenant, namespace and queue under a
   per-deployment key that only the application processes hold (`PostgresStoreOptions.NotificationKey`, from the secret store;
   `Server:NotificationKey`), and the count is the number of tasks that became ready. **[Built, tested]** (`WorkNotificationTests`, `WakeupTests`: a plain connection listens through a multi-tenant workload and the test fails if any identifier appears.) A listener computes the tags of the scopes
   it serves and recognises only those; a bystander sees stable, opaque hex (it can tell that *something* happens, and how often
   per tag, which the database's own statistics already show it). Anything finer than the count, such as which build a workflow
   task is pinned to, is read by the listener through the ordinary RLS-scoped query, never from the payload. Embedded
   single-tenant mode uses the same format (**[Built, untested]** through `AddKairos`; the format is tested on the store). A notice is only a hint: a listener with a different key does not recognise it and
   falls back to its other wake-ups, so the key can be rotated by restarting nodes without losing work. **[Built, untested]** (no test runs two nodes with different keys).
7. **Named exceptions to row-level security are listed here, and there are two.** A policy that lets the data-plane role read across
   scopes is a hole in isolation unless it is narrow, read-only, named and tested. `open_work_sweep` (commercial policy library,
   migration 0003) lets a session that sets `app.sweep = 'reconcile'` `SELECT` the budget reservations that still have work to do
   (reserved and not settled, or settled after a retry whose failed attempts are not yet added) in every scope, so that a node
   can find a scope whose workers are gone and settle what a crash left. It allows no write; it opens no other table or row; the
   marker is set by one query (a test fails if any other source file contains it); the result is used for scope ids only. It
   adds no power the role lacked (the role can already set `app.tenant_id` to any value, so isolation against a *compromised
   data plane* is not a property of RLS and is not claimed); it makes the one cross-scope read explicit. **[Built, tested]** (`SweepTests`; "ids only" is **[Built, untested]**).

   `unpublished_sweep` (migration 0010 of the engine's store) lets a session that sets `app.sweep = 'publish-events'` `SELECT` the
   runs that still have events to publish (`last_seq > published_seq`) in every scope, so that a node can hand the events of a run
   to the deployment's event sink even when no worker of its scope is polling. It allows no write, opens no other table, and is
   granted to the data-plane role only (the read-only role gets nothing from it); the marker is set by one query (a test fails if any
   other source file contains it); the result is used for scope ids only, and the events are then read scope by scope under the
   ordinary policy. The same argument applies as above: it adds no power the role lacked. **[Built, tested]** (`UnpublishedSweepOnPostgresTests`).

   Any further exception belongs in this list, with its test, before it is merged.

## 4. Data protection

- **Client-side payload encryption is the default.** Payloads (workflow input and result, activity input and result, signal payloads, failure messages) are sealed with
  envelope encryption (AES-256-GCM data keys, wrapped by the customer's KMS / Key Vault key); Kairos stores ciphertext
  only and never holds the customer's key. **[Built, tested]** (`DecryptionBoundaryTests`, `DataKeyScopeTests`, `ConfidentialityTests`).
  - "By default" means this: a host built with `AddKairos` (embedded workers, a client, and a worker connected to a server) **refuses to start** without a payload protector unless the code says
    `AllowUnencryptedPayloads()`; that choice is logged as a warning at startup, and a worker connected to a server reports it, so that the dashboard shows the namespace as holding readable payloads. **[Built, tested]** (`PayloadProtectionDefaultTests`, `WorkerPostureTests`).
  - The low-level engine types (`KairosClient`, `WorkerRuntime`, `ReplayChecker`) still accept no protector, because the simulator, the contract tests and the conformance suites build them directly. Code that builds them by hand is outside the guarantee. **[Built, untested]**
  - Search attributes (plaintext, opt-in per field) do not exist. **[Planned]**
- **An unwrapped data key opens everything sealed with it.** Data keys are reused briefly (default one hour or 10,000 payloads, per tenant and
  namespace). Whoever is given an unwrapped data key (a worker; a key broker serving a browser) can open every value sealed under it in that scope until it is rotated, not only the value
  they asked about. **[Built, tested]** (`DataKeyScopeTests`). A "reference" in an unwrap request is therefore a **log of intent, not a limit** **[Built, tested]** (`KeyBrokerTests`), and no component may treat it as an access control.
  `EnvelopeOptions.OneKeyPerValue` / `ShortLived(...)` shrink the unit for customers who want a tighter scope, at the cost of more provider calls. **[Built, tested]**
  Unwrapped keys are held for `UnwrappedKeyTtl` in workers **[Built, tested]**, and non-extractable and briefly in browsers **[Built, untested]**.
- **The key windows have defaults and ceilings.** `DataKeyLifetime` 1 hour (at most 24 hours), `UnwrappedKeyTtl` 15 minutes (at most 1 hour), `MaxUsesPerDataKey` 10,000; a protector made with a value above a ceiling is refused when it is created. **[Built, tested]** (`EnvelopeOptionsLimitsTests`)
- **Every call to the key provider is recorded by the party that makes it.** The protector tells an `IKeyUseObserver` about every wrap and unwrap (tenant, namespace, key identifier, outcome; never a key or a payload), and a record that cannot be made stops the operation. **[Built, tested]** (`KeyUseObserverTests`). Kairos writes nothing by default: the observer is the hook, and the key broker logs its own unwraps **[Built, tested]**. The audit log does not hold key use.
- **Keys in Vault or OpenBao (Transit key provider, Apache, `Kairos.Cloud.SelfHosted`).** Data keys are wrapped with a Transit key on one configured mount and key name through plain HTTPS calls.
  - The Kairos context is Transit's `context` on a **derived** key, so Vault itself refuses to unwrap for another tenant or namespace; a key that is not derived, or cannot encrypt, is refused on first use. **[Built, tested]** (`VaultTransitServerTests` and `OpenBaoTransitServerTests` run the key-provider conformance suite and the tenant-binding cases against Vault 1.18 and OpenBao 2.7 in containers; `VaultTransitContractTests`; category Integration).
  - Wrong context, altered ciphertext, an unknown or other key id and a deleted key give the same `KeyUnwrapException`; outages and permission failures are not disguised as one. **[Built, tested]** (same tests).
  - The stored key id must be exactly the configured mount and key (with an optional version that must agree with the ciphertext); mount and key names are matched against a strict pattern; no URL is built from a stored value; ciphertext must be canonical `vault:v<n>:<base64>` (Vault's own decoder ignores spare bits). **[Built, tested]** (`VaultTransitContractTests`).
  - Tokens: only from a file (re-read on change and on 403), a caller-supplied function, Kubernetes login or AppRole login; no option takes a token as text; a token-shaped value where a path belongs is refused; no exception message carries a token, a key or a response body; https is required unless loopback; redirects are not followed; time and response size are bounded. **[Built, tested]** against a fake server; file token against a real server with a policy-limited token. **Kubernetes and AppRole login against a real Vault: [Built, untested]** (fake server only). The provider writes no log lines. A root token cannot be detected: using one is a rule for the operator, documented in `docs-site/concepts/key-providers-vault.md`.
  - The least-privilege policy (read `transit/keys/<key>`, update `transit/encrypt/<key>` and `transit/decrypt/<key>`) is the one the container tests run with. **[Built, tested]**
  - Vault is trusted with the data keys it wraps (they are sent to it over TLS), as a cloud key service is. Vault audit devices record each call on its side.
- **Server-side at-rest encryption** on storage and backups as a second layer. **[Built, tested]** for the database in the cell stacks (`Kairos.Infra.Tests`); backups are not asserted separately.
- **TLS 1.3 everywhere.**
  - Clients to the server: **[Built, tested]** (`ServerDefaultsTests`; TLS 1.3 is the default protocol).
  - Server (and migrator) to Postgres: whenever the connection string insists on TLS (`Require`, `VerifyCA`, `VerifyFull`) the handshake asks for 1.3 only; `Server:Database:MinimumTls=1.2` or `AllowTls12ToDatabase()` relaxes it in configuration or code. **[Built, tested]** (`DatabaseTlsTests`: a stand-in database with the real Npgsql client). A connection string that does not insist on TLS is not changed; token modes insist on `VerifyFull`.
- **No secrets in history.** An analyzer flags workflow inputs typed as credentials (a name-based heuristic, warning severity); secrets come from the worker's own vault. **[Built, tested]** (`AnalyzerTests`, KAI006)

## 5. Identity & access

- **Workers authenticate with short-lived tokens or cloud identities; no long-lived shared secrets.** Tokens: session tokens at most 15 minutes; tokens from a trusted issuer at most one hour; identity-provider tokens (Entra, OIDC) as issued, accepted up to two hours. Cloud identities: AWS IAM (signed request exchanged for a session), Azure (Entra), Google (ID token). **[Built, tested]** (`TokenTests`, the authenticator suites).
  Mutual TLS client certificates for workers are **[Planned]**.
- **API keys.** `ApiKeyAuthenticator` is a helper for **embedded** applications that authenticate their own callers (in-memory records, HMAC-SHA256 with a caller-supplied pepper). It is not part of the standalone server, and this document makes no claim about API keys beyond that.
- **Roles per namespace:** `reader`, `approver` (read, decide approvals), `worker` (poll/complete only), `agent worker` (a worker that may also start and read conversations of durable agents),
  `operator` (start/signal/cancel/terminate, export histories), `admin` (everything, including the audit log, policy changes and evidence export). Least privilege: a worker credential cannot start or read other workflows.
  **[Built, tested]** (`AuthorizationMatrixTests`: every route, role and place over HTTP and gRPC).
- **Control plane** uses workforce SSO (Entra ID / OIDC) with MFA, never API keys. **[Planned]** (there is no control plane).
- **Approval evidence is asymmetric.** Who decided an approval is proven by ES256 evidence signed through Key Vault / KMS by the application that authenticated the person, and verified by
  workers with the public key alone (kid-pinned). Bound to tenant, namespace, run, approval, direction and subject; five minutes at most; an id that one verification step may use. No
  shared secret exists between the signer and the workers, so a compromised worker, or a holder of the public key, cannot forge an approval. HMAC keys are refused at startup. **[Built, tested]** (`ApprovalEvidenceTests`, `ApprovalSigningTests`).
  Verification is the default: an agent host with no approver verifier **refuses every approval decision** unless the code says `AllowUnverifiedApprovers()`; that choice is logged as a warning when the workers start, reported by a worker connected to a server, and shown in the dashboard. Even then, an approval that names approvers or roles is refused. **[Built, tested]** (`ApproverVerificationTests`, `PayloadProtectionDefaultTests`, `WorkerPostureTests`). Code that drives `AgentContext` outside a host that registers the agent activities with `AddKairos` is not warned at start-up, only refused at decision time. **[Built, untested]**
- **No passwords in the server's own sign-in, by default.** The standalone server does not start unless `Server:Database:Authentication` names how it signs in to PostgreSQL: a token (AWS RDS IAM, Entra, Cloud SQL IAM) or, by name, `password`, which is allowed and logged as a warning at every start. In a token mode a connection string with a password, or a TLS mode that does not verify the server, is refused at startup. **[Built, tested]** (`PasswordlessDatabaseTests`).
- **Failures look identical** (unknown, expired, malformed credentials get the same answer) **[Built, tested]**; repeated failures from one address are locked out with HTTP 429 **[Built, tested]** (`LimitsTests`). Revocation of a credential before its expiry does not exist; sessions end within 15 minutes. **[Planned]**

## 6. Integrity & audit

- **Hash-chained history:** each event stores `SHA-256(prev_hash || event)`, so tampering or reordering is detectable. **[Built, tested]** (`TamperTests`). The chain proves the order and integrity of the events that are present, not that none was dropped from the tail. **[Built, untested]**
- **A database restored from an older backup keeps verifying and resumes.** The restored histories are prefixes of the originals, their chains verify end to end, runs continue from the snapshot's point, activities after it run again under the same idempotency key, and a different key ring cannot open the restored payloads. **[Built, tested]** (`RestoreTests`: `pg_dump` and `pg_restore` in a PostgreSQL container, nightly). This says nothing about a backup that was altered before the restore beyond what the chain proves.
- **Append-only audit log** and history events: the data-plane role can add and read, never update or delete. **[Built, tested]** (`AppendOnlyTests`).
- **Privileged data-plane actions are audited with the verified principal** (cancel, terminate — recorded as a failure, fail, resume, history export, audit evidence export), in the same transaction as the action. **[Built, tested]** (`IdentityTests`, `AuditEvidenceRouteTests`). There is no "reset" operation.
- **History export** (`kairos history export`) needs a reason and is audited. Through a server it needs the operator role or above and a sign-in; straight from the database it is refused unless the connection is the migrator login, so the application's own credential cannot export. **[Built, tested]** (`HistoryExportCredentialTests`, `HistoryExportServerTests`).
- **Audit evidence export** (`kairos audit export`, `POST /v1/namespaces/{id}/audit/evidence`): administrators only, a reason required, signed with a key of its own that is not the session key, each history read audited under the verified caller, and no payload key on the server. **[Built, tested]** (`AuditEvidenceRouteTests`; the bundle verifies offline with `kairos audit verify`).
- **Audit of every control-plane action:** **[Planned]** (there is no control plane).
- Optimistic concurrency on run state: a completion applies only to the head the worker replayed (last sequence, head hash and lease token), so no lost updates under concurrent workers. **[Built, tested]** (`StoreContractTests`). There is no `version` column; the mechanism is the sequence and hash.

## 7. Workflow correctness as a security property

- Roslyn analyzers block non-deterministic APIs inside workflow code (`DateTime.Now`, `Guid.NewGuid`, `Random`,
  direct I/O, `Task.Run`, static mutable state) and steer to `ctx.Now`, `ctx.NewGuid`, activities. **[Built, tested]** (`Kairos.Analyzers.Tests`)
- Replay verification: a worker that produces a command sequence differing from history fails the task rather than
  silently diverging. **[Built, tested]** (`VersioningTests`)

## 8. Platform & supply chain

- Central package management, NuGet lock files (locked restore in CI), `NuGetAudit` set to fail on high/critical vulnerabilities, `TreatWarningsAsErrors`, nullable and analyzers on. **[Built, untested]** (build configuration: the build itself is the enforcement).
- No reflection-based serialization (System.Text.Json source generators). **[Built, untested]**
- The libraries are AOT-compatible (`IsAotCompatible`). The server image is a framework-dependent application on a chiseled (distroless, no shell) ASP.NET base, run as non-root. **[Built, untested]** (no test inspects the image). Publishing the server as Native AOT is **[Planned]**.
- Read-only root filesystem in the container tasks. **[Built, tested]** for AWS and Google Cloud (`Kairos.Infra.Tests`); the Azure stack does not assert it.
- Secrets via managed identity and Key Vault: database credentials are tokens, not secrets (section 5). **[Built, tested]** Nothing in configuration or environment files committed to git (both repositories; the private one has a per-commit pipeline with the same scan): `build/check-secrets.ps1` scans every tracked text file for secrets by their shape (private keys, cloud and source-control tokens, storage keys, signatures, JWTs, literal client secrets, long literal passwords) and proves first, in a self-test, that it can fail. **[Built, tested]** (the self-test; it replaced a pattern scan that matched the word "password" in test code and so could not have been passing).
- Signed container images and SBOM generation in the release pipeline. **[Built, untested]** (the release pipeline checks its own output; there is no test of the pipeline's results).
- Security fixes go to the latest release before 1.0, and to the current and previous minor release from 1.0; actively exploited vulnerabilities are reported to the authorities under the EU Cyber Resilience Act (24 hours, 72 hours, final report), by a procedure in `SECURITY.md` and the private incident runbook. **[Built, untested]** (a procedure, rehearsed by a table-top exercise that has not yet been held).
- A release gate refuses to publish with place-holders in public text, without a security policy, off the main branch, with a high or critical advisory, without the public signing key, or with a review that lists open findings. **[Built, tested]** (`Test-ReleaseTooling.ps1`).

## 9. Solution layout (separation in code)

```
src/                      (this repository, Apache 2.0)
  Kairos.Abstractions     contracts only, zero dependencies
  Kairos.Core             history model, replay, state machine (no I/O)
  Kairos.Persistence.Postgres   RLS-aware storage, migrations
  Kairos.Worker           EnvelopePayloadProtector: payload encryption in the customer's process
  Kairos.Hosting          AddKairos: client and in-process workers
  Kairos.Analyzers        determinism + secret-handling analyzers
  Kairos.Cli              kairos replay-check, history export/verify, audit export/verify
tests/
  Kairos.Core.Tests, Kairos.Persistence.Tests (cross-tenant leak tests that must fail closed), ...
Kairos.Commercial         (private repository)
  Kairos.Server           data-plane API (gRPC + HTTP)
  Kairos.Server.Client    remote worker store
  Kairos.Admin            control-plane API (separate host) — Planned, not in the first version
```

Dependency rule: Core depends only on Abstractions **[Built, untested]**; the server never references the Worker **[Built, tested]** (`ArchitectureTests`); the Worker never references Persistence **[Built, untested]**. The private repository has a per-commit pipeline (secret scan, vulnerable-package check, pinned images, build with warnings as errors, server, policy and stack tests) in `azure-pipelines.yml`, which has not run on an agent yet. **[Built, untested]** `Kairos.Admin` follows the same rule when it exists **[Planned]**.

## 10. Performance, redundancy & memory (top-priority design goals)

The goals below are design goals. The status says what exists today; nothing marked Planned may be presented as a property.

### Speed
- **One transaction per workflow task:** append new events, update run state, and enqueue follow-up tasks/timers in a
  single statement batch. **[Built, untested]** (the crash matrix exercises it; no test counts round trips).
- **Push, not poll:** workers long-poll; the server wakes them via Postgres `LISTEN/NOTIFY` (opaque tag and count only, see section 3.6; no more pollers
  are woken than there are ready tasks). **[Built, tested]** (`WakeupTests`, `LongPollTests`). `FOR UPDATE SKIP LOCKED` task claims so many workers never contend on the same row. **[Built, tested]** (`EmbeddedModeTests`, `MaintenanceConcurrencyTests`).
- **Sticky execution:** a worker keeps hot workflow state cached, so a new task only replays the *new* events. **[Planned]** (every task replays the whole history today).
- gRPC over HTTP/2 between workers and server, binary payloads. **[Built, tested]** Connection pooling (Npgsql defaults). **[Built, untested]** Prepared statements. **[Planned]**
- History partitioned by run-id hash. **[Built, untested]** Timers partitioned by due time: **[Planned]** (a plain table with an index today).
- **Benchmarks as gates.** Gating today: two allocation budgets on every commit (`AllocationBudgetTests`: replay and hash-chain verification) and, in the private repository's nightly run, the server benchmark (p99 and database queries per task, +20% fails; it compares only where a baseline exists for the agent's profile). Reporting only: the BenchmarkDotNet suites and the server benchmark's throughput. A PostgreSQL hot-path gate per commit is **[Planned]**. See `Kairos.Commercial/docs/BENCHMARK-GATES.md`.

### Redundant operation (high availability, no duplicate side effects)
- **Stateless server nodes**, N+1 behind a load balancer, any node can serve any tenant. Rolling deploys with graceful drain. **[Built, tested]** (`TwoNodeTests`)
- **Postgres HA:** zone-redundant primary with a synchronous standby, point-in-time restore. **[Built, untested]** (set in the cell stacks, asserted as stack properties; no failover is exercised). Cross-region async replica for DR: **[Planned]**.
- **No single leader.** Timer and task-dispatch maintenance is done by any node in `SKIP LOCKED` batches, and workflow tasks carry **fencing tokens** (a lease token that never resets), so a paused or partitioned worker cannot complete after losing its lease. **[Built, tested]**. Shards owned by time-limited leases: **[Planned]** (not built; the batches make them unnecessary so far).
- **Exactly-once state, at-least-once delivery:** every activity attempt carries an idempotency key
  (`run_id + activity id`); completions for a stale attempt or stale lease are rejected; starting a workflow with an
  existing workflow id is deduplicated, not re-run. **[Built, tested]** (`StoreContractTests`)
- Optimistic concurrency on runs prevents two workers advancing the same run. **[Built, tested]** Heartbeats + timeouts detect dead workers and reschedule tasks automatically. **[Built, tested]**

### Memory
- Hot paths allocation-free where practical: the replay and hash-chain paths use pooled buffers and spans **[Built, tested]** (the two allocation budgets); the dispatch path allocates (`Prepare`, command building). **[Planned]** for the rest.
- Payloads stored separately from event metadata and loaded lazily: **[Planned]** (an event body holds its payload inline; a poll reads every body). Payloads above a size (default 256 KiB) can be moved to a blob store by content hash, with a sealed reference in history. **[Built, tested]** (`BlobOffloadEngineTests`, the crash matrix and the cloud sandbox tests).
- Sticky cache as a bounded LRU with a byte budget: **[Planned]** (no workflow-state cache exists; the only cache is the unwrapped data-key cache, bounded by count).
- History size caps with **continue-as-new**, so no run grows without bound. **[Built, tested]**
- Streaming reads of history pages: readers have them (`GetHistoryAfterAsync`, `GetHistoryTailAsync`) **[Built, tested]**; the worker path materializes the whole history. **[Planned]**
- Server GC tuning and `DOTNET_GCHeapHardLimit` in containers: **[Planned]**.

## 11. Lessons from October 2026 incidents

Three incidents in October 2026 share one root: credentials. Each maps to a Kairos rule.

| Incident | What happened | Kairos rule |
|---|---|---|
| Railway (20–22 Oct) | Attacker reached build infrastructure, took CI credentials, and decrypted a subset of customers' envelope-encrypted environment variables (at most 1.85% of workspaces). Everyone told to rotate keys. | **The platform must not be able to decrypt.** Payload keys are unwrapped only by the customer's worker via the customer's KMS/Vault. The Kairos server, its CI and its operators hold no unwrap rights. **[Built, tested]** (`DecryptionBoundaryTests`; the cell stacks grant unwrap to worker roles only, asserted in `Kairos.Infra.Tests`) |
| US banks (Oct) | Customers gave AI agents their bank passwords; banks found unvetted agents acting inside accounts. | **Agents never hold a user's password.** Each agent run gets short-lived, scoped credentials; tools are read-only by default; writes need a policy or an approval. **[Built, tested]** (`CredentialTests`, the agent suites) |
| South Korean financial firms (Sept–Oct) | Credential stuffing, accelerated by the open-source ARTEX AI agent, against weakly authenticated side systems (loan-agent portals, staff support apps); about 68,000 records exposed. | **No passwords on Kairos surfaces.** The data plane uses SSO or short-lived tokens, with rate limiting and lockout **[Built, tested]**; the dashboard signs in with the authorization-code flow **[Built, tested]**; absence of any password route is **[Built, untested]**. Side systems (dashboards, MCP) are held to the same bar as the core where they exist; the control plane is **[Planned]**. |

Requirements derived from these:
1. **Decryption-boundary test:** with client-side encryption on, a test acting as a fully compromised server
   (database, server process, CI identity) must fail to decrypt any payload. Canary scans cover storage, logs and traces. **[Built, tested]** (`DecryptionBoundaryTests`, `ConfidentialityTests`, `TelemetryConfidentialityTests`)
2. **No long-lived CI secrets:** pipelines authenticate to clouds and registries with workload identity / OIDC federation
   only. **[Built, untested]** (pipeline configuration). Hosted builds that are single-tenant and rebuilt from verified source: **[Planned]** (no hosted service exists).
3. **Bulk revocation:** an operator can revoke every worker credential for a namespace (or all) in one action. **[Planned]**. Every credential has an expiry: **[Built, untested]** (tokens carry one and the verifier requires it; no test asserts "every").
4. **Agent credentials:** `Kairos.Agents` issues per-run, per-tool scoped credentials with expiry (lifetime capped at 15 minutes); default tool policy is
   read-only; side-effecting tools require an explicit allow policy or a human approval. Single use of a credential holds only where the verifying service calls the one-time-use guard (in memory, per process). **[Built, tested]**. This baseline is free (see
   docs/LICENSING.md); budgets and the policy console are commercial.
5. **Authentication everywhere:** rate limiting and lockout on token endpoints **[Built, tested]**. MCP endpoints use enterprise-managed authorization: the identity-assertion validators are a library for the customer's own host; the Kairos server does not host an MCP endpoint. **[Built, tested]** for the library, **[Planned]** as a hosted feature.
6. **Pinned and checked:** NuGet lock files, checksum-verified tool downloads, and every container image pinned to a digest
   (`docker/pin-images.ps1`; CI fails on unpinned images). **[Built, untested]** (CI gates, not tests; the SDK and the .NET tools are pinned by version, release tools by hash).

Sources: Railway preliminary report (blog.railway.com, 22 Oct 2026); TechCrunch, 22 Oct 2026; CBS News, 23 Oct 2026;
Seoul Economic Daily, 7 Oct 2026; Tech Times, 5 Oct 2026.

## 12. What client-side payload encryption protects, and what it does not

Payload encryption (section 4) protects **content**. A server that is fully compromised (database, process, CI identity) cannot read any
payload and cannot forge a sealed one. **[Built, tested]** (`DecryptionBoundaryTests`, `ConfidentialityTests`)

**Protected (sealed with AES-256-GCM, bound to tenant, namespace, run, field and id):** workflow input and result, activity input and
result, signal payloads, failure messages (the only unsealed texts accepted are the store's own fixed lease-expiry record and the empty cancellation signal), and
the carried input of continue-as-new. **[Built, tested]** except the last item, which is **[Built, untested]** (no test continues a run with encryption on and checks the new input). Each signal also has a client-generated id, so two signals of one name are not interchangeable and
a redelivered id is ignored. **[Built, tested]** (`EncryptionReviewTests`)

**Not protected, by design (readable metadata):** workflow, activity and signal *names*, error *types*, event types and sequence,
timer durations, timestamps, run and workflow ids, sizes of payloads, the tenant structure, approval ids with the time each was asked for and whether it has been resolved (readable patch markers, `kairos.approval.requested:` and `kairos.approval.resolved:`, so a server can list what is waiting without opening anything), and who started a run (`WorkflowStarted.StartedBy`: the authenticated
principal as `role:subject`, set only by the host that verified the caller, never taken from a request, kept across continue-as-new). **[Built, tested]** for markers and `StartedBy`; the rest of the readable set is **[Built, untested]** (nothing pins which fields are plaintext). Do not put secrets or personal data in
names or ids; put them in payloads.

**What a compromised server can still do** (integrity of content holds, integrity of *scheduling* does not):
- withhold, delay or reorder events, or stop answering (denial of service). (Reordering of stored events is detected by the hash chain when the history is read.)
- fire timers early or late, or refuse to fire them;
- fabricate events that carry no sealed payload (for example a timer firing, an activity failure of the store-written lease-expiry kind,
  or a termination), and drop events from the tail of a run (the hash chain proves order and integrity of what is present, not completeness);
- observe traffic patterns: which workflows run, when, how often, and how large their payloads are.
It cannot make a workflow read data it did not write, and replaying a sealed value into another place fails closed. **[Built, tested]** (`PayloadProtectionTests`, `DecryptionBoundaryTests`)

**Revocation window.** After a customer revokes or rotates a master key in their KMS:
- a process started afterwards can open nothing and seal nothing **[Built, tested]**;
- a running worker stops *opening* previously cached keys after `UnwrappedKeyTtl` (default 15 minutes) **[Built, tested]**; the 15-minute default itself is **[Built, untested]**;
- a running worker stops *sealing* with its current data key after `DataKeyLifetime` (default 1 hour), then fails closed **[Built, tested]**;
- keys are wiped from memory when replaced or evicted **[Built, tested]**. Set shorter values for stricter response; the cost is more key-provider calls. Neither value has an upper bound.

If event authenticity against a malicious server matters (regulated settings), the standalone server gets an
event-authenticity design: commands signed by the worker with a customer-held key, verified before replay. **[Planned]**
