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
| Kairos.Dashboard | Commercial | Pro | Run list, history and replay view, approval inbox, cost by agent and budget meters (priced and enforced by the server) |
| Kairos.Agents.Policies | Commercial | Pro | Declarative argument-level policy rules (allow, deny, require approval by one or two people, rate limit), versioned and audited policy storage, token/money/step budgets enforced by atomic reservation, price tables with effective dates (the read-only baseline is free). The hooks they plug into are Community: `IRecordedToolPolicy` and recorded decisions, `IModelCallGuard`, `ApprovalRequirement.Count`, `ToolPolicyContext.StartedBy` |
| Kairos.Agents.Policies.Remote | Commercial | Pro | The worker side of the Policy service for workers that hold no database connection: policy source, rate counters, model call guard and tool step counter over gRPC with the worker's own credential, failing closed. Needs Kairos.Server with policies enabled |
| Kairos.Mcp.TaskStore | Apache 2.0 | Community | Durable `IMcpTaskStore` for any C# MCP server, backed by Kairos runs (the adoption entry point) |
| Kairos.Agents.McpClient | Apache 2.0 | Community | Durable, idempotent MCP client steps for agent workflows |
| Kairos.A2A | Apache 2.0 | Community | Agent Card signing and verification; a Kairos run projected as an A2A task |
| Kairos.Agents.A2aClient | Apache 2.0 | Community | Durable, idempotent outbound A2A calls with Agent Card verification |
| Kairos.Agents.AgentFramework | Apache 2.0 | Community | Makes Microsoft Agent Framework agents durable: each model call and tool call a recorded step, sessions that survive restarts, approvals that wait without holding compute, and the same tool policy, budgets and redaction as native agents. It needs only the Community hooks (`IToolPolicy`, `IRecordedToolPolicy`, `IModelCallGuard`, approvals); the policy engine, price tables, the Policy service and the dashboard's approval inbox are Pro and apply to these agents without any change to them |
| Kairos.Agents.A2a | Commercial | Pro | The hosted A2A server: workflows published as agents, signed cards served, server-stamped identity |
| Kairos.Agents.Mcp | Commercial | Pro | The Kairos MCP server: workflows as tools, approvals as input requests with verified approvers, operator tools |
| Kairos.Cloud.Azure, .Aws, .Gcp | Commercial | Pro | Managed key, identity, blob and event adapters (self-hosted equivalents stay free) |
| Kairos.Telemetry | Commercial | Pro | Packaged dashboards (Grafana, Azure Monitor workbooks) and alert rules. Raw spans, metrics, the GenAI conventions and the Aspire integration are Community and stay in Core, Hosting and Kairos.Aspire.Hosting |
| Kairos.Server, Kairos.Admin | Commercial | Business | Standalone multi-tenant data plane and control plane, including the endpoints to read and (Admin only) change policy, prices and budgets |
| Kairos.Dashboard.Enterprise | Commercial | Business | SSO, roles, audit export, bulk operator console |
| Offline license, escrow, custom terms | Commercial | Enterprise | Terms, not packages |
| Redistribution rights | Commercial | OEM | Ship Kairos inside a product installed in customer environments |

Open question to revisit after design-partner conversations: whether the managed-cloud adapters (Azure/AWS/GCP) should
move to Community to help adoption, keeping only the console and governance features paid.

## Contributions
- Inbound = outbound: contributions to this repository are accepted under Apache 2.0 with a Developer Certificate of
  Origin sign-off (`git commit -s`). No CLA is needed because commercial packages are separate code, not relicensed core.

## License keys (commercial packages only)
- Signed (ES256), offline-verifiable key: organization, tier, production-app count, expiry. No phone-home, so air-gapped sites
  work the same way. Development, test and CI never need a key.
- Warn only (decided 28 Oct 2026): a missing, expired, invalid or lower-tier key is logged when the feature starts and daily,
  and never stops a feature or a running workflow. Production is declared by `KAIROS_ENVIRONMENT=Production` and nothing else. The production-app
  count is reported, not enforced. It is a contract, not a lock.
- The key is read from `KAIROS_LICENSE` (the key), `KAIROS_LICENSE_FILE` (a path) or code. The private signing key lives in Key Vault and in no
  repository; the issuing tool signs through the vault.
- The Community packages never check for a key.

## Not legal advice
EULA, OEM and escrow terms must be drafted by a lawyer before the first paid release.
