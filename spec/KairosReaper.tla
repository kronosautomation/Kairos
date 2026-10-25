---------------------------- MODULE KairosReaper ----------------------------
(***************************************************************************)
(* The lease reaper, modelled with NO bound on worker crashes.             *)
(*                                                                         *)
(* Kairos.tla bounds crashes (MaxCrashes) so that its state space is      *)
(* finite. This model removes that bound and instead models what the       *)
(* engine does about repeated failure, so the state space stays finite    *)
(* because every expiry consumes an attempt:                               *)
(*                                                                         *)
(*   activity task  : each expiry is recorded as a failed attempt; after   *)
(*                    MaxActAttempts the failure is terminal and the      *)
(*                    workflow is told (ActFailed).                        *)
(*   workflow task  : each expiry counts; after MaxWfAttempts the task is  *)
(*                    parked ("stuck"): kept, visible to operators, never  *)
(*                    offered again until an operator resumes it.          *)
(*                                                                         *)
(* Mirrors ReapExpiredLeasesAsync in src/Kairos.Core/InMemoryKairosStore.cs*)
(* and the PostgreSQL store (StoreRules.MaxWorkflowTaskAttempts). Backoff  *)
(* delays are abstracted: a task that is "pending" is offered eventually.  *)
(*                                                                         *)
(* Claim: whatever the workers do, every run closes (success, failure or   *)
(* operator termination) or ends with its workflow task parked where an   *)
(* operator can see it. Nothing is silently dropped.                       *)
(*                                                                         *)
(*   Resolved   <>[] (Closed \/ Parked)    I1 with failures, no lost work  *)
(*   I2         well-formed, append-only history                           *)
(*   I3         a recorded result implies its idempotent effect            *)
(*   I9         closed runs leave no runnable work; attempts are bounded   *)
(*   ParkedIsVisible  a parked task is never also runnable                  *)
(***************************************************************************)
EXTENDS Naturals, Sequences, FiniteSets

CONSTANTS
    Runs,
    MaxSignals,
    MaxWfAttempts,   \* expiries a workflow task survives before it is parked
    MaxActAttempts,  \* attempts an activity gets before its failure is terminal
    MaxOperatorOps,  \* operator resumes / terminations over the whole run (bounded: an operator is finite)
    Recorded         \* TRUE = engine; FALSE = mutation: exhausted tasks vanish without a record

ASSUME MaxWfAttempts \in Nat \ {0} /\ MaxActAttempts \in Nat \ {0}

VARIABLES
    hist,
    wfTask,         \* "none" | "pending" | "leased" | "stuck"
    wfTok, wfAtt,   \* fencing token (never reset), expiries counted against the current task
    wfClaims,       \* in-flight claims: live workers and zombies
    actTask,        \* "none" | "pending" | "leased"
    actTok, actAtt, \* token, attempt number (1-based)
    actClaims,
    effect,
    sigLeft,
    opsLeft

vars == <<hist, wfTask, wfTok, wfAtt, wfClaims, actTask, actTok, actAtt, actClaims, effect, sigLeft, opsLeft>>

Count(h, e) == Cardinality({i \in 1..Len(h) : h[i] = e})
Has(h, e)   == Count(h, e) > 0
Started(r)  == Len(hist[r]) > 0
Closed(r)   == Has(hist[r], "Closed") \/ Has(hist[r], "Failed") \/ Has(hist[r], "Terminated")
Parked(r)   == wfTask[r] = "stuck"

Closed2(es) == Has(es, "Closed") \/ Has(es, "Failed")

\* What the workflow does when replayed over a snapshot.
Logic(h) ==
    IF ~Has(h, "ActSched") THEN <<"ActSched">>
    ELSE IF Has(h, "ActFailed") THEN <<"Failed">>
    ELSE IF Has(h, "ActDone") /\ Has(h, "Signal") THEN <<"Closed">>
    ELSE <<>>

Init ==
    /\ hist = [r \in Runs |-> <<>>]
    /\ wfTask = [r \in Runs |-> "none"] /\ wfTok = [r \in Runs |-> 0] /\ wfAtt = [r \in Runs |-> 0] /\ wfClaims = {}
    /\ actTask = [r \in Runs |-> "none"] /\ actTok = [r \in Runs |-> 0] /\ actAtt = [r \in Runs |-> 0] /\ actClaims = {}
    /\ effect = [r \in Runs |-> FALSE]
    /\ sigLeft = [r \in Runs |-> MaxSignals]
    /\ opsLeft = MaxOperatorOps

(* ---- clients ---------------------------------------------------------- *)

