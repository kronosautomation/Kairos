---------------------------- MODULE KairosSaga ----------------------------
(***************************************************************************)
(* Saga rollback: compensations run in reverse order, each takes effect    *)
(* once, and a rollback that began always completes, however often the     *)
(* worker dies. No crash bound.                                            *)
(*                                                                         *)
(* Abstraction. The forward steps of a saga register compensations 1..reg. *)
(* When the body fails (an exception, a refused approval, a cancel        *)
(* request) the compensations run from reg down to 1. Running one is an    *)
(* idempotency-keyed activity: its effect is a set, so repeating it is     *)
(* harmless; its completion is recorded in history. A worker can die after *)
(* the effect and before the record (EffectOnly), so the same compensation *)
(* runs again. Replay rebuilds the plan from history, which is why a crash *)
(* never loses the rollback (Resumable = TRUE). History itself is modelled *)
(* by `recorded`, and only grows.                                          *)
(*                                                                         *)
(* Mirrors src/Kairos.Agents/Saga.cs (SagaScope.RollBackAsync).            *)
(*                                                                         *)
(*   I11        a finished rollback undid every registered step, in        *)
(*              reverse order, with each effect applied                    *)
(*   Completes  a rollback that began finishes (liveness)                  *)
(*                                                                         *)
(* Mutations (each must make TLC fail):                                    *)
(*   RecordFirst = TRUE      completion recorded before the effect         *)
(*   Order = "forward"       undo in registration order                    *)
(*   Resumable = FALSE       a crash abandons the rollback                 *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
    MaxComps,        \* compensations a saga may register
    RecordFirst,     \* mutation: record the completion before (instead of with) the effect
    Order,           \* "reverse" (engine) or "forward" (mutation)
    Resumable        \* TRUE (engine): replay resumes a rollback after a crash

ASSUME MaxComps \in Nat \ {0}

VARIABLES
    status,     \* "running" | "rolling" | "ended" | "done" | "lost"
    reg,        \* number of compensations registered
    recorded,   \* compensations whose completion is in history
    effect      \* compensations whose effect happened (idempotent: a set)

vars == <<status, reg, recorded, effect>>

Pending == {i \in 1..reg : i \notin recorded}

Max(S) == CHOOSE x \in S : \A y \in S : y <= x
Min(S) == CHOOSE x \in S : \A y \in S : y >= x

Next1 == IF Order = "reverse" THEN Max(Pending) ELSE Min(Pending)

TypeOK ==
    /\ status \in {"running", "rolling", "ended", "done", "lost"}
    /\ reg \in 0..MaxComps
    /\ recorded \subseteq 1..reg
    /\ effect \subseteq 1..reg

Init ==
    /\ status = "running"
    /\ reg = 0
    /\ recorded = {}
    /\ effect = {}

\* A forward step succeeded and registered its undo.
Register ==
    /\ status = "running"
    /\ reg < MaxComps
    /\ reg' = reg + 1
    /\ UNCHANGED <<status, recorded, effect>>

\* The body finished: the compensations are discarded.
Succeed ==
    /\ status = "running"
    /\ status' = "done"
    /\ UNCHANGED <<reg, recorded, effect>>

\* The body threw, an approval was refused, or a cancel arrived at a step boundary.
Fail ==
    /\ status = "running"
    /\ status' = "rolling"
    /\ UNCHANGED <<reg, recorded, effect>>

\* The next compensation runs and its completion is recorded.
Complete ==
    /\ status = "rolling"
    /\ Pending # {}
    /\ LET i == Next1 IN
         IF RecordFirst
            THEN /\ recorded' = recorded \cup {i}
                 /\ effect' = effect              \* the worker died before applying the effect
            ELSE /\ recorded' = recorded \cup {i}
                 /\ effect' = effect \cup {i}
    /\ UNCHANGED <<status, reg>>

\* The worker applied the effect and died before the record reached history: the step will run again.
EffectOnly ==
    /\ status = "rolling"
    /\ Pending # {}
    /\ effect' = effect \cup {Next1}
    /\ UNCHANGED <<status, reg, recorded>>

\* Nothing left to undo: the rollback ends and the run fails with the original reason.
Finish ==
    /\ status = "rolling"
    /\ Pending = {}
    /\ status' = "ended"
    /\ UNCHANGED <<reg, recorded, effect>>

\* Mutation only: a crash during the rollback abandons it (the plan was not rebuilt from history).
Abandon ==
    /\ ~Resumable
    /\ status = "rolling"
    /\ status' = "lost"
    /\ UNCHANGED <<reg, recorded, effect>>

Next == Register \/ Succeed \/ Fail \/ Complete \/ EffectOnly \/ Finish \/ Abandon

Spec == Init /\ [][Next]_vars /\ WF_vars(Complete) /\ WF_vars(Finish)

(* ---- properties ---- *)

\* A recorded completion always has its effect (I3 for compensations).
RecordedHasEffect == recorded \subseteq effect

\* Reverse order: whenever a step is undone, every later-registered step has been undone before it.
ReverseOrder == \A i \in recorded : \A j \in 1..reg : j > i => j \in recorded

\* I11: a rollback that ended undid everything that was registered, with every effect applied.
I11 == status = "ended" => (recorded = 1..reg /\ effect = 1..reg)

\* Only registered steps are ever undone.
NothingUnregistered == effect \subseteq 1..reg

\* History only grows.
AppendOnly == [][recorded \subseteq recorded']_vars

\* Liveness: a rollback that began finishes.
Completes == (status = "rolling") ~> (status = "ended")

=============================================================================
