# Concepts

Read these in order the first time; each one builds on the one before.

| Page | In one line |
|---|---|
| [Workflows and activities](workflows-and-activities.md) | Decisions go in workflows, side effects go in activities. |
| [Durable execution and replay](durable-execution.md) | How a method survives a crash, and what is guaranteed. |
| [Determinism](determinism.md) | What workflow code must not do, and what to use instead. |
| [History and integrity](history.md) | The recorded events, the hash chain, and how large a history may grow. |
| [Timers and signals](timers-and-signals.md) | Waiting for time, and for people and other systems. |
| [Retries and idempotency](retries.md) | Failure handling per activity, and making side effects safe to repeat. |
| [Leases and heartbeats](leases-and-heartbeats.md) | How work is handed to workers, long-running activities, and runs that get stuck. |
| [Versioning](versioning.md) | Changing workflow code while runs are in flight. |
| [Long-running workflows](continue-as-new.md) | Keeping a workflow that runs for months bounded. |
