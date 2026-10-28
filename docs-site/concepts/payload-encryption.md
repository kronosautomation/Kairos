# Payload encryption and its limits

With payload encryption on, every payload (workflow input and result, activity input and result, signal and approval payloads, failure messages) is sealed in your application before it
leaves it. Kairos, and the database under it, store ciphertext. This page says how that works, what it protects, and where its edges are.

## How a payload is sealed

- Each payload is encrypted with **AES-256-GCM** under a random **data key**.
- The data key is **wrapped** by your key provider (Key Vault, KMS, a local key ring) and the wrapped key is stored in the payload's own header. Kairos never sees a master key or an unwrapped data key.
- The encryption binds each payload to its **tenant, namespace, run, field and id**. A ciphertext copied into another run, another field or another tenant does not open.
- Event types, names, ids, the identifier of whoever started a run, sequence numbers and timestamps stay readable so that the system can route and index work. Keep secrets out of those.

## What one data key covers

A data key is reused for a while, because asking the key provider for a new one on every payload would make every step slower and every provider call a cost. That has a consequence that
you should plan for:

> **An unwrapped data key opens every value sealed with it, in that tenant and namespace, until the data key is rotated.**

By default a data key is replaced after **an hour** or **ten thousand payloads**, whichever comes first. So when a worker, or any service you run that unwraps keys for a person (a key
broker that serves a browser, for example), is given the unwrapped data key for one value, that key also opens the other values sealed under it in the same scope and period. Asking for
"this one value" and receiving a key that opens a thousand is how envelope encryption works, not a defect in a particular component.

Two things follow:

- **A request that names a value is a log of intent, not a limit.** A service that unwraps keys can record which value the caller said it wanted; that record is useful for audit and for
  spotting odd behaviour, but it does not restrict what the returned key can open. Do not treat it as an access control.
- **Access to unwrap is access to the period's values.** Decide who may unwrap keys for a tenant and namespace on that basis, and keep the log.

### Choosing a smaller unit

You can shorten the life of a data key, at the cost of more calls to your key provider:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#data-key-scope)]

`DataKeyLifetime` and `MaxUsesPerDataKey` are validated when the protector is made (a data key seals at least one payload and lives for a positive time). A shorter life also means a revoked
or rotated master key stops sealing new payloads sooner. Measure your key provider's latency before choosing `OneKeyPerValue` for a hot path.

Keys are never shared across tenants or namespaces, whatever the setting.

## Large payloads in a blob store

A history that carries megabytes is slow to replay and expensive to keep in the database. With **blob offload** on, a payload longer than a threshold (256 KiB unless you set another) is sealed as usual, stored in a blob store
you configure (a bucket, a container, a directory) under the SHA-256 of the sealed bytes, and the history keeps only a small **reference** to it. The reference is sealed like any other payload, so the server and the
database see neither that a payload was moved nor the hash it is stored under.

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#blob-offload)]

What you can rely on:

- **The blob store holds ciphertext only.** The blob is the sealed payload, bound to its tenant, namespace, run, field and id, so it opens nowhere else. Offload needs payload encryption and refuses to start without it.
- **The hash chain covers the reference.** Changing a reference in the database breaks the chain, which every reader checks before it opens anything.
- **Reads verify.** The length and the SHA-256 of what the blob store returns are checked against the reference, whatever the store itself claims. A blob that was replaced or damaged fails with `BlobMismatchException`.
- **A missing blob fails clearly.** The task fails with `BlobMissingException`, naming the first characters of the hash and the size, never a payload. It is retried and reported on every attempt, as for any payload that cannot be opened,
  and the run does not move on or complete on a guess. An activity whose input is missing fails for good rather than retrying.
- **A crash cannot leave a reference to nothing.** The blob is stored before the event that refers to it is. A crash in between leaves an unreferenced blob, which wastes space and nothing else.

What it does not do:

- **Nothing deletes old blobs yet.** Blobs of finished runs stay until you remove them with your blob store's own lifecycle rules. Unreferenced blobs from crashes stay too.
- **Sizes are visible.** A blob's size is visible to whoever can list the blob store, as the size of a payload is to the database.
- **Every process that opens a history needs the same blob store.** A reader without one is told so, in words, and is not handed the reference as if it were the payload.

## How long a process keeps a key it has opened

A process that has unwrapped a data key keeps it in memory for `UnwrappedKeyTtl` (15 minutes by default), then wipes it and asks the key provider again, so revoking access at the provider stops
a running process from opening anything it has not already opened within that time. Data keys are wiped from memory when replaced or evicted, and never while an operation is using them.

## What encryption does not protect

- Names, ids, timings, sizes, counts and error types are visible to the server and the database.
- Someone who can run code in a worker, or who can ask your key provider to unwrap, can read what those workers can read.
- Encryption does not make a run's behaviour private from the people who operate your workers.
