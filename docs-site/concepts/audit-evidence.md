# Verifying audit evidence

Kairos can produce a signed bundle of audit evidence for a time range or a set of runs: who started each run, every approval and who
decided it, every policy decision about a tool call, the credentials issued to tools, history exports, administrative actions, and the
last event (the *chain head*) of every run. The bundle is signed with an asymmetric key that belongs to **you**, held in your key
service, never with a key that Kairos holds.

Anyone can check such a bundle, offline, with the open source `kairos` tool. An auditor needs no licence and no access to your system.

```
kairos audit verify ./evidence --key auditor-copy-of-public-key.pem
```

## Getting a bundle from a Kairos server

A Kairos server can make the bundle for you. An administrator of the namespace signs in and asks for it:

```
kairos audit export --server https://kairos.example.com --namespace <guid> --reason "annual audit" --from 2026-01-01 --to 2026-07-01 --out ./evidence
```

The sign-in token is read from the environment variable `KAIROS_TOKEN` (or the one named by `--token-env`). The same request is available as `POST /v1/namespaces/{id}/audit/evidence` and as a gRPC method; the
answer is a zip of the three files below.

- **Administrators only.** The export reads the audit log and exports histories, so it needs both grants, which only the administrator role holds. Operators, readers, approvers and workers are refused.
- **A reason is required**, and it is written to the manifest and to the audit entry of every history the export reads. Each of those entries names the identity the server verified, not a name you typed.
- **The server signs with a key of its own**, set with `Server:AuditEvidence:Signing` (Key Vault, KMS or Cloud KMS, in your account). It must not be the key that signs sessions, and the server refuses to start if it is.
  A server without that setting answers that the export is not supported.
- **Sealed steps are not opened.** The server holds no payload key, so approval and tool steps that your application sealed are listed as not opened. To open them, make the bundle in your own process with a protector,
  using the library the server uses.

## Getting a bundle from an application with no server

An application that embeds Kairos exports with the commercial command line, `kairos-pro audit export --database`, run as the `kairos_migrator` login and signed with a key file you
hold. It can open sealed steps with your Vault key. See [The kairos-pro command line](../pro/kairos-pro.md).

## What you are given

A bundle is three files that belong together:

| File | What it is |
|---|---|
| `audit-<id>.jsonl` | The evidence: one JSON object per line. Every line carries the hash of the line before it. |
| `audit-<id>.manifest.json` | What the bundle covers, who asked and why, the size and SHA-256 of the evidence, how many records of each kind, and the chain head of every run. |
| `audit-<id>.sig` | A detached ES256 signature over the exact bytes of the manifest. |

The auditor needs one more thing, and it must come from you and not from the bundle: **the public key** that matches the signing key,
as a PEM `PUBLIC KEY` block, DER bytes, or the base64 of DER (an ECDSA P-256 `SubjectPublicKeyInfo`). Fetch it from your key service. The
verifier refuses a private key.

## What `kairos audit verify` checks

1. The signature, under the key you supplied. The algorithm must be `ES256`, the type `kairos-audit+jws`, the key identifier the one
   the key was given under, and nothing else may be in the header. Nothing in the manifest is believed before this holds.
2. The evidence file is the one the manifest names: its size, its SHA-256 and its number of records.
3. Every record is well formed, numbered from 1 without gaps, and linked by hash to the record before it, so a record that was
   deleted, added, repeated or moved is found. The number of records of each kind matches the manifest.
4. The chain heads in the manifest are the ones in the records, one per run, and every record about a run is about a run the bundle covers.
5. Everything in the bundle is inside the range the manifest states, or is about a run it names.

It prints each check and a verdict, and exits with 0 when verified, 1 when not, 2 when it could not run. It never prints what a record
says, and what it shows from a bundle (key names, file names) is cut short and stripped of control characters.

## Checking history files against the recorded heads

```
kairos audit verify ./evidence --key public.pem --history run-a.khist --history run-b.metadata.khist
```

Each `--history` file is one that `kairos history export` wrote, either complete or with `--metadata-only`. For each file the tool

- verifies the file by itself (its hash chain; for a metadata-only file also the digest chain that pins what was removed);
- finds the run in the bundle and requires the file to contain the event at the recorded chain head, with the same hash. A later file
  contains it; an earlier file cannot, and the tool says so;
- compares the file's first event with the bundle's start record for the run (workflow type, build, start time, and who started it);
- says which export record in the bundle ends at this file's head, so a file can be traced to the audit entry that authorised it.

A metadata-only file passes all of this although its payloads are empty, because the stored hashes stay. An auditor who is given only
metadata can therefore confirm that it belongs to the history the signed bundle vouches for.

## What a bundle holds, and what it never holds

| Record | Holds |
|---|---|
| `run` | The run, its workflow type and build, who started it (the authenticated caller, when the starting host recorded one), the run it continued. |
| `approval` | The approval's id and tool, when it was requested and decided, how it ended (approved, rejected, unresolved), the approver and roles **as verified** (not as claimed), and how many decisions were refused. |
| `policy_decision` | For each tool call, whether the policy allowed it, held it for approval, or refused it, with the policy's reason. When the policy records its decisions: the rule that decided, and the version and hash of the policy it came from (the rule's own name, never the values it looked at). |
| `credential` | The scopes and expiry of a credential issued to a tool for a call, and on whose behalf. Never the token. |
| `history_export` | Each export of a run's history: who, why, whether metadata only, and the chain head it was cut at. |
| `admin_action` | Every other entry of the audit log: cancel, resume, fail, with the database's own record of who connected. |
| `chain_head` | The last event of each run: its sequence number, hash and how the run ended. |

A bundle never holds a workflow input or result, a signal payload, the arguments or result of a tool, a model message, the text of an
approval request, a token or a key. Free text written by people or policies (a reason, a detail) is included as it was recorded, so
write reasons that say *why* and do not paste data into them.

Approvers, policy decisions and credentials are inside sealed payloads, so the program that writes a bundle needs your key identity to
read them. A bundle written without it is still valid; it says in its manifest that the sealed steps were not opened and reports only
what is readable. Add `--require-opened` to refuse such a bundle.

## What a verified bundle does and does not prove

It proves that the bundle is the one the owner of the key signed, that nothing in it has changed since, and that the records, the
chain heads and any history files you checked agree with each other. It does not prove that the system was honest before the export: the
hash chains and the database's own audit columns are the evidence for that, and a chain head in a signed bundle is a commitment from the
day it was signed. If you rotate the signing key, verify older bundles with the key that was current when they were made.
