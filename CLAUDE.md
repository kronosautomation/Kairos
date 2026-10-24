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
- **Deep tests are part of every milestone:** follow `docs/TEST-STRATEGY.md` (invariants I1–I10, deterministic
  simulation with seed replay, crash-at-every-step, Postgres fault injection, golden history corpus, authz matrix).
  Core must take `TimeProvider`, a seeded RNG and abstract transport/store so the simulator can control them.
- Commit in small steps with clear messages; never commit secrets, connection strings, or certificates.

## Milestones

The authoritative plan, solution layout, provider interfaces and agent requirements are in
`docs/MULTI-CLOUD-AND-AGENTS.md` (multi-cloud + AI agents). Summary:

1. Core engine — done.
2. Postgres store + embedded mode (`services.AddKairos(o => o.UsePostgres(...))`), plus workflow versioning
   (pin runs to their build + patch API), a .NET 10 / .NET 11 Runtime Async test matrix, and the M2 deep-test layers.
3. Provider interfaces + conformance suite + self-hosted adapters.
4. Kairos.Agents (durable model/tool steps, approvals, policies, budgets).
5. MCP server + durable MCP client steps.
6. Azure + AWS adapters, Pulumi cell stacks (see `docs/HOSTING-AWS.md`).
7. Standalone server, admin plane, client SDK, dashboard.
8. Agent Framework provider, Google Cloud adapters, marketplaces.

No cloud SDK outside `Kairos.Cloud.*`/`infra`; no model-vendor SDK in `Kairos.Agents*` (use Microsoft.Extensions.AI).
