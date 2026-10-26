using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairos.Agents;

/// <summary>
/// A token for one tool, in one run, with an expiry. It is minted when the tool call starts and is never written to history.
/// <see cref="ToString"/> hides the secret.
/// </summary>
public sealed class ToolCredential
{
    public ToolCredential(string token, DateTimeOffset expiresAt, IReadOnlyList<string> scopes)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(scopes);
        Token = token;
        ExpiresAt = expiresAt;
        Scopes = scopes;
    }

    /// <summary>The secret to present to the service. Treat it like a password: do not log it or put it in a result.</summary>
    public string Token { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>What the token allows, for example <c>refunds:read</c>.</summary>
    public IReadOnlyList<string> Scopes { get; }

    public override string ToString() => $"ToolCredential(scopes={string.Join(',', Scopes)}, expires={ExpiresAt:O}, token=***)";
}

/// <param name="Run">The run the credential belongs to.</param>
/// <param name="Tool">The tool it is for.</param>
/// <param name="Scopes">What it may do. Read-only tools get <c>{tool}:read</c> only.</param>
/// <param name="Lifetime">How long it lives.</param>
/// <param name="OnBehalfOf">Who the agent acts for.</param>
public sealed record ToolCredentialRequest(RunKey Run, string Tool, IReadOnlyList<string> Scopes, TimeSpan Lifetime, string? OnBehalfOf);

/// <summary>Mints per-run, per-tool credentials. An agent is never given a user's password; it is given one of these.</summary>
public interface IToolCredentialIssuer
{
    ValueTask<ToolCredential> IssueAsync(ToolCredentialRequest request, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>What a service learns by checking a token.</summary>
/// <param name="TenantId">The tenant of the run.</param>
/// <param name="NamespaceId">The namespace of the run.</param>
/// <param name="RunId">The run the token was issued to.</param>
/// <param name="Tool">The tool it was issued for.</param>
/// <param name="Scopes">What it allows.</param>
/// <param name="OnBehalfOf">Who the agent acts for.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="TokenId">Unique per token; see <see cref="OneTimeUseGuard"/>.</param>
public sealed record VerifiedToolCredential(
    Guid TenantId, Guid NamespaceId, Guid RunId, string Tool, IReadOnlyList<string> Scopes, string? OnBehalfOf, DateTimeOffset ExpiresAt, Guid TokenId);

/// <summary>Checks tool credentials. A service that only verifies should hold only this, never an issuer.</summary>
public interface IToolCredentialVerifier
{
    /// <summary>Returns the claims of a genuine, unexpired token issued for <paramref name="expectedTool"/> that carries <paramref name="requiredScope"/>; otherwise null.</summary>
    /// <param name="token">The presented token.</param>
    /// <param name="requiredScope">The scope this call needs, for example <c>refunds:write</c>.</param>
    /// <param name="expectedTool">The tool this service is. A token issued for another tool is refused even when its scope string matches.</param>
    /// <param name="now">The current time.</param>
    VerifiedToolCredential? Verify(string? token, string requiredScope, string expectedTool, DateTimeOffset now);
}

/// <summary>
/// Remembers tokens that were used, so a high-risk scope can insist on one use per token. In memory and per process: use a shared store
/// when several instances of the service accept the same scope. Entries are dropped once the token would have expired anyway.
/// </summary>
public sealed class OneTimeUseGuard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _used = new();

    /// <summary>True the first time a token is presented, false for every later presentation.</summary>
    public bool TryConsume(VerifiedToolCredential credential, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credential);
        foreach (KeyValuePair<Guid, DateTimeOffset> entry in _used)
        {
            if (entry.Value <= now)
            {
                _used.TryRemove(entry.Key, out _);
            }
        }

        return _used.TryAdd(credential.TokenId, credential.ExpiresAt);
    }
}

internal sealed record ToolClaims(Guid Tenant, Guid Namespace, Guid Run, string Tool, string[] Scopes, string? Obo, long ExpiresTicks, Guid Jti);

