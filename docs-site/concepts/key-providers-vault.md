# Keys in HashiCorp Vault or OpenBao (Transit)

If your keys live in HashiCorp Vault or OpenBao rather than in a cloud's key service, `VaultTransitKeyProvider` (package `Kairos.Cloud.SelfHosted`) wraps and unwraps Kairos data keys with a
**Transit** key. The master key stays inside Vault; your application sends it a data key to wrap and gets back an opaque string. See [Payload encryption and its limits](payload-encryption.md)
for what the data keys protect.

It uses plain HTTPS calls to Vault's API. There is no Vault client library and no extra package.

## Prepare the key

Enable the Transit engine and create a **derived** key. Derived is not optional: it is what lets Vault itself refuse to unwrap a key for the wrong tenant, in the way a cloud key service
does with its encryption context.

```shell
vault secrets enable transit
vault write -f transit/keys/kairos derived=true
```

Kairos passes its tenant, namespace and field binding as Transit's `context`. A key wrapped for one context cannot be unwrapped for another, and Vault says so, not Kairos.
When the provider first uses the key it reads `transit/keys/kairos` and **refuses to start** (with an `InvalidOperationException` that names the key) if the key is not derived or cannot encrypt.

## A least-privilege policy

The application needs to read the key's settings (once, to check it is derived) and to encrypt and decrypt with it. It does not need to create, rotate, export or delete anything.

```hcl
path "transit/keys/kairos"    { capabilities = ["read"] }
path "transit/encrypt/kairos" { capabilities = ["update"] }
path "transit/decrypt/kairos" { capabilities = ["update"] }
```

```shell
vault policy write kairos-worker kairos-policy.hcl
```

Give the application a token, or a role, that carries only this policy. **Never use the root token.** The provider cannot tell a root token from another one, so this is a rule for you to keep.

## Use it

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#vault-transit)]

Things to know about the options:

- **The address** must be `https`. Plain `http` is accepted only for a loopback address (a local test server). It must not contain a user name, a path or a query.
- **The mount and key name** are checked against a strict pattern (letters, digits, `-` and `_`; a mount may have up to four segments separated by `/`). A value that could change the request path is refused when the provider is created.
- **`Namespace`** sets the `X-Vault-Namespace` header for Vault Enterprise namespaces. Leave it out otherwise.
- **`RequestTimeout`** (10 seconds) and **`MaxResponseBytes`** (64 KiB) bound every call. Redirects are not followed.

## Signing in

There is deliberately no option that takes a token as text. A token in a connection string or a configuration file ends up in logs, backups and screenshots. Choose one of:

- **`UseTokenFile(path)`**: the token is read from a file, usually the sink of a Vault Agent that signs in and keeps the token renewed.
  The file is read again when it changes, and at once when Vault answers 403, so a renewed token is picked up without a restart.
- **`UseKubernetesAuth(role)`**: the pod signs in with its service account token (Kubernetes auth method).

  [!code-csharp[](../../samples/Concepts/ConceptExamples.cs#vault-transit-kubernetes)]
- **`UseAppRole(roleId, secretIdFile)`**: AppRole, with the secret id read from a file or a function you supply.
- **`UseTokenSource(function)`**: your own function that returns a token. It is called for each request, so cache inside it.

A Vault Agent sink looks like this:

```hcl
auto_auth {
  method "kubernetes" {
    config = { role = "kairos-worker" }
  }
  sink "file" {
    config = { path = "/var/run/vault/token", mode = 0440 }
  }
}
```

Passing something that looks like a token where a file path belongs is refused, and the value is not repeated in the error.

## What is stored, and what a failure says

`WrappedKey.KeyId` is `transit/kairos#3`: the mount, the key and the Transit key version that wrapped it. When a key is unwrapped, the stored id must be **exactly** the configured mount and key
name; anything else is refused without a request to Vault, and a stored id is never used to build a URL. The ciphertext must be in Vault's exact `vault:v<n>:<base64>` form with canonical base64.

| What happened | What you get |
|---|---|
| Wrong tenant or namespace, altered ciphertext, unknown or other key id, key version no longer allowed | `KeyUnwrapException`, the same message for all of them |
| Vault refused the token (401 or 403) after the token was read again | `VaultTransitException` with `Failure = Authentication` |
| The mount or key does not exist (404 on wrap) | `VaultTransitException` with `Failure = NotFound` |
| Vault is sealed, overloaded or unreachable, or the call timed out | `VaultTransitException` with `Failure = Unavailable`; try again |
| The token file is missing or empty, or sign-in was refused | `VaultTransitException` with `Failure = Token` |

No message contains a token, a key, or Vault's response text.

## Rotation and audit

Rotate the key in Vault (`vault write -f transit/keys/kairos/rotate`). New data keys are wrapped with the new version; older ones still unwrap as long as Vault allows that version
(`min_decryption_version`). Enable a Vault audit device to record every wrap and unwrap on the Vault side; Kairos can also record each call
through the [key use observer](payload-encryption.md).

Note what Transit sees: the data key is sent to Vault, over TLS, to be encrypted. Vault is therefore trusted with the data keys it wraps, as a cloud key service is.

## OpenBao

OpenBao has the same Transit API. The provider's tests run against OpenBao 2.7 as well as Vault 1.18, and pass.
