# Workflows and activities

Kairos splits your code into two kinds of method.

## Workflows decide

A **workflow** is an `async` method that coordinates a process: which step comes next, what to do when a step fails, when to wait. It
is the part Kairos records and replays, so it has to be [deterministic](determinism.md): given the same recorded history, it must make
the same decisions. A workflow does not perform side effects itself. It asks for them.

A workflow method (the `WorkflowFunc` delegate) takes an `IWorkflowContext` and the input bytes, and returns the result bytes.

The context is the workflow's only way to reach the outside world:

| You want to | Use |
|---|---|
| Do real work (call an API, write a file, send a message) | `ctx.ExecuteActivityAsync(name, input, retryPolicy)` |
| Wait for a duration | `ctx.DelayAsync(duration)` |
| Wait for a person or another system | `ctx.WaitForSignalAsync(name)` |
| Get the current time | `ctx.Now` |
| Get a new unique id | `ctx.NewGuid()` |
| Change behaviour safely for new runs only | `ctx.IsPatched(id)` |
| Hand over to a fresh run | `throw ctx.ContinueAsNew(input)` |

A workflow's result, or the exception it throws, ends the run: a returned value completes it, an unhandled exception fails it, and
the failure is recorded.

## Activities do

An **activity** is where real work happens. It can call services, read the clock, generate random numbers, touch the file system:
anything that can fail or that gives a different answer each time. Kairos does not replay activities. It runs one, records the
outcome, and from then on workflows are given the recorded outcome.

An activity receives an `ActivityContext` (which attempt this is, a stable [idempotency key](retries.md#idempotency-keys), a way to
[heartbeat](leases-and-heartbeats.md)), the input bytes and a cancellation token (the `ActivityFunc` delegate).

Activities run at least once. Design them so that a repeat is harmless, using the idempotency key.

## Payloads are bytes

Inputs, results and signal payloads are `byte[]`. Kairos does not choose a serializer for you; use whatever suits you (JSON, protobuf,
a plain string, as in the [getting started](../getting-started/index.md) sample). To the database they are opaque, and they can be
sealed with keys only your application holds, so the database stores ciphertext.

## Registering

Workflows and activities are registered by name when the host starts:

[!code-csharp[](../../samples/GettingStarted/OrderProcessing.cs#registration)]

The names are part of the recorded history. Renaming a workflow or an activity that has runs in flight is a change that needs
[versioning](versioning.md).
