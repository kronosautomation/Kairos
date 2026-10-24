using System.Security.Cryptography;
using System.Text;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// API keys and client certificates for humans, CI and workers. Keys are shown once at issue and only their HMAC-SHA256 (keyed by a
/// server-side pepper) is stored; comparison is constant time, and an unknown id costs the same as a wrong secret. Every key is scoped
/// to one namespace and one role and expires (default 90 days).
/// </summary>
public sealed class ApiKeyAuthenticator : IWorkerAuthenticator
{
    public const string Prefix = "kak_";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    private readonly byte[] _pepper;
    private readonly object _gate = new();
    private readonly Dictionary<string, KeyRecord> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Principal> _certificates = new(StringComparer.OrdinalIgnoreCase);

    public ApiKeyAuthenticator(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < 32)
        {
            throw new ArgumentException("The pepper must be at least 32 bytes and kept in a secret store.", nameof(pepper));
        }

        _pepper = [.. pepper];
    }

    /// <summary>Creates a key and returns its secret. This is the only time the secret exists in clear.</summary>
    public string Issue(TenantScope scope, Role role, string subject, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        string id = Base64Url(RandomNumberGenerator.GetBytes(9));
        string secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var record = new KeyRecord(Hash(secret), new Principal(scope, role, subject, now + (lifetime ?? DefaultLifetime)), Revoked: false);
        lock (_gate)
        {
            _keys.Add(id, record);
        }

        return $"{Prefix}{id}.{secret}";
    }

    public bool Revoke(string apiKey)
    {
        if (!TryParse(apiKey, out string id, out _))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_keys.TryGetValue(id, out KeyRecord? record))
            {
                return false;
            }

            _keys[id] = record with { Revoked = true };
            return true;
        }
    }

    /// <summary>Maps a TLS client certificate (SHA-256 thumbprint, validated by the host) to a principal.</summary>
    public void RegisterCertificate(string thumbprintSha256Hex, Principal principal)
    {
        ArgumentException.ThrowIfNullOrEmpty(thumbprintSha256Hex);
        ArgumentNullException.ThrowIfNull(principal);
        lock (_gate)
        {
            _certificates[thumbprintSha256Hex] = principal;
        }
    }

    public ValueTask<AuthResult> AuthenticateAsync(Credential credential, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ValueTask.FromResult(credential switch
        {
            BearerCredential b => AuthenticateKey(b.Secret, now),
            CertificateCredential c => AuthenticateCertificate(c.ThumbprintSha256Hex, now),
            _ => AuthResult.Fail(AuthFailure.Malformed),
        });
    }

    private AuthResult AuthenticateKey(string? apiKey, DateTimeOffset now)
    {
        if (!TryParse(apiKey, out string id, out string secret))
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        KeyRecord? record;
        lock (_gate)
        {
            _keys.TryGetValue(id, out record);
        }

        // Hash and compare even when the id is unknown, so response time does not reveal which ids exist.
        byte[] presented = Hash(secret);
        bool match = CryptographicOperations.FixedTimeEquals(presented, record?.SecretHash ?? new byte[presented.Length]);
        if (record is null || !match)
        {
            return AuthResult.Fail(AuthFailure.UnknownCredential);
        }

        if (record.Revoked)
        {
            return AuthResult.Fail(AuthFailure.Revoked);
        }

        return record.Principal.ExpiresAt is { } expires && expires <= now
            ? AuthResult.Fail(AuthFailure.Expired)
            : AuthResult.Success(record.Principal);
    }

    private AuthResult AuthenticateCertificate(string? thumbprint, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(thumbprint))
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        Principal? principal;
        lock (_gate)
        {
            _certificates.TryGetValue(thumbprint, out principal);
        }

        if (principal is null)
        {
            return AuthResult.Fail(AuthFailure.UnknownCredential);
        }

        return principal.ExpiresAt is { } expires && expires <= now ? AuthResult.Fail(AuthFailure.Expired) : AuthResult.Success(principal);
    }

    private byte[] Hash(string secret) => HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(secret));

    private static bool TryParse(string? apiKey, out string id, out string secret)
    {
        id = secret = "";
        if (apiKey is null || !apiKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int dot = apiKey.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0 || dot == apiKey.Length - 1 || apiKey.Length > 256)
        {
            return false;
        }

        id = apiKey[Prefix.Length..dot];
        secret = apiKey[(dot + 1)..];
        return id.Length > 0;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record KeyRecord(byte[] SecretHash, Principal Principal, bool Revoked);
}
