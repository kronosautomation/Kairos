# Kairos Documentation Site: Plan

Decided 24 Oct 2026: the product documentation lives in this repository, versioned with the code, built with **DocFX**
(.NET-native; generates API reference from the C# XML comments). Claude Code writes and maintains it; every feature PR
updates the docs it affects.

## Location and build
- `docs-site/` with `docfx.json`; conceptual pages in Markdown, API reference generated from the Apache packages only
  (Abstractions, Core, Persistence.Postgres, Hosting, Client, Worker, Agents, Cloud.SelfHosted, Testing, Conformance).
- Output: static HTML, deployable to Azure Static Web Apps (docs.<domain> once the name and domain are settled; the
  trademark search for "Kairos" is still open, so keep product naming in one variable in `docfx.json`).
- `GenerateDocumentationFile` on for all public packages; missing XML docs on public APIs become build warnings (errors in CI).
- Internal engineering notes (`docs/` reviews, research, strategy) are **not** published. **docs/DISCLOSURE-POLICY.md
  governs every public page**: rewrite customer-facing versions, never copy internal docs; no roadmap, milestone or
  review IDs; commercial features described by what they do, not how they are built.

## Code samples stay correct
- Every code snippet comes from `samples/` projects that compile and run in CI (DocFX code snippet references by region),
  never hand-typed into Markdown. A sample that stops compiling fails the build.
- Each sample has a test that runs it against the in-memory store, and the Postgres ones against Testcontainers.

## Information architecture
1. **Overview**: what Kairos is, when to use it, when not to (from "Applications That Benefit").
2. **Getting started** (10 minutes): install the NuGet package, `AddKairos(o => o.UsePostgres(...))`, write a workflow,
   run it, crash it, watch it resume.
3. **Concepts**: workflows and activities, durable execution and replay, determinism rules, history and the hash chain,
   timers, signals, retries, leases and heartbeats, versioning and patches, continue-as-new, sagas and compensation.
4. **How-to guides**: payments with idempotency keys, human approvals, long-running activities, scheduled jobs,
   migrating from Hangfire / Step Functions / Durable Functions, testing workflows with the simulator kit.
5. **AI agents**: durable model and tool steps, approvals, read-only tool baseline, scoped credentials, MCP and A2A,
   budgets (Pro), Agent Framework provider (Business).
6. **Security**: threat model, tenant isolation (RLS), client-side encryption and key providers, what encryption does
   and does not protect, credentials, audit log, October 2026 lessons in customer-facing form.
7. **Operations**: Postgres setup and roles, migrations, sizing, monitoring and metrics, stuck runs (resume/fail),
   key rotation and revocation, backup and DR, upgrading.
8. **Deploying**: embedded mode, Docker, Azure, AWS, Google Cloud, Kubernetes, air-gapped.
9. **Reference**: generated API, configuration options, metrics and log events, error codes, history event formats,
   CLI (later).
10. **Editions and licensing**: Community vs Pro/Business/Enterprise, from docs/LICENSING.md.
11. **Changelog and compatibility policy**: history-format compatibility promise (golden corpus), supported .NET and Postgres versions.

## CI
- Disclosure check (`build/check-disclosure.ps1`) runs before the docs build and fails on internal references.
- Pipeline step builds the site with warnings as errors, checks links (internal and external, external non-blocking),
  and runs the samples. Nightly publishes a preview build as an artifact.

## Order
1. Scaffold `docs-site/`, `samples/GettingStarted`, the build step, and the Getting started + Concepts pages now (they
   describe Milestones 1 to 3, which exist).
2. Security and Operations pages next (the material already exists in SECURITY-ARCHITECTURE.md and the reviews).
3. Agents, Deploying and Editions as their milestones land.
