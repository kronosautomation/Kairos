using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kairos.Agents;

/// <summary>
/// Signs approval evidence with ES256 (ECDSA on P-256 with SHA-256). The private key never leaves wherever it lives (Key Vault, KMS, an HSM): the application that authenticates people holds a
/// handle that can ask for a signature, and nothing else holds anything that can. Workers verify with the public key only (<see cref="EsApproverVerifier"/>), so a compromised worker cannot
/// mint evidence, and a holder of the public key cannot either.
/// </summary>
public interface IApprovalEvidenceSigner
{
    /// <summary>The key id written into the evidence header (<c>kid</c>): what the verifier pins.</summary>
    string KeyId { get; }

    /// <summary>The 64-byte ES256 signature (r then s, big endian, 32 bytes each) of SHA-256 over <paramref name="signingInput"/>.</summary>
    ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> signingInput, CancellationToken cancellationToken);
}

/// <summary>An ES256 key held in this process. For tests, samples and development only: in production the key lives in Key Vault or KMS and the signer asks it to sign.</summary>
public sealed class LocalApprovalEvidenceSigner(ECDsa key, string keyId) : IApprovalEvidenceSigner
{
    public string KeyId { get; } = keyId;

    /// <summary>The public half, as DER SubjectPublicKeyInfo: what a verifier is given.</summary>
    public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

    public static LocalApprovalEvidenceSigner Create(string keyId = "approval-dev-1") => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), keyId);

    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> signingInput, CancellationToken cancellationToken) =>
        ValueTask.FromResult(key.SignData(signingInput.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

/// <summary>What one piece of evidence says. Every field is checked against the decision it arrives with.</summary>
/// <param name="Run">The run waiting (tenant, namespace, run).</param>
/// <param name="ApprovalId">The approval being decided.</param>
/// <param name="Approved">The answer.</param>
/// <param name="Subject">The authenticated person.</param>
/// <param name="Roles">The roles that person had.</param>
/// <param name="IssuedAt">When it was made.</param>
/// <param name="ExpiresAt">When it stops working: at most <see cref="ApprovalEvidence.MaxLifetime"/> after <paramref name="IssuedAt"/>.</param>
/// <param name="EvidenceId">A random id (<c>jti</c>) so one piece can be used once.</param>
public sealed record ApprovalEvidenceClaims(
    RunKey Run, string ApprovalId, bool Approved, string Subject, IReadOnlyList<string> Roles, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string EvidenceId);

/// <summary>Makes approval evidence: a compact JWS, ES256, signed through <see cref="IApprovalEvidenceSigner"/>.</summary>
public static class ApprovalEvidence
{
    public const string Type = "kairos-approval+jwt";
    public const string Algorithm = "ES256";
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
    internal const int SignatureLength = 64;
    internal const int MaxLength = 4096;

    public static string NewEvidenceId() => Base64.Encode(RandomNumberGenerator.GetBytes(16));

    public static async ValueTask<string> MintAsync(IApprovalEvidenceSigner signer, ApprovalEvidenceClaims claims, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentException.ThrowIfNullOrEmpty(claims.ApprovalId);
        ArgumentException.ThrowIfNullOrEmpty(claims.Subject);
        ArgumentException.ThrowIfNullOrEmpty(claims.EvidenceId);
        TimeSpan life = claims.ExpiresAt - claims.IssuedAt;
        if (life <= TimeSpan.Zero || life > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(claims), "Approval evidence lives at most five minutes.");
        }

        string header = Base64.Encode(Utf8(w =>
        {
            w.WriteStartObject();
            w.WriteString("alg", Algorithm);
            w.WriteString("kid", signer.KeyId);
            w.WriteString("typ", Type);
            w.WriteEndObject();
        }));
        string payload = Base64.Encode(Utf8(w =>
        {
            w.WriteStartObject();
            w.WriteString("tid", claims.Run.TenantId.ToString("D"));
            w.WriteString("nid", claims.Run.NamespaceId.ToString("D"));
            w.WriteString("rid", claims.Run.RunId.ToString("D"));
            w.WriteString("aid", claims.ApprovalId);
            w.WriteString("dir", claims.Approved ? "approve" : "reject");
            w.WriteString("sub", claims.Subject);
            w.WriteStartArray("roles");
            foreach (string role in claims.Roles)
            {
                w.WriteStringValue(role);
            }

            w.WriteEndArray();
            w.WriteNumber("iat", claims.IssuedAt.ToUnixTimeSeconds());
            w.WriteNumber("exp", claims.ExpiresAt.ToUnixTimeSeconds());
            w.WriteString("jti", claims.EvidenceId);
            w.WriteEndObject();
        }));
        string input = header + "." + payload;
        byte[] signature = await signer.SignAsync(Encoding.ASCII.GetBytes(input), cancellationToken).ConfigureAwait(false);
        if (signature.Length != SignatureLength)
        {
            throw new InvalidOperationException("The signer did not return an ES256 signature.");
        }

        return input + "." + Base64.Encode(signature);
    }

    private static byte[] Utf8(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            write(w);
        }

        return stream.ToArray();
    }
}

