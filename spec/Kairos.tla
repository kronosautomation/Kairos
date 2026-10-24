---------------------------- MODULE Kairos ----------------------------
(***************************************************************************)
(* Kairos engine protocol, abstracted: history append, workflow tasks with *)
(* leases + fencing tokens + optimistic sequence check, activity tasks    *)
(* with fencing tokens, worker crash, zombie (late) completions, signals. *)
(*                                                                         *)
(* One abstract workflow per run:                                          *)
(*   schedule an activity; close once the activity result AND a signal    *)
(*   are both in history.                                                  *)
(*                                                                         *)
(* Mirrors src/Kairos.Core/InMemoryKairosStore.cs and the PostgreSQL store.*)
(* Invariant names match docs/TEST-STRATEGY.md; each has a simulator check *)
(* (tests/Kairos.Testing/Simulation/SimWorld.cs).                          *)
(*   I1 no lost work      -> Liveness         (sim: I1 never closed)     *)
(*   I2 exactly-once state-> I2, AppendOnly   (sim: I2 structure/chain)  *)
(*   I3 once per key      -> I3               (sim: I3 external effects)  *)
(*   I8 fencing           -> ActFencing=TRUE; with FALSE, TLC finds an I2*)
(*                           violation (KairosUnfenced.cfg)               *)
(*   I9 bounded/no leaks  -> I9               (sim: I9 store drained)     *)
(* I11 (compensation) joins this model with sagas in Milestone 4.          *)
(***************************************************************************)
EXTENDS Naturals, Sequences, FiniteSets

CONSTANTS
    Runs,          \* set of run ids
    MaxSignals,    \* signals each run may receive
    MaxExpiries,   \* total lease expiries (workflow + activity)
    MaxCrashes,    \* total worker crashes (a claim is abandoned, never completed)
    ActFencing     \* TRUE = engine behaviour; FALSE = mutation used to prove the invariants bite

VARIABLES
    hist,          \* [Runs -> Seq(event)]
    wfTask,        \* [Runs -> {"none","pending","leased"}]
    wfToken,       \* [Runs -> Nat]  fencing token, never reset
    wfClaims,      \* set of in-flight workflow-task claims (live or zombie workers)
    actTask,       \* [Runs -> {"none","pending","leased"}]
    actToken,      \* [Runs -> Nat]
    actClaims,     \* set of in-flight activity claims
    effect,        \* [Runs -> BOOLEAN]  external system applied the idempotency key
    calls,         \* [Runs -> Nat]      times the activity body ran (at-least-once)
    sigLeft,       \* [Runs -> Nat]
    expLeft,       \* Nat
    crashLeft      \* Nat

vars == <<hist, wfTask, wfToken, wfClaims, actTask, actToken, actClaims, effect, calls, sigLeft, expLeft, crashLeft>>

Count(h, e) == Cardinality({i \in 1..Len(h) : h[i] = e})
Has(h, e)   == Count(h, e) > 0
Started(r)  == Len(hist[r]) > 0
Closed(r)   == Has(hist[r], "Closed")

\* What workflow code does when replayed over a snapshot: the commands it emits.
Logic(h) ==
    IF ~Has(h, "ActSched") THEN <<"ActSched">>
    ELSE IF Has(h, "ActDone") /\ Has(h, "Signal") /\ ~Has(h, "Closed") THEN <<"Closed">>
    ELSE <<>>

Init ==
    /\ hist      = [r \in Runs |-> <<>>]
    /\ wfTask    = [r \in Runs |-> "none"]
    /\ wfToken   = [r \in Runs |-> 0]
    /\ wfClaims  = {}
    /\ actTask   = [r \in Runs |-> "none"]
    /\ actToken  = [r \in Runs |-> 0]
    /\ actClaims = {}
    /\ effect    = [r \in Runs |-> FALSE]
    /\ calls     = [r \in Runs |-> 0]
    /\ sigLeft   = [r \in Runs |-> MaxSignals]
    /\ expLeft   = MaxExpiries
    /\ crashLeft = MaxCrashes

(* ---- clients -------------------------------------------------------- *)

StartRun(r) ==
    /\ ~Started(r)
    /\ hist'   = [hist EXCEPT ![r] = <<"Started">>]
    /\ wfTask' = [wfTask EXCEPT ![r] = "pending"]
    /\ UNCHANGED <<wfToken, wfClaims, actTask, actToken, actClaims, effect, calls, sigLeft, expLeft, crashLeft>>

SendSignal(r) ==
    /\ Started(r) /\ ~Closed(r) /\ sigLeft[r] > 0
    /\ hist'    = [hist EXCEPT ![r] = Append(@, "Signal")]
    /\ wfTask'  = [wfTask EXCEPT ![r] = IF @ = "none" THEN "pending" ELSE @]
    /\ sigLeft' = [sigLeft EXCEPT ![r] = @ - 1]
    /\ UNCHANGED <<wfToken, wfClaims, actTask, actToken, actClaims, effect, calls, expLeft, crashLeft>>

