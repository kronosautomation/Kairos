# Kairos in Docker

| What | Command (repo root) |
|---|---|
| Run the PR pipeline locally (.NET 10) | `.\ci-local.ps1` |
| Same on .NET 11 / Runtime Async | `.\ci-local.ps1 -Tfm net11.0` |
| Postgres integration on another version | `.\ci-local.ps1 -Stage Integration -PgImage postgres:15` |
| Long simulation (new random seeds) | `.\ci-local.ps1 -Stage Simulation -SimMinutes 30` |
| Mutation testing | `.\ci-local.ps1 -Stage Mutation` |
| Postgres 15–18, HA pair, Toxiproxy | `docker compose -f docker/compose.yml --profile deps up -d` |
| Cloud stand-ins | `docker compose -f docker/compose.yml --profile emulators up -d` |
| Self-hosted Azure Pipelines agent | fill `docker/.env`, then `docker compose -f docker/compose.yml --profile agent up -d` |
| Stop everything | `docker compose -f docker/compose.yml --profile deps --profile emulators --profile agent down` |

## Ports
| Service | Port | Stands in for |
|---|---|---|
| pg15 / pg16 / pg17 / pg18 | 54315 / 54316 / 54317 / 54318 | Postgres version matrix |
| pg-primary / pg-replica | 54320 / 54321 | failover drill (see replica-entrypoint.sh) |
| Toxiproxy → pg17 | 54330 (API 8474) | network faults |
| Azurite | 10000–10002 | Azure Blob/Queue/Table |
| LocalStack 4.12 | 4566 | AWS S3, SQS, SNS, KMS, STS, EventBridge |
| SeaweedFS S3 | 8333 | S3-compatible self-hosted storage |
| Pub/Sub emulator | 8085 | Google Cloud Pub/Sub |
| Vault (dev) | 8200 | key provider |
| Keycloak | 8080 | Entra ID / Okta (OIDC) |
| NATS / Kafka | 4222 / 9092 | event buses |

## Notes
- Tests use Testcontainers by default. The CI container reaches them via `host.docker.internal` (Docker socket mounted).
- The CI container builds a copy of the repo, so it never touches your Windows `bin/obj` folders. Results land in `.ci-local/`.
- The agent container runs as root with the Docker socket mounted so Testcontainers works. That gives jobs full control of
  Docker Desktop: use it only for your own pipelines. Use a PAT scoped to Agent Pools (Read & manage) with a short expiry.
- Not emulated: real IAM / Entra workload identity, KMS key policies, PrivateLink, marketplaces. Those run against cloud sandboxes (M6).
- LocalStack: newer releases require an account token (from 2026-03-23); 4.12 is pinned as the last free community tag.
- MinIO stopped publishing community images in Oct 2025, so SeaweedFS is the S3-compatible stand-in.
