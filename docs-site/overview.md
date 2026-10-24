# Overview

## What Kairos does

A normal `async` method loses its place when the process stops. Kairos makes a method durable: each time the method performs a step
(calls an activity, waits on a timer, waits for a signal), Kairos records the outcome in PostgreSQL. If the process stops, another
process picks up the recorded history and runs the method again, feeding it the recorded outcomes until it reaches the point where it
stopped, and then carries on. That technique is called [replay](concepts/durable-execution.md).

What you get from it:

- **Work is not lost.** A run that has started finishes, fails visibly, or is cancelled. It does not quietly stall.
- **Steps are recorded once.** Each step's outcome appears in the run's history exactly once, in order, with no gaps.
- **Side effects can be made safe.** Every activity attempt carries a stable idempotency key, so a payment or an email can be applied at
  most once even though the activity itself may be retried. See [Retries](concepts/retries.md).
- **Waiting is free.** A workflow can wait for seconds or for months (a timer, or a signal from a person) and hold no thread and no
  memory while it waits.
- **Changes ship safely.** Runs in flight keep running correctly across deployments. See [Versioning](concepts/versioning.md).
- **The history is tamper-evident.** Every event is chained to the one before it with SHA-256, and the chain is checked on replay.

## How it runs

Kairos is a library. You register your workflows and activities, point it at a PostgreSQL database, and the workers run inside your
own application process. There is no separate cluster, broker or sidecar to deploy. Several instances of your application can share the
same database; they divide the work between them and fence one another off safely when one of them stops.

Payloads (workflow inputs and results, activity inputs and results, signals) are opaque bytes to the database. You can have Kairos
seal them with keys that only your application can use, so the database holds ciphertext only.

## When Kairos is a good fit

- Business processes that span several systems and must complete: orders, onboarding, payments, provisioning, document handling.
- Anything that waits for a person or an external event, or for a long time: approvals, reminders, trials, renewals.
- Jobs where a retry must not repeat a side effect.
- Processes that call AI models or external tools: put each call in an activity and it is recorded once and not repeated when the workflow is replayed.
- Teams that want durable workflows as plain C# and already run PostgreSQL.

## When it is the wrong tool

- **Very short, high-volume work with no need to survive a crash.** A durable record costs a database write per step. A plain method or a
  queue is cheaper when losing the work is acceptable.
- **Streaming or very large payloads.** Payloads travel through the history. Large blobs belong in blob storage, with a reference in the
  workflow.
- **Hard real-time latency.** A step involves a database round trip.
- **Workflows that must be written in a language other than C#.** The workflow code is .NET.
- **Logic that cannot be made deterministic.** Workflow code is replayed, so it must produce the same decisions each time it runs. See
  [Determinism](concepts/determinism.md). Non-deterministic work belongs in activities.

## Editions

The community packages are open source under the Apache License 2.0 and are free for any use, commercial production included.
