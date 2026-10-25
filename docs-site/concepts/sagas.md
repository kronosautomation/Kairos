# Sagas and compensation

A process that changes several systems cannot be rolled back in one transaction. If the flight is reserved and the hotel is reserved but
the card is declined, the reservations have to be undone one by one. A saga is the pattern for that: each step that changes something
registers the step that undoes it, and if the process fails the undo steps run in reverse.

Kairos makes the rollback itself durable. Each undo is a recorded step that is retried until it works and takes effect once, and a crash
in the middle of a rollback resumes the rollback instead of abandoning it.

## Writing a saga

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#saga)]

`RunSagaAsync` runs the body. `saga.Compensate(name, activityType, input)` registers the undo, **after** the step it undoes succeeded
(register it first only if the step can have an effect even when it fails). If the body throws, the registered undo steps run from the
last to the first, and then the original exception is rethrown, so the run fails with the reason it failed. If the body finishes, the
undo steps are simply discarded.

When an undo is more than one activity, register a function instead and give each activity the retry policy it receives:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#saga-custom-undo)]

## What triggers a rollback

- **Any exception from the body**, including an activity that failed after its retries.
- **A refused approval**: `ThrowIfNotApproved()` inside the body throws, and the saga rolls back.
- **A cancel request**, sent from outside:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#saga-cancel)]

Cancelling is cooperative. A run that is waiting for an approval stops waiting at once; a run in the middle of an activity finishes
that activity first (an effect already under way is never abandoned), and notices the request before its next model call, tool call or
approval, or when your code calls `saga.ThrowIfCancelled()`. The run then rolls back and fails with `SagaCancelledException`.

## Guarantees

- **Reverse order.** The last registered step is undone first.
- **Each undo takes effect once.** An undo is an activity with an idempotency key like any other; a retry after a lost result repeats the
  call, not the effect (see [Retries and idempotency](retries.md)).
- **Retried until done.** The default retry policy for undo steps is 100 attempts with waits growing to five minutes (`SagaOptions.CompensationRetry`).
- **Crash-safe.** The progress of a rollback is in the history. After a crash, replay returns the undo steps that already completed and
  carries on with the first one that did not.
- **One failing undo does not stop the others.** If an undo still fails after its retries, the remaining undo steps run, and the run
  then fails with `SagaCompensationException`, which lists the steps that could not be undone. Those need a person.
- **Visible.** A rollback leaves marker steps in the history. `SagaHistory.Rollbacks(history)` returns what was planned, what started and
  what finished, and `SagaHistory.CheckCompensationsComplete(history)` tells you whether a closed run left a rollback unfinished.

## Steps that start work elsewhere

A step that starts long work in another system (a remote agent's task, say) registers its undo while the work is running, and **discards it
when the work has ended by itself**: `saga.Compensate(...)` returns a handle, and `handle.Discard()` removes the undo so a later rollback does not
undo something that is already over. `saga.WhenCancelled` completes when a cancel is requested, so a step can race it against a durable timer and
stop waiting at once. `ctx.CurrentSaga` gives such a step the saga that is running through the agent context. Calls to remote A2A agents do all of this for you.

## Rules for undo steps

- **Make them idempotent, and make them cope with "nothing to undo".** A step can fail after its effect, or an earlier attempt of the
  undo may already have done part of the work.
- **Undo what you did, not what you hoped to do.** Compensation is semantic (cancel the reservation, issue the refund). It is not a
  database rollback, and some actions cannot be taken back; plan an approval before those instead.
- **A run that an operator terminates is not rolled back.** Terminating ends the run from outside without running its code. Cancel with
  `RequestCancelAsync` when you want the undo steps to run. A host that has authenticated the person asking uses
  `KairosClient.CancelRunAsync(principal, run, reason)` instead: it checks the role, sends the same request without a payload (so a component that holds no
  encryption key can send it), and records who asked in the audit log. The workflow sees an empty cancel reason in that case.
- **A saga cannot continue as new while undo steps are registered**, because they would be lost; it rolls back instead. Keep a saga short
  enough to finish in one run.
- Sagas nest: an inner saga that fails rolls itself back, and the exception then reaches the outer saga, which rolls back its own work.

## Agents

An agent that takes actions fits the same pattern. Do the action in a tool, register its undo, and let a rejected or expired approval
unwind what the agent already did. Wrap the whole agent loop in `ctx.RunSagaAsync` (the agent context has the same method), so model
calls, tool calls and approvals all notice a cancel request.