StartRun(r) ==
    /\ ~Started(r)
    /\ hist'   = [hist EXCEPT ![r] = <<"Started">>]
    /\ wfTask' = [wfTask EXCEPT ![r] = "pending"]
    /\ wfAtt'  = [wfAtt EXCEPT ![r] = 1]
    /\ UNCHANGED <<wfTok, wfClaims, actTask, actTok, actAtt, actClaims, effect, sigLeft, opsLeft>>

\* A task is created (attempt 1) only when none exists; an existing pending, leased or parked task is left alone.
EnsureWf(r) == IF wfTask[r] = "none" THEN "pending" ELSE wfTask[r]
EnsureWfAtt(r) == IF wfTask[r] = "none" THEN 1 ELSE wfAtt[r]

SendSignal(r) ==
    /\ Started(r) /\ ~Closed(r) /\ sigLeft[r] > 0
    /\ hist'    = [hist EXCEPT ![r] = Append(@, "Signal")]
    /\ wfTask'  = [wfTask EXCEPT ![r] = EnsureWf(r)]
    /\ wfAtt'   = [wfAtt EXCEPT ![r] = EnsureWfAtt(r)]
    /\ sigLeft' = [sigLeft EXCEPT ![r] = @ - 1]
    /\ UNCHANGED <<wfTok, wfClaims, actTask, actTok, actAtt, actClaims, effect, opsLeft>>

(* ---- workflow tasks ---------------------------------------------------- *)

ClaimWf(r) ==
    /\ wfTask[r] = "pending"
    /\ wfTask'   = [wfTask EXCEPT ![r] = "leased"]
    /\ wfTok'    = [wfTok EXCEPT ![r] = @ + 1]
    /\ wfClaims' = wfClaims \cup {[run |-> r, token |-> wfTok[r] + 1, snap |-> hist[r]]}
    /\ UNCHANGED <<hist, wfAtt, actTask, actTok, actAtt, actClaims, effect, sigLeft, opsLeft>>

HeldWf(r) == \E c \in wfClaims : c.run = r /\ c.token = wfTok[r]
LeasedWf(r) == wfTask[r] = "leased"

\* The lease expired. What the reaper does with it, for a workflow task: count it; park at the cap.
ReapWfEffect(r) ==
    IF wfAtt[r] + 1 > MaxWfAttempts
       THEN /\ wfTask' = [wfTask EXCEPT ![r] = IF Recorded THEN "stuck" ELSE "none"]
            /\ wfAtt'  = wfAtt
       ELSE /\ wfTask' = [wfTask EXCEPT ![r] = "pending"]
            /\ wfAtt'  = [wfAtt EXCEPT ![r] = @ + 1]

\* Holder gone (crashed): time passing is certain, so this is fair.
ReapWfOrphan(r) ==
    /\ LeasedWf(r) /\ ~HeldWf(r)
    /\ ReapWfEffect(r)
    /\ UNCHANGED <<hist, wfTok, wfClaims, actTask, actTok, actAtt, actClaims, effect, sigLeft, opsLeft>>

\* Holder still alive (slow, paused, skewed): may happen at any time, never has to. Unbounded in number.
ReapWfLive(r) ==
    /\ LeasedWf(r) /\ HeldWf(r)
    /\ ReapWfEffect(r)
    /\ UNCHANGED <<hist, wfTok, wfClaims, actTask, actTok, actAtt, actClaims, effect, sigLeft, opsLeft>>

CompleteWf(c) ==
    LET r     == c.run
        owns  == wfTask[r] = "leased" /\ wfTok[r] = c.token
        fresh == Len(hist[r]) = Len(c.snap)
        new   == <<"Boundary">> \o Logic(c.snap)
    IN /\ c \in wfClaims
       /\ wfClaims' = wfClaims \ {c}
       /\ IF ~owns
             THEN UNCHANGED <<hist, wfTask, wfAtt, actTask, actAtt>>           \* stale token: nothing is written
          ELSE IF ~fresh
             THEN /\ wfTask' = [wfTask EXCEPT ![r] = "pending"]                \* history moved: release, not a failure
                  /\ UNCHANGED <<hist, wfAtt, actTask, actAtt>>
          ELSE /\ hist'    = [hist EXCEPT ![r] = @ \o new]
               /\ wfTask'  = [wfTask EXCEPT ![r] = "none"]
               /\ wfAtt'   = [wfAtt EXCEPT ![r] = 0]
               /\ actTask' = IF Closed2(new) THEN [actTask EXCEPT ![r] = "none"]
                             ELSE IF Has(new, "ActSched") THEN [actTask EXCEPT ![r] = "pending"]
                             ELSE actTask
               /\ actAtt'  = IF Has(new, "ActSched") /\ ~Closed2(new) THEN [actAtt EXCEPT ![r] = 1] ELSE actAtt
       /\ UNCHANGED <<wfTok, actTok, actClaims, effect, sigLeft, opsLeft>>

