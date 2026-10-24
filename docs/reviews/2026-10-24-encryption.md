# Review: client-side payload encryption (commit c458d12)

Scope: `EnvelopePayloadProtector`, `PayloadCrypto`, `IKeyProvider`/`IPayloadProtector`, `ConfidentialityTests`,
`PayloadProtectionTests`. Threat model: docs/SECURITY-ARCHITECTURE.md sections 4 and 11 (a compromised server, database or
CI identity must not read or forge payloads).

## What is solid
- AES-256-GCM with random 96-bit nonces; data keys wrapped by the customer's `IKeyProvider` with tenant + namespace as context.
- Authenticated data binds every payload to tenant, namespace, run, field and id, so moving ciphertext between tenants,
  runs, fields or activities fails. All failure modes are indistinguishable to the caller.
- Hash chain is verified over ciphertext, replay runs on the opened copy; the store never needs a key.
- Canary scan of every Postgres table and the logs, with a control run proving the scan finds plaintext when encryption is off.
- Data-key reuse is bounded (10,000 payloads per key, well under the GCM nonce bound); the unwrap cache is bounded.

## Findings

### E1 Medium-High: unprefixed failure text is accepted as plaintext, so a compromised server can forge it
`PayloadCrypto` opens `ActivityFailed.Message` and `WorkflowFailed.Message` only if they start with `enc:`; anything else is
returned as-is ("store-written diagnostics"). A compromised server or database can therefore write any failure message
and the workflow will read it as genuine. For agent workflows that feed errors back to a model this is a prompt-injection
path that bypasses encryption entirely.
Fix: accept unprefixed text only for the event the store itself writes (`ErrorType == LeaseExpired`, fixed message);
every other failure message must be sealed or opening fails closed. With that rule a forged `ActivityFailed` cannot carry a
valid message for that activity and attempt, so the workflow task fails instead of trusting it.
Tests: a store-level forgery of an `ActivityFailed` with plaintext message is rejected; the LeaseExpired record still opens.

### E2 Medium: signals with the same name share an authentication context, so they can be replayed
Signal payloads are bound to `signal:<name>` with id 0. Two signals with the same name in one run (two approvals, two
user messages to an agent) are interchangeable: a compromised server can replay the first approval in place of a later
one, or duplicate it. Approval replay is exactly what the agent safety model must prevent.
Fix: give each signal a client-generated id (also usable for deduplication), store it in `SignalReceived`, and include it
in the context. The same applies to any future event that can repeat with the same field and id.
Tests: copy one signal's ciphertext into a second signal of the same name; opening must fail.

### E3 Medium: keys never expire by time, so revoking the customer's key does not take effect promptly
The active data key rotates only after 10,000 uses, and unwrapped keys stay cached until the cache fills. A quiet worker
can keep one data key for days, and after a customer revokes or rotates their KMS key (an incident response step, see
Railway in section 11) a running worker keeps decrypting from cache indefinitely.
Fix: rotate the active data key after a time limit as well as a use limit (for example 1 hour), and give cached unwrapped
keys a TTL (for example 15 minutes, configurable). Document the revocation time this gives.
Tests: with a virtual clock, a revoked key stops opening new payloads once the TTL passes.

### E4 Low: key material is not wiped
Data keys and cached unwrapped keys are plain `byte[]` and are dropped without zeroing. Use
`CryptographicOperations.ZeroMemory` on eviction and when an active key is replaced.

### E5 Low: document what encryption does not protect
Metadata stays readable by design (workflow and activity types, signal names, error types, timers, timestamps). A
compromised server can still withhold, delay or reorder events, fire timers early, or fabricate payload-free events. It
cannot read payloads or forge sealed ones. State this in the security architecture so customers can decide what to put in
names, and so the standalone server (Milestone 7) gets an event-authenticity design (worker-signed commands) if needed.

### E6 Follow-up: decryption-boundary test (security requirement 1)
In embedded mode the application holds the key, so the boundary that matters is the standalone server. Now: add an
architecture test that `Kairos.Persistence.Postgres`, `Kairos.Server` and `Kairos.Admin` never reference `IKeyProvider`,
`IPayloadProtector` or `Kairos.Worker`. At Milestone 7: run the server with full database and process access and assert
it cannot open any payload.

## Suggested order
E1 and E2 before closing Milestone 3 (both are integrity holes under the threat model we just wrote down); E3 with them;
E4 and E5 small; E6 architecture test now, end-to-end test at Milestone 7.

## Status (24 Oct 2026)

| Finding | Status | Where |
|---|---|---|
| E1 unsealed failure text | Done | `PayloadCrypto` refuses any unsealed failure message except the store's fixed `LeaseExpired` record (`StoreRules.LeaseExpiredMessage`, exact text required); a user exception named `LeaseExpired` is recorded as `LeaseExpiredException`. Tests: `FailureTextIntegrityTests` (forged activity/workflow failure rejected, genuine lease expiry still reaches an encrypted workflow, a server-forged final failure faults the task and never reaches workflow code). |
| E2 replayable signals | Done | `SignalReceived.SignalId` (client-generated, optional trailing field so older histories keep their bytes and hashes), bound into the authentication context; stores ignore a redelivered id (`kairos.signal_ids`, migration 0005); `IKairosStore.SignalRunAsync` takes the id. Tests: `SignalIdentityTests` (swapping equal-named signal ciphertexts fails), contract tests on both stores (dedup, per-run ids, closed runs), corpus `m3/` histories with ids. |
| E3 key lifetimes | Done | `EnvelopeOptions`: `DataKeyLifetime` (1 h) and `UnwrappedKeyTtl` (15 min), virtual-clock tests for revocation; window documented in SECURITY-ARCHITECTURE.md section 12. |
| E4 key wiping | Done | Keys are zeroed on rotation, eviction and `Dispose`, never while an operation uses them (reference counted). Tests: zero-after-rotation, zero-after-TTL, concurrent churn with 1 ms lifetimes. |
| E5 document limits | Done | SECURITY-ARCHITECTURE.md section 12. |
| E6 architecture test | Done (end-to-end test: Milestone 7) | `DecryptionBoundaryTests.Server_side_assemblies_...` covers `Kairos.Persistence.Postgres`, `Kairos.Server`, `Kairos.Admin` (when they exist) for key-handling types and the client-side assemblies. The attacker-with-everything test already runs against the Postgres store; the standalone-server version joins in Milestone 7. |

