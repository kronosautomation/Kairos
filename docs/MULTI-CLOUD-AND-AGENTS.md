# Kairos — Multi-Cloud & AI Agent Engineering Spec

Strategy and benefits write-up: the "Kairos: Multi-Cloud and AI Agent Strategy" doc (claude.ai artifact).
This file is the engineering contract for Claude Code.

## 1. Connected solution layout

```
src/
  Kairos.Abstractions           contracts + provider interfaces (zero dependencies)
  Kairos.Core                   engine: replay, history, hash chain, scheduler (no I/O)
  Kairos.Persistence.Postgres   IKairosStore on PostgreSQL (RLS, roles, SKIP LOCKED, LISTEN/NOTIFY)
  Kairos.Hosting                services.AddKairos(...) embedded mode + in-process workers
  Kairos.Server                 standalone data plane (gRPC + HTTP)
  Kairos.Admin                  control plane
  Kairos.Worker                 remote worker SDK, client-side payload encryption
  Kairos.Client                 start/signal/query client (gRPC + HTTP), OpenAPI/proto published
  Kairos.Agents                 durable agent steps over Microsoft.Extensions.AI IChatClient
  Kairos.Agents.Mcp             MCP server (expose workflows) + durable MCP client steps
  Kairos.Agents.AgentFramework  durability provider for Microsoft Agent Framework
  Kairos.Cloud.Aws              KMS, IAM/STS auth, S3 offload, EventBridge/SQS
  Kairos.Cloud.Azure            Key Vault, Entra workload identity, Blob offload, Event Grid/Service Bus
  Kairos.Cloud.Gcp              Cloud KMS, workload identity, GCS offload, Pub/Sub
  Kairos.Cloud.SelfHosted       Vault/PKCS#11/local keys, mTLS/SPIFFE, file/MinIO offload, Kafka/NATS/webhooks
  Kairos.Analyzers              determinism + secret-handling analyzers
tests/
  Kairos.Conformance            ONE provider test suite every adapter must pass
  ...                           per-project unit/integration tests
infra/
  Kairos.Infra (Pulumi C#)      cell stacks: AWS ECS Fargate, Azure Container Apps, GCP Cloud Run; Helm chart
```

Dependency rules (enforced by an architecture test):
- Abstractions → nothing. Core → Abstractions only.
- No project except `Kairos.Cloud.*` and `infra` may reference a cloud SDK.
- `Kairos.Agents*` depend on Core/Abstractions + Microsoft.Extensions.AI abstractions only (no model vendor SDKs).
- Server/Admin never reference Worker; Worker never references Persistence.

## 2. Provider interfaces (Kairos.Abstractions)

| Interface | Purpose | Notes |
|---|---|---|
| `IKairosStore` | history append, run state, task queue, timers | Postgres is the reference; conformance suite defines semantics |
| `IKeyProvider` | wrap/unwrap data keys for envelope encryption | cache data keys; never send plaintext keys to server |
| `IWorkerAuthenticator` | verify worker/client identity → (tenant, namespace, role) | STS-signed request, Entra/GCP tokens, mTLS/SPIFFE |
| `IBlobOffload` | store payloads above size limit, reference by SHA-256 | encrypted before upload |
| `IEventSink` / `IEventSource` | publish run events; accept starts/signals from buses | at-least-once, idempotent by event id |

Every adapter: async, cancellation-aware, allocation-light, AOT-compatible, OpenTelemetry spans, and passes `Kairos.Conformance`.

## 3. Agent support (Kairos.Agents)

API sketch:
```csharp
[Workflow]
public sealed class SupportAgent(IAgentContext ctx)
{
    public async Task<Answer> RunAsync(Ticket t)
    {
        var history = new List<ChatMessage> { new(ChatRole.User, t.Text) };
        while (true)
        {
            var reply = await ctx.ChatAsync("support-model", history, tools: ctx.Tools("crm", "refunds"));
            if (reply.ToolCalls.Count == 0) return Answer.From(reply);
            foreach (var call in reply.ToolCalls)
                history.Add(await ctx.InvokeToolAsync(call));   // policy may pause here for approval
        }
    }
}
```

Requirements:
- `ChatAsync` = an activity. Response (and token usage) recorded in history; replay never calls the model.
- `InvokeToolAsync` = an activity with idempotency key `run_id + step_seq`; tool receives the key.
- Tool policies per namespace: `Auto | RequireApproval | Deny`, argument-based rules, max calls per run.
- Budgets per run: tokens, cost, wall-clock, step count. Exceeding → workflow fails or pauses for approval (configurable).
- `RequestApprovalAsync(request)` → durable wait for a signal; approvals carry approver identity into history.
- Streaming: model tokens may stream to a live observer, but only the final response is recorded.
- Redaction hooks before persistence; payloads always client-side encrypted.
- Model-neutral via `IChatClient`; named model aliases configured per namespace.
- Large conversations: history compaction via continue-as-new with a summary step.
- Durable sagas: `ctx.Compensate(...)` registers undo steps; on failure, cancel or rejected approval they run in reverse,
  exactly once, resumable after crashes (docs/RESEARCH.md R1).

MCP (Kairos.Agents.Mcp):
- Server tools: `start_workflow`, `get_run`, `list_runs`, `signal_run`, `approve`, `reject`, `cancel_run`; scoped tokens per namespace + role; never exposes payload plaintext unless the caller holds the key.
- Client: `ctx.CallMcpToolAsync(server, tool, args)` as a durable, idempotent step with timeout/retry.

Agent Framework (Kairos.Agents.AgentFramework):
- Durability/checkpoint provider backed by Kairos on Postgres; no Azure dependency.

## 4. Order of work (replaces earlier milestones 2–5 in CLAUDE.md)

1. Core engine — DONE (commit 71ead46).
2. Postgres store + `Kairos.Hosting` embedded mode + cross-tenant leak tests + benchmarks.
3. Provider interfaces + `Kairos.Conformance` + `Kairos.Cloud.SelfHosted`.
4. `Kairos.Agents` v1 (chat/tool steps, approvals, policies, budgets).
5. `Kairos.Agents.Mcp`.
6. `Kairos.Cloud.Azure` + `Kairos.Cloud.Aws` + Pulumi cell stacks.
7. `Kairos.Server`, `Kairos.Admin`, `Kairos.Client`, dashboard with approval inbox.
8. `Kairos.Agents.AgentFramework`, `Kairos.Cloud.Gcp`, marketplace listings.
