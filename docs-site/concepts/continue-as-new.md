# Long-running workflows

A workflow that loops for months (a subscription, a polling process, a conversation) would add events forever. A history is bounded,
and replaying a very long one takes longer and longer. The answer is to **continue as new**: the run ends and a fresh run of the same
workflow id starts with an empty history, carrying forward only the state you pass it.

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#batches)]

`ctx.ShouldContinueAsNew` turns true when the history is long enough that handing over is sensible (40,000 events by default, tunable
on `WorkerOptions`). `ctx.HistoryLength` gives the exact count if you prefer your own threshold. Throwing the exception returned by
`ctx.ContinueAsNew(input)` ends the run and starts the next one with that input.

What happens at the hand-over, in one step with no moment in between:

- The old run is closed as *continued as new* and its pending activities and timers are discarded. Hand over at a point where nothing
  you still need is in flight.
- The new run has the same workflow id, the same workflow type and the same build, and its first event records which run it continues.
- The workflow id now means the new run. `KairosClient.StartAsync` with that id is a duplicate of the current run, not a new start, and
  `GetCurrentRunAsync` returns the latest run of the chain.
- Signals sent to the old run's id are refused once it has closed; send them to the current run.

## The limit

The history limit is a backstop (50,000 events by default). A run that reaches it without handing over is ended and marked failed with
a reason that names the limit, and it leaves no work behind. Treat reaching it as a bug in the workflow: hand over earlier.

## Keeping state across hand-overs

Pass what the next run needs in the input: a counter, a cursor, the unfinished part of a list. Keep it small. Anything that should
survive for good (the results of earlier batches) belongs in your own storage, written by an activity.
