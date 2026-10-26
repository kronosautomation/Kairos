# History and integrity

## What a history contains

| Event | Recorded when |
|---|---|
| Workflow started | The run is created, with its input, its workflow type and the build it is pinned to. |
| Activity scheduled | The workflow asks for an activity; it carries the activity name, input and retry policy. |
| Activity completed / failed | An attempt finishes. A failure says whether it will be retried. |
| Timer started / fired | The workflow starts a timer; the timer falls due. |
| Signal received | A client delivers a signal; it carries the signal name, its id and its payload. |
| Workflow task completed | A batch has been processed: a marker between the events that arrived and the decisions made about them. |
| Patch marker | A [patched](versioning.md) code path was taken. |
| Workflow completed / failed | The run ends with a result or an error. |
| Continued as new | The run handed over to a fresh run. See [Long-running workflows](continue-as-new.md). |
| Run terminated | An operator closed the run, or the history limit was reached. |

Every event has a sequence number, a timestamp, and the tenant, namespace and run it belongs to. Sequence numbers run from 1 without gaps.

## The hash chain

Each event stores a SHA-256 hash that covers the event's content and the hash of the event before it. The first event chains to a fixed
starting value. Changing an event, removing one, inserting one or reordering two changes hashes from that point on, so it is detected.

The chain is checked every time a worker replays a run, and you can check it yourself on any history you read with
`HashChain.Verify`, which throws a `HistoryIntegrityException` naming the first event that fails.

What the chain proves: the events you have are exactly the events that were written, in the order they were written, with none missing
from the middle. What it does not prove on its own: that nothing was cut from the *end*. A history that has had its last events removed is
a valid, shorter chain. The database also keeps each run's latest sequence number and hash, and every write is checked against them, so
the live system does not accept such a gap. Keep that in mind when you compare exported histories.

## Payloads and sizes

Payloads are stored as opaque bytes. With payload encryption enabled they are sealed before they leave your application, and the
history holds ciphertext. Event types, names, sequence numbers and timestamps stay readable so that the system can route and index work.
Keep secrets and personal data out of workflow and activity *names* and out of ids. How payloads are sealed, and what one data key covers, is on [Payload encryption and its limits](payload-encryption.md).

A run's history has a size limit: **50,000 events by default**, configurable per store. A workflow is expected to hand over to a fresh
run well before that; `ctx.ShouldContinueAsNew` turns true at 40,000 events. A run that reaches the limit without handing over is ended
and marked failed, rather than being allowed to grow without bound. See [Long-running workflows](continue-as-new.md).

## Reading a history

`KairosClient.GetHistoryAsync` returns the events as stored. With payload encryption on, use `GetOpenedHistoryAsync` with the same
protector your workers use to read the plaintext; it verifies the chain first.

## Compatibility

A history written by one version of Kairos keeps replaying on later versions. Every change to the engine is tested against a corpus of
recorded histories from earlier builds, covering retries, timers, signals, patches, continue-as-new and operator actions. A change that
would make an old history diverge fails the build.
