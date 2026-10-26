using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairos.Agents;

namespace Kairos.Agents.Tests;

/// <summary>
/// Approval evidence is ES256, signed where the private key lives and verified with the public key alone. Whoever can verify cannot mint: a worker that is compromised, or anyone who holds the
/// public key, cannot make evidence that another worker accepts.
/// </summary>
public sealed class ApprovalEvidenceTests
{
    private static readonly RunKey Run = new(Guid.Parse("11111111-0000-0000-0000-000000000001"), Guid.Parse("11111111-0000-0000-0000-0000000000a1"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApproverIdentity Alice = new("alice@example.test", "Alice", ["approver", "finance"]);

    private static (LocalApprovalEvidenceSigner Signer, EsApproverVerifier Verifier) Pair(string kid = "k-1")
    {
        var signer = LocalApprovalEvidenceSigner.Create(kid);
        return (signer, new EsApproverVerifier(new Dictionary<string, byte[]> { [kid] = signer.PublicKey }));
    }

    private static ValueTask<string> Mint(IApprovalEvidenceSigner signer, string approvalId = "refund-1", bool approved = true, string subject = "alice@example.test", RunKey? run = null,
        DateTimeOffset? issued = null, TimeSpan? life = null, string? jti = null) =>
        ApprovalEvidence.MintAsync(
            signer, new ApprovalEvidenceClaims(run ?? Run, approvalId, approved, subject, ["approver", "finance"], issued ?? Now, (issued ?? Now) + (life ?? TimeSpan.FromMinutes(5)), jti ?? ApprovalEvidence.NewEvidenceId()));

    private static ApproverEvidenceContext Ask(string? evidence, string approvalId = "refund-1", bool approved = true, string subject = "alice@example.test", RunKey? run = null, DateTimeOffset? now = null, string? step = "step-1") =>
        new(run ?? Run, approvalId, approved, subject, evidence, now ?? Now) { StepKey = step };

    // ---- what good evidence is ----

    [Fact]
    public async Task Evidence_signed_by_the_pinned_key_is_accepted_and_yields_the_subject_and_roles_from_the_evidence()
    {
        (var signer, var verifier) = Pair();
        string evidence = await Mint(signer);

        VerifiedApprover? ok = await verifier.VerifyAsync(Ask(evidence), default);

        Assert.NotNull(ok);
        Assert.Equal("alice@example.test", ok.Subject);
        Assert.Equal(["approver", "finance"], ok.Roles);
        Assert.Equal(3, evidence.Split('.').Length); // a compact JWS
        using JsonDocument header = JsonDocument.Parse(Convert.FromBase64String(Pad(evidence.Split('.')[0])));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("kairos-approval+jwt", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("k-1", header.RootElement.GetProperty("kid").GetString());
    }

    private static string Pad(string b64url)
    {
        string s = b64url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("namespace")]
    [InlineData("run")]
    [InlineData("approval")]
    [InlineData("direction")]
    [InlineData("subject")]
    public async Task Evidence_is_bound_to_the_decision_it_was_made_for(string what)
    {
        (var signer, var verifier) = Pair();
        string evidence = await Mint(signer);
        ApproverEvidenceContext asked = what switch
        {
            "tenant" => Ask(evidence, run: Run with { TenantId = Guid.NewGuid() }),
            "namespace" => Ask(evidence, run: Run with { NamespaceId = Guid.NewGuid() }),
            "run" => Ask(evidence, run: Run with { RunId = Guid.NewGuid() }),
            "approval" => Ask(evidence, approvalId: "refund-2"),
            "direction" => Ask(evidence, approved: false),
            _ => Ask(evidence, subject: "mallory@example.test"),
        };

        Assert.Null(await verifier.VerifyAsync(asked, default));
        Assert.NotNull(await verifier.VerifyAsync(Ask(evidence), default)); // and a mismatch did not use it up
    }

    [Fact]
    public async Task Evidence_expires_is_not_accepted_from_the_future_and_never_lives_longer_than_five_minutes()
    {
        (var signer, var verifier) = Pair();

        Assert.Null(await verifier.VerifyAsync(Ask(await Mint(signer, life: TimeSpan.FromMinutes(1)), now: Now.AddMinutes(1).AddSeconds(1)), default)); // expired
        Assert.Null(await verifier.VerifyAsync(Ask(await Mint(signer, issued: Now.AddMinutes(2))), default)); // made for later
        Assert.NotNull(await verifier.VerifyAsync(Ask(await Mint(signer, issued: Now.AddSeconds(20))), default)); // a little clock skew is fine

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Mint(signer, life: TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)))); // cannot even be made
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Mint(signer, life: TimeSpan.Zero));

