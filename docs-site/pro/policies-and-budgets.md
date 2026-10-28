# Policies, prices and budgets

The policy engine decides, for each tool call an agent makes, whether it is allowed, denied, held for approval or limited in rate. It works from rules you write as
data, records its decision before the tool runs, and fails closed when it cannot read the policy. Budgets limit what agents may spend on model calls, and a
price table turns recorded usage into money. This page covers the rules, the budgets, how to turn them on and who may change them. It is a commercial feature. The
free baseline (read-only tools run, side-effecting tools wait for approval) is described in [Tool policy and credentials](../concepts/tool-policy-and-credentials.md);
this page does not repeat it.

## Policy rules

A policy is a JSON document with a list of rules (at most 500, and at most 256 KiB). Each rule has an `id`, an `effect` and an optional `match`.

| `effect` | Meaning |
|---|---|
| `allow` | The call may run. |
| `deny` | The call is refused. |
| `require_approval` | The call waits for people. `approval` names `roles` or `approvers`, and `count` (1 or 2 different people). |
| `rate_limit` | At most `max` calls per `perSeconds`, counted per `run`, `starter` or `namespace`. |

A rule applies when every part of its `match` holds. A part left out matches anything.

- `tools`: tool names; a `*` may start or end a name.
- `access`: `read_only` or `side_effecting`, as the tool declares itself.
- `args`: conditions on the arguments, by dotted path (`customer.country`, `items.0.sku`), with `equals`, `in`, `lt`, `gt` or `regex`.
- `starters`: the verified identity that started the run, as `role:subject`, `role:*` or `none`.
- `namespaces` and `time`: namespace ids, and days and a time-of-day range in UTC.

```json
{
  "schema": "kairos-policy/1",
  "rules": [
    { "id": "no-large-refunds", "effect": "require_approval",
      "match": { "tools": ["payments.refund"], "args": [ { "path": "amount", "op": "gt", "value": 500 } ] },
      "approval": { "roles": ["finance"], "count": 2 } },
    { "id": "refunds-allowed", "effect": "allow", "match": { "tools": ["payments.refund"] } },
    { "id": "slow-down", "effect": "rate_limit", "match": { "tools": ["crm.*"] },
      "rateLimit": { "max": 30, "perSeconds": 60, "per": "run" } }
  ]
}
```

**How a call is decided.** Deny wins. Then an exhausted rate limit denies. Then a rule that requires approval holds the call (the one needing most people wins). Then an
allow rule lets it through. A call that no rule allows is allowed if the tool is read-only and denied if it has side effects. The order of the rules does not change
the result. A condition that cannot be evaluated, for example because the arguments could not be read, makes a deny, approval or rate rule apply and an allow rule not apply.

## What is recorded

The decision is recorded in the run's history before the tool runs: the decision, the rule, the version and hash of the policy, and the *names* of the arguments that
matched, never their values. Replaying a run reads what was recorded, so changing the policy later does not change what a running workflow already decided. The audit
export includes these decisions (see [Verifying audit evidence](../concepts/audit-evidence.md)).

**Fail closed.** If a namespace has no policy, or its policy cannot be loaded, does not validate or does not match its stored hash, every side-effecting call is denied
(the rule is shown as `(policy-unavailable)`) and read-only calls still run. A worker that is meant to use a policy but has no source for one denies side-effecting calls too.

## Budgets and prices

A budget is a JSON document of limits (at most 100). A limit has a `scope` (`run`, `agent_day` for one agent over a UTC day, or `namespace_day`), an `agent` (or `*`), and
any of `tokens`, `money` and `steps`. `onExceed` is `fail`, which fails the run with a budget error naming the limit, or `pause`, which asks people to let that one call through.

```json
{
  "schema": "kairos-budgets/1",
  "limits": [
    { "id": "support-daily", "scope": "agent_day", "agent": "support-agent", "money": 50, "onExceed": "pause",
      "approval": { "roles": ["finance"] } }
  ]
}
```

The price table lists, for each model, the price per million input, output and cached input tokens and the largest input and output a call can have. A table applies from
its `effectiveFrom` day; each past day is priced with the version in force that day. Money limits and the cost shown in the [dashboard](dashboard.md) use it.

**Atomic reservation.** Before a model is called, the most the call can cost is reserved against every limit that applies, in one conditional database update that succeeds only if
the limit still has room. Concurrent calls therefore cannot overspend by more than a provider's overrun of the maximum it was told. After the call, the reservation is
settled to the recorded usage; a retry of the same step reuses its reservation. If the guard cannot decide, the step is retried and the model is not called.

## Turning it on

Policies, prices and budgets are stored in the same PostgreSQL database as the runs, under the same row-level security, and enforced by `Kairos.Agents.Policies`.
On a [standalone server](standalone-server.md), set `Server:Policies:Enabled` to `true`. Without it, the policy endpoints answer that they are not supported. Workers
without a database of their own ask the server over gRPC, and are refused if it cannot answer.

| Endpoint (under `/v1/namespaces/{namespaceId}`) | Read | Change |
|---|---|---|
| `/policy`, `/prices`, `/budgets` | `GET`: operators and administrators | `PUT`: administrators only |
| `/budgets/status`, `/cost/summary`, `/runs/costs` | `GET`: any role that can read runs | Not applicable |

## Who may change what

**Only administrators change a policy, a price table or a budget.** Every change needs a reason and is written to the audit log with the verified identity and the hash of
the version before and after. Each change makes a new version; earlier versions are kept. A document that does not validate is refused whole, and the messages name members and
rules, never argument values. Unknown members are refused, not ignored.

## Limits

- Rules are matched on the tool's name and arguments, not on what a tool really does. A tool that is declared read-only is trusted to be so.
- A rate-limit rule needs a counter store; without one the limit counts as used up and the call is denied.
- Reading the policy is not the same as reading payloads. The engine sees argument values only to compare them, and records only argument names.
