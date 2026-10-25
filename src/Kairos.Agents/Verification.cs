using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairos.Agents;

/// <summary>What a verifier is asked to check about one approval decision.</summary>
/// <param name="Run">The run waiting.</param>
/// <param name="ApprovalId">The approval being decided.</param>
/// <param name="Approved">The answer the decision claims to give.</param>
/// <param name="ClaimedSubject">The identity the decision claims.</param>
/// <param name="Evidence">The proof that came with the decision (a signed approval token, an OIDC ID token).</param>
/// <param name="Now">The current time, for expiry.</param>
public sealed record ApproverEvidenceContext(RunKey Run, string ApprovalId, bool Approved, string ClaimedSubject, string? Evidence, DateTimeOffset Now);

/// <summary>The identity a verifier established. Only this is used; the identity claimed in the decision is not.</summary>
public sealed record VerifiedApprover(string Subject, IReadOnlyList<string> Roles);

/// <summary>
/// Proves who decided an approval. The proof must be bound to the run, the approval, the answer and the subject, so it cannot be moved to
/// another decision. Return null for anything that does not check out. The result is recorded with the step, so it is never re-asked on replay.
/// </summary>
public interface IApproverVerifier
{
    ValueTask<VerifiedApprover?> VerifyAsync(ApproverEvidenceContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Approval tokens signed with HMAC-SHA256, bound to one run, approval, answer and subject, and short-lived. Your application issues one
/// after it has authenticated the person; the worker verifies it with the same key. Keep the key in the application that authenticates people
/// and in the workers; nothing that can merely send signals holds it, so nothing that can merely send signals can approve.
/// For identity providers that issue ID tokens, implement <see cref="IApproverVerifier"/> over the provider's validation instead.
/// </summary>
public sealed class HmacApprovalTokens : IApproverVerifier
{
    public const string Prefix = "kap_";
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    private readonly byte[] _key;

    public HmacApprovalTokens(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("The signing key must be at least 32 bytes.", nameof(signingKey));
        }

        _key = [.. signingKey];
    }

    /// <summary>Issues a token for a decision the person has just made. Call it only for an identity you authenticated.</summary>
    public string Issue(RunKey run, string approvalId, bool approved, ApproverIdentity approver, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        TimeSpan life = lifetime ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(life, MaxLifetime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero);
        var claims = new ApprovalClaims(run.TenantId, run.NamespaceId, run.RunId, approvalId, approved, approver.Subject, [.. approver.Roles ?? []], (now + life).UtcTicks);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims, CredentialJson.Default.ApprovalClaims);
        return Prefix + Base64.Encode(payload) + "." + Base64.Encode(HMACSHA256.HashData(_key, payload));
    }

    public ValueTask<VerifiedApprover?> VerifyAsync(ApproverEvidenceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(Verify(context));
    }

    private VerifiedApprover? Verify(ApproverEvidenceContext c)
    {
        string? token = c.Evidence;
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
            byte[] payload = Base64.Decode(token[Prefix.Length..dot]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), Base64.Decode(token[(dot + 1)..])))
            {
                return null;
            }

            ApprovalClaims? claims = JsonSerializer.Deserialize(payload, CredentialJson.Default.ApprovalClaims);
            if (claims is null || claims.ExpiresTicks <= c.Now.UtcTicks ||
                claims.Tenant != c.Run.TenantId || claims.Namespace != c.Run.NamespaceId || claims.Run != c.Run.RunId ||
                !string.Equals(claims.ApprovalId, c.ApprovalId, StringComparison.Ordinal) || claims.Approved != c.Approved ||
                !string.Equals(claims.Subject, c.ClaimedSubject, StringComparison.Ordinal))
            {
                return null;
            }

            return new VerifiedApprover(claims.Subject, claims.Roles);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    internal sealed record ApprovalClaims(Guid Tenant, Guid Namespace, Guid Run, string ApprovalId, bool Approved, string Subject, string[] Roles, long ExpiresTicks);
}

internal static class Base64
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
