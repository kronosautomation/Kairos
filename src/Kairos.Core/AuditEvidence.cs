using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kairos;

/// <summary>
/// The format of a signed audit evidence bundle: <c>audit-&lt;id&gt;.jsonl</c> (the records), <c>audit-&lt;id&gt;.manifest.json</c> (what they cover) and <c>audit-&lt;id&gt;.sig</c>
/// (a detached ES256 signature over the manifest's bytes). Whoever writes a bundle follows this; <see cref="AuditEvidenceVerifier"/> checks it offline.
/// </summary>
public static class AuditEvidenceFormat
{
    /// <summary>The <c>format</c> member of a manifest.</summary>
    public const string Version = "kairos-audit-evidence/1";

    /// <summary>The <c>typ</c> of the signature's protected header.</summary>
    public const string SignatureType = "kairos-audit+jws";

    /// <summary>The only signature algorithm: ECDSA over P-256 with SHA-256.</summary>
    public const string Algorithm = "ES256";

    /// <summary>The <c>prev</c> of the first record: 64 hexadecimal zeros.</summary>
    public static readonly string ZeroHash = new('0', 64);

    /// <summary>The <c>sealedSections</c> value of a bundle whose writer opened the sealed steps it reports on.</summary>
    public const string Opened = "opened";

    /// <summary>The <c>sealedSections</c> value of a bundle written without a key identity: it makes no claim about what only sealed data says.</summary>
    public const string NotOpened = "not opened";

    public const string KindRun = "run";
    public const string KindApproval = "approval";
    public const string KindPolicyDecision = "policy_decision";
    public const string KindCredential = "credential";
    public const string KindHistoryExport = "history_export";
    public const string KindAdminAction = "admin_action";
    public const string KindChainHead = "chain_head";

    /// <summary>Every record kind, in the order a manifest lists their counts.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        [KindRun, KindApproval, KindPolicyDecision, KindCredential, KindHistoryExport, KindAdminAction, KindChainHead];

    /// <summary>The names of a record's members after the common ones (<c>n</c>, <c>prev</c>, <c>kind</c>, <c>at</c>, <c>tenant</c>, <c>namespace</c>), with the type each must have.</summary>
    internal static IReadOnlyDictionary<string, (string Name, char Type)[]> Fields { get; } = new Dictionary<string, (string, char)[]>
    {
        // G guid, g guid or null, S string, s string or null, T time, t time or null, I integer, B boolean, A array of strings, H 64 hex digits, E one of a set (checked by the kind)
        [KindRun] = [("run", 'G'), ("workflowType", 'S'), ("buildId", 'S'), ("startedBy", 's'), ("previousRun", 'g')],
        [KindApproval] = [("run", 'G'), ("approvalId", 'S'), ("tool", 's'), ("requestedAt", 't'), ("decidedAt", 't'), ("outcome", 'E'), ("approver", 's'), ("roles", 'A'), ("refused", 'I')],
        [KindPolicyDecision] = [("run", 'G'), ("step", 'I'), ("tool", 'S'), ("verdict", 'E'), ("reason", 's'), ("rule", 's'), ("policyVersion", 'I'), ("policyHash", 'h')],
        [KindCredential] = [("run", 'G'), ("step", 'I'), ("tool", 'S'), ("scopes", 'A'), ("expiresAt", 'T'), ("onBehalfOf", 's')],
        [KindHistoryExport] = [("run", 'g'), ("actor", 'S'), ("reason", 'S'), ("metadataOnly", 'B'), ("head", 'h')],
        [KindAdminAction] = [("run", 'g'), ("action", 'S'), ("actor", 'S'), ("detail", 'S'), ("sessionUser", 's'), ("clientAddress", 's'), ("claimedActor", 's')],
        [KindChainHead] = [("run", 'G'), ("sequence", 'I'), ("hash", 'H'), ("outcome", 'E')],
    };

    internal static readonly string[] ApprovalOutcomes = ["approved", "rejected", "unresolved", "sealed"];
    internal static readonly string[] Verdicts = ["allow", "require_approval", "deny"];
    internal static readonly string[] RunOutcomes = ["open", "completed", "failed", "terminated", "continued"];

    /// <summary>How a <see cref="HistoryFile.Outcome"/> is spelled in a <c>chain_head</c> record.</summary>
    public static string OutcomeOf(HistoryEvent last)
    {
        ArgumentNullException.ThrowIfNull(last);
        return last switch
        {
            WorkflowCompleted => "completed",
            WorkflowFailed => "failed",
            WorkflowContinued => "continued",
            RunTerminated => "terminated",
            _ => "open",
        };
    }

