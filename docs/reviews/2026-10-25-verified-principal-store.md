# Verified principals in the store: resume, fail, cancel (decisions and open items)

Written 25 Oct 2026. Internal. Needed by `Kairos.Commercial/docs/SERVER-DESIGN.md` section 6 (the standalone server must pass the authenticated principal to every store call that is audited).

## What changed

- `Principal.AuditName` (`role:subject`) is the one way audit entries name a verified principal (export used the same text already).
- `IKairosStore.ResumeWorkflowTaskVerifiedAsync(run, Principal, now)` and `FailRunVerifiedAsync(run, reason, Principal, now)`: the audit entry's `Actor` and `VerifiedPrincipal` are the principal. The
  distinct names are deliberate: the leak suite requires one case per method name (no overloads hiding behind a name).
- `IKairosStore.CancelRunAsync(run, reason, Principal, traceContext, now)` (new): the reserved `kairos.cancel` signal (fixed signal id `cancel`, empty payload) and the `CancelRun` audit entry in one transaction.
  Audited only when the run was open; a second request adds an audit entry but no second signal. A closed, unknown or foreign run returns false and records nothing.
- The plain-string `ResumeWorkflowTaskAsync` / `FailRunAsync` now follow the export rule (closes the previous review's open item): `Actor` is `db:<session_user>`, the supplied name is stored only as `ClaimedActor`.
- A principal whose scope is not the run's is refused with `ArgumentException` by every store (defence in depth; the caller has applied `AccessPolicy`). It throws instead of answering "not found" because it is a bug in the caller.
- `KairosClient.CancelRunAsync`, `TerminateRunAsync`, `FailRunAsync`, `ResumeStuckTaskAsync` take a verified principal and check `AccessPolicy` first (`UnauthorizedAccessException`, nothing written).
- `ReservedSignals.Cancel`, `.CancelSignalId`, `.EnginePrefix` live in Core; `AgentClient.CancelSignalName` is an alias.

## The cancel payload

A server that holds no key cannot seal a payload, and workers refuse unsealed payloads (a compromised server must not author data that workflow code trusts). So the cancel written by the store has **no payload**, and
`PayloadCrypto` accepts exactly one unsealed form on open: a `kairos.cancel` signal with an empty payload. A sealed cancel from `AgentClient.RequestCancelAsync` (with a reason) still works. Tests: a keyless
client cancels a run whose worker holds a key; an empty unsealed payload under any other signal name is still refused. The reason a caller gives goes to the audit entry, not into history, so a cancel through the server
shows the workflow an empty reason.

What this lets a compromised server do: stop runs. It could already (it can close a run). Nothing new is trusted as content.

## Terminate versus fail

`AccessPolicy` has both `TerminateRun` (operator) and `FailRun` (admin). The store has one action behind both (close an open run from outside, `RunTerminated`, audit action `FailRun`). The audit entry shows the principal's
role, which shows which grant was used. If the two should ever differ in effect, that is a separate Apache decision.

## Open items

- `Principal` is still a plain record: "verified" is a contract. The server builds principals only in its authenticators (and has an architecture test that no other code constructs one).
- The audit action for a terminate is `FailRun`.
- Cancel's reason is limited to the audit log; the workflow never sees it when sent through a keyless host.
