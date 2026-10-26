# Kairos Licensing: the Open-Core Boundary

Decided 24 Oct 2026. Pricing and rationale: the "Kairos: Licensing and Pricing Options" doc.

## Model
- **Community (this repository): Apache License 2.0.** Free for any use, including commercial production.
- **Pro, Business, Enterprise, OEM: commercial license**, per organization per year, metered by production applications.
  Commercial code lives in a separate private repository (`Kairos.Commercial`) and ships from a private NuGet feed.
  It is never committed here.
- Copyright holder: Kronos Automation LLC.

## Rule that decides the boundary
**Correctness, safety and security are never paid.** Anything that keeps work from being lost, duplicated, leaked or
corrupted is Apache, including the APIs an operator needs to recover a stuck run. Paid tiers sell convenience at scale:
visibility, consoles, governance, multi-tenant operation, compliance and support.

## Package map

| Package | License | Tier | Notes |
|---|---|---|---|
| Kairos.Abstractions | Apache 2.0 | Community | Contracts and provider interfaces |
| Kairos.Core | Apache 2.0 | Community | Engine, replay, hash chain, worker runtime, raw spans (ActivitySource `Kairos`) and metrics (meter `Kairos`), trace context in history |
| Kairos.Persistence.Postgres | Apache 2.0 | Community | RLS, roles, leases, fencing, audit log, Resume/Fail store APIs |
| Kairos.Hosting | Apache 2.0 | Community | Embedded mode (`AddKairos`), stuck-task monitor, health checks, OpenTelemetry registration |
| Kairos.Aspire.Hosting | Apache 2.0 | Community | .NET Aspire AppHost integration: `AddKairos("kairos").WithReference(postgres)` |
| Kairos.Client, Kairos.Worker | Apache 2.0 | Community | Start/signal/query; remote worker SDK incl. client-side payload encryption |
| Kairos.Agents | Apache 2.0 | Community | Durable model and tool steps, approvals primitive, sagas/compensation, per-run scoped credentials, baseline tool policy (read-only by default; writes need an allow policy or approval) |
| Kairos.Cloud.SelfHosted | Apache 2.0 | Community | Vault, mTLS/SPIFFE, file/S3-compatible offload, Kafka/NATS/webhooks |
| Kairos.Analyzers | Apache 2.0 | Community | Determinism and secret-handling analyzers |
| Kairos.Cli | Apache 2.0 | Community | The `kairos` tool: `replay-check` (deploy safety gate), `history export`, `history verify` and `audit verify` (the offline verifier of signed audit evidence; the export that produces it is Business). Token usage recorded in history and `GetUsageAsync` are Core; price tables and budgets are Kairos.Agents.Policies (Pro) |
| Kairos.Testing, Kairos.Conformance | Apache 2.0 | Community | Simulator test kit and provider conformance suite (customers test their own workflows) |
| Kairos.Dashboard | Commercial | Pro | Run list, history and replay view, approval inbox |
| Kairos.Agents.Policies | Commercial | Pro | Argument-level policy rules, token/cost/step budgets, policy console (the read-only baseline is free) |
| Kairos.Mcp.TaskStore | Apache 2.0 | Community | Durable `IMcpTaskStore` for any C# MCP server, backed by Kairos runs (the adoption entry point) |
| Kairos.Agents.McpClient | Apache 2.0 | Community | Durable, idempotent MCP client steps for agent workflows |
| Kairos.A2A | Apache 2.0 | Community | Agent Card signing and verification; a Kairos run projected as an A2A task |
| Kairos.Agents.A2aClient | Apache 2.0 | Community | Durable, idempotent outbound A2A calls with Agent Card verification |
| Kairos.Agents.A2a | Commercial | Pro | The hosted A2A server: workflows published as agents, signed cards served, server-stamped identity |
| Kairos.Agents.Mcp | Commercial | Pro | The Kairos MCP server: workflows as tools, approvals as input requests with verified approvers, operator tools |
| Kairos.Cloud.Azure, .Aws, .Gcp | Commercial | Pro | Managed key, identity, blob and event adapters (self-hosted equivalents stay free) |
| Kairos.Telemetry | Commercial | Pro | Packaged dashboards (Grafana, Azure Monitor workbooks) and alert rules. Raw spans, metrics, the GenAI conventions and the Aspire integration are Community and stay in Core, Hosting and Kairos.Aspire.Hosting |
| Kairos.Server, Kairos.Admin | Commercial | Business | Standalone multi-tenant data plane and control plane |
| Kairos.Dashboard.Enterprise | Commercial | Business | SSO, roles, audit export, bulk operator console |
| Kairos.Agents.AgentFramework | Commercial | Business | Microsoft Agent Framework durability provider |
| Offline license, escrow, custom terms | Commercial | Enterprise | Terms, not packages |
| Redistribution rights | Commercial | OEM | Ship Kairos inside a product installed in customer environments |

Open question to revisit after design-partner conversations: whether the managed-cloud adapters (Azure/AWS/GCP) should
move to Community to help adoption, keeping only the console and governance features paid.

## Contributions
- Inbound = outbound: contributions to this repository are accepted under Apache 2.0 with a Developer Certificate of
  Origin sign-off (`git commit -s`). No CLA is needed because commercial packages are separate code, not relicensed core.

## License keys (commercial packages only)
- Signed, offline-verifiable key: organization, tier, production-app count, expiry. No phone-home, so air-gapped sites
  work the same way. Development, test and CI never need a key.
- The Community packages never check for a key.

## Not legal advice
EULA, OEM and escrow terms must be drafted by a lawyer before the first paid release.
