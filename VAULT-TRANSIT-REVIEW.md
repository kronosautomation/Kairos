# Vault / OpenBao Transit key provider: review note

## What was built
- `src/Kairos.Cloud.SelfHosted/VaultTransitKeyProvider.cs` and `VaultTransitOptions.cs` (Apache, Community). Plain `HttpClient`, source-generated System.Text.Json, no new package in `src/`.
- Wrap and unwrap through `transit/encrypt/<key>` and `transit/decrypt/<key>`; the Kairos context goes in Transit's `context` (base64). On first use the provider reads `transit/keys/<key>` and throws `InvalidOperationException` if the key is not `derived` or does not `supports_encryption`.
- `WrappedKey.KeyId` = `<mount>/<key>#<version>`; the ciphertext is the ASCII of Vault's `vault:v<n>:<base64>` string. Unwrap requires the id to equal the configured `mount/key` exactly (the `#version`, if present, must agree with the ciphertext prefix), before any request is made.
- Authentication: `UseTokenFile` (re-read on file change, throttled to one stat per second, and at once on 401/403 with one retry), `UseTokenSource(Func<CancellationToken, ValueTask<string>>)`, `UseKubernetesAuth`, `UseAppRole` (secret id from file or function). No member takes a token as text (a reflection test pins that); a token-shaped string where a path belongs is refused without being echoed.
- Typed failures: `VaultTransitException { Failure, StatusCode }` (Authentication, NotFound, Unavailable, Rejected, Token). Messages never contain a token, a key, a URL or a response body. On unwrap 400 and 404 become the same `KeyUnwrapException` as a wrong context; 403/5xx/network are not disguised.
- Tests: `VaultTransitServerTests` / `OpenBaoTransitServerTests` (conformance suite plus tenant binding, protector end to end, non-derived key, missing key, rotation, bad token; category Integration), `VaultTransitContractTests` (fake `HttpMessageHandler`, a small model of Transit derived keys), `Concepts.Tests/VaultTransitExamplesTests`.
- Docs: `docs-site/concepts/key-providers-vault.md` (+ toc), compiled samples in `samples/Concepts/ConceptExamples.cs` (regions `vault-transit`, `vault-transit-kubernetes`), `docs/SECURITY-ARCHITECTURE.md` section 4 bullet with statuses, `docs/LICENSING.md` row wording.

## Deviations from the plan
- The policy also grants `read` on `transit/keys/<key>`: the derived check needs it. The plan named encrypt and decrypt only.
- Key id carries `#<version>`. The plan said "plus the version where useful"; it gives the key-use observer the version and lets rotation be seen. Pinning compares the part before `#`.
- Ciphertext canonical-base64 check on our side: Vault's Go decoder ignores spare bits in the last character, so flipping that bit would still unwrap and the conformance "altering any byte" test would fail. Found by reasoning, covered by a contract test and the real-server runs.
- A bug the tests caught in my first draft: .NET regex `$` matches before a trailing newline, so `"k\n"` passed name validation; all patterns now use `\z`.
- `Testcontainers` (4.16.0, already in the graph via `Testcontainers.PostgreSql`) was added to `Directory.Packages.props` and referenced by `Kairos.SelfHosted.Tests` only; lock file updated. `Concepts` now references `Kairos.Cloud.SelfHosted`.
- OpenBao image pinned to `openbao/openbao:2.7.2`, Vault `hashicorp/vault:1.18` (1.18.5); override with `KAIROS_OPENBAO_IMAGE` / `KAIROS_VAULT_IMAGE`.
- Wrap with an empty context throws `ArgumentException` (a derived key cannot bind to nothing); unwrap with an empty context is a `KeyUnwrapException`.

## Mutation check (each bug applied, tests run, bug removed)
| # | Deliberate bug | Result |
|---|---|---|
| 1 | Context not sent (fixed `AA==` on wrap) | Fails: contract `The_context_the_key_and_the_token_are_sent...`; real Vault: unwrap, tenant, concurrency and rotation tests |
| 2 | Derived check removed | Fails: contract `A_key_that_is_not_derived...(derived: False)`; real `A_key_that_is_not_derived_is_refused...` |
| 3 | Key-id pinning removed | Fails: 13 `A_stored_key_id_that_is_not_the_configured...` cases (the first attempt did not compile; a no-op condition was used instead) |
| 4 | Token echoed in the 401/403 error | Fails: `Failures_on_wrap_are_typed_and_never_contain_the_token` (401, 403), the 403 unwrap case, and the real-server bad-token test |
| 5 | Mount and key-name validation removed | Fails: 16 `Mounts_and_key_names_that_could_change_the_request_path_are_refused` cases |
| 6 | Wrong context (400) mapped to `VaultTransitException` instead of `KeyUnwrapException` | Fails: `On_unwrap_a_400_or_404...(400)`, contract and real tenant-binding tests, conformance `A_key_wrapped_for_one_context...` |
| 7 | Canonical base64 check removed | Fails: `Base64_with_spare_bits_set_in_the_last_character...` |

## Could not verify
- Kubernetes and AppRole login only against the fake server, not a real Vault (status in the architecture doc: Built, untested).
- Vault Enterprise namespaces (`X-Vault-Namespace`): header sent is tested; no Enterprise server available.
- A user-supplied `HttpClient` with automatic redirects on would follow a redirect and forward the token header; the provider's own client does not follow redirects, and 3xx from a supplied client is treated as a failure only if it is surfaced. Documented in the constructor summary; not tested.
- `build/check-secrets.ps1` fails in this worktree on its own file (lines 72, 76, 77: the self-test seeds match the scan rules); I did not touch `build/`. It reports nothing in files I added. `build/check-disclosure.ps1` passes.
- The pipeline's `-p:KairosTargetFramework=net11.0` build was not run.
- Not run: the other test projects (Postgres suites); the whole solution builds with 0 warnings and locked-mode restore passes.

## Open items
- Run the new container tests in `azure-pipelines.yml` (they are tagged Integration and need Docker and the two images).
- Decide whether to make the root-token warning mechanical (for example, a startup call to `auth/token/lookup-self` that refuses a token whose policies include `root`); needs the `lookup-self` permission in the policy, so it was not added.
- Cache negative key checks? Today a transient failure of the key check is retried on the next call, which is intended.
