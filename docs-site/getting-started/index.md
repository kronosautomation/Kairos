# Getting started

In about ten minutes you will run a workflow that charges a card, waits, and ships an order; stop the process in the middle; start it
again; and watch the run finish without charging twice.

You need .NET 10 and a PostgreSQL database (15 or later) that you can reach as an administrator once, for setup.

The code on this page is the `samples/GettingStarted` project. It is compiled and run by the test suite on every build, including
against a real PostgreSQL server, so what you read here is what runs.

## 1. Install

Add the hosting package to your application. It brings the engine and the PostgreSQL store with it.

```
dotnet add package Kairos.Hosting
```

## 2. Prepare the database

Kairos uses four database roles, so that the role your application runs as can read and write workflow data but can never change the
schema or read anything outside its own tenant. Create them and the schema once, as an administrator:

[!code-csharp[](../../samples/GettingStarted/Database.cs#provision)]

The sample wraps this behind a command-line switch:

```
dotnet run --project samples/GettingStarted -- --provision "<admin connection string>" "<password for kairos_dataplane>"
```

From now on your application connects as `kairos_dataplane` only.

## 3. Write the workflow

A workflow is an `async` method over an `IWorkflowContext`. Everything it does that has to be remembered goes through that context:

[!code-csharp[](../../samples/GettingStarted/OrderProcessing.cs#workflow)]

Three things to notice:

- `ExecuteActivityAsync` runs an *activity*, a piece of code that is allowed to do real work: call a payment service, send an email,
  write to a database. The result is recorded. The retry policy says what happens if it fails.
- `DelayAsync` is a durable timer. It is recorded too, and it holds no thread while it waits.
- The method itself must make the same decisions each time it is replayed, so it asks the context for the time, for new GUIDs and for
  waiting, instead of using `DateTime.UtcNow`, `Guid.NewGuid()` or `Task.Delay`. [Determinism](../concepts/determinism.md) explains why.

## 4. Write the activities

[!code-csharp[](../../samples/GettingStarted/OrderProcessing.cs#activities)]

`activity.IdempotencyKey` is the same on every attempt of this activity in this run. Pass it to the service you are calling and a
repeated attempt becomes a harmless repeat instead of a second charge.

## 5. Register everything and start the host

[!code-csharp[](../../samples/GettingStarted/OrderProcessing.cs#registration)]

[!code-csharp[](../../samples/GettingStarted/Program.cs#host)]

`AddKairos` adds the engine, a `KairosClient` and background workers to your host. The workers poll PostgreSQL (and wake instantly when
work arrives), so there is nothing else to start.

## 6. Start a run

[!code-csharp[](../../samples/GettingStarted/Program.cs#start)]

The workflow id (`"order-1001"`) is yours to choose. Starting an id that already has a current run returns that run instead of creating
a second one, so a retried request cannot start an order twice.

## 7. Run it, interrupt it, run it again

```
dotnet run --project samples/GettingStarted
```

You will see:

```
Started the run.
Charging order-1001 (idempotency key ...)
```

Press Ctrl+C during the two-second wait, then run the same command again:

```
The run already exists; continuing it.
Shipping order-1001
order-1001: shipped, receipt rcpt-order-1001
```

The card was not charged a second time. The second process loaded the run's history, replayed the workflow up to the timer, and
carried on from there.

> [!NOTE]
> The guarantee is about steps *recorded* in the history. If you stop the process while an activity is running, before its result is
> recorded, that activity is attempted again once its lease expires (one minute by default), with the **same idempotency key**. The
> service the activity calls must honour the key, or the effect can happen twice. See [Retries](../concepts/retries.md) and
> [Leases and heartbeats](../concepts/leases-and-heartbeats.md).

## Next

- [Concepts](../concepts/index.md): how replay works and what it asks of your code.
- [Retries](../concepts/retries.md): shaping failure handling per activity.
- [Signals](../concepts/timers-and-signals.md): waiting for a person or another system instead of a timer.
