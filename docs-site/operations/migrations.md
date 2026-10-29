# Migrations

Kairos keeps its tables in the `kairos` schema of your database. Two things change that schema: setting the roles up once, and applying migrations when a new version of Kairos needs
new tables or columns. Neither is done by the credential your application runs with.

## The four roles

`kairos db provision` creates them. They can log in, and none of them is a superuser, can create databases or roles, or can bypass row-level security.

| Role | What it is for |
|---|---|
| `kairos_migrator` | Changes the schema. Used by `kairos db migrate` and, if you choose it, by the application at start. Never the credential the application runs with. |
| `kairos_dataplane` | The application's own login: reads and writes run data, cannot change the schema, and is bound by row-level security. |
| `kairos_controlplane` | Reserved for administration tooling: tenant and namespace tables only. Your embedded application does not use it. |
| `kairos_readonly` | Reporting and support, bound by row-level security. The [replay check](../concepts/deploy-safety.md) reads with it. |

## Recommended: a deploy step

Run the two commands from your pipeline, before the new version of the application starts.

Once per database, with a login that may create roles (the passwords come from the environment, never from the command line, so they stay out of process listings and logs):

```
export KAIROS_ADMIN_CONNECTION="Host=db.example.com;Database=kairos;Username=admin;SSL Mode=VerifyFull"
export KAIROS_MIGRATOR_PASSWORD=...
export KAIROS_DATAPLANE_PASSWORD=...
export KAIROS_CONTROLPLANE_PASSWORD=...
export KAIROS_READONLY_PASSWORD=...
kairos db provision
```

A role that already exists keeps its password, and the command lets `kairos_migrator` create the schema in the database. It prints no connection string and no password.

On every deploy, as `kairos_migrator`:

```
export KAIROS_CONNECTION="Host=db.example.com;Database=kairos;Username=kairos_migrator;SSL Mode=VerifyFull"
kairos db migrate --lock-timeout 120
```

- The whole run is **one transaction** behind a database lock. If any migration fails, nothing is changed and the command exits with 1.
- Running it twice, or from several machines at once, is harmless: the later runs wait for the lock, find nothing to apply, and say so.
- `--lock-timeout` is in seconds. Without it a run waits for another run for five minutes, then stops with a message that names the lock, so that a stuck migration is reported
  rather than waited out.
- It refuses to run with any login but `kairos_migrator` (exit code 2). The application's credential and an administrator's are both refused.
- Exit codes: 0 done (including "nothing to apply"), 1 failed and rolled back, 2 could not run (a missing variable, a bad option, the wrong login).

The application's own login, `kairos_dataplane`, cannot change the schema. That is the point of doing it as a separate step: a flaw in your application cannot alter the tables.

## Allowed: migrate when the application starts

For a small team with one or a few instances, the application can migrate on its way up:

[!code-csharp[](../../samples/Operations/ProductionSetup.cs#migrate-on-start)]

- The migration uses a **second credential**. The host refuses to start if the migration connection and the application's connection log in as the same database user, if the migration
  connection is not `kairos_migrator`, or if the application's connection is.
- Instances that start together queue on the same database lock as `kairos db migrate`, so the schema is migrated once and none of them fails. If another run holds the lock longer
  than `lockTimeout` (five minutes unless you say otherwise), the host does not start, and the message says so.
- The migration runs before anything else of Kairos starts: before the check of the application's login, and before any worker claims work. Hosted services that you register before
  `AddKairos` start before the migration does, and must not use Kairos while they start.
- A failed migration stops the host from starting, and leaves the schema as it was.
- The connection string overload builds its connection at start, with the same TLS default as the application's, and disposes it after the migration. The data source overload leaves it to you.

The price is that the process must hold the migrator credential, at least while it starts. If that matters, use the deploy step.

## If the application's login is too powerful

When it starts, `AddKairos` asks PostgreSQL what the application's login can do. A superuser, a role with `BYPASSRLS`, or the owner of a table in the `kairos` schema is refused, because each
of them switches off row-level security without any error. `AllowPrivilegedDatabaseRole()` accepts it on purpose, writes a warning to the log at every start, and reports it to the dashboard.
[The database login](../concepts/database-roles.md) explains the check and what to do instead.

## What Kairos does not check

- The host does not compare the schema with the version of the package at start. Migrate before you roll out a new version (see [Upgrading](upgrades.md)).
- There is no command that undoes a migration. Take a [backup](backup-and-restore.md) before you migrate a production database.