(* ---- activity tasks ---------------------------------------------------- *)

ClaimAct(r) ==
    /\ actTask[r] = "pending"
    /\ actTask'   = [actTask EXCEPT ![r] = "leased"]
    /\ actTok'    = [actTok EXCEPT ![r] = @ + 1]
    /\ actClaims' = actClaims \cup {[run |-> r, token |-> actTok[r] + 1, ran |-> FALSE]}
    /\ UNCHANGED <<hist, wfTask, wfTok, wfAtt, wfClaims, actAtt, effect, sigLeft, opsLeft>>

HeldAct(r) == \E c \in actClaims : c.run = r /\ c.token = actTok[r]
LeasedAct(r) == actTask[r] = "leased"

\* An activity lease expired: recorded as a failed attempt. Retry while attempts remain, else terminal and the workflow is told.
ReapActEffect(r) ==
    IF actAtt[r] < MaxActAttempts
       THEN /\ hist'    = [hist EXCEPT ![r] = Append(@, "ActRetry")]
            /\ actTask' = [actTask EXCEPT ![r] = "pending"]
            /\ actAtt'  = [actAtt EXCEPT ![r] = @ + 1]
            /\ UNCHANGED <<wfTask, wfAtt>>
       ELSE IF Recorded
       THEN /\ hist'    = [hist EXCEPT ![r] = Append(@, "ActFailed")]
            /\ actTask' = [actTask EXCEPT ![r] = "none"]
            /\ wfTask'  = [wfTask EXCEPT ![r] = EnsureWf(r)]
            /\ wfAtt'   = [wfAtt EXCEPT ![r] = EnsureWfAtt(r)]
            /\ UNCHANGED actAtt
       ELSE /\ actTask' = [actTask EXCEPT ![r] = "none"]                         \* mutation: dropped without a record
            /\ UNCHANGED <<hist, wfTask, wfAtt, actAtt>>

ReapActOrphan(r) ==
    /\ LeasedAct(r) /\ ~HeldAct(r) /\ ~Closed(r)
    /\ ReapActEffect(r)
    /\ UNCHANGED <<wfTok, wfClaims, actTok, actClaims, effect, sigLeft, opsLeft>>

ReapActLive(r) ==
    /\ LeasedAct(r) /\ HeldAct(r) /\ ~Closed(r)
    /\ ReapActEffect(r)
    /\ UNCHANGED <<wfTok, wfClaims, actTok, actClaims, effect, sigLeft, opsLeft>>

ExecAct(c) ==
    /\ c \in actClaims /\ ~c.ran
    /\ actClaims' = (actClaims \ {c}) \cup {[c EXCEPT !.ran = TRUE]}
    /\ effect'    = [effect EXCEPT ![c.run] = TRUE]
    /\ UNCHANGED <<hist, wfTask, wfTok, wfAtt, wfClaims, actTask, actTok, actAtt, sigLeft, opsLeft>>

CompleteAct(c) ==
    LET r    == c.run
        owns == actTask[r] = "leased" /\ actTok[r] = c.token
    IN /\ c \in actClaims /\ c.ran
       /\ actClaims' = actClaims \ {c}
       /\ IF ~owns
             THEN UNCHANGED <<hist, wfTask, wfAtt, actTask, actAtt>>
          ELSE /\ hist'    = [hist EXCEPT ![r] = Append(@, "ActDone")]
               /\ actTask' = [actTask EXCEPT ![r] = "none"]
               /\ wfTask'  = [wfTask EXCEPT ![r] = EnsureWf(r)]
               /\ wfAtt'   = [wfAtt EXCEPT ![r] = EnsureWfAtt(r)]
               /\ UNCHANGED actAtt
       /\ UNCHANGED <<wfTok, wfClaims, actTok, effect, sigLeft, opsLeft>>

(* ---- failures and operators -------------------------------------------- *)

\* A worker dies at any moment, any number of times. THERE IS NO CRASH BUDGET.
CrashWorker ==
    /\ \E c \in wfClaims \cup actClaims :
         IF c \in wfClaims
            THEN wfClaims' = wfClaims \ {c} /\ UNCHANGED actClaims
            ELSE actClaims' = actClaims \ {c} /\ UNCHANGED wfClaims
    /\ UNCHANGED <<hist, wfTask, wfTok, wfAtt, actTask, actTok, actAtt, effect, sigLeft, opsLeft>>

