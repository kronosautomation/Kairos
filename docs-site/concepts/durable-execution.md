# Durable execution and replay

## What is recorded

Every run has a **history**: an ordered, append-only list of events. When a workflow schedules an activity, the history gets an event
for it. When the activity finishes, the history gets its result. The same goes for timers, signals and the final outcome. The
history lives in PostgreSQL, and it is the only state a run has.

## How a run continues after a stop

Nothing about a run lives in a process. Any worker can continue any run, like this:

1. Kairos hands the worker the run's history.
2. The worker starts the workflow method from the top, with the original input.
3. Each time the code asks for a step that the history already contains (run this activity, wait for this timer), the worker does not
   do it again. It hands the code the recorded result and moves on.
4. When the code reaches a step that is not in the history yet, that is new work. The worker records what the code asked for, and the
   activity or timer is scheduled.

That is **replay**. It is why a process can stop anywhere and the run carries on from the last recorded step.

## Batches

Events that arrive close together (an activity result and a signal, say) are handled together: the workflow code runs once over
everything that is new, then records what it decided as a unit. Replay reproduces the same batches, so the code sees the same events in
the same groupings every time. You do not manage this, but it explains why a workflow is never run "in the middle" of two events that
arrived together.

## What is guaranteed

- **Each step is recorded once, in order, with no gaps.** The history never contains the same result twice or skips a position.
- **A worker that has lost its place cannot write.** Work is handed out under a lease with a fencing token. If a worker stalls and
  another takes over, the stalled worker's late writes are refused, not merged. See [Leases and heartbeats](leases-and-heartbeats.md).
- **Changes that would make replay diverge are detected, not applied.** If the code asks for something different from what the history
  recorded, replay stops with a `NonDeterminismException`. The run is left exactly where it was and can be repaired. See
  [Determinism](determinism.md).
- **A run does not silently stall.** Work that cannot be done is retried with backoff, recorded as failed, or parked and reported where
  an operator will see it. See [Leases and heartbeats](leases-and-heartbeats.md#runs-that-get-stuck).

## What is not guaranteed

- **Activities run at least once, not exactly once.** If a worker stops after an activity did its work but before the result was
  recorded, the activity is attempted again. Use the [idempotency key](retries.md#idempotency-keys) so a repeat has no further effect.
- **Replay costs time.** A worker replays the whole history to continue a run. Short and medium histories replay quickly; a history with
  tens of thousands of events does not. [Long-running workflows](continue-as-new.md) explains how to keep histories bounded.
- **The workflow method is not run exactly once.** It runs as many times as there are tasks to process, replaying from the top each
  time. Code outside the context (logging, counters, static state) will run repeatedly. `ctx.IsReplaying` tells you whether the code is
  replaying recorded steps, for example to log only new progress.
