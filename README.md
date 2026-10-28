# Kairos

Durable execution for .NET. You write a workflow as an ordinary `async` C# method; Kairos records every step in PostgreSQL, so the workflow carries on through crashes, deployments and long waits, and no step's side effect is applied twice.

It runs inside your application. The only thing to operate is the PostgreSQL database you already have: no cluster, no sidecar, no service that belongs to someone else.

> **Status:** pre-release. The first public release has not been cut; package names and versions below are what it will ship.

## What it gives you

- **Workflows as plain C# methods.** Activities do the real work and are retried by policy; timers and signals hold no thread while they wait.
- **Replay, checked.** Runs resume by deterministic replay of a hash-chained history. Analyzers flag code that would break replay, and `kairos replay-check` replays your own recorded histories against a new build before you deploy it.
- **Your data stays yours.** Payloads are encrypted in your process (AES-256-GCM, keys from your key service) before they reach the database, and Kairos will not start without a protector unless your code says `AllowUnencryptedPayloads()`.
- **Isolation in the database.** Every table carries tenant and namespace and is protected by forced row-level security; the application connects as a role that cannot change the schema or bypass it.
- **Agents.** Durable model and tool steps, approvals by a verified person, budgets, and rollback of multi-step actions. Microsoft Agent Framework agents can be made durable with one line.
- **Evidence.** The core protocol is specified in TLA+ and model-checked; the engine is tested under deterministic simulation, crash-at-every-step and database fault injection.

## Try it

The walk-through in [Getting started](docs-site/getting-started/index.md) takes about ten minutes: a workflow, a run, an interruption and a resume. You need .NET 10 and a PostgreSQL 15 or later.

```
dotnet add package Kairos.Hosting
dotnet add package Kairos.Persistence.Postgres
dotnet add package Kairos.Worker
```

Then run the sample it is built from:

```
dotnet run --project samples/GettingStarted -- --provision "<admin connection string>" "<password for the kairos_dataplane role>"
dotnet run --project samples/GettingStarted
```

## Packages

| Package | For |
|---|---|
| `Kairos.Hosting` | `services.AddKairos(...)`: the engine, a client and in-process workers in your host |
| `Kairos.Persistence.Postgres` | The PostgreSQL store, roles and migrations |
| `Kairos.Worker` | Client-side payload encryption (`EnvelopePayloadProtector`) |
| `Kairos.Cloud.SelfHosted` | Key, blob and event adapters that need no cloud account |
| `Kairos.Agents`, `Kairos.Agents.AgentFramework`, `Kairos.Agents.McpClient`, `Kairos.Agents.A2aClient` | Durable agents and their tools |
| `Kairos.Analyzers` | Compile-time checks for determinism |
| `Kairos.Cli` | The `kairos` tool: `replay-check`, `history export` and `history verify` |
| `Kairos.Testing` | Simulator kit and test helpers for your own workflows |
| `Kairos.Aspire.Hosting` | .NET Aspire integration |

## Documentation

The documentation site is built from [`docs-site/`](docs-site/index.md): concepts, guides and the API reference. Every typed C# snippet in it is taken from a sample under [`samples/`](samples) that is compiled and tested with every build.

## Security

Please report vulnerabilities privately, as [SECURITY.md](SECURITY.md) describes. Do not open a public issue for one.

## Contributing

Contributions are welcome under the Developer Certificate of Origin. Build with `dotnet build` and run `dotnet test`; some tests start PostgreSQL in a container.

## License

Apache License 2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE). Kairos is a product of Kronos Automation LLC.
