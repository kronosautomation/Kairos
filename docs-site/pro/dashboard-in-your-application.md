# The dashboard inside your application

The [dashboard](dashboard.md) can run inside your own ASP.NET Core application instead of as a host of its own. Your application embeds Kairos (see
[getting started](../getting-started/index.md)), signs people in the way it already does, and mounts the dashboard at a path. There is no second host, no
Kairos server to run, and no second sign-in: the dashboard uses the signed-in person your application already has. It is a commercial feature.

This page covers what to call, how claims from your sign-in become Kairos roles, the key broker that decrypts payloads, the approval signing key, running
behind a proxy that ends TLS, and what is and is not served.

## What to call

Two calls, next to `AddKairos` and your authentication:

- `AddKairosDashboard` registers the dashboard. It takes the namespaces to offer, the role map, and the optional settings below.
- `MapKairosDashboard` serves it at a path: `/kairos` by default, or `/` to serve it at the root of the application. Call it after `UseAuthentication` and
  `UseAuthorization`. It returns a route group, so you can add your own conventions.

The dashboard needs three things from your application, and refuses to start without them. Each refusal names what is missing.

| It needs | Why |
|---|---|
| An authentication scheme, with a default scheme | The dashboard asks the scheme who the person is. It does not sign anyone in. |
| An authorization policy (named `KairosDashboard` unless you choose another name) | Every route of the dashboard requires it. You define what "allowed in" means for your application. |
| At least one role mapping, and every mapping that names a namespace must name one the dashboard offers | A dashboard nobody can use, or one that grants into a namespace that is not there, is a mistake. |

The policy is applied by the dashboard itself, so forgetting `RequireAuthorization` on the route group does not leave it open. The dashboard also answers 401 to
a person who is not authenticated, even if your policy would let them through.

