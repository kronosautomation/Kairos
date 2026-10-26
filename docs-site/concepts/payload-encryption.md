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

## How long a process keeps a key it has opened

A process that has unwrapped a data key keeps it in memory for `UnwrappedKeyTtl` (15 minutes by default), then wipes it and asks the key provider again, so revoking access at the provider stops
a running process from opening anything it has not already opened within that time. Data keys are wiped from memory when replaced or evicted, and never while an operation is using them.

## What encryption does not protect

- Names, ids, timings, sizes, counts and error types are visible to the server and the database.
- Someone who can run code in a worker, or who can ask your key provider to unwrap, can read what those workers can read.
- Encryption does not make a run's behaviour private from the people who operate your workers.