    /// <summary>SHA-256 of <paramref name="bytes"/> as 64 lower-case hexadecimal digits.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The ISO 8601 spelling of a time in a record or manifest: UTC, seven fractional digits.</summary>
    public static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The unpadded base64url spelling of <paramref name="bytes"/>, as the signature file uses.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[]? FromBase64Url(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }

        string padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            byte[] bytes = Convert.FromBase64String(padded);
            return Base64Url(bytes) == text ? bytes : null; // the canonical spelling only
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>The public key an auditor trusts for a bundle: its identifier and the key as DER <c>SubjectPublicKeyInfo</c> (ECDSA P-256).</summary>
public sealed class AuditKey
{
    private AuditKey(string keyId, byte[] spki)
    {
        KeyId = keyId;
        SubjectPublicKeyInfo = spki;
    }

    /// <summary>What the signature's <c>kid</c> must equal.</summary>
    public string KeyId { get; }

    /// <summary>The key as DER <c>SubjectPublicKeyInfo</c>.</summary>
    public byte[] SubjectPublicKeyInfo { get; }

    /// <summary>SHA-256 of <see cref="SubjectPublicKeyInfo"/>, as the manifest names the key it was signed with.</summary>
    public string Fingerprint => AuditEvidenceFormat.Sha256Hex(SubjectPublicKeyInfo);

    /// <summary>
    /// Reads a key from a PEM <c>PUBLIC KEY</c> block, DER bytes, or the base64 of DER. Refuses anything that is not a P-256 public key, and a private key in any form.
    /// </summary>
    /// <exception cref="FormatException">The material is not a P-256 public key.</exception>
    public static AuditKey Parse(string keyId, ReadOnlySpan<byte> material)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        byte[] der = ToDer(material);
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(der, out int read);
            if (read != der.Length)
            {
                throw new FormatException("The key has bytes after the public key.");
            }

            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
            if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7") // nistP256
            {
                throw new FormatException("The key is not an ECDSA P-256 key.");
            }
        }
        catch (CryptographicException ex)
        {
            throw new FormatException("The file is not a public key (a P-256 SubjectPublicKeyInfo, as PEM, DER or base64).", ex);
        }

        return new AuditKey(keyId, der);
    }

    private static byte[] ToDer(ReadOnlySpan<byte> material)
    {
        if (material.Length > 0 && material[0] == 0x30)
        {
            return material.ToArray(); // DER starts with a SEQUENCE
        }

        string text = Encoding.UTF8.GetString(material).Trim();
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new FormatException("That is a private key. Give the verifier the public key only.");
        }

        const string Begin = "-----BEGIN PUBLIC KEY-----";
        const string End = "-----END PUBLIC KEY-----";
        if (text.StartsWith(Begin, StringComparison.Ordinal))
        {
            int end = text.IndexOf(End, StringComparison.Ordinal);
            text = end < 0 ? throw new FormatException("The PEM block is not closed.") : text[Begin.Length..end];
        }

        try
        {
            return Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException ex)
        {
            throw new FormatException("The file is not a public key (a P-256 SubjectPublicKeyInfo, as PEM, DER or base64).", ex);
        }
    }
}

/// <summary>One check the verifier made: its name, whether it held, and one line about it that never contains a record's contents.</summary>
public sealed record AuditFinding(string Check, bool Passed, string Detail);

/// <summary>A record of a bundle as parsed (after the bundle verified): its position, kind and time, the run it names if any, and its members.</summary>
public sealed record AuditRecord(long Number, string Kind, DateTimeOffset At, Guid? Run, JsonElement Members);

/// <summary>What a manifest says (after its signature verified).</summary>
public sealed record AuditManifest(
    string ExportId, DateTimeOffset CreatedAt, Guid TenantId, Guid NamespaceId, DateTimeOffset? From, DateTimeOffset? To, IReadOnlyList<Guid> SelectedRuns,
    string RequestedBy, string Reason, string SignerKeyId, string SignerFingerprint, string SealedSections, string EvidenceFile, long EvidenceBytes, string EvidenceSha256,
    long RecordCount, string RecordHead, IReadOnlyDictionary<string, long> Counts, IReadOnlyList<AuditChainHead> Heads);

/// <summary>The last event of a run when the bundle was made.</summary>
public sealed record AuditChainHead(Guid Run, long Sequence, string Hash);

/// <summary>The result of verifying a bundle (and then, optionally, history files against it).</summary>
public sealed class AuditVerification
{
    private readonly List<AuditFinding> _findings = [];