(* ---- workflow tasks ------------------------------------------------- *)

ClaimWf(r) ==
    /\ wfTask[r] = "pending"
    /\ wfTask'   = [wfTask EXCEPT ![r] = "leased"]
    /\ wfToken'  = [wfToken EXCEPT ![r] = @ + 1]
    /\ wfClaims' = wfClaims \cup {[run |-> r, token |-> wfToken[r] + 1, snap |-> hist[r]]}
    /\ UNCHANGED <<hist, actTask, actToken, actClaims, effect, calls, sigLeft, expLeft, crashLeft>>

\* A lease whose holder crashed: no in-flight claim carries the current token.
OrphanedWf(r) == wfTask[r] = "leased" /\ ~\E c \in wfClaims : c.run = r /\ c.token = wfToken[r]

\* Time passes and the orphaned lease expires; the poll query re-offers it. Always eventually happens (fair),
\* needs no budget because crashes are bounded. Mirrors "leased AND lease_expires_at <= now" in the stores.
RecoverWf(r) ==
    /\ OrphanedWf(r)
    /\ wfTask' = [wfTask EXCEPT ![r] = "pending"]
    /\ UNCHANGED <<hist, wfToken, wfClaims, actTask, actToken, actClaims, effect, calls, sigLeft, expLeft, crashLeft>>

\* Premature expiry while the holder is still alive (slow worker, GC pause, clock skew): creates a zombie.
\* Bounded and not fair: it may happen, it never has to.
ExpireWf(r) ==
    /\ wfTask[r] = "leased" /\ ~OrphanedWf(r) /\ expLeft > 0
    /\ wfTask'  = [wfTask EXCEPT ![r] = "pending"]
    /\ expLeft' = expLeft - 1
    /\ UNCHANGED <<hist, wfToken, wfClaims, actTask, actToken, actClaims, effect, calls, sigLeft, crashLeft>>

\* The holder (possibly a zombie that lost its lease) tries to complete.
CompleteWf(c) ==
    LET r      == c.run
        owns   == wfTask[r] = "leased" /\ wfToken[r] = c.token
        fresh  == Len(hist[r]) = Len(c.snap)
        new    == <<"Boundary">> \o Logic(c.snap)
    IN /\ c \in wfClaims
       /\ wfClaims' = wfClaims \ {c}
       /\ IF ~owns
             THEN \* stale lease: nothing is written
                  UNCHANGED <<hist, wfTask, actTask>>
          ELSE IF ~fresh
             THEN \* sequence conflict: history moved; release the task for a re-run
                  /\ wfTask' = [wfTask EXCEPT ![r] = "pending"]
                  /\ UNCHANGED <<hist, actTask>>
          ELSE \* applied: boundary + commands, task retired, follow-up work derived from the events
                  /\ hist'   = [hist EXCEPT ![r] = @ \o new]
                  /\ wfTask' = [wfTask EXCEPT ![r] = "none"]
                  /\ actTask' = IF Has(new, "Closed") THEN [actTask EXCEPT ![r] = "none"]
                                ELSE IF Has(new, "ActSched") THEN [actTask EXCEPT ![r] = "pending"]
                                ELSE actTask
       /\ UNCHANGED <<wfToken, actToken, actClaims, effect, calls, sigLeft, expLeft, crashLeft>>

(* ---- activity tasks ------------------------------------------------- *)

ClaimAct(r) ==
    /\ actTask[r] = "pending"
    /\ actTask'   = [actTask EXCEPT ![r] = "leased"]
    /\ actToken'  = [actToken EXCEPT ![r] = @ + 1]
    /\ actClaims' = actClaims \cup {[run |-> r, token |-> actToken[r] + 1, ran |-> FALSE]}
    /\ UNCHANGED <<hist, wfTask, wfToken, wfClaims, effect, calls, sigLeft, expLeft, crashLeft>>

OrphanedAct(r) == actTask[r] = "leased" /\ ~\E c \in actClaims : c.run = r /\ c.token = actToken[r]

RecoverAct(r) ==
    /\ OrphanedAct(r)
    /\ actTask' = [actTask EXCEPT ![r] = "pending"]
    /\ UNCHANGED <<hist, wfTask, wfToken, wfClaims, actToken, actClaims, effect, calls, sigLeft, expLeft, crashLeft>>

ExpireAct(r) ==
    /\ actTask[r] = "leased" /\ ~OrphanedAct(r) /\ expLeft > 0
    /\ actTask' = [actTask EXCEPT ![r] = "pending"]
    /\ expLeft' = expLeft - 1
    /\ UNCHANGED <<hist, wfTask, wfToken, wfClaims, actToken, actClaims, effect, calls, sigLeft, crashLeft>>

