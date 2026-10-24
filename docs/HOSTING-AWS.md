# Kairos — Hosting on AWS & Integrating Other Services

Living tracker. Update the checklists as decisions are made. Nothing in the core may depend on AWS:
AWS-specific code lives only in `Kairos.Hosting.Aws` / IaC, so the same build runs on Azure, on-prem, or in a customer's account.

## Part 1 — What it takes to host Kairos on AWS

### Target architecture (cell-based)

A **cell** is one complete, independent copy of Kairos: data-plane service, its own Postgres cluster, its own task queues.
Tenants are assigned to cells by a thin routing layer. One bad tenant or one bad deploy affects one cell, never the fleet.
Enterprise tenants get a dedicated cell. Same templates, same code.

```
Route 53 ──► Global router (tenant → cell)
                 │
   ┌─────────────┴─────────────┐
   ▼                           ▼
 Cell A (3 AZs)              Cell B …
  NLB (mTLS gRPC) / ALB+WAF (HTTP API)
  ECS Fargate · Graviton · Kairos.Server  (N+1 tasks, stateless)
  Aurora PostgreSQL Multi-AZ (writer + reader in another AZ), KMS-encrypted, IAM DB auth
  Private subnets only; VPC endpoints instead of NAT for AWS APIs

Control plane VPC (separate account): Kairos.Admin on ECS, internal ALB, SSO-only access,
own Aurora cluster for tenants/keys/billing. No network path to cell databases.
```

### Account & network layout
- [ ] AWS Organizations with separate accounts: `kairos-shared` (ECR, CI roles), `kairos-control`, `kairos-cell-*`, `kairos-logs` (central, write-only audit), `kairos-dev`.
- [ ] SCPs: deny leaving the org, deny disabling CloudTrail/GuardDuty, region allowlist.
- [ ] VPC per cell, 3 AZs, private subnets for compute and DB; only load balancers are public.
- [ ] VPC endpoints (ECR, S3, KMS, Secrets Manager, CloudWatch/OTel, STS) so tasks need no NAT gateway.
- [ ] **AWS PrivateLink endpoint service** per cell so customers can connect workers privately from their VPCs.

### Compute
- [ ] ECS Fargate on ARM64 (Graviton); Native AOT images; non-root, read-only root FS.
- [ ] Separate ECS services, task roles, and security groups for `Server` and `Admin` (plane separation enforced by IAM + network).
- [ ] Auto scaling on CPU + task-queue depth metric; minimum 2 tasks per AZ-pair for N+1.
- [ ] Rolling deploys with connection draining; deployment circuit breaker with automatic rollback.

### Ingress & identity
- [ ] gRPC worker traffic: NLB with TLS passthrough (mTLS terminates in Kairos) **or** ALB mutual TLS with a trust store — decide after benchmark.
- [ ] HTTP API: ALB + AWS WAF (rate limiting, managed rule sets).
- [ ] **IAM-based worker auth:** workers running in AWS prove identity with a signed `sts:GetCallerIdentity` request (no secrets to distribute); mapped to a namespace + role.
- [ ] ACM certificates; private CA (AWS Private CA or self-managed) for worker client certs.

### Data
- [ ] Aurora PostgreSQL, Multi-AZ, encrypted with a customer-managed KMS key per cell; enterprise cells can use the customer's own key.
- [ ] IAM database authentication; roles `kairos_migrator`, `kairos_dataplane`, `kairos_controlplane`, `kairos_readonly` per the architecture doc.
- [ ] Evaluate Aurora I/O-Optimized vs Standard once write volume is benchmarked (Kairos is append-heavy).
- [ ] Note: RDS Proxy pins sessions that use `LISTEN/NOTIFY`; use Npgsql pooling directly, or a dedicated notify connection per task.
- [ ] Backups: automated + point-in-time restore; Aurora Global Database (cross-region) for DR on paid tiers.
- [ ] Large payloads (> configurable limit) offloaded to S3 with SSE-KMS, referenced by hash from history.

### Secrets & keys
- [ ] Secrets Manager for the API-key pepper and signing keys; rotated automatically.
- [ ] KMS key hierarchy: per-cell data key, per-tenant key for enterprise; key policies deny the control plane decrypt rights on cell data.

