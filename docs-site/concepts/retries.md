# Retries and idempotency

## Retry policy

Every activity call takes a `RetryPolicy`. If you pass none, the default applies.

| Setting | Default | Meaning |
|---|---|---|
| `MaximumAttempts` | 3 | Total attempts, including the first. `1` means never retry (`RetryPolicy.NoRetry`). |
| `InitialInterval` | 1 second | Wait before the second attempt. |
| `BackoffCoefficient` | 2.0 | Each wait is the previous one multiplied by this. |
| `MaximumInterval` | 1 minute | Waits never grow beyond this. |
| `NonRetryableErrorTypes` | none | Exception type names (`Type.Name`) that end retrying immediately. |

With the defaults the waits are 1 s, then 2 s, then give up. Each attempt that fails is recorded in the history with its attempt number
and error, so you can see exactly what happened. A retry does not repeat anything the workflow already did: only the failed activity
runs again.

## When retries run out

After the last attempt (or on a non-retryable error) the failure is delivered to the workflow as an `ActivityFailureException` at the
`await`. The workflow can catch it and decide: compensate, take another path, or let it propagate, which fails the run. The exception
carries `ActivityType`, `ErrorType` (the exception type name) and `ErrorMessage`.

## Idempotency keys

Activities run **at least once**. An attempt can do its work and then be lost before its result is recorded: the process stops, the
network drops, the lease runs out. The next attempt then does the work again.

So every attempt of an activity carries an **idempotency key** (`ActivityContext.IdempotencyKey`). It is the same for every attempt of
that activity in that run, and different for every other activity. Pass it to the service you call:

[!code-csharp[](../../samples/GettingStarted/OrderProcessing.cs#activities)]

A payment API that accepts an idempotency key applies the charge once however many times it is asked. For a service that has no such
feature, record your own "already done" marker keyed the same way before and after the side effect.

This pair of guarantees is what makes retries safe: the *recording* happens once, and the *effect* happens once if the receiver
honours the key.

## Attempts also end when a worker disappears

If a worker stops holding an attempt without finishing it (it crashed, hung, or lost its connection), the attempt's lease expires and
the attempt is recorded as failed with the error type `LeaseExpired`. That counts as an attempt against `MaximumAttempts` and waits the
policy's backoff before the next one. An activity that kills its worker every time (a runaway memory use, say) therefore ends in a
recorded failure after the last attempt instead of being retried forever. See [Leases and heartbeats](leases-and-heartbeats.md).

## Choosing a policy

- Calls to services that fail transiently: the defaults, or more attempts with a longer maximum interval.
- Calls that must not be repeated at all, and cannot be made idempotent: `RetryPolicy.NoRetry`, and handle the failure in the workflow.
- Errors you know are permanent (a rejected card): list the exception type in `NonRetryableErrorTypes` so the workflow hears about it at once.
