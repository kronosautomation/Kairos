# The dashboard

The dashboard is a web application for the people who watch and steer Kairos runs: they see runs, decide approvals, and follow what agents cost. It talks to a
[Kairos server](standalone-server.md) with the signed-in person's own session. Payloads are decrypted in the person's browser, with a key broker that you host,
so the dashboard host and the server see ciphertext and metadata only. This page covers the screens, the sign-in, the key broker and what the dashboard
cannot do. It is a commercial feature. To run it inside your own application instead of as a host of its own, see [the dashboard inside your application](dashboard-in-your-application.md).

## Screens

The sidebar has five sections and a switcher for the namespaces you listed.

| Screen | What it shows |
|---|---|
| Runs | A status sentence, a filter (for example `type:checkout build:abc`), and the recent runs. |
| Run | One run's history as a timeline, with the chain verified in the browser, each step's input and error, and the command to export the history for a local replay. Retry (resume a stuck run) and Cancel and roll back ask once and are written to the audit log with the person's name. |
| Approvals | The requests waiting for a decision, oldest first, with the exact call being approved. The decision is recorded under the identity the server verified. |
| Agents and cost | Model calls, tokens and cost by agent for today, 7 days or 30 days, and each budget with what is spent and what is held for calls in progress. |
| Workflows | Workflow types by build, with the number of open and closed runs. |
| Audit | Who changed what in the namespace, newest first, with the verified person. |

What a person can do on a screen is still decided by the server and their role in that namespace (see [roles](standalone-server.md#roles)). The namespace list in
the configuration only offers choices; the server's answer decides whether a person may see one.

## Turning it on

The dashboard runs as a host of its own, configured only by configuration. Run several behind a load balancer if you need to.

| Key | Meaning |
|---|---|
| `Dashboard:PublicUrl` | Where people reach the dashboard. Used for the sign-in redirect and to check the origin of changes. |
| `Dashboard:ServerUrl` | The Kairos server it talks to. |
| `Dashboard:Oidc:Authority`, `ClientId`, `ClientSecret` | Your identity provider. People sign in with OpenID Connect (authorization code with PKCE). Keep the client secret in a secret store. |
| `Dashboard:Namespaces:n` | `TenantId`, `NamespaceId` and `Name` of each namespace offered. |
| `Dashboard:KeyBroker:Url`, `TokenScope`, `KeyTtl` | Your key broker (below). Without it, values stay encrypted. |
| `Dashboard:Approvals:KeyVault:KeyId` or `Dashboard:Approvals:Kms` | The key that signs approval evidence. Without it, approving from the dashboard is switched off. |
| `Dashboard:SessionIdleTimeout`, `SessionLifetime`, `PlaintextIdleTimeout` | Sign-in lasts 30 minutes without use and 8 hours at most; plaintext is cleared after 5 minutes without activity. |
| `Dashboard:KnownProxies:n`, `KnownNetworks:n` | Load balancers whose `X-Forwarded-For` is believed. Off unless set. |
| `Dashboard:DataProtection:KeysDirectory` | Where the session cookie keys live. Set it to storage all nodes share when you run more than one node. |

A wrong setting stops the host at start and names the setting. Prices and budgets are not configured here: they are kept by the server, and an administrator changes
them there (see [policies and budgets](policies-and-budgets.md)).

## Decrypting in the browser

The browser, not the server, opens payloads. When a person asks to see a value, the browser asks your **key broker** to unwrap the value's data key, decrypts
in the page, and shows the value as decrypted in this browser. Plaintext is not sent back, logged, kept on disk or put in a URL, and it is cleared when the person
navigates away and after the idle timeout. The page allows connections only to the dashboard and to your broker.

The key broker is a small HTTP service in front of the key provider your workers already use. A reference implementation ships as a library for you to host in your
own environment. It accepts only access tokens from your identity provider, lets one dashboard origin call it, decides per person and namespace whether they may use keys, and
limits calls per person (600 a minute by default).

**What this implies.** Envelope encryption seals many payloads under one data key. A key the broker hands out opens every value sealed under it in that tenant and
namespace until the workers rotate it (an hour or ten thousand payloads by default). So:

- The value the browser names is written to the broker's log as a record of intent. It is not a limit, and the key returned opens more than that value.
- Permission to use the broker is permission to read that namespace's values for the period. Grant it as you would grant read access to the data.
- `Dashboard:KeyBroker:KeyTtl` limits how long the browser keeps a key (five minutes by default). It narrows the window; it does not narrow what the key opens.

The details are in [Payload encryption and its limits](../concepts/payload-encryption.md).

## The payload line in the sidebar

Workers report to the server whether they store payloads sealed or readable. The sidebar shows one line for the namespace:

- *Payloads readable: a worker in this namespace reports storing them unencrypted.* A worker reported readable payloads in the last 10 minutes.
- *Approvals unverified: a worker in this namespace accepts approval decisions without proof of who made them.* A worker reported that in the last 10 minutes (and none reported readable payloads).
- *Payloads: encrypted by the workers.* Workers reported sealed payloads and none reported readable ones in that time.
- *Payloads: no worker has reported lately.* Neither was reported in the last 10 minutes.

It reports what workers say. It is not a scan of the stored data.

## What it cannot do

- It cannot open a payload without a key broker, and it cannot decrypt on the server.
- It cannot change a policy, a price table or a budget; it shows what the server holds.
- It does not replace the server's checks. A person's role in the namespace decides what the server lets them do, whatever the screen offers.
- Approving needs the approval signing key to be set, and a decision made there carries the person's verified identity, not a name typed into a form.