\* ResumeWorkflowTask: a parked task gets a fresh attempt budget.
OperatorResume(r) ==
    /\ opsLeft > 0 /\ Parked(r)
    /\ wfTask'  = [wfTask EXCEPT ![r] = "pending"]
    /\ wfAtt'   = [wfAtt EXCEPT ![r] = 1]
    /\ opsLeft' = opsLeft - 1
    /\ UNCHANGED <<hist, wfTok, wfClaims, actTask, actTok, actAtt, actClaims, effect, sigLeft>>

\* FailRun: the operator ends the run; its tasks are removed.
OperatorTerminate(r) ==
    /\ opsLeft > 0 /\ Started(r) /\ ~Closed(r)
    /\ hist'    = [hist EXCEPT ![r] = Append(@, "Terminated")]
    /\ wfTask'  = [wfTask EXCEPT ![r] = "none"]
    /\ actTask' = [actTask EXCEPT ![r] = "none"]
    /\ opsLeft' = opsLeft - 1
    /\ UNCHANGED <<wfTok, wfAtt, wfClaims, actTok, actAtt, actClaims, effect, sigLeft>>

Next ==
    \/ \E r \in Runs : StartRun(r) \/ SendSignal(r) \/ ClaimWf(r) \/ ClaimAct(r)
                     \/ ReapWfOrphan(r) \/ ReapWfLive(r) \/ ReapActOrphan(r) \/ ReapActLive(r)
                     \/ OperatorResume(r) \/ OperatorTerminate(r)
    \/ \E c \in wfClaims : CompleteWf(c)
    \/ \E c \in actClaims : ExecAct(c) \/ CompleteAct(c)
    \/ CrashWorker

Fairness ==
    /\ \A r \in Runs : WF_vars(StartRun(r)) /\ WF_vars(SendSignal(r)) /\ WF_vars(ClaimWf(r)) /\ WF_vars(ClaimAct(r))
    \* Time passes: a lease nobody holds eventually expires and the reaper handles it. Live expiry and operator actions are not fair.
    /\ \A r \in Runs : WF_vars(ReapWfOrphan(r)) /\ WF_vars(ReapActOrphan(r))
    /\ WF_vars(\E c \in wfClaims : CompleteWf(c))
    /\ WF_vars(\E c \in actClaims : ExecAct(c))
    /\ WF_vars(\E c \in actClaims : CompleteAct(c))

Spec == Init /\ [][Next]_vars /\ Fairness

(* ---- properties --------------------------------------------------------- *)

TypeOK ==
    /\ \A r \in Runs : hist[r] \in Seq({"Started", "Signal", "Boundary", "ActSched", "ActRetry", "ActDone", "ActFailed", "Closed", "Failed", "Terminated"})
    /\ wfTask \in [Runs -> {"none", "pending", "leased", "stuck"}]
    /\ actTask \in [Runs -> {"none", "pending", "leased"}]

WellFormed(h) ==
    \/ Len(h) = 0
    \/ /\ h[1] = "Started" /\ Count(h, "Started") = 1
       /\ Count(h, "ActSched") <= 1
       /\ Count(h, "ActDone") <= 1
       /\ Count(h, "ActFailed") <= 1
       /\ ~(Has(h, "ActDone") /\ Has(h, "ActFailed"))
       /\ (Has(h, "ActDone") => Has(h, "ActSched"))
       /\ (Has(h, "ActFailed") => Has(h, "ActSched"))
       /\ Count(h, "ActRetry") < MaxActAttempts
       /\ (Count(h, "Closed") + Count(h, "Failed") + Count(h, "Terminated") <= 1)
       /\ ((Has(h, "Closed") \/ Has(h, "Failed") \/ Has(h, "Terminated")) => h[Len(h)] \in {"Closed", "Failed", "Terminated"})

I2 == \A r \in Runs : WellFormed(hist[r])

I3 == \A r \in Runs : Has(hist[r], "ActDone") => effect[r]

\* A closed run leaves no runnable work; attempt counters never exceed their caps (the reaper cannot loop forever).
I9 == /\ \A r \in Runs : Closed(r) => wfTask[r] \in {"none"} /\ actTask[r] = "none"
      /\ \A r \in Runs : wfAtt[r] <= MaxWfAttempts /\ actAtt[r] <= MaxActAttempts

\* A parked task is retained for the operator and is not offered to workers.
ParkedIsVisible == \A r \in Runs : Parked(r) => Started(r) /\ ~Closed(r)

AppendOnly ==
    [][\A r \in Runs : /\ Len(hist'[r]) >= Len(hist[r])
                       /\ SubSeq(hist'[r], 1, Len(hist[r])) = hist[r]]_vars

\* No lost work, with failures: the run is eventually and permanently either closed or parked for an operator.
Resolved == \A r \in Runs : <>[](Closed(r) \/ Parked(r))

=============================================================================
