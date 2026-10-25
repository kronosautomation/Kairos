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
public sealed record VerifiedToolCredential(
    Guid TenantId, Guid NamespaceId, Guid RunId, string Tool, IReadOnlyList<string> Scopes, string? OnBehalfOf, DateTimeOffset ExpiresAt);

/// <summary>
/// Self-contained tokens signed with HMAC-SHA256: run, tool, scopes and expiry. Any service that holds the verification key can check
/// one without calling back. Lifetimes are capped, and rotating the key revokes every token at once.
/// </summary>
public sealed class HmacToolCredentialIssuer : IToolCredentialIssuer
{
    public const string Prefix = "ktc_";
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

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
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"A tool credential lives at most {MaxLifetime.TotalMinutes:0} minutes.");
        }

        if (request.Scopes.Count == 0)
        {
            throw new ArgumentException("A credential needs at least one scope.", nameof(request));
        }

        DateTimeOffset expires = now + request.Lifetime;
        var claims = new Claims(
            request.Run.TenantId, request.Run.NamespaceId, request.Run.RunId, request.Tool, [.. request.Scopes], request.OnBehalfOf, expires.UtcTicks, Guid.NewGuid());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims, CredentialJson.Default.Claims);
        string token = Prefix + Base64Url(payload) + "." + Base64Url(HMACSHA256.HashData(_key, payload));
        return ValueTask.FromResult(new ToolCredential(token, expires, request.Scopes));
    }

    /// <summary>Checks a token the way a downstream service would. Returns null for anything wrong: forged, altered, expired or malformed.</summary>
    /// <param name="token">The presented token.</param>
    /// <param name="requiredScope">The scope the call needs, for example <c>refunds:write</c>.</param>
    /// <param name="now">The current time.</param>
    public VerifiedToolCredential? Verify(string? token, string requiredScope, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(requiredScope);
        if (token is null || token.Length > 2048 || !token.StartsWith(Prefix, StringComparison.Ordinal))
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
            byte[] payload = FromBase64Url(token[Prefix.Length..dot]);
            byte[] mac = FromBase64Url(token[(dot + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), mac))
            {
                return null;
            }

            Claims? claims = JsonSerializer.Deserialize(payload, CredentialJson.Default.Claims);
            if (claims is null || claims.ExpiresTicks <= now.UtcTicks || !claims.Scopes.Contains(requiredScope, StringComparer.Ordinal))
            {
                return null;
            }

            return new VerifiedToolCredential(
                claims.Tenant, claims.Namespace, claims.Run, claims.Tool, claims.Scopes, claims.Obo, new DateTimeOffset(claims.ExpiresTicks, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    internal sealed record Claims(Guid Tenant, Guid Namespace, Guid Run, string Tool, string[] Scopes, string? Obo, long ExpiresTicks, Guid Jti);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HmacToolCredentialIssuer.Claims))]
internal sealed partial class CredentialJson : JsonSerializerContext;
