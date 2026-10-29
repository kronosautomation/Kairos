# Several instances, one database

You can run as many copies of your application as you like against one database. They coordinate through the database and nothing else: there is no leader to elect and no cluster to form.

## Why that is safe

Work is handed out under a **lease** with a **fencing token** (see [Leases and heartbeats](../concepts/leases-and-heartbeats.md)). Only the instance that holds the current token can report a
result, so an instance that stalled and woke up late is refused rather than merged with the one that took over. Lease and retry times follow the database's clock, so instances whose clocks differ cannot take work early.

When an instance dies, the work it held is not lost:

- A workflow task it held is offered again when its lease runs out (30 seconds by default).
- An activity attempt it held is offered again when its lease runs out (one minute by default; a running activity renews its lease about every third of that, so a long activity does not lose it).
- Every task loop of every other instance looks for expired leases as part of its normal turn, and the `kairos.lease.expiries` counter goes up when it finds some.

Because an activity can run again after a crash, give every side effect the activity's idempotency key (see [Retries and idempotency](../concepts/retries.md)).

## Every instance runs workers by default

`AddKairos` starts `WorkerCount` task loops in each process, two unless you change it. Each loop takes workflow tasks, activity attempts and due timers. Two rules follow.

- **Every worker instance must register the same activities.** Activity tasks are not filtered by type: any loop may claim any activity attempt in the namespace. An instance that has not registered
  the activity fails that attempt for good, without a retry, and the workflow sees the failure. The test for this is in `samples/Operations.Tests`.
- **An instance claims workflow tasks only for the builds it has registered.** During a rolling deployment, runs pinned to the old build stay with the instances that still have it. See [Versioning](../concepts/versioning.md).

## An instance that runs no workers

Some processes only start runs, send signals, decide approvals or read history: a web front end, for example. Give them `WorkerCount = 0`.

[!code-csharp[](../../samples/Operations/ProductionSetup.cs#client-only)]

There is no separate "client only" option; a count of zero is how it is done. What such an instance does and does not do:

- `KairosClient` works, and so do the health check and the telemetry. The health check does not call a process with no task loops stalled; it still checks that the store can be reached.
- It runs no workflow or activity code, so it needs no workflows or activities registered.
- It does not fire timers and does not handle expired leases. **At least one instance with workers must be running**, or timers never fire and a dead worker's tasks are never offered again.
- The gauges `kairos.queue.depth` and `kairos.workflow_tasks.stuck` come from a task loop, so an instance without workers reports none, and does not log the warning for a parked run. Read them from the instances that have workers.
- It still keeps one connection open to listen for new work, although nothing in the process waits for it.

## Connections

Kairos does not set a pool size. Npgsql's own default applies: **up to 100 connections per data source**, none kept open in advance. One data source serves each instance.

Per instance, count:

- one connection for each task loop that is working at that moment (`WorkerCount`),
- one connection that stays open for the life of the process, to listen for new work,
- one for each call your own code makes at the same moment: starting runs, reading history, deciding approvals.

An idle instance with three task loops holds at most five (the test asserts this).

The number that matters is the total. A stock PostgreSQL server accepts 100 connections (`max_connections`), a few of them reserved for administrators; managed services set their own limit. If you run several instances,
leave the default pool and you can run out of connections before any one instance gets near its pool. Pick a number for each instance so that all instances, plus everything else that uses the database, stay below the server's limit:

[!code-csharp[](../../samples/Operations/ProductionSetup.cs#pool-size)]

The connection that listens for new work cannot go through a pooler that gives out connections per transaction (RDS Proxy, a managed connection pool, PgBouncer in transaction mode). Connect to the database itself.
Without that connection workers still find work, by polling, at `PollFallbackInterval` (two seconds by default); they are slower to notice it.

## Stopping an instance

When the host is told to stop (a deploy, a scale-in), every task loop is cancelled. What happens to work in hand:

- **A workflow task** that was claimed is given back at once, without counting as a failed attempt, and another instance takes it.
- **An activity** has its cancellation token cancelled. If it stops, its attempt is given back at once, again without counting as a failure, and another instance runs it. The next attempt has the same idempotency key.
- The host waits for the loops to finish for at most `ShutdownTimeout`, 30 seconds unless you change it. An activity that ignores its cancellation token keeps the host waiting until then. After that the process ends,
  and the activity is treated like one that crashed: its lease runs out and another instance takes it over, with the attempt counted.
- Only then is the connection pool closed.

`samples/Operations.Tests` stops an instance in the middle of an activity and checks that a second instance runs the activity again in seconds, not after the one-minute lease, with the same idempotency key.

Give the host more time if your activities need it, and give your orchestrator at least that long before it kills the process:

[!code-csharp[](../../samples/Operations/ProductionSetup.cs#shutdown-time)]

A rolling deployment therefore goes one instance at a time: start the new instance, wait until its `kairos` health check is healthy, then stop an old one.
