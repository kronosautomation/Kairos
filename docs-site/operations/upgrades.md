# Upgrading

An upgrade is a new version of the Kairos packages, a new version of your own workflow code, or both. The order below keeps the cheap checks in front of the changes that are hard to undo.

1. **Upgrade the packages and build.** Run your tests, including the [determinism test](../concepts/deploy-safety.md#in-a-unit-test) if you have one.
2. **Replay real histories against the new build.** `kairos replay-check` replays a sample of the runs in your database against the build you are about to ship, and fails if any of them diverges.
3. **Back up, then migrate.** `kairos db migrate` as `kairos_migrator` (see [Migrations](migrations.md)).
4. **Roll the new version out**, one instance at a time (see [Several instances, one database](instances.md#stopping-an-instance)).

## The replay check

```
dotnet tool update --global Kairos.Cli
export KAIROS_CONNECTION="Host=db.example.com;Database=kairos;Username=kairos_readonly;SSL Mode=VerifyFull"
kairos replay-check --build ./publish/MyApp.dll --sample 50 --since 7d --strict-coverage
```

Keep the tool on the same version as the Kairos packages your build uses. The build needs a `ReplayCheckSetup` class that registers its workflows and, if payloads are sealed, the same payload protector as the
application; [Deploy safety](../concepts/deploy-safety.md) shows it, and how to run the check as a pipeline job. The check reads only. It prints verdicts, ids, names and sizes, never a payload.

If you would rather not read the production database, run it against a [restored copy](backup-and-restore.md).

## What each failure looks like

| Where | What you see | What it means | What to do |
|---|---|---|---|
| Replay check | `FAIL  nondeterministic`, with the first event that differs, exit code 1 | The new code issues a different command than a recorded run did at that point. | Fix the code, or make the change with the [patch API](../concepts/versioning.md) so that runs already past the point keep the old behaviour. Do not deploy. |
| Replay check | `FAIL  integrity failure`, exit code 1 | The stored history's hash chain does not verify: events were altered, dropped from the middle, or reordered. | Do not deploy over it. Find out why, and restore from a backup if the database was changed. |
| Replay check | `FAIL  unreadable`, exit code 1 | The payloads could not be opened: the setup has no payload protector, or the key provider refused. Nothing was replayed for that history. | Fix the setup or the check's access to the key. A history that was not replayed is not a history that passed. |
| Replay check | `FAIL  error`, exit code 1 | The new code threw while being replayed. The detail names the exception type. | Fix the code. |
| Replay check | `NOTE  not covered` | Runs pinned to a build that the new assembly does not contain. They stay with the instances that still have that build. | Expected during a rolling deployment. `--strict-coverage` turns it into a failure, which is right when the deploy is meant to replace every build in flight. |
| Replay check | `Nothing was replayed`, exit code 1 | The sample was empty. A check that sees nothing proves nothing. | Check the scope, the sample and the workflows the setup registers. |
| Replay check | A message and exit code 2 | The check could not run: a bad option, no connection, a build that could not be loaded, or a store that cannot list runs. | Fix the pipeline step. |
| `kairos db migrate` | `Refused: migrations are applied as kairos_migrator`, exit code 2 | The connection is the application's or an administrator's. | Use the migrator login. |
| `kairos db migrate` | `Migration failed and was rolled back`, exit code 1 | A migration failed. The schema is as it was. | Read the message, fix the cause, run again. |
| `kairos db migrate` | `Another Kairos migration has held the migration lock for more than ...`, exit code 1 | Another run is stuck. Nothing was changed. | Find the session that holds the advisory lock named in the message (`pg_locks`) before you run again. |
| Application start | The host refuses to start, naming the login | The application's login is a superuser, can bypass row-level security, or owns tables. | Connect as `kairos_dataplane` (see [The database login](../concepts/database-roles.md)). |
| Application start | The host refuses to start for lack of a payload protector | Encryption is on by default. | Configure a protector (see [Payload encryption](../concepts/payload-encryption.md)). |
| After the rollout | `kairos.workflow_task.faults` rises and the log has `Workflow task for run {RunId} failed replay and was released` | The deployed code does not replay a run the check did not sample, or the check was skipped. | The runs are not lost and do not advance. A failed task is offered again after 30 seconds by default, so deploy corrected code and they continue by themselves. |

## Rolling back

Kairos has no command that undoes a migration, and it does not promise that the previous version of the application works against a schema that a newer version migrated. Take a backup before
you migrate; if you have to go back, go back to the backup, as described in [Backup and restore](backup-and-restore.md).

A history written by one version keeps replaying on later versions (see [History and integrity](../concepts/history.md)); that promise runs forward only.
