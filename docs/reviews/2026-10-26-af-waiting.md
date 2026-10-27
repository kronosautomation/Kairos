# Agent Framework follow-up: waiting for an answer

Written 26 Oct 2026. Internal. Code: engine `src/Kairos.Abstractions/{HistoryPage,IKairosStore}.cs`, `src/Kairos.Core/{UpdateGate,InMemoryKairosStore,KairosClient,HashChain}.cs`, `src/Kairos.Persistence.Postgres/PostgresKairosStore.cs`; adapter `DurableAgents.cs`, `DurableAIAgent.cs`, `TurnReader.cs`. Commits `215caf0` (engine) and `3ebec1c` (adapter).

## What was asked

"Replace polling the whole history with a cheap engine query for 'the latest turn record of this run' (and use the existing long-poll machinery if it can be extended to run updates without weakening the worker path). Measure the cost of a wait on a 100-message conversation before and after."

## The long poll: answer first

**The existing long poll could not be extended without weakening the worker path, so I did not extend it.** What exists is the server's wake-up machinery for *workers* (`WorkWakeups`, Commercial): a waiter polls for tasks, and a notice of work (`LISTEN/NOTIFY` on Postgres) wakes exactly as many waiters as there are tasks. Its guarantees are about counting: one notice, one task, one waiter.
A run update is not a task: any number of watchers may care about one write, and none of them consumes it. Sharing the machinery would have meant either waking workers for writes they cannot use, or giving watchers a share of wake-ups that workers need. So:

- The engine gets its **own, separate** wait. `IKairosStore.WaitForHistoryAsync(run, afterSequence, wait)` completes with true as soon as the run has an event after `afterSequence`, with false when `wait` passes. In memory it is a broadcast gate (`UpdateGate`: a waiter takes the next-change task *before* it looks, so a write between the look and the await is never missed; one write wakes every waiter and keeps nothing for later).
  On Postgres it is woken by the notices the worker path already listens to: the store subscribes to the notifier's `Notified` event and does **not** take part in its semaphore, so a watcher can never take a wake-up from a worker. A look at the table every second backs it up (a notice can be lost; the look cannot).
- A test proves the worker path is not weakened: with a client waiter and a worker waiting at the same time, one write that makes work wakes both at once (`RunUpdateWaitTests`, against real Postgres), and 50 waiters on a run that does not exist leave a worker's wake-up alone.
- What is **not** built: the standalone server does not expose the wait over HTTP/gRPC. The wait is an `IKairosStore` method, so embedded mode and any host that gives its clients a store have it; a remote client of the server would need an operation in the data plane (an authorization-matrix entry, a leak case, and a bound on how many waits a tenant may hold open). That is a change to Kairos.Server (Commercial) and I did not make it.

## The query

`GetHistoryAfterAsync(run, afterSequence, limit)` and `GetHistoryTailAsync(run, count)` return a `HistoryPage` (the events, and where the history ends). The cost is the slice's, not the history's. `KairosClient` verifies each page (`HashChain.VerifyPage`: in order without a gap, one run, linked, every hash recomputed). A page proves its own consistency and **not** that nothing before or after it was changed; `Verify` over the
whole history still does that, and the documentation of the method says so. Cross-tenant: one leak case per method (a run of another scope reads as empty, and does not even say how long its history is; a wait on it never completes).

"The latest turn record of this run" in the adapter: a message remembers the sequence number it has read up to (a cursor); a look reads the events after it, recognises its own steps by their turn id as they are scheduled, counts their results as they complete (possibly in a later look), and ends at the final step. A message sent without an id of the caller's starts at where the run stood when it was sent, since nothing earlier can be its answer; a message sent with an id (a retry) starts from the beginning,
because it may be asking for an answer that was written long ago.

## The figures

100-message conversation, sealed with client-side encryption, in memory (`MeasurementTests.What_it_costs_to_wait_for_an_answer_in_a_conversation_of_a_hundred_messages`; the first release is `e75671b`, now `3ebec1c`). **Per look:**

| | before | after |
|---|---|---|
| while the agent has not answered yet | 1,068 ms; 1 read of the **whole history**: 1,718 events, 359,799,249 bytes | **0.1 ms; 1 read of 0 events, 11 bytes** |
| first look after the answer is there | 1,114 ms; 1,717 events, 359,799,026 bytes | **2.7 ms; 17 events, 519,693 bytes** (mean of five messages; about three messages in eight have a whole session in one of their steps, see the session-size note) |

Before, a caller who waited looked at 25 ms and then at growing intervals up to a second, and every look cost what the table says; an answer that took three seconds cost on the order of fifteen looks, 16 seconds of work and several gigabytes. After, a wait is one look, one wait that ends at the write that has the answer (it was the engine's own write, so on Postgres it is the notice), and one look: the work does not depend on how long the answer takes, and does not grow with the conversation. (I measured per look and did not time a whole wait; the totals are arithmetic on the per-look figures.) The state read that follows an empty look is one more cheap call and is not in the table (a `GetStateAsync` returns a row, not events).

## How it is proved

- 11 store-contract tests, run on the in-memory store and on Postgres: pages after a sequence, joined pages are the whole history, past the end, the tail, a run that does not exist, impossible arguments, `VerifyPage` finds a changed event / a gap / a reordering, a wait returns at once when the run is already beyond, gives up after its time, wakes on a write without waiting out its time, ends false for a run that does not exist, and wakes many waiters with one write.
- `WaitingTests` (8): a look reads only what is new (and a repeated look reads nothing and returns the same answer), a message repeated with its id finds its own answer and not the latest one, a caller that waits is woken by the write and not by the end of its wait, **does not look again until the run is written to** (a poll would look twenty times in 600 ms; it looks once), follows a hand-over to the fresh run and looks there from then on, and a run that fails while the caller waits ends the wait with the failure.
- The crash-at-every-step and nested-agent matrices ran unchanged on the new reads.

## Things to know

- `ReplyWait` (30 s by default) is the longest silence before a caller looks anyway; the options `MinReplyPollInterval` and `MaxReplyPollInterval` are gone (the adapter has not been released).
- A `DurableTurn` is looked at by one caller at a time (it holds the cursor).
- `GetStateAsync` is still read after a look that finds no answer: one cheap call that tells a run that is running from one that closed (the wait ends with the failure, or the look moves to the fresh run after a hand-over).
