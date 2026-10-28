# The standalone server

The Kairos server runs the same engine as the embedded library, as a service of its own for many tenants. Your workers connect to it from your own
network and run your code; the server stores and schedules, and never runs your code and never holds a payload key. People and services sign in
with the identities you already have, every call is checked against a role in one namespace, and the database separates tenants itself.
This page says what the server does, how to start one, and what it does not do. It is a commercial feature.

## What it is

- **One engine.** Runs, history, timers, signals, approvals and leases behave as described in [Concepts](../concepts/index.md). The server is a host around that engine.
- **Workers connect outbound.** A worker opens a connection to the server and asks for work. The server never connects to a worker and never loads or
  deserializes your types.
- **Payloads stay sealed.** The worker seals payloads before they leave it (see [Payload encryption and its limits](../concepts/payload-encryption.md)). The server
  stores ciphertext and has no key.
- **Several nodes.** You can run as many nodes as you like behind a load balancer, all on the same PostgreSQL database.
- **Two transports.** An HTTP/JSON API under `/v1/namespaces/{namespaceId}/...` and gRPC. Workers use gRPC. `GET /health/live` and `GET /health/ready` are
  for the load balancer.

## Signing in

| Who | How | What the server checks |
|---|---|---|
| People | An OpenID Connect access token (`Auth:Oidc`) | Signature, issuer, audience, lifetime, then the person's groups against the group mapping you configured. |
| Azure services | A Microsoft Entra token (`Auth:Entra`) | Directory, audience, and the object id against the identities you listed. |
| AWS services | A signed IAM request, exchanged for a session (`Auth:Iam`) | The role ARN against the roles you listed. A request is accepted once. |
| Google Cloud services | A Google ID token, exchanged for a session (`Auth:Google`) | Audience, age, and the service account's unique id against the identities you listed. A token is accepted once. |

The tenant, the namespace and the role always come from your configuration. Nothing inside a token can name a namespace or raise a role. A credential
that is good for one presentation (IAM, Google) is accepted only at `POST /v1/sessions`, which answers with a short-lived session token. Sessions are signed
with a key in Key Vault, KMS or Cloud KMS that you own, last at most 15 minutes (`Server:SessionLifetime`, default 10), and other nodes verify them with the
public key only. The memory of credentials already used is kept in PostgreSQL so that every node refuses a replay.

Mutual TLS for workers is **not available**. The server does not ask clients for certificates. Workers use tokens and cloud identities.

## Roles

A principal acts only in its own tenant and namespace. A caller with no grant in a namespace gets the same refusal as one whose role is too weak.

| Role | May |
|---|---|
| `Reader` | Read runs, histories, usage and cost. |
| `Approver` | Read runs and decide approvals. Cannot start, signal, cancel or terminate. |
| `Worker` | Poll and complete workflow and activity tasks, heartbeat, and ask the policy and budget services what agent steps need. Cannot start or read runs. |
| `AgentWorker` | Everything a worker may do, plus start, signal, stop and read the conversations of durable agents. Granted only to credentials you name. |
| `Operator` | Start, signal, cancel, terminate and read runs; export a run's history; read the policy and price table. |
| `Admin` | Everything an operator may do, plus read the audit log, resume or fail stuck runs, change policy, prices and budgets, and export audit evidence. |

An approval is a separate operation from a signal: a signal cannot carry one.

## Isolation in the database

Every table carries the tenant and namespace and is protected by row-level security that is forced on the table owner too. The server connects as a role
named `kairos_dataplane`. At start it checks that this role is not a superuser, does not bypass row-level security and owns no table, and it refuses to start
otherwise. The tenant is set for each transaction from the verified credential, never from a request field.

## TLS

The listeners accept TLS 1.3 only. The connection to PostgreSQL negotiates TLS 1.3 only whenever the connection string requires TLS; set
`Server:Database:MinimumTls` to `1.2` for a database that cannot do 1.3.

`Server:Database:Authentication` **must be set**: the server does not start without it. The values are `aws-rds-iam`, `azure-entra` and `gcp-cloudsql-iam`, which replace the database
password with a short-lived token from the identity the server runs as (the connection string then must have no password and must use `SSL Mode=VerifyFull` or `VerifyCA`), and `password`,
which keeps the password in the connection string. Choosing `password` is allowed and is logged as a warning every time the node starts.

