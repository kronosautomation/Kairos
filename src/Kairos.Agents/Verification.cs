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
public sealed record ApproverEvidenceContext(RunKey Run, string ApprovalId, bool Approved, string ClaimedSubject, string? Evidence, DateTimeOffset Now)
{
    /// <summary>
    /// Identifies the verification step asking (the same on every retry of that step, different for another step). A verifier that refuses to see one piece of evidence twice uses it to
    /// tell a retry of the same step, which must still work after a crash, from a second use.
    /// </summary>
    public string? StepKey { get; init; }
}

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
