# Kairos

Durable execution for .NET that runs anywhere: an embedded, Postgres-only library (no cluster, no sidecar,
no vendor control plane), with an optional standalone multi-tenant server. See `docs/POSITIONING.md`.
Workflows are plain C# async methods; every step is checkpointed to an event history in Postgres,
and runs survive crashes and redeploys by deterministic replay. Product of Kronos Automation LLC.

**Read `docs/SECURITY-ARCHITECTURE.md` before any design or code change. It is binding.**
**Embedded mode is first-class:** `services.AddKairos(o => o.UsePostgres(...))` with in-process workers must always work;
the standalone server is the same engine with a host around it. No AWS/Azure dependency in core projects.

## Non-negotiable rules

- The server never executes or deserializes customer code/types. Workers are customer-hosted and connect outbound.
- Every table has `tenant_id` + `namespace_id` and Postgres RLS with FORCE ROW LEVEL SECURITY.
  Services connect as non-owner roles and `SET LOCAL app.tenant_id` per transaction.
- Data plane (`Kairos.Server`) and control plane (`Kairos.Admin`) are separate hosts, identities, and DB roles.
- Payloads are opaque byte arrays to the server; the Worker SDK encrypts them client-side (AES-256-GCM envelope).
- Credentials: mTLS or short-lived tokens for workers; API keys stored only as HMAC-SHA256 hashes; scoped per namespace + role.
- History events are hash-chained (SHA-256 of previous hash + event).
- Respect the dependency rules in the solution layout section. Core does no I/O.
- Every persistence feature gets a cross-tenant leak test that proves access fails closed.

## Top priorities: speed, redundant operation, memory

See section 10 of the architecture doc. In short: one DB transaction per workflow task, LISTEN/NOTIFY + SKIP LOCKED
dispatch, sticky workflow caching, stateless N+1 servers, lease + fencing-token sharding, idempotency keys on every
activity attempt, pooled buffers and allocation-free hot paths, bounded byte-budget caches. Benchmarks with allocation
budgets are CI gates; a change that regresses them is not done.

## Engineering standards

- Target .NET 10 (latest LTS). Nullable enabled, `TreatWarningsAsErrors`, latest analyzers.
- Central package management (`Directory.Packages.props`), NuGet lock files, NuGetAudit failing on high/critical.
- System.Text.Json source generators only; no reflection serialization. Keep Server AOT-compatible.
- Minimal dependencies. Prefer the BCL. Npgsql for Postgres; no ORM in the hot path.
- Tests: xUnit, Testcontainers for Postgres integration tests.
- Run `dotnet build` and `dotnet test` before declaring any task done.
- Commit in small steps with clear messages; never commit secrets, connection strings, or certificates.

## Milestones

1. **Core engine (in-memory):** workflow API, event history model, replay, timers, activities with retries, signals,
   determinism checks. Fully unit tested with no database.
2. **Postgres persistence:** schema + migrations, RLS policies, DB roles, task queues with `SKIP LOCKED`, hash chain,
   cross-tenant leak tests.
3. **Server + Worker SDK:** data-plane API, worker polling, client-side payload encryption, auth.
4. **Admin plane, analyzers, dashboard, containers.**
5. **Hosting & integrations:** see `docs/HOSTING-AWS.md` (cell-based AWS hosting, Pulumi C# IaC, EventBridge/SQS/Lambda
   adapters, webhooks, OpenAPI/proto clients, MCP server). AWS-specific code stays out of the core projects.