\* The activity body runs: the external system applies the idempotency key (once) however often the body runs.
ExecAct(c) ==
    /\ c \in actClaims /\ ~c.ran
    /\ actClaims' = (actClaims \ {c}) \cup {[c EXCEPT !.ran = TRUE]}
    /\ effect'    = [effect EXCEPT ![c.run] = TRUE]
    /\ calls'     = [calls EXCEPT ![c.run] = @ + 1]
    /\ UNCHANGED <<hist, wfTask, wfToken, wfClaims, actTask, actToken, sigLeft, expLeft, crashLeft>>

CompleteAct(c) ==
    LET r     == c.run
        owns  == actTask[r] = "leased" /\ actToken[r] = c.token
        apply == IF ActFencing THEN owns ELSE TRUE   \* FALSE = mutation: no fencing token check
    IN /\ c \in actClaims /\ c.ran
       /\ actClaims' = actClaims \ {c}
       /\ IF ~apply
             THEN UNCHANGED <<hist, wfTask, actTask>>
          ELSE IF Closed(r)
             THEN /\ actTask' = [actTask EXCEPT ![r] = "none"]
                  /\ UNCHANGED <<hist, wfTask>>
          ELSE /\ hist'    = [hist EXCEPT ![r] = Append(@, "ActDone")]
               /\ actTask' = [actTask EXCEPT ![r] = "none"]
               /\ wfTask'  = [wfTask EXCEPT ![r] = IF @ = "none" THEN "pending" ELSE @]
       /\ UNCHANGED <<wfToken, wfClaims, actToken, effect, calls, sigLeft, expLeft, crashLeft>>

(* ---- failures ------------------------------------------------------- *)

CrashWorker ==
    /\ crashLeft > 0
    /\ \E c \in wfClaims \cup actClaims :
         /\ IF c \in wfClaims THEN wfClaims' = wfClaims \ {c} /\ UNCHANGED actClaims
            ELSE actClaims' = actClaims \ {c} /\ UNCHANGED wfClaims
    /\ crashLeft' = crashLeft - 1
    /\ UNCHANGED <<hist, wfTask, wfToken, actTask, actToken, effect, calls, sigLeft, expLeft>>

Next ==
    \/ \E r \in Runs : StartRun(r) \/ SendSignal(r) \/ ClaimWf(r) \/ ExpireWf(r) \/ RecoverWf(r)
                     \/ ClaimAct(r) \/ ExpireAct(r) \/ RecoverAct(r)
    \/ \E c \in wfClaims : CompleteWf(c)
    \/ \E c \in actClaims : ExecAct(c) \/ CompleteAct(c)
    \/ CrashWorker

Fairness ==
    /\ \A r \in Runs : WF_vars(StartRun(r)) /\ WF_vars(SendSignal(r)) /\ WF_vars(ClaimWf(r)) /\ WF_vars(ClaimAct(r))
    \* Time keeps moving: orphaned leases eventually expire. Premature expiry (ExpireWf/ExpireAct) is deliberately not fair.
    /\ \A r \in Runs : WF_vars(RecoverWf(r)) /\ WF_vars(RecoverAct(r))
    /\ WF_vars(\E c \in wfClaims : CompleteWf(c))
    /\ WF_vars(\E c \in actClaims : ExecAct(c))
    /\ WF_vars(\E c \in actClaims : CompleteAct(c))

Spec == Init /\ [][Next]_vars /\ Fairness

(* ---- properties ----------------------------------------------------- *)

TypeOK ==
    /\ \A r \in Runs : hist[r] \in Seq({"Started", "Signal", "Boundary", "ActSched", "ActDone", "Closed"})
    /\ wfTask \in [Runs -> {"none", "pending", "leased"}]
    /\ actTask \in [Runs -> {"none", "pending", "leased"}]
    /\ effect \in [Runs -> BOOLEAN]

WellFormed(h) ==
    \/ Len(h) = 0
    \/ /\ h[1] = "Started" /\ Count(h, "Started") = 1
       /\ Count(h, "ActSched") <= 1
       /\ Count(h, "ActDone") <= 1
       /\ (Has(h, "ActDone") => Has(h, "ActSched"))
       /\ (Has(h, "Closed") => Count(h, "Closed") = 1 /\ h[Len(h)] = "Closed")

\* I2: each result is recorded once, in a well-formed order.
I2 == \A r \in Runs : WellFormed(hist[r])

\* I3: a recorded result implies the external effect happened for that run's key.
I3 == \A r \in Runs : Has(hist[r], "ActDone") => effect[r]

\* I9: a closed run leaves no runnable work behind.
I9 == \A r \in Runs : Closed(r) => wfTask[r] = "none" /\ actTask[r] = "none"

\* I2 (temporal): history only ever grows by appending.
AppendOnly ==
    [][\A r \in Runs : /\ Len(hist'[r]) >= Len(hist[r])
                       /\ SubSeq(hist'[r], 1, Len(hist[r])) = hist[r]]_vars

\* I1: no lost work. Every run is started and then closes (under fairness, bounded crashes/expiries).
Liveness == \A r \in Runs : <>Closed(r)

=============================================================================