        // And one made by hand with a longer life, signed with the real key, is refused by the verifier all the same.
        string longLived = await Forge(signer, "k-1", "ES256", "kairos-approval+jwt", exp: Now.AddMinutes(30));
        Assert.Null(await verifier.VerifyAsync(Ask(longLived), default));
    }

    // ---- used once ----

    [Fact]
    public async Task One_piece_of_evidence_serves_one_verification_step_and_a_retry_of_that_step_still_works()
    {
        (var signer, var verifier) = Pair();
        string evidence = await Mint(signer);

        Assert.NotNull(await verifier.VerifyAsync(Ask(evidence, step: "run:7"), default));
        Assert.NotNull(await verifier.VerifyAsync(Ask(evidence, step: "run:7"), default)); // the same step, after a crash: it must find the decision good again
        Assert.Null(await verifier.VerifyAsync(Ask(evidence, step: "run:9"), default)); // another step: a replay
        Assert.Null(await verifier.VerifyAsync(Ask(evidence, step: null), default)); // a caller that cannot say which step it is: refused, the safe way round
    }

    [Fact]
    public async Task Evidence_with_the_same_id_cannot_be_reused_for_a_second_decision_even_if_it_is_otherwise_good()
    {
        (var signer, var verifier) = Pair();
        string id = ApprovalEvidence.NewEvidenceId();
        string first = await Mint(signer, jti: id);
        string second = await Mint(signer, jti: id); // a different piece (different issue time) with a reused id

        Assert.NotNull(await verifier.VerifyAsync(Ask(first, step: "a"), default));
        Assert.Null(await verifier.VerifyAsync(Ask(second, step: "b"), default));
    }

    [Fact]
    public async Task The_memory_of_used_evidence_is_bounded_and_forgets_what_has_expired()
    {
        var signer = LocalApprovalEvidenceSigner.Create("k-1");
        var verifier = new EsApproverVerifier(new Dictionary<string, byte[]> { ["k-1"] = signer.PublicKey }, usedIdCapacity: 16);
        for (int i = 0; i < 100; i++)
        {
            Assert.NotNull(await verifier.VerifyAsync(Ask(await Mint(signer), step: "s" + i), default)); // never throws, never grows without end
        }

        string later = await Mint(signer, issued: Now.AddHours(2));
        Assert.NotNull(await verifier.VerifyAsync(Ask(later, now: Now.AddHours(2), step: "x"), default));
    }

    // ---- public keys only: nothing here can make evidence ----

    [Fact]
    public async Task Evidence_signed_by_another_key_under_the_pinned_key_id_is_refused()
    {
        (_, var verifier) = Pair("k-1");
        LocalApprovalEvidenceSigner attacker = LocalApprovalEvidenceSigner.Create("k-1"); // the right name, somebody else's key

        Assert.Null(await verifier.VerifyAsync(Ask(await Mint(attacker)), default));
    }

    [Fact]
    public async Task Evidence_naming_a_key_id_that_was_not_pinned_is_refused_even_if_it_is_validly_signed_by_the_holder_of_that_key()
    {
        (_, var verifier) = Pair("k-1");
        LocalApprovalEvidenceSigner other = LocalApprovalEvidenceSigner.Create("k-2");

        Assert.Null(await verifier.VerifyAsync(Ask(await Mint(other)), default));
    }

    [Fact]
    public async Task A_holder_of_the_public_key_cannot_mint_by_using_it_as_a_secret_or_by_choosing_another_algorithm()
    {
        (var signer, var verifier) = Pair();
        byte[] publicKey = signer.PublicKey;

        // The classic confusion: HS256 with the public key as the shared secret. And "none". And RS256.
        string hs256 = ForgeHmac(publicKey, "k-1");
        string none = await Forge(signer, "k-1", "none", "kairos-approval+jwt", signature: "");
        string rs256 = await Forge(signer, "k-1", "RS256", "kairos-approval+jwt");
        string wrongType = await Forge(signer, "k-1", "ES256", "JWT"); // validly signed, but not evidence: a session token or an ID token has this type

        foreach (string forged in new[] { hs256, none, rs256, wrongType })
        {
            Assert.Null(await verifier.VerifyAsync(Ask(forged), default));
        }

        Assert.NotNull(await verifier.VerifyAsync(Ask(await Forge(signer, "k-1", "ES256", "kairos-approval+jwt")), default)); // the control: the real signer's own forge-by-hand is good
    }

    [Fact]
    public void The_verifier_has_no_way_to_make_evidence()
    {
        // Verification takes the public key and offers no operation that signs: there is nothing in a worker process to steal that mints.
        var surface = typeof(EsApproverVerifier).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(m => m.Name).ToArray();
        Assert.Equal(["Dispose", "VerifyAsync"], surface.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(typeof(EsApproverVerifier).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(IApprovalEvidenceSigner) || p.ParameterType == typeof(ECDsa));
    }

    [Fact]
    public async Task Tampering_with_any_part_of_the_evidence_is_refused()
    {
        (var signer, var verifier) = Pair();
        string[] parts = (await Mint(signer)).Split('.');
        string tamperedPayload = parts[0] + "." + Flip(parts[1]) + "." + parts[2];
        string tamperedSignature = parts[0] + "." + parts[1] + "." + Flip(parts[2]);

        foreach (string bad in new[] { tamperedPayload, tamperedSignature, parts[0] + "." + parts[1], string.Join(".", parts) + ".x", "", "garbage", new string('a', 5000) })
        {
            Assert.Null(await verifier.VerifyAsync(Ask(bad), default));
        }

        Assert.Null(await verifier.VerifyAsync(Ask(null), default));
    }

    private static string Flip(string b64url) => (b64url[0] == 'A' ? "B" : "A") + b64url[1..];

    // ---- what the verifier will take as a key ----

    [Fact]
    public void Only_ecdsa_p256_public_keys_are_accepted_as_keys()
    {
        using var rsa = RSA.Create(2048);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        foreach (byte[] notAKey in new[] { rsa.ExportSubjectPublicKeyInfo(), p384.ExportSubjectPublicKeyInfo(), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(64), [] })
        {
            Assert.Throws<ArgumentException>(() => new EsApproverVerifier(new Dictionary<string, byte[]> { ["k"] = notAKey })); // an HMAC secret is refused here too
        }

        Assert.Throws<ArgumentException>(() => new EsApproverVerifier(new Dictionary<string, byte[]>()));
        Assert.Throws<ArgumentException>(() => new EsApproverVerifier(new Dictionary<string, byte[]> { [""] = LocalApprovalEvidenceSigner.Create().PublicKey }));
    }

    [Fact]
    public async Task Several_keys_can_be_pinned_for_rotation_and_each_is_good_only_under_its_own_id()
    {
        var a = LocalApprovalEvidenceSigner.Create("k-old");
        var b = LocalApprovalEvidenceSigner.Create("k-new");
        var verifier = new EsApproverVerifier(new Dictionary<string, byte[]> { ["k-old"] = a.PublicKey, ["k-new"] = b.PublicKey });

        Assert.NotNull(await verifier.VerifyAsync(Ask(await Mint(a)), default));
        Assert.NotNull(await verifier.VerifyAsync(Ask(await Mint(b)), default));
        Assert.Null(await verifier.VerifyAsync(Ask(await Mint(new LocalApprovalEvidenceSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256), "k-new"))), default));
    }

    [Fact]
    public async Task A_signer_that_returns_something_that_is_not_an_es256_signature_cannot_make_evidence()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Mint(new ShortSigner()));
    }

    private sealed class ShortSigner : IApprovalEvidenceSigner
    {
        public string KeyId => "k-1";

        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> signingInput, CancellationToken cancellationToken) => ValueTask.FromResult(new byte[10]);
    }

    // ---- forging by hand ----

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly string[] Approver = ["approver"];

    private static string Payload(DateTimeOffset? exp = null) => B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["tid"] = Run.TenantId.ToString("D"), ["nid"] = Run.NamespaceId.ToString("D"), ["rid"] = Run.RunId.ToString("D"), ["aid"] = "refund-1", ["dir"] = "approve",
        ["sub"] = "alice@example.test", ["roles"] = Approver, ["iat"] = Now.ToUnixTimeSeconds(), ["exp"] = (exp ?? Now.AddMinutes(5)).ToUnixTimeSeconds(), ["jti"] = ApprovalEvidence.NewEvidenceId(),
    }));

    /// <summary>Evidence made by hand with every claim right: only the header (or the lifetime) differs. Signed with the real key, unless a signature is given.</summary>
    private static async Task<string> Forge(LocalApprovalEvidenceSigner signer, string kid, string alg, string type, DateTimeOffset? exp = null, string? signature = null)
    {
        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = alg, ["kid"] = kid, ["typ"] = type }));
        string input = header + "." + Payload(exp);
        return input + "." + (signature ?? B64(await signer.SignAsync(Encoding.ASCII.GetBytes(input), default)));
    }

    private static string ForgeHmac(byte[] secret, string kid)
    {
        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "HS256", ["kid"] = kid, ["typ"] = "kairos-approval+jwt" }));
        string input = header + "." + Payload();
        return input + "." + B64(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(input)));
    }
}
