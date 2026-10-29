# Running Kairos in production

These pages are for the people who deploy and look after an application that has Kairos inside it: the library, your own PostgreSQL, and no separate server. Each page says what
to do, what Kairos does for you, and what it does not. Where a page quotes C#, the code comes from the `samples/Operations` project, and the claims it makes are run by that project's tests against a real PostgreSQL server.

| Page | In one line |
|---|---|
| [Migrations](migrations.md) | Create the roles once, migrate as a deploy step, or let the application migrate at start. |
| [Several instances, one database](instances.md) | Leases, connection pools, instances without workers, and stopping an instance without losing a task. |
| [Upgrading](upgrades.md) | Package, replay check, migrate, roll; and what each failure looks like. |
| [Backup and restore](backup-and-restore.md) | What to save, what restoring an older copy does to running work, and the test that proves it. |
| [Monitoring and alerts](monitoring.md) | The metrics, logs and health check that exist, and the alerts built on them. |
| [Hardening checklist](hardening.md) | The settings to check before the first production run. |
