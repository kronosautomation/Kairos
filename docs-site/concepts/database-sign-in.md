# Connecting to the database without a password

By default your application connects to PostgreSQL as the `kairos_dataplane` role with a password in the connection string. On a managed database you can do without the password: the cloud's identity
service gives your application a short-lived **token**, and the database accepts it in place of a password (Amazon RDS IAM authentication, Microsoft Entra authentication for Azure Database for
PostgreSQL, Google Cloud SQL IAM authentication). Nothing long-lived is then kept in configuration, in an environment variable or in a secret store.

## How it is wired

`UsePostgres` has an overload that lets you configure the data source before Kairos builds it:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#use-token)]

`UseTokenPassword` takes an `IDatabaseTokenSource`, which is asked for a password with the host, port and role of the connection about to be made:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#token-source)]

Things worth knowing:

- **One data source, every connection.** Kairos builds one data source for the store. The long-lived connection it keeps for waking idle workers when new work arrives is opened from the same data source, so it signs in with
  the token too. The token source is asked again every five minutes (and again soon after a failure); connections that are already open stay open after their token expires.
- **No password in the connection string.** If the connection string has one as well, `UseTokenPassword` refuses to start rather than silently replacing it.
- **A token is a secret.** It is never written to a log or an exception message by Kairos; keep your own token source to the same rule. A token source that cannot give a token throws; an empty token is an error.
- **TLS.** Cloud databases require an encrypted connection when a token is used. Set `SSL Mode=VerifyFull` in the connection string.
- **LISTEN/NOTIFY.** A connection pooler that works per transaction (a managed connection pool, RDS Proxy) does not carry the wake-up connection. Connect to the database itself.

You write the token source with your cloud's SDK (each service documents how to produce its token). The `kairos_dataplane` role still has to exist in the database, and the cloud identity has to be allowed to sign in as it.
