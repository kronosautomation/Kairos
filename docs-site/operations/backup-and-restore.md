# Backup and restore

Everything Kairos knows about a run is in your PostgreSQL database, sealed with keys that are not in it. A backup that has the one without the other restores nothing you can read.

## What to back up

| What | Why |
|---|---|
| **The database.** Use your provider's snapshots or point-in-time recovery, or `pg_dump`. | Histories, timers, tasks and the audit log. |
| **The key ring, or the key in your key service.** With a key ring the application holds (`LocalKeyProvider`), back up every key in it, old ones included. With Key Vault, KMS or [Vault Transit](../concepts/key-providers-vault.md), the key and its versions. | Payloads are sealed on the client. Without the key that sealed them, a restored database is ciphertext. A rotated key keeps unwrapping what it sealed, so deleting an old key makes the data sealed under it unreadable. |
| **The public keys you verify with.** The approval verifier's keys, by key id, and the key you pass to `kairos audit verify`. | Without them you cannot tell whether an approval or an evidence bundle is genuine. They are public; losing them is a nuisance, and the private halves stay in your key service. |
| **The blob store, if you use blob offload.** | Large payloads live there, by hash of their sealed bytes, and the history holds only a reference. A restored database without its blobs fails those tasks with a named error. |
| **The roles and their passwords.** Roles belong to the PostgreSQL server, not to the database, so a dump of the database does not contain them. | After a restore on a new server, `kairos db provision` creates them again. Keep the passwords in your secret store. |

Back up the key material in a different place and under different access than the database. A database backup that sits next to the key that opens it protects nothing.

## What restoring an older backup does

A restore puts the database back as it was at the moment of the backup. The world did not go back with it. Concretely:

- **Runs go back.** A run is where its history was at the backup. A run that had completed since is running again; a run started since does not exist, and whatever started it has to start it again.
- **Activities after the backup run again.** The work they did the first time is not in the restored history, so the activity is offered again. It runs with the **same idempotency key** as before
  (the key is made from the run and the activity, not from the attempt), so a payment service or other API that honours the key does the work once. One that does not honour it does it twice.
  This is why every side effect should use the key (see [Retries and idempotency](../concepts/retries.md)).
- **Timers come due as usual.** A timer is stored with the time it falls due. One that fell due while the database was away fires as soon as a worker looks, once the restored database is in use.
- **Signals and approvals sent after the backup are lost.** The run waits for them again. A person who approved something has to approve it again, and a system that signalled has to signal again.
- **Agent steps after the backup are made again.** A model call is paid for again and may answer differently the second time; a tool call runs again under the same idempotency key.
- **Leases from before the backup have run out.** Tasks that were leased at the moment of the backup are offered again by the first workers that start.
- **Hash chains verify.** A restored history is a prefix of the history that existed, and the chain verifies end to end, including after the work that follows the restore.

The runs are never in a state the application could not have been in. They are in an earlier one.

## Restoring

1. Restore the database from the backup into a new database (provider restore, or `pg_restore`).
2. On a new server, create the roles with `kairos db provision`, and give `kairos_migrator` the right to create the schema before the next migration.
3. Make the key ring, or the key service access, available to the application exactly as before.
4. Start **one** instance first. Watch the health check and the log; tasks that were leased at the backup are handed out again, and `kairos.lease.expiries` may rise while it happens.
5. Start the others. Tell the people and systems that started or signalled runs since the backup what is gone.
6. Run `kairos replay-check` against the restored database if you restored onto a different version of your code.

## The restore test

The restore is tested, not only described. The nightly run of the test suite does this against a real PostgreSQL server in a container:

1. Starts two runs, one that has finished its first activity and waits for a signal, and one with a running timer.
2. Copies the database with `pg_dump` (custom format), executed inside the container.
3. Lets the work go on: sends the signal, lets the charge activity run and the timer fire, and lets both runs complete.
4. Restores the older copy with `pg_restore` into a new database, and starts an application instance on it.
5. Asserts what this page says: the order is running again with exactly the history it had at the copy; its first activity does not run again; the timer fires; the charge and the tick run again with the keys they had the first time; both hash chains verify;
   and a key ring other than the original cannot open the restored history.

The restore goes into a second database on the same PostgreSQL server, so the four roles already exist, as they do after `kairos db provision`. It is the test you should repeat with your own backups: restore into a scratch database, start an instance, and read a run.