### Observability & audit
- [ ] OpenTelemetry collector sidecar → Amazon Managed Prometheus + Grafana (metrics), sampled traces, **minimal** CloudWatch Logs (cost).
- [ ] CloudTrail org trail + Kairos audit log shipped to the write-only `kairos-logs` account (S3 Object Lock).
- [ ] GuardDuty, Security Hub, Inspector for ECR images.

### CI/CD & IaC
- [ ] Infrastructure as code in **Pulumi (C#)** so infra is the same language as the product; one stack per cell.
- [ ] Azure DevOps pipelines → build, test, benchmark gates → push to ECR → sign image (AWS Signer / Notation) → SBOM → deploy cell by cell (canary cell first).
- [ ] OIDC federation from ADO to AWS (no long-lived AWS keys in pipelines).

### Cost line items to measure (fill in from benchmarks, not guesses)
| Item | Driver | Notes |
|---|---|---|
| Fargate vCPU/GB-hours | tasks × size × hours | Graviton is cheaper per vCPU |
| Aurora instances / ACUs | writer + reader | Serverless v2 for dev cells |
| Aurora storage + I/O | history volume | compare I/O-Optimized |
| Load balancers | LCUs | NLB vs ALB |
| NAT gateway | avoided via VPC endpoints | common hidden cost |
| Data transfer | cross-AZ + egress | keep workers ↔ cell in-region |
| CloudWatch / AMP | metrics + logs | sample, don't log payloads |
| KMS / Secrets Manager | requests, keys | cache data keys |
| WAF, PrivateLink | per rule / per endpoint-hour | |

Target to validate: cost per 1M workflow steps at the bottom tier, compared with Step Functions standard workflow pricing.

### Deployment models offered
1. **Kairos Cloud (multi-tenant cells)** — we run it.
2. **Dedicated cell** — we run it, single tenant, optional customer KMS key.
3. **Bring-your-own-cloud** — Pulumi/Terraform/CloudFormation module deploys a cell into the customer's AWS account; license key, no data leaves their account.
4. **Self-hosted anywhere** — container + Postgres (Azure, on-prem, air-gapped).

## Part 2 — How other services take advantage of Kairos

### Universal entry points (any language, any cloud)
- **gRPC + HTTP/JSON API** with published `.proto` and OpenAPI specs: start, signal, query, cancel, list runs.
- **Generated client SDKs** beyond .NET (TypeScript, Python, Go, Java) for *clients* (start/signal/query); full worker SDK starts with .NET.
- **Outbound webhooks / event stream** of run lifecycle events (started, step completed, failed, completed), HMAC-signed,
  with retries and a dead-letter view.
- **Idempotent start** via client-supplied workflow id, so callers can retry safely.

### AWS-native integrations
- [ ] **Amazon EventBridge:** publish run events to the customer's event bus (cross-account, via a role they grant);
      accept EventBridge **API destinations** as a way to start or signal workflows.
- [ ] **SQS / SNS trigger adapter:** a worker-side component that turns queue messages into workflow starts/signals.
- [ ] **Lambda as an activity:** worker SDK adapter that invokes a Lambda function as a step (customer's role, customer's account).
- [ ] **Step Functions interop:** a step that starts/awaits a Step Functions execution, and an ASL import tool to migrate state machines into C# workflows.
- [ ] **AWS Marketplace** SaaS listing (metered by workflow steps) so customers can pay through their AWS bill.

### Azure & general integrations
- [ ] Azure Service Bus / Event Grid adapters (mirror of the AWS ones); Entra ID workload identity for worker auth.
- [ ] OpenTelemetry trace propagation end to end (caller → workflow → activities).
- [ ] **MCP server** so AI agents can start, inspect, signal, and approve (human-in-the-loop) workflows with scoped credentials.
- [ ] **Managed HTTP worker (opt-in):** a separately deployed, sandboxed worker pool that runs "call this HTTPS endpoint"
      steps for customers who don't want to host workers. Isolated from the server, egress allowlist per tenant,
      so the core rule "the server never runs customer code" still holds.

## Open decisions
- NLB passthrough vs ALB mTLS for gRPC.
- Aurora vs RDS for PostgreSQL for the first cell (cost vs failover time).
- Which client SDK language comes second after .NET.