internal static class ToolTokens
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    public static ToolClaims Claims(ToolCredentialRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"A tool credential lives at most {MaxLifetime.TotalMinutes:0} minutes.");
        }

        if (request.Scopes.Count == 0)
        {
            throw new ArgumentException("A credential needs at least one scope.", nameof(request));
        }

        return new ToolClaims(
            request.Run.TenantId, request.Run.NamespaceId, request.Run.RunId, request.Tool, [.. request.Scopes], request.OnBehalfOf,
            (now + request.Lifetime).UtcTicks, Guid.NewGuid());
    }

    public static byte[] Payload(ToolClaims claims) => JsonSerializer.SerializeToUtf8Bytes(claims, CredentialJson.Default.ToolClaims);

    public static string Token(string prefix, byte[] payload, byte[] signature) => prefix + Base64.Encode(payload) + "." + Base64.Encode(signature);

    public static VerifiedToolCredential? Check(
        string? token, string prefix, Func<byte[], byte[], bool> signatureValid, string requiredScope, string expectedTool, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(requiredScope);
        ArgumentException.ThrowIfNullOrEmpty(expectedTool);
        if (token is null || token.Length > 2048 || !token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        int dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return null;
        }

        try
        {
            byte[] payload = Base64.Decode(token[prefix.Length..dot]);
            if (!signatureValid(payload, Base64.Decode(token[(dot + 1)..])))
            {
                return null;
            }

            ToolClaims? claims = JsonSerializer.Deserialize(payload, CredentialJson.Default.ToolClaims);
            if (claims is null || claims.ExpiresTicks <= now.UtcTicks ||
                !string.Equals(claims.Tool, expectedTool, StringComparison.Ordinal) || !claims.Scopes.Contains(requiredScope, StringComparer.Ordinal))
            {
                return null;
            }

            return new VerifiedToolCredential(
                claims.Tenant, claims.Namespace, claims.Run, claims.Tool, claims.Scopes, claims.Obo, new DateTimeOffset(claims.ExpiresTicks, TimeSpan.Zero), claims.Jti);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Self-contained tokens signed with HMAC-SHA256. One shared key both mints and verifies, so every service that can verify can also mint:
/// use this when one service holds the key (or every holder is equally trusted), and <see cref="EcdsaToolCredentialIssuer"/> when several
/// tool services verify. Lifetimes are capped at fifteen minutes. Rotating the key revokes every token at once.
/// </summary>
public sealed class HmacToolCredentialIssuer : IToolCredentialIssuer, IToolCredentialVerifier
{
    public const string Prefix = "ktc_";
    public static readonly TimeSpan MaxLifetime = ToolTokens.MaxLifetime;

    private readonly byte[] _key;

    public HmacToolCredentialIssuer(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("The signing key must be at least 32 bytes.", nameof(signingKey));
        }

        _key = [.. signingKey];
    }

    public ValueTask<ToolCredential> IssueAsync(ToolCredentialRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ToolClaims claims = ToolTokens.Claims(request, now);
        byte[] payload = ToolTokens.Payload(claims);
        string token = ToolTokens.Token(Prefix, payload, HMACSHA256.HashData(_key, payload));
        return ValueTask.FromResult(new ToolCredential(token, new DateTimeOffset(claims.ExpiresTicks, TimeSpan.Zero), request.Scopes));
    }

    public VerifiedToolCredential? Verify(string? token, string requiredScope, string expectedTool, DateTimeOffset now) =>
        ToolTokens.Check(token, Prefix, (payload, mac) => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), mac), requiredScope, expectedTool, now);
}

/// <summary>
/// Tokens signed with ECDSA P-256. Only the holder of the private key can mint; tool services verify with the public key
/// (<see cref="EcdsaToolCredentialVerifier"/>) and cannot mint, so a compromised tool service cannot forge access to another. This is the
/// recommended default when more than one service checks tokens.
/// </summary>
public sealed class EcdsaToolCredentialIssuer : IToolCredentialIssuer
{
    public const string Prefix = "ktp_";

    private readonly ECDsa _key;

    /// <param name="privateKey">A P-256 key pair. The issuer keeps a reference; dispose it yourself.</param>
    public EcdsaToolCredentialIssuer(ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (privateKey.KeySize != 256)
        {
            throw new ArgumentException("Tool credentials use ECDSA P-256.", nameof(privateKey));
        }

        _key = privateKey;
    }

    /// <summary>The public half, to give to services that verify (SubjectPublicKeyInfo).</summary>
    public byte[] ExportPublicKey() => _key.ExportSubjectPublicKeyInfo();

    public ValueTask<ToolCredential> IssueAsync(ToolCredentialRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ToolClaims claims = ToolTokens.Claims(request, now);
        byte[] payload = ToolTokens.Payload(claims);
        byte[] signature = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ValueTask.FromResult(new ToolCredential(
            ToolTokens.Token(Prefix, payload, signature), new DateTimeOffset(claims.ExpiresTicks, TimeSpan.Zero), request.Scopes));
    }
}

/// <summary>Verifies <see cref="EcdsaToolCredentialIssuer"/> tokens with the public key alone. It has no way to issue one.</summary>
public sealed class EcdsaToolCredentialVerifier : IToolCredentialVerifier, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create();

    /// <param name="publicKey">The issuer's <see cref="EcdsaToolCredentialIssuer.ExportPublicKey"/>.</param>
    public EcdsaToolCredentialVerifier(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        _key.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (_key.KeySize != 256)
        {
            throw new ArgumentException("Tool credentials use ECDSA P-256.", nameof(publicKey));
        }
    }

    public VerifiedToolCredential? Verify(string? token, string requiredScope, string expectedTool, DateTimeOffset now) =>
        ToolTokens.Check(
            token, EcdsaToolCredentialIssuer.Prefix,
            (payload, signature) => signature.Length == 64 && _key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            requiredScope, expectedTool, now);

    public void Dispose() => _key.Dispose();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ToolClaims))]
internal sealed partial class CredentialJson : JsonSerializerContext;
