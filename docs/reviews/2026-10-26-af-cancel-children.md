# Agent Framework follow-up: a nested agent does not outlive the run that asked it

Written 27 Oct 2026. Internal. Closes the first open item of `docs/reviews/2026-10-26-agent-framework.md` ("Cancelling or terminating a parent does not stop its child runs"). Code: `AgentFrameworkDriver.cs`, `ChildRuns.cs`, `DurableAgents.cs`, `DurableAIAgent.cs`, `Wire.cs`.

## What was done

- A run started now is a **saga** (patch `kairos.af.cancel`; runs recorded before have no marker and are not cancel-aware). The conversation checks for a cancel between steps and between turns, and waits for the next message, for an approval and for a nested agent race against the cancel, so a waiting conversation stops at once. A model or tool call in flight finishes and is recorded.
- When a tool call starts a nested agent, the driver registers a compensation "cancel nested agent X" and takes it back when the answer arrives (or the child is found ended). The compensation is the activity `kairos.af.child.cancel`: it finds the nested conversation's **current** run (it may have handed over) and sends the reserved cancel signal with its fixed signal id. The nested agent is itself a saga, so the cancel cascades to its own nested agents.
- `DurableAIAgent.CancelAsync(session, reason)` asks a conversation to stop. The run ends as failed with the cancellation, and `TryReadAsync`/`RunAsync` report "was cancelled", not a failure.
- **Terminate** gives the workflow no chance to run, so two things cover it. `DurableAIAgent.TerminateAsync(session, principal, reason)` asks the nested agents first, found from the tool results in the run's history, then terminates. And a nested agent's run **watches its parent**: it looks (first after `ParentWatchInterval`, one minute by default, doubling to a quarter of an hour) whether the run that asked it is still open, and asks itself to stop when it is not. That covers a terminate made in a dashboard, or a `TerminateAsync` that died halfway.
- Docs page section "Stopping a conversation, and the agents it asked", with a compiled sample region.

## A bug I made and fixed on the way

The first version read `ParentWatchInterval` from the worker's options inside workflow code. A history recorded with 20 s did not replay with the default 60 s (non-determinism), and neither would a run in flight after anyone changed the option. The interval is now part of the run's input, as `IdleSeconds` is. The same hazard existed in `MaxStepsPerTurn` (read from options in workflow code since step 1); it is now in the input too (runs recorded earlier fall back to the option). The corpus covers it by construction: `af-cancel-*` were recorded with a 20 s interval and replay under the default.

## Proof

- `CancelTests` (17): cancelling a three-level chain (manager → researcher → specialist, the last waiting for a person's approval) ends all three runs as cancelled, each asked exactly once, the refund never called, immediately (no timer waited); the rollback is readable in history; asking twice is one request; an idle conversation stops and refuses the next message; a step in flight finishes first; a cancel arriving mid-turn stops before the next model call; a nested agent that already answered is not asked to stop; `TerminateAsync` cancels the nested agents first; a nested agent whose parent was terminated by someone else notices and stops; one whose parent is open is left alone however long it looks; without a watch interval it is left alone; the activities refuse another tenant's parent and a workflow that is not a durable agent's; asking a conversation that does not exist is not an error; the same step asking three times leaves one cancel in the child's history.
- **Crash test, exactly once:** the chain with a pending approval, then a cancel, killed at every store operation, before and after it was applied: **343 store operations, 686 crashes injected and survived.** After each: nothing refunded; the manager, researcher and specialist each have exactly one cancel signal; every rollback ended, with exactly the planned compensation and none failed (`SagaHistory.CheckCompensationsComplete`); the three outcomes equal the fault-free run's.
- Corpus v5 gained `af-cancel-parent`, `af-cancel-child`, `af-cancel-watch`; v4 and the rest of v5 still replay.
- 235 Agent Framework tests, 4 sample tests, 338 core tests and 159 agent tests pass.

## Mutation checks (one pass over the new code of all three follow-ups)

42 mutants of the snapshot codec, chain, worker and driver, the waiting reads, the cancel code and the engine's page and wait code. The first pass was interrupted twice (a mutant that loops forever held the runner for 30 minutes; the machine then ran short of memory and the runner was stopped), so the last mutants were run by hand. Result: all caught except three survivors that I examined:

- *A cancel without the fixed signal id* — a real gap: the crash matrix does not make a step send twice. A test now runs the same step three times and expects one cancel in the child's history; the mutant is caught.
- *A cancelled run described as a failure* — the assertion was too weak ("cancelled" is in the failure text too). It now asserts "was cancelled" and no "failed ("; caught.
- *Turns do not look for a cancel* — equivalent: the check at the top of the step loop makes the one before the turn redundant. Kept; it is cheaper to stop there.
- Two engine mutants needed a second look. The page hash-check mutant had hit `Verify`, not `VerifyPage`; re-aimed, it is caught. The mutant that removes the sequence-gap check in `VerifyPage` survives because the link check catches every gap anyway (equivalent). The mutant that did not compile was rewritten and is caught. The mutants that leave the cursor, the wait cursor or the wake-up broken ran until the harness timed them out, which counts as caught.

## Open items

- **Remote workers have no client.** Starting a nested agent, cancelling it and the parent watch all call Kairos from the worker with `Client`. A worker that only connects outbound to a server has `RemoteWorkerStore`, which refuses calls that are not worker calls. Nested agents already needed this to start at all; they still do. It needs a decision about what such a worker may be allowed to start and signal (a scoped credential for its own tenant's conversations), probably in the server, not here.
- A cancel does not interrupt a model call in flight; it waits for it, by design (the call is recorded or not at all).
- The watch costs one timer and one activity per interval per nested run (two events a minute at first, then fewer). `null` turns it off.
- A cancelled conversation is a failed run with the cancellation as its error, as native sagas are; there is no separate "cancelled" state in the engine.