    internal AuditVerification()
    {
    }

    /// <summary>Every check made, in order.</summary>
    public IReadOnlyList<AuditFinding> Findings => _findings;

    /// <summary>True when every check held and at least the signature was checked.</summary>
    public bool Passed => _findings.Count > 0 && _findings.All(f => f.Passed);

    /// <summary>The manifest, once its signature verified; null before that.</summary>
    public AuditManifest? Manifest { get; internal set; }

    /// <summary>True once the bundle itself verified (before any history file was checked against it).</summary>
    internal bool BundleVerified { get; set; }

    /// <summary>The records, once the bundle verified; empty before that.</summary>
    public IReadOnlyList<AuditRecord> Records { get; internal set; } = [];

    internal bool Add(string check, bool passed, string detail)
    {
        _findings.Add(new AuditFinding(check, passed, detail));
        return passed;
    }

    /// <summary>Records a check made by the caller of the verifier (for example that the files of a bundle belong together).</summary>
    public void AddFinding(string check, bool passed, string detail) => Add(check, passed, detail);
}

/// <summary>
/// Verifies a signed audit evidence bundle offline: the signature under a public key the caller supplies, the evidence against the manifest, every record's shape and link, and the chain heads. Then
/// history files (full or metadata only) against the recorded heads. Nothing here reads a payload, and no finding quotes a record.
/// </summary>
public static class AuditEvidenceVerifier
{
    private const int MaxRecordBytes = 64 * 1024;

