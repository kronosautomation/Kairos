# API reference

The reference is generated from the XML comments of the public packages.

| Package | What it contains |
|---|---|
| `Kairos.Abstractions` | The contracts: `IWorkflowContext`, `RetryPolicy`, history events, `IKairosStore`, and the provider interfaces for keys, blobs, identity and events. |
| `Kairos.Core` | The engine: replay, the hash chain, the worker runtime, `KairosClient`, the in-memory store. |
| `Kairos.Persistence.Postgres` | The PostgreSQL store, role provisioning and migrations. |
| `Kairos.Hosting` | `AddKairos(...)` for ASP.NET Core and generic hosts, with background workers and a health check. |
| `Kairos.Worker` | Client-side payload encryption. |
| `Kairos.Cloud.SelfHosted` | Key, blob, identity and event adapters that need no cloud service. |
| `Kairos.Testing` | The simulator kit: a virtual clock, a deterministic simulation world and store contract tests for your own tests. |
| `Kairos.Conformance` | Test suites that a provider adapter must pass. |

Start with [`IWorkflowContext`](xref:Kairos.IWorkflowContext), [`RetryPolicy`](xref:Kairos.RetryPolicy) and
[`KairosClient`](xref:Kairos.KairosClient).