[!code-csharp[](../../samples/Concepts/EmbeddedApplicationExamples.cs#app-services)]

The sample defines the policy and the engine. Then add `AddKairosDashboard(...)` with your namespaces and role map, and `MapKairosDashboard("/kairos")`
after `UseAuthentication()` and `UseAuthorization()`. The page that follows shows what goes inside `AddKairosDashboard`.

## Who may do what: the role map

Your sign-in decides who a person is. The role map decides what Kairos lets them do. Each mapping gives everyone who has a claim value the role **Reader**,
**Operator**, **Approver** or **Admin**, either in every namespace the dashboard offers or in one namespace by name. A person with no matching claim has no
grant anywhere: every route answers 403 and the namespace switcher is empty. The worker roles cannot be mapped, because a person is not a worker.

Claims are read from the types in `Roles.ClaimTypesRead`. By default that is the standard role claim and `roles`. Add `groups` to map group claims.
The person is identified by the first of `Roles.SubjectClaims` that is present: `sub`, then `oid`, then the name identifier by default. That identity is what
is written to the audit log, shown as the approver, and signed into approval evidence.

### Example: Microsoft Entra ID

Entra ID puts **app roles** in the `roles` claim and **group object ids** in the `groups` claim, and identifies the person by the `oid` claim (the object id,
stable for the person across applications of your tenant).

| In Entra ID | Claim | Value you map | Kairos role |
|---|---|---|---|
| App role `Kairos.Reader` | `roles` | `Kairos.Reader` | Reader, all namespaces |
| App role `Kairos.Operator` | `roles` | `Kairos.Operator` | Operator, all namespaces |
| App role `Kairos.PaymentsApprover` | `roles` | `Kairos.PaymentsApprover` | Approver, namespace `payments` only |
| App role `Kairos.Admin` | `roles` | `Kairos.Admin` | Admin, all namespaces |
| Security group `Support operations` | `groups` | the group's object id | Operator, namespace `support` only |

To use it: set `Roles.SubjectClaims` to `oid` only, add `groups` to `Roles.ClaimTypesRead`, and call `Roles.Map("Kairos.Reader", Role.Reader)`,
`Roles.Map("Kairos.PaymentsApprover", Role.Approver, "payments")` and so on, one call per row. The role map is code, and the compiled version of this example
is part of the Kairos test suite, so the table above is kept true by a test.

Two points about Entra ID itself:

- Add the app roles to your app registration and assign people or groups to them in the enterprise application. A role that is not assigned does not appear in the token.
- Add the `groups` claim to the token configuration only if you need it. A person who belongs to many groups gets an overage indicator in place of the list, and no
  groups are mapped for them. Prefer app roles; use groups where the list is short.

The map is evaluated on every request from the claims of the current sign-in. Removing a person from a role takes effect when their sign-in is refreshed
or they sign in again, as it does for the rest of your application.

## Decrypting in the browser

Payloads are decrypted in the person's browser, as in the [standalone dashboard](dashboard.md#decrypting-in-the-browser): the dashboard serves ciphertext, and the browser
asks a key broker to unwrap the data key. Inside your application the broker is part of the application:

- It is served under the dashboard path, on the same origin as the page, with the signed-in person's own session. There is no second service to host, no
  bearer token and no cross-origin call. It sends no cross-origin permission headers, and a change must come from the dashboard's own origin.
- It uses the key provider you configure: `KeyProvider` on the dashboard settings, or the `IKeyProvider` registered in the application. With neither, the
  dashboard shows metadata only and says why.
- It decides with the same role map. A person may use keys for a namespace if they hold a grant that can read runs there, which is the grant that lets them see the
  ciphertext in the first place.

The consequence is the one described for the standalone dashboard: a data key opens everything sealed under it until the workers rotate it, so permission to use the
broker is permission to read that namespace's values. See [Payload encryption and its limits](../concepts/payload-encryption.md). `KeyTtl` (5 minutes by default,
between 10 seconds and 5 minutes) limits how long the browser keeps a key, and `PlaintextIdleTimeout` (5 minutes by default) how long plaintext stays on screen without use.

## Approving from the dashboard

Deciding an approval needs evidence that the decision came from a verified person. The dashboard mints it for the signed-in person, signed with ES256, and the
workflow checks it with the matching **public** key (see [approvals](../concepts/approvals.md)). You choose where the private key lives, with `Approvals.Signer`:

| Option | Use it when |
|---|---|
| The Key Vault signer (Azure) or the KMS signer (AWS) | You have a key service. The key never leaves it, and every signature is in its log. Preferred. |
| `Es256KeyFileSigner.FromFile(path)` | A small deployment with no key service. The private key is a P-256 PEM file in your process's memory; a compromise of the process can sign without limit. A file that other users can read is refused on Unix. |
| No signer (the default) | Approving from the dashboard is off, and the Approvals screen says what to configure. You can still decide approvals from your own code with `AgentClient`. |

A person gets evidence only if the role map gives them the right to decide approvals in that namespace. `Approvals.AllowedRoles` can require, in addition, one of
a list of claim values (for example a finance role). `Approvals.EvidenceLifetime` is at most five minutes.

## HTTPS, and running behind a proxy

The dashboard serves a session and decrypted payloads, so it answers only over HTTPS. Over plain HTTP, every dashboard route answers 403, in every environment except
`Development`, where it works and logs a warning.

Every change the page makes (retry, cancel, a decision) is checked: it must carry the dashboard's header, come from the dashboard's own origin, and not be a
cross-site request. The origin it must match is the address people use.

When a load balancer or reverse proxy ends TLS and speaks plain HTTP to your application, the application sees `http` and the proxy's address, not what is in the
person's browser. Two things follow, and each has its own fix:

- **The scheme.** Your application must see `https`, or the dashboard answers 403 to every request, reads included. Have the proxy send `X-Forwarded-Proto` and
  `X-Forwarded-Host`, and tell ASP.NET Core to believe that proxy, with `UseForwardedHeaders` first in the pipeline. Believe only proxies you name: a forwarded header from anyone else
  could claim any address.
- **The address.** A change is refused with 403 when its origin is not the address the dashboard expects, which is the scheme and host of the request as your application
  sees it. When the proxy rewrites the host, or the application does not receive the forwarded host, reads work and **every change is refused**. Set `PublicUrl` on the
  dashboard settings to the address people type (for example `https://app.example.com`), and the origin is checked against that instead.

Setting both is the most robust. `PublicUrl` does not make a plain-HTTP request count as HTTPS.

[!code-csharp[](../../samples/Concepts/EmbeddedApplicationExamples.cs#behind-a-proxy)]

## What is served

Everything is under the path you mount it at. The page, its scripts and styles (each with an integrity hash, with a policy that allows only this origin), the
few session endpoints (who the person is, which namespaces they can see, sign out, approver evidence, their theme), the key broker, and sixteen operations of the
Kairos API, exactly the ones the screens use:

- Reads: list runs, stuck runs, the audit log, worker posture, waiting approvals, run costs, budget status, cost and usage summaries, run groups, one run, its history and its usage.
- Changes: retry a stuck run, cancel a run and roll it back, decide an approval.

Not served: starting a run, signalling, terminating or failing a run, exporting a history, and every change to a policy, price table or budget. Those remain
available to your own code and to the [server](standalone-server.md). The dashboard also does not replace your pages: only paths under its mount carry its security
headers, and your other routes are untouched.

Mounted at the root (`/`), the dashboard uses `/`, `/assets`, `/auth`, `/session`, `/api` and `/broker`, so your application should not use those paths. Mounted under a path, it uses only that path.

## What the sidebar warns about

The sidebar shows a standing line, in words, when one of these is true:

- **The application's database login is privileged.** Row-level security does not protect tenants from a process that connects as an owner or superuser. Use the data-plane login.
- **Payload encryption is off.** The application said `AllowUnencryptedPayloads()`, so inputs, results and signals are stored readable in the database.
- **The license.** Development use needs no key and says so; production use needs one.

## What it cannot do

- Show a payload without a key provider, or decrypt on the server.
- Grant anyone more than the role map gives them. The server's checks apply to every call, whatever a screen offers.
- Sign anyone in. That stays with your application.
