# Deploy safety: replay check

Changing workflow code is safe only if the new code still does what old runs already did. A run that started yesterday is replayed
by tomorrow's build, step by step, against its recorded history (see [Determinism](determinism.md) and [Versioning](versioning.md)).
If the new code issues a different command at any point, the task fails replay and the run stops until you fix it.

`kairos replay-check` finds that **before** you deploy. It replays a sample of real histories against the build you are about to ship
and exits with a non-zero code if any of them diverges, naming the workflow type, the run and the first command that differs.

```
dotnet tool install --global Kairos.Cli
kairos replay-check --build ./publish/MyApp.dll --sample 50 --since 7d --strict-coverage
```

```
Replay check: 100 replayed, 1 failed, 0 not covered. FAILED
FAIL  nondeterministic  process-order  build=2026.10.1  run:3f2a9c1e-...  events=14
      first divergence at event 9
      recorded: ActivityScheduled: activity 3 'ship-order' (14 bytes)
      emitted:  StartTimer: timer 1 for 00:00:05
```

## Your data stays where it is

The check runs inside your environment, as a pipeline job with a **read-only** database role and, if you use client-side encryption,
the right to open payloads through your own key provider. Payloads are decrypted in that process to replay them and go nowhere else.
The output is verdicts, ids, workflow and activity names, sequence numbers and sizes. It never contains a payload, and a divergent
input is reported as "a different input (14 bytes against 12 recorded)", not as the bytes.

## Telling the tool about your build

The tool loads the assembly you point it at and asks it for its workflows. It does not start your application. Add a setup class
next to the code that configures Kairos:

[!code-csharp[](../../samples/GettingStarted/ReplayCheck.cs#replay-setup)]

Activities are never run, so they can be left empty. If the application seals payloads, call `UsePayloadProtector` with the same key
provider the application uses, otherwise sealed histories are reported as unreadable instead of being guessed at.

## What is replayed

For every workflow type and build id in the database, the tool replays the newest `--sample` **open** runs (the ones a new build would
take over) and the newest `--sample` closed runs started within `--since`. `--sample all` replays everything. Open runs are always
considered, whenever they started.

- Runs pinned to a build that the candidate does not contain are **not covered**: they stay with the older build. They are listed, and
  `--strict-coverage` turns them into a failure, which is what you want when a deploy is meant to replace every build in flight.
- Each history is checked against its hash chain first. A history that does not verify is reported as an integrity failure, not as
  nondeterminism.
- `--corpus <directory>` adds `.khist` history files kept next to your code, so a fixed set of known-hard histories is replayed on
  every build as well.
- A check that replays nothing fails. A pipeline that silently saw no histories would prove nothing.

| Exit code | Meaning |
|---|---|
| 0 | Everything replayed |
| 1 | A history diverged, failed its integrity check, could not be opened, or could not be replayed; or nothing was replayed; or (with `--strict-coverage`) a workflow type or build is missing from the candidate |
| 2 | The check could not run: a bad option, no connection, a build that could not be loaded |

Give the connection string through the `KAIROS_CONNECTION` environment variable (or name another variable with `--connection-env`).
`--connection` exists, but a connection string on a command line ends up in process listings and pipeline logs. `--format json` prints
the same result for scripts.

## In a pipeline

The job needs a runner that can reach the database over its private network, and a federated identity that can read the secret holding
the connection string of the read-only role.

**Azure DevOps**

[!code-yaml[](../../samples/ci/azure-devops-replay-check.yml)]

**GitHub Actions**

[!code-yaml[](../../samples/ci/github-actions-replay-check.yml)]

## In a unit test

The same check is available to tests, against a store you fill with the old code (or against histories saved as files). A test is
cheaper than a pipeline job and runs on every commit:

[!code-csharp[](../../samples/Concepts.Tests/DeterminismCheckTests.cs#determinism-test)]

`DeterminismCheck.AssertCorpusReplaysAsync` does the same for a directory of `.khist` files, and throws `ReplayCheckFailedException`
with the report as its message.

## What it cannot tell you

A replay check proves that the new code reproduces the runs you sampled. It cannot see a code path none of them took, a brand new
workflow, or what activities do. Keep your own tests for those, and use the [patch API](versioning.md) for changes that are meant to
differ from what in-flight runs already did.
