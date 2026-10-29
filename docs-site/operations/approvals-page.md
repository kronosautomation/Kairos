# A minimal approvals page

When an agent or a workflow asks for an [approval](../concepts/approvals.md), a person has to see it and decide. In embedded mode the library gives you the calls (`AgentHistory.PendingApprovals`, `AgentClient.ApproveAsync` and `RejectAsync`) and your application provides the screen. This
page describes a small one, `samples/ApprovalsPage`, that you can use as it is or copy from. It is an ASP.NET Core minimal-API application with one HTML page, and its tests run on every build.

## What it does

- `GET /approvals` lists every approval that nobody has validly decided yet, across the open runs of one tenant and namespace: the action, the details, when it was requested and who may decide.
- Each row has two plain forms, Approve and Reject, with a comment field. A decision is a `POST` to `/approvals/{runId}/{approvalId}/approve` or `/reject`.
- The person who decides is **the one your application signed in**, never a field of the form. The page passes their identity and role claims to `AgentClient`, which signs the decision for them. The workers check the signature with the public key before the run goes on, so identity is
  verified, not asserted. The sample never calls `AllowUnverifiedApprovers()`.

## Wiring it up

[!code-csharp[](../../samples/ApprovalsPage/Program.cs#wire-up)]

The page needs three things from your application: Kairos with the agent activities, an `AgentClient` built with the evidence signer, and authentication. It brings two authorization policies:

[!code-csharp[](../../samples/ApprovalsPage/ApprovalsEndpoints.cs#register)]

| Role claim | May |
|---|---|
| `approvals-reader` | See the list. |
| `approvals-approver` | See the list and decide. |
| neither | Nothing: the page answers 403 to both the list and a decision. Not signed in: 401. |

These two role claims only decide who may use the page. Whether a decision **counts** is decided by the approval itself: if it asks for the role `finance`, the person's claims must include `finance`, or the workers ignore the decision and the approval stays in the list.

[!code-csharp[](../../samples/ApprovalsPage/ApprovalsEndpoints.cs#map)]

## How a decision is handled

[!code-csharp[](../../samples/ApprovalsPage/ApprovalsEndpoints.cs#decide)]

- The page does not tell the person whether the workers accepted the decision. It cannot: the check happens when the run next runs. After a decision it says so, and the approval stays in the list if it was not accepted
  (not signed, signed with another key, expired, from someone who does not qualify). That is how a person finds out.
- Cross-site requests are refused twice: the anti-forgery token must come back, and a request the browser marks as cross-site (`Sec-Fetch-Site`) is refused before anything else happens.
- Everything a run wrote (the action text, details) is HTML-encoded. A test puts a script tag in an approval request and checks that it arrives as text.

## What it leaves to you

- **Sign-in.** The page works with any ASP.NET Core authentication scheme. The sample's `Program.cs` uses a cookie and, in development only, an address that signs you in as a named person. Replace it with your identity provider.
- **The signing key.** `LocalApprovalEvidenceSigner` keeps a key in the process and is for development. In production use a signer over Key Vault or KMS, and let only this application ask it to sign. Whoever can ask the signer to sign can approve as anyone.
- **Scale.** Each load of the page reads the history of up to `MaxRuns` open runs (200 unless you set it). That suits a team's inbox, not thousands of open runs; build an index of pending approvals in your own storage when you reach that.
- **A header for the token.** A plain form cannot add a header, so the token travels in a hidden field and in a cookie. The page also accepts it in the `X-CSRF-TOKEN` header for a caller that can set one.

## Why the page is strict

The page sets `Content-Security-Policy: default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'`, with `no-store` caching. It has no script, no inline style, no event handler and no resource from another origin, and its one style sheet is served by the page itself, so it works under that policy
and keeps working if you tighten your own. The tests check the headers and the markup.