## Limits

Limits are checked before anything is stored. Set them under `Server:Limits`.

| Key | Default | Meaning |
|---|---|---|
| `MaxPayloadBytes` | 1 MiB | Largest workflow input, signal or approval payload. |
| `MaxRequestBytes` | 2 MiB | Largest request body. |
| `MaxOpenRunsPerNamespace` | 100,000 | Starting more is refused with HTTP 429. The count is approximate across nodes. |
| `MaxPendingTimersPerNamespace` | 100,000 | A workflow task that would start more ends its run with `TimerLimitExceeded`. |
| `MaxListLimit`, `MaxIdentifierLength`, `MaxSignalIdLength` | 200, 256, 128 | Page size and identifier lengths. |

Rate limits are under `Server:RateLimits` (`PreAuthentication`, `FailureBudget`, `FailureRefillSeconds`, `Tenant`, `TenantWrites`; each bucket has `PerSecond` and
`Burst`). They apply to each node, so with N nodes a tenant can reach N times the figure. Requests per address are limited before authentication, and
repeated failed sign-ins lock an address out for a while. `Server:KnownProxies` and `Server:KnownNetworks` name the load balancers whose `X-Forwarded-For` is believed; with
neither set, the header is ignored.

## Events and large payloads

- **Event feed.** `Server:Events:Webhook:Url` sends run events to an HTTPS endpoint (plain HTTP only to a loopback address). Events carry metadata only, never payloads.
  They are signed with `Server:Events:Webhook:Secret` (at least 16 characters), delivered at least once, and retried up to `MaxAttempts` times (default 4).
  `CloudEvents` switches the body to the CloudEvents format. Redirects are not followed.
- **Blobs.** `Server:Blob:Directory` is an absolute path where large payloads (ciphertext) are kept by content hash. Every node must see the same directory.

## Audit evidence and history export

- **Audit evidence.** Set `Server:AuditEvidence:Signing` (a Key Vault, KMS or Cloud KMS key in your account, not the key that signs sessions) to turn on the
  export. Only administrators may use it. It requires a reason and opens no payload. See [Verifying audit evidence](../concepts/audit-evidence.md).

  ```
  kairos audit export --server https://kairos.example.com --namespace <guid> --reason "annual audit" --out ./evidence
  ```

- **History export.** Operators and administrators can copy one run's history, as stored (ciphertext), for a [local replay](../concepts/replay-locally.md). A reason is
  required and the export is written to the audit log with the verified identity.

  ```
  kairos history export <run-id> --reason "investigate" --server https://kairos.example.com --namespace <guid>
  ```

The sign-in token is read from `KAIROS_TOKEN`, or from the variable named by `--token-env`.

## Starting one

Configuration comes from any .NET configuration provider (files, environment variables, a secret store). A wrong setting stops the node at start and names the setting,
never its value. A minimal node that signs people in through OpenID Connect:

```json
{
  "ConnectionStrings": { "kairos": "Host=db.example.com;Database=kairos;Username=kairos_dataplane;SSL Mode=VerifyFull" },
  "Server": { "Deployment": "kairos-prod", "Database": { "Authentication": "aws-rds-iam", "Region": "eu-west-1" } },
  "Auth": {
    "Oidc": [
      {
        "Issuer": "https://login.example.com/",
        "Audience": "kairos",
        "TenantId": "00000000-0000-0000-0000-000000000001",
        "Groups": [ { "Group": "kairos-admins", "NamespaceId": "00000000-0000-0000-0000-000000000002", "Role": "Admin" } ]
      }
    ]
  }
}
```

Add `Server:Signing` and the `Auth:Entra`, `Auth:Iam` or `Auth:Google` sections for service sign-in, and `Server:Policies:Enabled` for
[policies and budgets](policies-and-budgets.md). The server refuses to start with no way to authenticate anyone.

## What is not available

- **Mutual TLS** for workers.
- **The control plane** (`Kairos.Admin`, for creating tenants and namespaces and managing keys and quotas). Tenants and namespaces come from configuration.
- Rate limits that hold across nodes: they are per node.
