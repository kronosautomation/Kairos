## Addendum: root-token check (follow-up)

**Built.** On first use of each token value the provider calls `auth/token/lookup-self`; a token whose `policies` contain `root` is refused with an `InvalidOperationException` (no token text), before any Transit call. A new token value (file re-read after agent renewal, new login) is checked again; unchanged tokens are not (one lookup per token, not per call).
**Decision on denied lookup:** secure default, refuse. 401/403 on lookup-self gives `VaultTransitException { Failure = Authentication }` whose message names `path "auth/token/lookup-self" { capabilities = ["read"] }`. It is typed Authentication, not InvalidOperationException, because Vault answers 403 both for an invalid token and for a missing permission, so the two cannot be told apart. It goes through the same one re-read-and-retry as any 403. No opt-out option was added.
**Verified against both servers:** the `default` policy carries `lookup-self` (token with the default policy and a policy without the rule works on Vault 1.18.5 and OpenBao 2.7.2). The documented policy now lists the rule, and works alone on a token created with `no_default_policy`.
**Limit:** only the literal `root` policy is detected. A non-root token with broad policies is not.

**Tests added.** Container (both servers): root refused and not echoed; documented policy alone works; no-lookup token refused naming the permission; default-policy token works. Contract: policies with root (3 shapes), scoped (3 shapes), lookup 401 and 403 with the token echoed by the server, renewed token re-checked and refused if root, one lookup per token.
**Mutations (all killed, then removed):**
| # | Bug | Failing tests |
|---|---|---|
| 8 | Root policy check disabled | 3 contract root cases, renewed-root case, container root test on Vault and OpenBao |
| 9 | Token checked once only, never again after a change | `A_renewed_token_is_checked_again...` |
| 10 | lookup-self denial accepted | both `A_token_that_cannot_look_itself_up...` cases |

**Pipeline.** The Vault and OpenBao tests are category Integration, which the existing per-commit test job (`Category!=Nightly&Soak&CloudSandbox`) already runs, on ubuntu with Docker. Added: pinned `KAIROS_VAULT_IMAGE` / `KAIROS_OPENBAO_IMAGE` variables and a pull step before the tests so a slow pull does not eat the test timeout. No new job (about 15 s of test time once images are present). **Not run:** the pipeline itself.

**Superseded in the main note:** "Open items: make the root-token warning mechanical" is done; the "root token cannot be detected" statements no longer hold for the `root` policy.
