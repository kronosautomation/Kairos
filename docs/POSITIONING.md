# Kairos — Product Positioning (Oct 2026)

## Why not "cheaper AWS Step Functions"
AWS made the **Lambda Durable Execution SDK for .NET** generally available on 23 Jul 2026 (C#, NuGet, steps, callbacks,
waits up to a year, local emulator). Step Functions keeps adding integrations. Competing head-on with AWS on AWS is
competing with their roadmap. Microsoft likewise owns the Azure-native path (Durable Task Scheduler, Agent Framework).

What neither hyperscaler will ship: something that runs **outside their cloud** and **inside your app**.

## Position
**Kairos is durable execution for .NET that runs anywhere: an embedded, Postgres-only library. No cluster, no sidecar,
no vendor control plane in your data path.** "Temporal semantics without the cluster."

- Runs on-prem, any cloud, air-gapped, sovereign. AWS and Azure are deployment targets, not dependencies.
- Data control as the headline: client-side payload encryption, tenant isolation, offline license keys.
- .NET-native depth competitors can't match quickly: async/await determinism, Roslyn analyzers, Native AOT, Aspire integration.

### Secondary: durable AI agents without Azure
A Kairos durability adapter + MCP tool-call/approval steps for **Microsoft Agent Framework** (whose durable option today
targets Durable Task Scheduler). "Durable agents on Postgres." Adoption hook, not core revenue: the adapter (`Kairos.Agents.AgentFramework`) is Apache 2.0
and free, because it is the way in. What is paid stays what it was: the policy engine and budgets, the Policy service for remote workers, the dashboard's approval inbox and cost views.

## Competitive gap (as researched)
- Temporal .NET SDK: mature, strong adoption, but self-hosting = a multi-service cluster.
- DBOS (embedded Postgres model): no official .NET SDK; only an alpha community port.
- Restate, Inngest: community-only .NET.
- Microsoft DTS: Azure connectivity required; MSSQL is its only first-party on-prem backend; no first-party Postgres.
- Elsa / Hangfire / MassTransit: adjacent (designer workflows, jobs, messaging), not code-first durable execution.

## Packaging & pricing direction
- **Free core** (permissive license), usable in production at small scale.
- **Pro** per organization per year (Hangfire-style $500–$1,500 range): dashboard, multi-tenant isolation, key management. (The Agent Framework package is free.)
- **Enterprise / Sovereign**: custom quote. Air-gapped license, source escrow, SLA (Duende-style $5k–$25k+/yr reference).
- **Hosted** later: a dashboard/control plane that never touches payloads. Avoid per-action metered pricing at launch.
- AWS Marketplace + bring-your-own-cloud modules as distribution, not as the product.

## Top risks
1. Fast follow: DBOS ships an official .NET SDK, or Microsoft adds a Postgres / self-hostable DTS backend.
2. Willingness to pay for an embedded library: revenue depends on Pro/Enterprise and tooling.
3. Solo-vendor trust for regulated buyers: invest in determinism tests, published benchmarks, failure-mode docs, escrow.

## Product implication for the build
**Embedded mode is first-class**: `services.AddKairos(o => o.UsePostgres(...))` inside the customer's app, with workers
in-process. The standalone server (gRPC, multi-tenant, cells) is the same engine with a host around it.
