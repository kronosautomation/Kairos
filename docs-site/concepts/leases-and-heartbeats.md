# Leases and heartbeats

## Leases

Work is handed to workers under a **lease**: a worker takes a task, and for the length of the lease no other worker can. A workflow
task has a 30-second lease and an activity attempt a one-minute lease by default (both configurable on `WorkerOptions`). If the
worker finishes, it reports back and the lease ends. If it stops, the lease runs out and the work becomes available again.

Each lease carries a **fencing token** that increases every time the task is handed out. When a worker reports a result, the token it
presents must still be the current one. A worker that stalled, lost its lease and then woke up is therefore refused: its late result
is dropped, not merged with the new worker's. This is what lets several instances of your application share one database safely.

Lease and retry times follow the database's clock, so servers whose own clocks drift apart cannot take work from one another early.

## Long-running activities

An activity may take far longer than its lease, as long as it keeps renewing it. Kairos does this for you: while an activity runs, the
worker renews its lease automatically about every third of the lease length. An activity can also report progress itself.

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#long-activity)]

If the lease is lost anyway (the worker was paused for longer than the lease, or the database was unreachable), the next renewal is
refused and the activity's **cancellation token is cancelled**. Pass the token to whatever the activity waits on or calls, so that it
stops promptly. An activity that ignores the token keeps running to the end, and its result is then refused; any side effect it has
caused still happened, which is why the [idempotency key](retries.md#idempotency-keys) matters.

## Shutting down cleanly

When a host stops gracefully, a worker that is holding a task gives it back immediately instead of making another instance wait for the
lease to expire. Giving a task back this way does not count as a failed attempt. A deployment that drains instances one at a time does
not delay the work in flight by a lease.

## Runs that get stuck

If a task keeps running out of lease, the system backs off between offers and, after repeated expiries, **parks** the run's workflow task
instead of offering it forever. A parked run stays open and nothing is lost. It is reported in three places so that it cannot go
unnoticed:

- a warning is logged once per run, with the run id, workflow type and build;
- the gauge `kairos.workflow_tasks.stuck` reports how many are parked;
- the health check registered with `AddKairosHealthCheck()` reports *degraded* while any are parked.

An operator then chooses:

- **Resume** (`IKairosStore.ResumeWorkflowTaskAsync`) once the cause is fixed. The task is offered again from the start.
- **Fail the run** (`IKairosStore.FailRunAsync`) with a reason. The run is closed as failed and all of its pending work and timers are
  removed.

Both actions are written to an audit log with the acting identity. A workflow task that fails *replay* (see [Determinism](determinism.md))
is retried with a delay but is not parked: the usual remedy is to deploy corrected code, after which the run continues by itself.
