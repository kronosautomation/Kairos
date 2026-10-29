# Hardening checklist

Go through this before the first production run. Each line says what to set, what happens if you do not, and where it is explained. Most of them are defaults that Kairos enforces; the list is there so that you know which, and so that
nobody turns one off by accident.

[!code-csharp[](../../samples/Operations/ProductionSetup.cs#hardened)]

| Check | What to do | If you do not |
|---|---|---|
| **Payload encryption is on** | Give `AddKairos` a payload protector (`UsePayloadProtector`). Do not call `AllowUnencryptedPayloads()`. | The host refuses to start without one unless the code says otherwise. If it does say otherwise, the host writes a warning at every start and payloads are stored readable. See [Payload encryption](../concepts/payload-encryption.md) for what encryption protects and what it does not. |
| **Approver verification is on** | Register an approver verifier (`UseApproverVerifier`) that holds public keys only. Do not call `AllowUnverifiedApprovers()`. | Every approval decision is refused until you do. If you opt out, a warning is written at every start, and an approval that names approvers or roles is refused anyway. See [Approvals](../concepts/approvals.md). |
| **A separate migrator credential** | Migrate with `kairos db migrate` from a pipeline as `kairos_migrator`, or use `MigrateOnStart` with a second credential. | `MigrateOnStart` refuses the same login for both. See [Migrations](migrations.md). |
| **TLS 1.3 to PostgreSQL** | Put `SSL Mode=VerifyFull` (or at least `Require`) in the connection string. | When the connection string insists on TLS, the handshake asks for TLS 1.3 only. A connection string that does not insist on TLS is not changed, so a database without TLS stays usable and unprotected. `AllowTls12ToDatabase()` accepts 1.2 if your database cannot do 1.3. |
| **The data-plane role does not own tables** | Connect the application as `kairos_dataplane`. | A superuser, a role with `BYPASSRLS` or a table owner is refused at start. `AllowPrivilegedDatabaseRole()` accepts it with a warning at every start. See [The database login](../concepts/database-roles.md). |
| **No password in configuration, if you can avoid it** | On a cloud database, sign in with a short-lived token. See [Connecting without a password](../concepts/database-sign-in.md). | The password is a long-lived secret in a place that is backed up, logged and screenshotted. |
| **The key ring is not in configuration** | Keep the keys in a key service (Key Vault, KMS), or in Vault or OpenBao through [Transit](../concepts/key-providers-vault.md). If you hold a key ring in the application, load it from a secret store or a mounted file, never from source or plain configuration. | A key ring in the same place as the database backup protects nothing. A key ring has no audit trail of its own; a key service has. |
| **Key use is observed** | Register an `IKeyUseObserver` and alert on failures (see [Monitoring](monitoring.md#where-there-is-no-signal)). | Kairos writes nothing about key use by default. |
| **Agent messages with personal data are redacted** | Register a redactor (`UseRedactor`) on the agent builder. It is applied to every piece of text before it is recorded: messages, tool arguments and results, approval details. | Whatever the model and the tools say is recorded in the run's history. With encryption on it is sealed, but anyone who may open it can read it. |
| **Analyzers on, warnings as errors** | Keep the Kairos analyzers referenced and set `TreatWarningsAsErrors`. | The analyzers flag non-deterministic code in workflows (`DateTime.Now`, `Guid.NewGuid`, `Random`, direct I/O) and credentials passed as workflow inputs. A warning nobody reads does not stop a deploy. |
| **Replay check in the pipeline** | Run `kairos replay-check --strict-coverage` before each deploy ([Upgrading](upgrades.md)). | A change that breaks in-flight runs is found in production. |
| **Health check and alerts** | Wire the `kairos` health check to your probes, and the alerts of [Monitoring](monitoring.md). | A parked run is silent: nothing else tells you. |
| **A tested restore** | Back up the database **and** the key. Restore into a scratch database on a schedule ([Backup and restore](backup-and-restore.md)). | You find out what is missing when you need it. |

## What this does not cover

Kairos in your application is only as separate from the rest of the application as you make it. An approval is signed by the code that authenticated the person, so the endpoint that decides approvals needs your authentication
and authorization (the [approvals page](approvals-page.md) shows one). The database role keeps tenants apart from each other, not from a compromised application that holds the key.
