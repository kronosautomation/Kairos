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
- **Short-lived.** With the built-in issuer, five minutes by default and never more than fifteen. Every attempt of a step gets a fresh token.
- **Never recorded.** The token reaches the tool code in `call.Credential` and nowhere else. If a tool returns it in its result, it is
  removed before the result is written to the history. Printing a `ToolCredential` shows `token=***`.

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#tools)]

The service the tool calls decides whether to honour the token. `HmacToolCredentialIssuer` makes self-contained tokens signed with
HMAC-SHA256, so the service can check one without calling back:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#verify-credential)]

`Verify` returns nothing for a token that is forged, altered, expired, signed with another key, or missing the scope. Rotate the signing
key to invalidate every outstanding token at once; the key must be at least 32 bytes. You can supply your own `IToolCredentialIssuer` to
mint tokens in your identity provider's format instead.

## Limits

- **A credential protects the service only if the service checks it.** Kairos mints the token; it cannot make a downstream API verify it.
- **A tool is your code.** It can still use its own network access and secrets. Scoped credentials limit what the *agent's* access
  allows, not what code you wrote can do on its own.
- **Prompt injection is not solved here.** Text from a web page or an email can steer a model. The protection is that what the model can
  trigger is bounded: read-only by default, approval for changes, tokens that expire.
- **Approvals show what the model asked for, not why.** Give approvers the arguments (the approval request includes them) rather than
  the model's explanation.
