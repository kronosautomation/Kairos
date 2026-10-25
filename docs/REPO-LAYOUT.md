# Where Things Go: Repositories and Projects

Decided 24 Oct 2026. When in doubt, this file decides; if it does not cover a case, stop and ask Aaron rather than guess.

## Repositories (all under C:\Kronos)

| Folder | Visibility | Holds | License |
|---|---|---|---|
| `Kairos` | Private now; published as open source later from a clean history (docs/DISCLOSURE-POLICY.md) | Apache core packages, their tests, samples, docs-site, TLA+ spec, CI, Docker tooling, and (until publication) the internal docs under `docs/` | Apache-2.0 |
| `Kairos.Commercial` | Private, never published | Commercial packages and their tests and design notes. References the core through `$(KairosPublicRoot)` project references during development, NuGet packages later | Commercial EULA (to be drafted) |
| `Kairos.Website` | Private source, public output | Marketing site built from the "Kairos Landing Page" design canvas; static, deployed to Azure Static Web Apps | Proprietary |
| `Kairos.Internal` | Private | Created at publication time: receives `docs/` internal material from `Kairos` per the disclosure policy. Not needed before then | n/a |

Each repository has its own git history, solution and CLAUDE.md. Commit in the repository the change belongs to; a change that
spans both (a new core API plus its commercial user) is two commits, core first.

## Placement rule
1. Correctness, safety and security features: `Kairos` (Apache), always.
2. Building blocks that drive adoption and have no paid alternative: `Kairos` (Apache).
3. Convenience at scale, operator consoles, governance, multi-tenant hosting, managed-cloud adapters: `Kairos.Commercial`.
4. Anything describing strategy, pricing, competitors or roadmap: never in public output (docs/DISCLOSURE-POLICY.md).

## Project map

| Project | Repository | Notes |
|---|---|---|
| Kairos.Abstractions, Core, Persistence.Postgres, Hosting, Worker, Agents, Analyzers, Cloud.SelfHosted | Kairos | Existing |
| Kairos.Testing, Kairos.Conformance | Kairos (tests/) | Shipped as packages for customers' tests |
| **Kairos.Mcp.TaskStore** | **Kairos** | Durable `IMcpTaskStore` for any C# MCP server (ModelContextProtocol.Extensions.Tasks), backed by Kairos runs. Free on purpose: the adoption entry point. |
| **Kairos.Agents.McpClient** | **Kairos** | Durable, idempotent MCP client steps for agent workflows (`ctx.CallMcpToolAsync`). Agent durability is free. |
| **Kairos.A2A** | **Kairos** | The A2A protocol layer: Agent Card signing and verification (JWS over RFC 8785 canonical JSON, ES256), and the projection of a Kairos run as an A2A task. Security-relevant, so free. Built on the official `A2A` SDK (pinned preview). |
| **Kairos.Agents.A2aClient** | **Kairos** | Durable, idempotent outbound A2A calls for agent workflows (`ctx.CallA2aAgentAsync`), with Agent Card verification. Agent durability is free. |
| **Kairos.Agents.A2a** | **Kairos.Commercial** (Pro) | Hosted A2A server: workflows published as A2A agents, signed Agent Cards served, server-stamped caller identity. Mirrors Kairos.Agents.Mcp. |
| **Kairos.Agents.Mcp** | **Kairos.Commercial** (Pro) | The Kairos MCP server: workflows exposed as tools, approvals as input requests with server-stamped, verified approver identity, operator tools. Uses Kairos.Mcp.TaskStore. |
| **Kairos.Cloud.Azure** | **Kairos.Commercial** (Pro) | Blob payload offload, Key Vault key wrapping, Service Bus event sink and source, Entra worker authentication, behind the Apache provider interfaces; passes the Apache conformance suites. |
| **Kairos.Cloud.Aws** | **Kairos.Commercial** (Pro) | KMS key wrapping, S3 offload, SQS and EventBridge events, IAM worker authentication, role credentials only; passes the Apache conformance suites. |
| **infra/** (Pulumi C# cell stacks), **build/** (cloud sandbox pipeline) | **Kairos.Commercial** | One Azure Container Apps cell and one ECS Fargate cell, each with its own PostgreSQL, private networking only, least-privilege identities, no secrets in stack configuration; and the nightly `CloudSandbox` pipeline that deploys a throwaway cell, runs the Apache conformance suites against the real services, and destroys it. |
| Kairos.Agents.Policies, Telemetry, Dashboard, Cloud.Gcp | Kairos.Commercial (Pro) | Future |
| **Kairos.Server**, **Kairos.Server.Host** | **Kairos.Commercial** (Business) | The multi-tenant data plane: run operations over HTTP and gRPC, Entra / IAM / OIDC authentication, per-tenant limits, health. The worker task protocol comes next. Host = the executable that composes it from configuration. |
| Kairos.Admin, Dashboard.Enterprise, Agents.AgentFramework | Kairos.Commercial (Business) | Future |
| Marketing site | Kairos.Website | From the design canvas |
| Product docs | Kairos/docs-site | Customer-facing rewrites only |

## Claude Code sessions
- The running session starts in `C:\Kronos\Kairos` and may edit `C:\Kronos\Kairos.Commercial` as well. Before working in a
  repository, read that repository's CLAUDE.md. Run `git` commands from inside the repository being changed.
- Build order for a cross-repo change: build and test `Kairos`, commit; then build and test `Kairos.Commercial`, commit.
