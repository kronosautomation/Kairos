# Timers and signals

## Timers

`ctx.DelayAsync(duration)` waits for a duration. The timer is recorded when it starts and again when it fires, and the workflow holds
no thread and no memory in between, so a run can wait for seconds or for months. A zero duration is allowed and fires at the next
opportunity.

When a timer falls due is decided by the database's clock, not by any one application server's, so servers whose clocks disagree do
not fire timers early or late relative to one another. A timer fires shortly after it falls due, the next time a worker looks for due timers; it is not a precision scheduler.

A timer that has started cannot be cancelled. The run ends when the workflow ends, and any timer still pending is discarded then.

## Signals

A signal is a message from outside to a running workflow: a person's approval, an event from another system, a user's reply.

The workflow waits for one by name:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#approval)]

and anything holding a `KairosClient` sends it:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#send-signal)]

Things worth knowing:

- **Signals of one name arrive in order**, and each `WaitForSignalAsync` takes the next one.
- **A signal that arrives early is kept.** If it is delivered before the workflow reaches the wait, the wait returns immediately.
- **Every signal has an id.** If you do not pass one, the client generates one. Delivering the same id to the same run twice has no
  further effect, so a request that is retried after a lost reply cannot signal twice. Give the id yourself when the retry might come
  from a different process.
- **Signalling a run that has ended returns `false`.** The signal is not recorded.
- **A signal's payload is bound to its id when payload encryption is on**, so one approval cannot be substituted for another of the same name.
- A signal wait, like a timer, cannot be cancelled once the workflow is waiting on it.

## Waiting for either

A workflow that must stop waiting after a deadline can start the work in an activity that checks a deadline, or end the run from the
outside (see [Leases and heartbeats](leases-and-heartbeats.md#runs-that-get-stuck) for the operator actions). Racing a timer against a
signal inside one workflow method is not supported.
