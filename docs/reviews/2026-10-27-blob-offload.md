# Blob offload in the engine

Written 27 Oct 2026. Internal.

## What was built

Payloads longer than a threshold (default 256 KiB) are kept in an `IBlobOffload` instead of the history. It lives in `PayloadCrypto`, the one place every payload field is sealed and opened (client, worker, replay check, audit export), so no caller changed
except to pass the option (`BlobOffloadOptions`, `KairosOptions.UseBlobOffload`).

* **Write**: the plaintext (with an escape in front if it could be mistaken for a reference) is sealed under the field's context; if it was over the threshold the *ciphertext* is `PutAsync`ed, and what goes into the history is the **sealed reference** (magic, version, SHA-256, length), sealed under the same context.
* **Read**: open the field; a body that begins with the reference magic is a reference (anything else beginning that way is damage and throws); fetch; verify length and SHA-256 **here** as well as in the adapter; open the blob under the same context.
* **Escape**: a payload that begins with the reference magic, or the escape magic, is stored with the escape in front, so a client cannot make a small payload that reads as a reference to another blob (tested, including to another tenant's blob).
* **Chain**: the reference is inside the event body, so the hash chain covers it. A replaced blob is caught by the hash in the reference, not by the chain; a changed reference is caught by the chain. Both tested on PostgreSQL.
* **Errors**: `PayloadBlobException` (a `PayloadProtectionException`), with `BlobMissingException` and `BlobMismatchException`. Messages carry twelve hex characters of a ciphertext hash and a size, never a payload. A workflow task that hits one is released and counted like any unreadable payload (loud on every attempt). An activity whose input blob is gone fails once, not retryable.
* **Order**: the blob is stored before the event. A crash between leaves an orphan blob, never a reference to nothing.

## Tests

* `BlobOffloadEngineTests`: every field, exact threshold, ciphertext only, references small and sealed, scopes asked, missing/replaced/lengthened blobs (with a store that does not verify), activity input missing, no store configured, field and run binding, look-alike payloads, large look-alikes, cross-tenant reference and blob, chain over the reference, configuration.
* `BlobOffloadCrashMatrixTests`: the crash-at-every-operation matrix with every payload offloaded and the blob store's calls on the same operation counter (before and after each put and get). Same result as without a crash, every history opens completely, and every blob is exactly something the protector returned.
* `BlobOffloadOnPostgresTests`: 3 MiB payloads leave a few KiB in `history_events`, no canary and no blob hash in the rows, no canary in the files; replaced, deleted and restored files; a changed row breaks the chain.
* Sandbox (`EngineOffloadSandboxTests`, Commercial): the engine through S3, Blob Storage and Cloud Storage with each cell's own payload key. Not run here (needs accounts).
* Mutation: 10 mutants, 9 caught, 1 did not compile (the "no store configured" guard; the behaviour has its own test).

## Limits

* Nothing garbage-collects blobs: finished runs' blobs and crash orphans stay until the blob store's own lifecycle rules remove them. A retention sweep needs a way to ask which blobs a run references, which means opening its history; it is not done.
* A run whose blob is gone cannot be replayed (replay needs every payload), so it is stuck on the same error rather than failed. An operator can fail it with the existing recovery API.
* Text fields (failure messages) are not offloaded.
* The server's payload size limit now applies to references for offloaded fields, so large payloads no longer need it raised.