    /// <summary>Verifies a bundle.</summary>
    /// <param name="manifest">The exact bytes of the manifest file.</param>
    /// <param name="evidence">The exact bytes of the evidence file.</param>
    /// <param name="signature">The text of the signature file.</param>
    /// <param name="key">The public key the caller trusts for this bundle.</param>
    /// <param name="requireOpened">Fail a bundle that was written without opening the sealed steps (it then says nothing about approvers, policy decisions or credentials).</param>
    public static AuditVerification Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> evidence, string signature, AuditKey key, bool requireOpened = false)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(key);
        var result = new AuditVerification();

        // 1. The signature, over the exact bytes of the manifest. Nothing in the manifest is believed before this holds.
        if (!CheckSignature(result, manifest, signature, key))
        {
            return result;
        }

        AuditManifest? parsed = ParseManifest(result, manifest, key);
        if (parsed is null)
        {
            return result;
        }

        result.Manifest = parsed;
        if (!CheckEvidenceAgainstManifest(result, parsed, evidence))
        {
            return result;
        }

        List<AuditRecord>? records = ParseRecords(result, parsed, evidence);
        if (records is null)
        {
            return result;
        }

        result.Records = records;
        CheckHeadsAndSelection(result, parsed, records);
        CheckSealedSections(result, parsed, records, requireOpened);
        result.BundleVerified = result.Passed;
        return result;
    }

    /// <summary>
    /// The <c>kid</c> a signature file names, for a caller that was not told which key identifier to expect. It is a label only: what is trusted is the public key the caller supplies, and
    /// <see cref="Verify"/> refuses a signature whose <c>kid</c> is not the one the key was given.
    /// </summary>
    public static string? KeyIdOf(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        string[] parts = signature.Trim().Split('.');
        if (parts.Length != 3 || AuditEvidenceFormat.FromBase64Url(parts[0]) is not { } header)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(header);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "kid") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- 1. signature -----------------------------------------------------------------------------------------

    private static bool CheckSignature(AuditVerification result, ReadOnlySpan<byte> manifest, string signature, AuditKey key)
    {
        string[] parts = signature.Trim().Split('.');
        if (parts.Length != 3 || parts[1].Length != 0)
        {
            return !result.Add("signature", false, "The signature file is not a detached compact signature (header, empty payload, signature).");
        }

        byte[]? header = AuditEvidenceFormat.FromBase64Url(parts[0]);
        byte[]? raw = AuditEvidenceFormat.FromBase64Url(parts[2]);
        if (header is null || raw is null || raw.Length != 64)
        {
            return !result.Add("signature", false, "The signature is malformed.");
        }

        string? alg = null, typ = null, kid = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(header, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return !result.Add("signature", false, "The signature header is not an object.");
            }

            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                string? value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                switch (p.Name)
                {
                    case "alg": alg = value; break;
                    case "typ": typ = value; break;
                    case "kid": kid = value; break;
                    default:
                        return !result.Add("signature", false, $"The signature header has a member '{Clip(p.Name)}' that this format does not use."); // jku, jwk, x5u, crit ...
                }
            }
        }
        catch (JsonException)
        {
            return !result.Add("signature", false, "The signature header is not JSON.");
        }

        if (alg != AuditEvidenceFormat.Algorithm)
        {
            return !result.Add("signature", false, $"The algorithm must be {AuditEvidenceFormat.Algorithm}; the signature names '{Clip(alg)}'.");
        }

        if (typ != AuditEvidenceFormat.SignatureType)
        {
            return !result.Add("signature", false, $"The signature type must be {AuditEvidenceFormat.SignatureType}.");
        }

        if (!string.Equals(kid, key.KeyId, StringComparison.Ordinal))
        {
            return !result.Add("signature", false, $"The signature was made with key '{Clip(kid)}', not the key '{Clip(key.KeyId)}' you supplied.");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.SubjectPublicKeyInfo, out _);
        byte[] signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + AuditEvidenceFormat.Base64Url(manifest));
        bool valid = ecdsa.VerifyData(signingInput, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return result.Add("signature", valid, valid
            ? $"The manifest is signed by key '{Clip(key.KeyId)}' (ES256)."
            : "The signature does not verify under the supplied key: the manifest was changed or was signed by another key.");
    }

    // ---- 2. manifest ------------------------------------------------------------------------------------------

    private static readonly string[] ManifestMembers =
        ["format", "exportId", "createdAt", "generator", "tenant", "namespace", "select", "requestedBy", "reason", "signer", "sealedSections", "evidence", "counts", "runs"];

    private static AuditManifest? ParseManifest(AuditVerification result, ReadOnlySpan<byte> manifest, AuditKey key)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(manifest.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement root = doc.RootElement;
            if (!HasExactly(root, ManifestMembers, out string? problem))
            {
                return Bad(result, "manifest", $"The manifest is not well formed ({problem}).");
            }

            if (Str(root, "format") != AuditEvidenceFormat.Version)
            {
                return Bad(result, "manifest", $"The manifest format is not {AuditEvidenceFormat.Version}.");
            }

            JsonElement select = root.GetProperty("select");
            JsonElement signer = root.GetProperty("signer");
            JsonElement evidence = root.GetProperty("evidence");
            if (!HasExactly(select, ["from", "to", "runs"], out problem) || !HasExactly(signer, ["kid", "publicKeySha256"], out problem) ||
                !HasExactly(evidence, ["file", "bytes", "sha256", "records", "head"], out problem))
            {
                return Bad(result, "manifest", $"The manifest is not well formed ({problem}).");
            }

            string signerKid = Str(signer, "kid") ?? "";
            string fingerprint = Str(signer, "publicKeySha256") ?? "";
            if (signerKid != key.KeyId || fingerprint != key.Fingerprint)
            {
                return Bad(result, "manifest", "The manifest names a different signing key than the one you supplied.");
            }

            var counts = new Dictionary<string, long>();
            JsonElement countsElement = root.GetProperty("counts");
            if (!HasExactly(countsElement, [.. AuditEvidenceFormat.Kinds], out problem))
            {
                return Bad(result, "manifest", $"The manifest counts are not well formed ({problem}).");
            }

            foreach (string kind in AuditEvidenceFormat.Kinds)
            {
                if (!Int(countsElement, kind, out long n) || n < 0)
                {
                    return Bad(result, "manifest", "A count is not a whole number.");
                }

                counts[kind] = n;
            }

            var heads = new List<AuditChainHead>();
            var seen = new HashSet<Guid>();
            foreach (JsonElement h in root.GetProperty("runs").EnumerateArray())
            {
                if (!HasExactly(h, ["run", "sequence", "hash"], out problem) || !TryGuid(h, "run", out Guid run) || !Int(h, "sequence", out long seq) || seq < 1 ||
                    Str(h, "hash") is not { Length: 64 } hash || !IsHex(hash) || !seen.Add(run))
                {
                    return Bad(result, "manifest", "A chain head in the manifest is not well formed, or a run is listed twice.");
                }

                heads.Add(new AuditChainHead(run, seq, hash));
            }

            var selected = new List<Guid>();
            foreach (JsonElement r in select.GetProperty("runs").EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String || !System.Guid.TryParseExact(r.GetString(), "D", out Guid g))
                {
                    return Bad(result, "manifest", "A selected run is not a run id.");
                }

                selected.Add(g);
            }

            if (!Time(root, "createdAt", out DateTimeOffset created) || !TryGuid(root, "tenant", out Guid tenant) || !TryGuid(root, "namespace", out Guid ns) ||
                !TimeOrNull(select, "from", out DateTimeOffset? from) || !TimeOrNull(select, "to", out DateTimeOffset? to) ||
                Str(root, "exportId") is not { Length: > 0 } exportId || Str(root, "requestedBy") is not { } requestedBy || Str(root, "reason") is not { } reason ||
                Str(root, "sealedSections") is not ({ } sealedSections) || (sealedSections != AuditEvidenceFormat.Opened && sealedSections != AuditEvidenceFormat.NotOpened) ||
                Str(evidence, "file") is not { Length: > 0 } file || file.Contains('/', StringComparison.Ordinal) || file.Contains('\\', StringComparison.Ordinal) ||
                !Int(evidence, "bytes", out long bytes) || !Int(evidence, "records", out long records) || Str(evidence, "sha256") is not { Length: 64 } sha || !IsHex(sha) ||
                Str(evidence, "head") is not { Length: 64 } head || !IsHex(head))
            {
                return Bad(result, "manifest", "The manifest has a value that is missing or not of the right kind.");
            }

            if (from is { } f && to is { } t && f >= t)
            {
                return Bad(result, "manifest", "The range is empty or reversed.");
            }

            result.Add("manifest", true, $"The manifest is well formed: export {Clip(exportId)}, {records} records, {heads.Count} runs.");
            return new AuditManifest(
                exportId, created, tenant, ns, from, to, selected, requestedBy, reason, signerKid, fingerprint, sealedSections, file, bytes, sha, records, head, counts, heads);
        }
        catch (JsonException)
        {
            return Bad(result, "manifest", "The manifest is not JSON.");
        }
        catch (InvalidOperationException)
        {
            return Bad(result, "manifest", "The manifest has a value that is not of the right kind.");
        }
    }

    // ---- 3. evidence against the manifest ---------------------------------------------------------------------

    private static bool CheckEvidenceAgainstManifest(AuditVerification result, AuditManifest m, ReadOnlySpan<byte> evidence)
    {
        if (evidence.Length != m.EvidenceBytes)
        {
            return !result.Add("evidence", false, $"The evidence file is {evidence.Length} bytes; the signed manifest says {m.EvidenceBytes}.");
        }

        if (AuditEvidenceFormat.Sha256Hex(evidence) != m.EvidenceSha256)
        {
            return !result.Add("evidence", false, "The evidence file is not the one the signed manifest names (its SHA-256 differs).");
        }

        return result.Add("evidence", true, $"The evidence file is the one the manifest names ({evidence.Length} bytes, SHA-256 matches).");
    }

    // ---- 4. records --------------------------------------------------------------------------------------------

    private static List<AuditRecord>? ParseRecords(AuditVerification result, AuditManifest m, ReadOnlySpan<byte> evidence)
    {
        var records = new List<AuditRecord>();
        var counts = AuditEvidenceFormat.Kinds.ToDictionary(k => k, _ => 0L);
        string prev = AuditEvidenceFormat.ZeroHash;
        int start = 0;
        long number = 0;
        while (start < evidence.Length)
        {
            int end = evidence[start..].IndexOf((byte)'\n');
            if (end < 0)
            {
                return Fail(result, "records", "The last line is not terminated by a newline.");
            }

            ReadOnlySpan<byte> line = evidence.Slice(start, end);
            start += end + 1;
            number++;
            if (line.Length == 0 || line.Length > MaxRecordBytes || line.Contains((byte)'\r'))
            {
                return Fail(result, "records", $"Line {number} is empty, too long, or ends with a carriage return.");
            }

            AuditRecord? record = ParseRecord(line, number, prev, m, out string? problem);
            if (record is null)
            {
                return Fail(result, "records", $"Record {number} is not valid: {problem}.");
            }

            counts[record.Kind]++;
            records.Add(record);
            prev = AuditEvidenceFormat.Sha256Hex(line);
        }

        if (number != m.RecordCount)
        {
            return Fail(result, "records", $"The evidence holds {number} records; the signed manifest says {m.RecordCount}.");
        }

        if (prev != m.RecordHead && !(number == 0 && m.RecordHead == AuditEvidenceFormat.ZeroHash))
        {
            return Fail(result, "records", "The last record is not the one the signed manifest names as the head of the chain.");
        }

        foreach (string kind in AuditEvidenceFormat.Kinds)
        {
            if (counts[kind] != m.Counts[kind])
            {
                return Fail(result, "records", $"The evidence holds {counts[kind]} '{kind}' records; the signed manifest says {m.Counts[kind]}.");
            }
        }

        result.Add("records", true, $"{number} records, numbered without gaps and each linked to the one before; the head matches the manifest.");
        return records;
    }

    private static AuditRecord? ParseRecord(ReadOnlySpan<byte> line, long number, string expectedPrev, AuditManifest m, out string? problem)
    {
        problem = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "kind") is not { } kind || !AuditEvidenceFormat.Fields.TryGetValue(kind, out (string Name, char Type)[]? fields))
            {
                problem = "unknown kind";
                return null;
            }

            string[] expected = ["n", "prev", "kind", "at", "tenant", "namespace", .. fields.Select(f => f.Name)];
            if (!HasExactly(root, expected, out string? shape))
            {
                problem = shape;
                return null;
            }

            if (!Int(root, "n", out long n) || n != number)
            {
                problem = "numbering has a gap or a repeat";
                return null;
            }

            if (Str(root, "prev") != expectedPrev)
            {
                problem = "it is not linked to the record before it";
                return null;
            }

            if (!Time(root, "at", out DateTimeOffset at) || !TryGuid(root, "tenant", out Guid tenant) || !TryGuid(root, "namespace", out Guid ns) || tenant != m.TenantId || ns != m.NamespaceId)
            {
                problem = "its time, tenant or namespace is wrong";
                return null;
            }

            foreach ((string name, char type) in fields)
            {
                if (!TypeOk(root.GetProperty(name), type, kind, name))
                {
                    problem = $"member '{name}' is not of the right kind";
                    return null;
                }
            }

            Guid? run = fields.Any(f => f.Name == "run") && root.GetProperty("run").ValueKind == JsonValueKind.String && TryGuid(root, "run", out Guid g) ? g : null;
            return new AuditRecord(number, kind, at, run, root.Clone());
        }
        catch (JsonException)
        {
            problem = "not JSON";
            return null;
        }
    }

    private static bool TypeOk(JsonElement e, char type, string kind, string name)
    {
        bool isNull = e.ValueKind == JsonValueKind.Null;
        switch (type)
        {
            case 'G': return TryGuid(e, out _);
            case 'g': return isNull || TryGuid(e, out _);
            case 'S': return e.ValueKind == JsonValueKind.String;
            case 's': return isNull || e.ValueKind == JsonValueKind.String;
            case 'T': return e.ValueKind == JsonValueKind.String && TimeText(e.GetString(), out _);
            case 't': return isNull || (e.ValueKind == JsonValueKind.String && TimeText(e.GetString(), out _));
            case 'I': return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) && v >= 0;
            case 'B': return e.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case 'H': return e.ValueKind == JsonValueKind.String && e.GetString() is { Length: 64 } h && IsHex(h);
            case 'h': return isNull || (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: 64 } h2 && IsHex(h2));
            case 'A': return e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String);
            case 'E':
                string[] set = name == "verdict" ? AuditEvidenceFormat.Verdicts : kind == AuditEvidenceFormat.KindApproval ? AuditEvidenceFormat.ApprovalOutcomes : AuditEvidenceFormat.RunOutcomes;
                return e.ValueKind == JsonValueKind.String && set.Contains(e.GetString());
            default: return false;
        }
    }

    // ---- 5. heads and selection --------------------------------------------------------------------------------

    private static void CheckHeadsAndSelection(AuditVerification result, AuditManifest m, List<AuditRecord> records)
    {
        var heads = m.Heads.ToDictionary(h => h.Run);
        var recorded = new Dictionary<Guid, (long Sequence, string Hash)>();
        bool ok = true;
        foreach (AuditRecord r in records.Where(r => r.Kind == AuditEvidenceFormat.KindChainHead))
        {
            Guid run = r.Run!.Value;
            long seq = r.Members.GetProperty("sequence").GetInt64();
            string hash = r.Members.GetProperty("hash").GetString()!;
            if (!recorded.TryAdd(run, (seq, hash)) || !heads.TryGetValue(run, out AuditChainHead? h) || h.Sequence != seq || h.Hash != hash)
            {
                ok = false;
            }
        }

        ok &= recorded.Count == heads.Count;
        result.Add("chain heads", ok, ok
            ? $"The {heads.Count} chain heads in the signed manifest are the ones in the records, one per run."
            : "The chain heads in the signed manifest are not the ones in the records (a run is missing, repeated, or has another head).");

        // Records about a run must be about a run the bundle covers; every 'run' record is that run's start.
        bool runsOk = true;
        var runRecords = new HashSet<Guid>();
        foreach (AuditRecord r in records)
        {
            bool needsHead = r.Kind is AuditEvidenceFormat.KindRun or AuditEvidenceFormat.KindApproval or AuditEvidenceFormat.KindPolicyDecision or AuditEvidenceFormat.KindCredential;
            if (needsHead && !heads.ContainsKey(r.Run!.Value))
            {
                runsOk = false;
            }

            if (r.Kind == AuditEvidenceFormat.KindRun && !runRecords.Add(r.Run!.Value))
            {
                runsOk = false;
            }
        }

        runsOk &= runRecords.Count == heads.Count;
        result.Add("runs", runsOk, runsOk
            ? "Every run has exactly one 'run' record and one chain head, and no record names a run the bundle does not cover."
            : "The runs in the bundle are inconsistent (a run without a start record or a head, a repeated start, or a record about another run).");

        // The range the manifest claims is the range it respects: a run started outside it is there only because it was named.
        bool rangeOk = true;
        var named = m.SelectedRuns.ToHashSet();
        foreach (AuditRecord r in records)
        {
            bool outside = (m.From is { } f && r.At < f) || (m.To is { } t && r.At >= t);
            if (r.Kind == AuditEvidenceFormat.KindRun && outside && !named.Contains(r.Run!.Value))
            {
                rangeOk = false;
            }

            if (r.Kind is AuditEvidenceFormat.KindHistoryExport or AuditEvidenceFormat.KindAdminAction && outside && !(r.Run is { } rr && (named.Contains(rr) || heads.ContainsKey(rr))))
            {
                rangeOk = false;
            }
        }

        result.Add("range", rangeOk, rangeOk
            ? "Everything in the bundle is inside the range the manifest states, or is about a run it covers."
            : "The bundle holds runs or audit entries outside the range the manifest states.");
    }

    private static void CheckSealedSections(AuditVerification result, AuditManifest m, List<AuditRecord> records, bool requireOpened)
    {
        bool opened = m.SealedSections == AuditEvidenceFormat.Opened;

        // An opened bundle reports what the sealed steps say, so none of its approvals is "sealed"; one that was not opened can say nothing that only they know, so all its approvals are.
        bool consistent = !records.Any(r =>
            (!opened && r.Kind is AuditEvidenceFormat.KindPolicyDecision or AuditEvidenceFormat.KindCredential) ||
            (r.Kind == AuditEvidenceFormat.KindApproval && (r.Members.GetProperty("outcome").GetString() == "sealed") == opened) ||
            (!opened && r.Kind == AuditEvidenceFormat.KindApproval && r.Members.GetProperty("approver").ValueKind != JsonValueKind.Null));
        result.Add("sealed sections", consistent && (opened || !requireOpened), !consistent
            ? "The manifest's statement about the sealed steps (opened or not) does not fit the records."
            : opened
                ? "The writer opened the sealed approval and tool steps: approvers, policy decisions and credentials are reported."
                : requireOpened
                    ? "The bundle was written without opening the sealed steps, so it does not report approvers, policy decisions or credentials; you asked for one that does."
                    : "The bundle was written without opening the sealed steps: it reports what is readable and makes no claim about approvers, policy decisions or credentials.");
    }

    // ---- history files ----------------------------------------------------------------------------------------

    /// <summary>
    /// Checks one history file (as <c>kairos history export</c> wrote it, full or metadata only) against a verified bundle: the file verifies, its run is one the bundle covers, it contains the recorded
    /// chain head, and its first event agrees with the bundle's start record. Adds its findings to <paramref name="verification"/>.
    /// </summary>
    /// <param name="verification">The result of <see cref="Verify"/>, which must have passed.</param>
    /// <param name="historyText">The text of the history file.</param>
    /// <param name="label">What to call the file in the findings (its name).</param>
    public static bool VerifyHistory(AuditVerification verification, string historyText, string label)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(historyText);
        string name = Clip(label);
        if (verification.Manifest is null || !verification.BundleVerified)
        {
            return verification.Add($"history {name}", false, "The bundle did not verify, so nothing can be checked against it.");
        }

        HistoryFileContents file;
        try
        {
            file = HistoryFile.ParseVerified(historyText);
        }
        catch (Exception ex) when (ex is FormatException or HistoryIntegrityException)
        {
            return verification.Add($"history {name}", false, "The file does not verify on its own (" + (ex is HistoryIntegrityException ? "its hash chain is broken" : "it is not a history file") + ").");
        }

        HistoryEvent first = file.History[0];
        AuditManifest m = verification.Manifest;
        string kind = file.MetadataOnly ? "metadata-only" : "full";
        if (first.TenantId != m.TenantId || first.NamespaceId != m.NamespaceId)
        {
            return verification.Add($"history {name}", false, "The file is from another tenant or namespace than the bundle.");
        }

        AuditChainHead? head = m.Heads.FirstOrDefault(h => h.Run == first.RunId);
        if (head is null)
        {
            return verification.Add($"history {name}", false, "The file is for a run the bundle does not cover.");
        }

        HistoryEvent? at = file.History.FirstOrDefault(e => e.Sequence == head.Sequence);
        if (at is null)
        {
            return verification.Add($"history {name}", false, $"The {kind} file ends at event {file.History[^1].Sequence}, before the recorded head (event {head.Sequence}): it is an earlier copy and cannot be tied to the signed head.");
        }

        if (Convert.ToHexStringLower(at.Hash) != head.Hash)
        {
            return verification.Add($"history {name}", false, $"The {kind} file has a different event {head.Sequence} than the recorded head: it is not the history the bundle vouches for.");
        }

        string? disagreement = null;
        AuditRecord? start = verification.Records.FirstOrDefault(r => r.Kind == AuditEvidenceFormat.KindRun && r.Run == first.RunId);
        if (start is not null && first is WorkflowStarted s)
        {
            JsonElement x = start.Members;
            if (Str(x, "workflowType") != s.WorkflowType || Str(x, "buildId") != s.BuildId || start.At != s.Timestamp.ToUniversalTime() ||
                (x.GetProperty("startedBy").GetString() ?? "") != s.StartedBy)
            {
                disagreement = "its first event disagrees with the bundle's start record (workflow type, build, time or starter)";
            }
        }
        else
        {
            disagreement = "its first event is not the start of the run";
        }

        if (disagreement is not null)
        {
            return verification.Add($"history {name}", false, $"The {kind} file verifies and contains the head, but {disagreement}.");
        }

        string headHex = Convert.ToHexStringLower(file.History[^1].Hash);
        AuditRecord? export = verification.Records.FirstOrDefault(r =>
            r.Kind == AuditEvidenceFormat.KindHistoryExport && r.Run == first.RunId && r.Members.GetProperty("head").GetString() == headHex);
        string traced = export is null
            ? " No export record in the bundle ends at this file's head."
            : $" It is the history of export record {export.Number}, made by {Clip(Str(export.Members, "actor"))}.";
        return verification.Add($"history {name}", true, $"The {kind} file verifies and contains the recorded head (event {head.Sequence}) of its run.{traced}");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static AuditManifest? Bad(AuditVerification result, string check, string detail)
    {
        result.Add(check, false, detail);
        return null;
    }

    private static List<AuditRecord>? Fail(AuditVerification result, string check, string detail)
    {
        result.Add(check, false, detail);
        return null;
    }

    private static bool HasExactly(JsonElement e, string[] names, out string? problem)
    {
        problem = null;
        if (e.ValueKind != JsonValueKind.Object)
        {
            problem = "not an object";
            return false;
        }

        int count = 0;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            count++;
            if (!names.Contains(p.Name))
            {
                problem = $"unexpected member '{Clip(p.Name)}'";
                return false;
            }
        }

        if (count != names.Length)
        {
            problem = "a member is missing";
            return false;
        }

        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Int(JsonElement e, string name, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
    }

    private static bool TryGuid(JsonElement e, string name, out Guid value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && TryGuid(v, out value);
    }

    private static bool TryGuid(JsonElement e, out Guid value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.String && System.Guid.TryParseExact(e.GetString(), "D", out value);
    }

    private static bool Time(JsonElement e, string name, out DateTimeOffset value)
    {
        value = default;
        return Str(e, name) is { } text && TimeText(text, out value);
    }

    private static bool TimeOrNull(JsonElement e, string name, out DateTimeOffset? value)
    {
        value = null;
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (Time(e, name, out DateTimeOffset t))
        {
            value = t;
            return true;
        }

        return false;
    }

    private static bool TimeText(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: 28 } && text.EndsWith('Z') &&
               DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static bool IsHex(string s) => s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Text from the bundle is shown only cut short and without control characters, so a hostile bundle cannot fill a terminal or forge a line of output.</summary>
    internal static string Clip(string? s)
    {
        if (s is null)
        {
            return "";
        }

        string clean = new([.. s.Select(c => char.IsControl(c) ? '?' : c)]);
        return clean.Length <= 80 ? clean : clean[..80] + "…";
    }
}
