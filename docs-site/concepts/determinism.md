# Determinism

Because a workflow is replayed from the top, it must make the same decisions every time it sees the same history. The rule is simple to
state: **everything that can differ from one run of the code to the next must come from the context, or from an activity.**

## What to use instead

| Don't use in a workflow | Why | Use |
|---|---|---|
| `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Stopwatch` | The clock differs on every replay. | `ctx.Now` (the time recorded for the step being processed) |
| `Guid.NewGuid()`, `Random`, `RandomNumberGenerator` | New value on every replay. | `ctx.NewGuid()`; or generate it in an activity and use the recorded result |
| `Task.Delay`, `Thread.Sleep` | Not durable, and not recorded. | `ctx.DelayAsync(duration)` |
| HTTP calls, database access, file or network I/O | The answer can change, and the call would repeat on every replay. | An activity |
| `Task.Run`, `Parallel.*`, threads, timers, `ConfigureAwait(false)` | They move the code off the scheduler that keeps replay in order. | Plain `await` on context calls; `Task.WhenAll` over several context calls is fine |
| Static or shared mutable state, environment variables, configuration read at run time | It can differ between replays and between machines. | Pass what you need in the input, or fetch it in an activity |
| Iterating a `HashSet` or `Dictionary` whose order you depend on across runs of the process | Enumeration order is not guaranteed to be stable. | Sort first |

Reading configuration that never changes, or calling pure functions, is fine. If in doubt, ask whether two machines running the same
code over the same history would always reach the same decision.

## The difference in code

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#wrong)]

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#right)]

The first version asks an activity to send a different id each time it is replayed, so the second replay no longer matches the step the
history recorded.

## What happens when code is not deterministic

Replay compares what the code asks for with what the history recorded, step by step. A difference raises a
`NonDeterminismException` for that task. Kairos does not apply the result:

- the run is left exactly where it was, with nothing appended on top of it;
- the task is released and offered again after a delay, so the run recovers as soon as the code is deployed correctly;
- the failure is logged with the run id, and counted in the `kairos.workflow_task.faults` metric, so a bad deployment is visible.

The usual causes are a changed activity name or order, a changed input, a new or removed step, or one of the habits in the table above.
When you mean to change a workflow that has runs in flight, use [versioning](versioning.md).

## Logging and metrics from workflow code

Workflow code runs more than once, so a log line in it would repeat. Check `ctx.IsReplaying` and write the line only when it is false, or
log from an activity.

## Testing for it

The simulator kit (`Kairos.Testing`) runs workflows against a virtual clock and replays the recorded history after every step, so
non-determinism fails a test rather than a deployment. The samples in this documentation are tested that way.
