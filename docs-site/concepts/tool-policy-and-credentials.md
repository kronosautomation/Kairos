# Tool policy and scoped credentials

Giving an agent access to your systems is giving a program that can be talked into things the ability to act. Kairos starts from the
safest position and makes widening it a deliberate act.

## The read-only baseline

Every tool declares what it can do:

| `ToolAccess` | Meaning | Default behaviour |
|---|---|---|
| `ReadOnly` | Only reads. | Runs without asking. Its credential cannot write. |
| `SideEffecting` | Changes something outside Kairos. | **Held for a person's approval.** It runs only when you explicitly allow it, or when someone approves that call. |

You change the default per tool when you build the runtime. A tool that is named in `Allow` or `Deny` need not exist yet; the names are matched when a call arrives:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#policy)]

A denial always wins: a tool that policy denies cannot be unlocked by an approval.

## Where the decision is made, and why it is recorded

The policy is evaluated on the worker, inside the tool step, immediately before the tool would run. The decision is part of the step's
recorded result, so:

- changing a policy affects the **next** decision, never a run that is already waiting or replaying;
- a workflow bug cannot skip the check, because the tool's own step performs it;
- the history shows why a tool did or did not run.

To use your own rules, implement `IToolPolicy` and register it with `UsePolicy`. It receives the run, the tool, the arguments and any
approval that already covers the call, and returns `Auto`, `RequireApproval` (with who may approve) or `Deny` (with a reason).

## Per-run scoped credentials

An agent never receives a user's password, and a tool never needs one. With `UseCredentials`, each tool call gets a credential minted
for that call:

- **Scoped to one run and one tool.** The token names the run, the tool, the tenant and namespace, and who the agent acts for
  (`ctx.OnBehalfOf`).
- **Limited by what the tool may do.** A read-only tool's credential carries `tool:read` only. A side-effecting tool's carries
  `tool:read` and `tool:write` only because policy allowed it or a person approved it. Whatever the tool's code tries, the token it holds
  cannot do more.
- **Short-lived.** Five minutes by default, and the built-in issuers refuse to mint a token that lives longer than fifteen. Every attempt of a step gets a fresh token with its own id.
- **Never recorded.** The token reaches the tool code in `call.Credential` and nowhere else. If a tool returns it in its result, it is
  removed before the result is written to the history. Printing a `ToolCredential` shows `token=***`.
  What is recorded, with the step's result and sealed like it, is the grant: the scopes, when it expires and who the agent acted for. An audit can read that back
  (`AgentAudit.Read`) and say what each tool call was allowed to do, without the token ever having been stored.

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#tools)]

The service the tool calls decides whether to honour the token. Two issuers are provided:

| Issuer | Signature | Use it when |
|---|---|---|
| `EcdsaToolCredentialIssuer` | ECDSA P-256 | Several services check tokens. **Recommended.** Only the issuer holds the private key; each tool service holds the public key (`EcdsaToolCredentialVerifier`) and so cannot mint a token, even if it is compromised. |
| `HmacToolCredentialIssuer` | HMAC-SHA256, shared key (at least 32 bytes) | One service holds the key, or every holder is equally trusted. Whoever can verify can also mint. |

A service checks a token by naming itself, so a token issued for one tool is refused by another even when the scope text matches:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#verify-credential)]

`Verify` returns nothing for a token that is forged, altered, expired, signed with another key, issued for a different tool, or missing
the scope. The two token kinds carry different prefixes and cannot be mistaken for each other. Rotate the key to invalidate every
outstanding token at once. You can supply your own `IToolCredentialIssuer` to mint tokens in your identity provider's format.

**One use only.** Each token has a unique id (`VerifiedToolCredential.TokenId`). Within its few minutes of life a token can be presented
more than once; for a high-risk scope, pass the verified token to `OneTimeUseGuard.TryConsume`, which accepts it the first time only. The
guard is in memory and per process: when several instances of a service accept the same scope, back it with a shared store.

## Limits

- **A credential protects the service only if the service checks it.** Kairos mints the token; it cannot make a downstream API verify it.
- **A tool is your code.** It can still use its own network access and secrets. Scoped credentials limit what the *agent's* access
  allows, not what code you wrote can do on its own.
- **Prompt injection is not solved here.** Text from a web page or an email can steer a model. The protection is that what the model can
  trigger is bounded: read-only by default, approval for changes, tokens that expire.
- **Approvals show what the model asked for, not why.** Give approvers the arguments (the approval request includes them) rather than
  the model's explanation.
