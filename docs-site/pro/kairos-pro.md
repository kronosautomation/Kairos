# The kairos-pro command line

`kairos-pro` is the command line of the commercial packages, for an application that embeds Kairos and has no Kairos server. It does two things the open `kairos` tool does not:
it migrates the tables of the policy engine, and it exports signed audit evidence straight from your database. Checking a bundle stays with the open tool (`kairos audit verify`),
so an auditor needs no licence. It is installed as a .NET tool from the package `Kairos.Pro.Cli`, from the same feed as the other commercial packages.

Both commands run as the `kairos_migrator` login and nothing else (see [Database roles](../concepts/database-roles.md)). The application's login, the reporting login and an administrator's
login are refused with exit code 2, before anything is read or changed. The connection string comes from the environment variable `KAIROS_CONNECTION`, or the one `--connection-env` names,
never from the command line.

Exit codes: 0 done, 1 failed (nothing written or changed), 2 could not run.

## kairos-pro db migrate

```
export KAIROS_CONNECTION="Host=db.example.com;Database=kairos;Username=kairos_migrator;SSL Mode=VerifyFull"
kairos-pro db migrate --lock-timeout 120
```

It applies the engine's migrations and then those of the policy engine ([Policies, prices and budgets](policies-and-budgets.md)). Use it as the deploy step instead of
`kairos db migrate` when your application uses the policy engine; for an application that does not, the two do the same. Each part is one transaction behind the same
database lock as `kairos db migrate` ([Migrations](../operations/migrations.md)): a failed migration changes nothing, and several runs at once queue and apply each migration once.

## kairos-pro audit export --database

```
export KAIROS_CONNECTION="Host=db.example.com;Database=kairos;Username=kairos_migrator;SSL Mode=VerifyFull"
kairos-pro audit export --database --reason "annual audit" --actor "ann.auditor" \
    --from 2026-01-01 --to 2026-07-01 --signing-key ./audit-signing.pem --out ./evidence
```

It writes the same three-file bundle a Kairos server writes ([Verifying audit evidence](../concepts/audit-evidence.md)), for the namespace your application runs in, or for
`--tenant <guid> --namespace <guid>`. Select runs with `--from` and `--to`, with `--run <guid>` (repeatable), or both.

- **Who is recorded.** Every history the export reads is written to the audit log by the database, under the login it saw (`kairos_migrator`) and the address of the connection.
  The name you give (`--actor`, or `KAIROS_ACTOR`, or your user name) is stored beside it as a *claim*, never as the verified identity, and the manifest says
  `requestedBy: "db:kairos_migrator (claimed: <name>)"`. Use a migrator credential that only the people allowed to export hold.
- **A reason is required**, and is written to the manifest and to each of those audit entries.
- **Signing.** `--signing-key` is an ES256 (P-256) private key in a PEM file; the open tool verifies with its public half. Keep it apart from the key that signs approver evidence.
  On Linux the key file must not be readable by other users.
- **Sealed steps.** Without more options, approval and tool steps your application sealed are reported as not opened. To open them with a Vault Transit key, add
  `--vault-address <url> --vault-key <name> --vault-token-file <path>`; the token needs the decrypt permission of the policy your application uses
  ([Keys in Vault or OpenBao](../concepts/key-providers-vault.md)). Payloads are never written to the bundle; opening them lets the bundle say who approved and what the policy decided.

Then, anywhere, with the public key:

```
kairos audit verify ./evidence/audit-<id>.manifest.json --key audit-signing.pub.pem --require-opened
```

## Licence

Audit evidence export is a Business feature. `kairos-pro` reads the licence key from `KAIROS_LICENSE` or `KAIROS_LICENSE_FILE` and prints what it found. Without a key, or with an
expired one, the command still works and says so: the licence is a contract, not a lock.
