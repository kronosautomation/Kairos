# The database login your application uses

Kairos separates what changes the schema from what runs your workflows. `kairos db provision` creates four roles; your application connects as `kairos_dataplane`, which can read and
write run data and cannot change the schema. Every Kairos table carries tenant and namespace and is protected by forced row-level security, and that protection holds only for a login that is
subject to it.

## What `AddKairos` refuses

When the host starts, `AddKairos` asks PostgreSQL what the application's login can do. It refuses to start, with a message that names the login, if the login is any of:

- a superuser,
- a role with `BYPASSRLS`,
- the owner of a table in the `kairos` schema (for example `kairos_migrator`).

Each of these turns row-level security off without any error, so one tenant's code path could read another tenant's rows. The check runs after `MigrateOnStart` (if you use it) and before the first
worker claims any work, so a refused start has done nothing.

A wrong or missing password now also stops the host at start, instead of at the first call.

## Accepting a privileged login on purpose

A throwaway development database or a test that has to inspect tables may need an administrator login. Say so in code with `AllowPrivilegedDatabaseRole()` on the Kairos options. Kairos then starts,
writes a warning to the log at every start, and reports the choice to the dashboard, which shows it as a standing warning. Use it for development and tests, never for production.

## What to do instead

- Create the roles once with `kairos db provision`, and migrate with `kairos db migrate` as `kairos_migrator`, from your pipeline.
- Give the application only the `kairos_dataplane` connection string.
- In development with .NET Aspire, the sample's development database helper creates the roles and hands the application the `kairos_dataplane` login, so no opt-out is needed.