/// <summary>
/// Verifies approval evidence with public keys only. A key is pinned by its id: evidence whose <c>kid</c> is not one of the configured keys is refused, whatever it is signed with. Accepted only
/// when: three canonical segments; header <c>alg</c> exactly ES256 and <c>typ</c> exactly the approval type (an ID token or a session token is not evidence); a 64-byte signature that verifies;
/// tenant, namespace, run, approval id, direction and subject equal to the decision it came with; issued no later than a little ahead of now, not expired, and living at most five minutes; and an
/// evidence id not seen before. A piece of evidence is good for one verification step: a second step presenting the same id is refused, which makes a captured piece useless after the decision it was made for (the step that used it first may repeat its check, which is what a retry after a crash does). The memory of
/// used ids is per process and bounded; evidence is also short-lived and bound to one decision, which is what keeps another worker from being a way around it.
/// Without a step key (a caller that cannot say which step it is) a second use is always refused, which fails closed.
/// </summary>
public sealed class EsApproverVerifier : IApproverVerifier, IDisposable
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Until, string? Step)> _used = new(StringComparer.Ordinal);
    private readonly int _capacity;

    /// <param name="publicKeysByKeyId">Key id to DER SubjectPublicKeyInfo of an ECDSA P-256 key. Anything else (an RSA key, another curve, a shared secret) is refused.</param>
    /// <param name="usedIdCapacity">How many used evidence ids are remembered (default 100,000). When full, the oldest are forgotten.</param>
    public EsApproverVerifier(IReadOnlyDictionary<string, byte[]> publicKeysByKeyId, int usedIdCapacity = 100_000)
    {
        ArgumentNullException.ThrowIfNull(publicKeysByKeyId);
        ArgumentOutOfRangeException.ThrowIfLessThan(usedIdCapacity, 16);
        if (publicKeysByKeyId.Count == 0)
        {
            throw new ArgumentException("At least one public key is needed.", nameof(publicKeysByKeyId));
        }

        _capacity = usedIdCapacity;
        foreach ((string keyId, byte[] spki) in publicKeysByKeyId)
        {
            if (string.IsNullOrEmpty(keyId))
            {
                throw new ArgumentException("A key id is empty.", nameof(publicKeysByKeyId));
            }

            var key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(spki, out int read);
                if (read != spki.Length || key.ExportParameters(false).Curve.Oid?.Value != "1.2.840.10045.3.1.7")
                {
                    throw new ArgumentException($"Key '{keyId}' is not an ECDSA P-256 public key.", nameof(publicKeysByKeyId));
                }
            }
            catch (CryptographicException)
            {
                key.Dispose();
                throw new ArgumentException($"Key '{keyId}' is not an ECDSA P-256 public key (approval evidence is verified with public keys only).", nameof(publicKeysByKeyId));
            }
            catch (ArgumentException)
            {
                key.Dispose();
                throw;
            }

            _keys[keyId] = key;
        }
    }

    public void Dispose()
    {
        foreach (ECDsa key in _keys.Values)
        {
            key.Dispose();
        }
    }

    public ValueTask<VerifiedApprover?> VerifyAsync(ApproverEvidenceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(Verify(context));
    }

    private VerifiedApprover? Verify(ApproverEvidenceContext c)
    {
        if (c.Evidence is not { Length: > 0 and <= ApprovalEvidence.MaxLength } token)
        {
            return null;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using JsonDocument header = JsonDocument.Parse(Base64.Decode(parts[0]));
            JsonElement h = header.RootElement;
            if (h.ValueKind != JsonValueKind.Object || Str(h, "alg") != ApprovalEvidence.Algorithm || Str(h, "typ") != ApprovalEvidence.Type ||
                Str(h, "kid") is not { } kid || !_keys.TryGetValue(kid, out ECDsa? key))
            {
                return null;
            }

            byte[] signature = Base64.Decode(parts[2]);
            if (signature.Length != ApprovalEvidence.SignatureLength ||
                !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return null;
            }

            using JsonDocument payload = JsonDocument.Parse(Base64.Decode(parts[1]));
            JsonElement p = payload.RootElement;
            if (p.ValueKind != JsonValueKind.Object ||
                !Guid.TryParseExact(Str(p, "tid"), "D", out Guid tenant) || !Guid.TryParseExact(Str(p, "nid"), "D", out Guid ns) || !Guid.TryParseExact(Str(p, "rid"), "D", out Guid run) ||
                tenant != c.Run.TenantId || ns != c.Run.NamespaceId || run != c.Run.RunId ||
                !string.Equals(Str(p, "aid"), c.ApprovalId, StringComparison.Ordinal) ||
                Str(p, "dir") != (c.Approved ? "approve" : "reject") ||
                Str(p, "sub") is not { Length: > 0 } subject || !string.Equals(subject, c.ClaimedSubject, StringComparison.Ordinal) ||
                Str(p, "jti") is not { Length: >= 16 and <= 64 } jti ||
                !Seconds(p, "iat", out DateTimeOffset issued) || !Seconds(p, "exp", out DateTimeOffset expires) ||
                expires <= c.Now || issued - Skew > c.Now || expires < issued || expires - issued > ApprovalEvidence.MaxLifetime)
            {
                return null;
            }

            string[] roles = [];
            if (p.TryGetProperty("roles", out JsonElement r))
            {
                if (r.ValueKind != JsonValueKind.Array || r.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                {
                    return null;
                }

                roles = [.. r.EnumerateArray().Select(x => x.GetString()!)];
            }

            return TryUse(jti, expires, c.Now, c.StepKey) ? new VerifiedApprover(subject, roles) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the evidence id as used until the evidence would have expired. False when another step used it before. The step that used it first may ask again (a retry after a crash
    /// between checking and recording must still find the decision good, or the same run would end differently depending on when a worker died).
    /// </summary>
    private bool TryUse(string jti, DateTimeOffset expires, DateTimeOffset now, string? step)
    {
        lock (_gate)
        {
            if (_used.TryGetValue(jti, out var seen))
            {
                return step is not null && seen.Step == step;
            }

            if (_used.Count >= _capacity / 2)
            {
                long cutoff = now.ToUnixTimeSeconds();
                foreach (string old in _used.Where(u => u.Value.Until <= cutoff).Select(u => u.Key).ToArray())
                {
                    _used.Remove(old);
                }

                while (_used.Count >= _capacity)
                {
                    _used.Remove(_used.OrderBy(u => u.Value.Until).First().Key); // full of live ids: forget the one that expires first
                }
            }

            _used[jti] = (expires.ToUnixTimeSeconds() + (long)Skew.TotalSeconds, step);
            return true;
        }
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Seconds(JsonElement o, string name, out DateTimeOffset value)
    {
        value = default;
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long s) || s < 0 || s > 253_402_300_799)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(s);
        return true;
    }
}
