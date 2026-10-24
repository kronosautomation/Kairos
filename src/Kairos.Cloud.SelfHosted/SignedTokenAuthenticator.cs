using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// Short-lived, self-contained worker tokens: tenant, namespace, role, subject and expiry sealed with HMAC-SHA256. Stateless (any server
/// node verifies it), capped at one hour, with no shared long-lived secret handed to workers. Rotate the signing key to revoke all.
/// </summary>
public sealed class SignedTokenAuthenticator : IWorkerAuthenticator
{
    public const string Prefix = "kat_";
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    private const byte Version = 1;
    private readonly byte[] _signingKey;

    public SignedTokenAuthenticator(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("The signing key must be at least 32 bytes.", nameof(signingKey));
        }

        _signingKey = [.. signingKey];
    }

    public string Issue(Principal principal, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        TimeSpan life = lifetime ?? MaxLifetime;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(life, MaxLifetime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero);

        byte[] subject = Encoding.UTF8.GetBytes(principal.Subject);
        byte[] payload = new byte[1 + 16 + 16 + 1 + 8 + 2 + subject.Length];
        payload[0] = Version;
        principal.Scope.TenantId.TryWriteBytes(payload.AsSpan(1), bigEndian: true, out _);
        principal.Scope.NamespaceId.TryWriteBytes(payload.AsSpan(17), bigEndian: true, out _);
        payload[33] = (byte)principal.Role;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(34), (now + life).UtcTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(42), (ushort)subject.Length);
        subject.CopyTo(payload, 44);

        byte[] mac = HMACSHA256.HashData(_signingKey, payload);
        return Prefix + Base64Url(payload) + "." + Base64Url(mac);
    }

    public ValueTask<AuthResult> AuthenticateAsync(Credential credential, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ValueTask.FromResult(credential is BearerCredential b ? Verify(b.Secret, now) : AuthResult.Fail(AuthFailure.Malformed));
    }

    private AuthResult Verify(string? token, DateTimeOffset now)
    {
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length > 1024)
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        int dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        byte[] payload, mac;
        try
        {
            payload = FromBase64Url(token[Prefix.Length..dot]);
            mac = FromBase64Url(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_signingKey, payload), mac))
        {
            return AuthResult.Fail(AuthFailure.UnknownCredential);
        }

        // The signature is valid, so the payload is ours; still bounds-check it before trusting any length field.
        if (payload.Length < 44 || payload[0] != Version || !Enum.IsDefined((Role)payload[33]))
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        int subjectLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(42));
        if (payload.Length != 44 + subjectLength)
        {
            return AuthResult.Fail(AuthFailure.Malformed);
        }

        var expires = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(34)), TimeSpan.Zero);
        if (expires <= now)
        {
            return AuthResult.Fail(AuthFailure.Expired);
        }

        var scope = new TenantScope(new Guid(payload.AsSpan(1, 16), bigEndian: true), new Guid(payload.AsSpan(17, 16), bigEndian: true));
        return AuthResult.Success(new Principal(scope, (Role)payload[33], Encoding.UTF8.GetString(payload, 44, subjectLength), expires));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        byte[] bytes = Convert.FromBase64String(padded);

        // Accept only the canonical spelling: Base64 can encode one byte string several ways (unused low bits), and a token should
        // have exactly one valid text form.
        if (!string.Equals(Base64Url(bytes), text, StringComparison.Ordinal))
        {
            throw new FormatException("Non-canonical encoding.");
        }

        return bytes;
    }
}
