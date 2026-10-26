using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kairos.Core.Tests;

/// <summary>
/// A test-only writer of audit evidence bundles. It is deliberately independent of the exporter that ships (which lives in the commercial repository): the verifier is tested against what this writes,
/// and the exporter is tested against the verifier, so the format is checked from both sides.
/// </summary>
internal sealed class AuditBundleBuilder
{
    public static readonly Guid Tenant = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Namespace = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    public static readonly DateTimeOffset T0 = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    public const string KeyId = "https://vault.example/keys/audit/0123456789abcdef";

    private static readonly string[] Finance = ["finance"];

    private static readonly string[] Scopes = ["issue_refund:read", "issue_refund:write"];

    public ECDsa SigningKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public List<string> Lines { get; } = [];

    public List<(Guid Run, long Sequence, string Hash)> Heads { get; } = [];

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public List<Guid> SelectedRuns { get; } = [];

    public string SealedSections { get; set; } = AuditEvidenceFormat.Opened;

    public string ExportId { get; set; } = "test-export-1";

    public string Kid { get; set; } = KeyId;

    public byte[] PublicKey => SigningKey.ExportSubjectPublicKeyInfo();

    public AuditKey Key => AuditKey.Parse(Kid, PublicKey);

    private string _prev = AuditEvidenceFormat.ZeroHash;

    // ---- records ----------------------------------------------------------------------------------------------

    public AuditBundleBuilder Add(string kind, DateTimeOffset at, params (string Name, object? Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("n", Lines.Count + 1);
            w.WriteString("prev", _prev);
            w.WriteString("kind", kind);
            w.WriteString("at", AuditEvidenceFormat.FormatTime(at));
            w.WriteString("tenant", Tenant.ToString("D"));
            w.WriteString("namespace", Namespace.ToString("D"));
            foreach ((string name, object? value) in fields)
            {
                w.WritePropertyName(name);
                Write(w, value);
            }

            w.WriteEndObject();
        }

        string line = Encoding.UTF8.GetString(stream.ToArray());
        Lines.Add(line);
        _prev = AuditEvidenceFormat.Sha256Hex(Encoding.UTF8.GetBytes(line));
        return this;
    }

    private static void Write(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case Guid g: w.WriteStringValue(g.ToString("D")); break;
            case DateTimeOffset t: w.WriteStringValue(AuditEvidenceFormat.FormatTime(t)); break;
            case string[] a:
                w.WriteStartArray();
                foreach (string x in a)
                {
                    w.WriteStringValue(x);
                }

                w.WriteEndArray();
                break;
            default: throw new ArgumentException(value.GetType().Name);
        }
    }

    /// <summary>Adds a run's start, chain head and the records for a small agent run, from a real sealed history.</summary>
    public AuditBundleBuilder AddRun(IReadOnlyList<HistoryEvent> history, string? startedBy)
    {
        var started = (WorkflowStarted)history[0];
        Guid run = started.RunId;
        Add(AuditEvidenceFormat.KindRun, started.Timestamp, ("run", run), ("workflowType", started.WorkflowType), ("buildId", started.BuildId), ("startedBy", startedBy), ("previousRun", null));
        Add(AuditEvidenceFormat.KindApproval, started.Timestamp.AddSeconds(2),
            ("run", run), ("approvalId", "ap-" + run.ToString("N")[..8]), ("tool", "issue_refund"), ("requestedAt", started.Timestamp.AddSeconds(1)), ("decidedAt", started.Timestamp.AddSeconds(2)),
            ("outcome", "approved"), ("approver", "olivia@example.test"), ("roles", Finance), ("refused", 0));
        Add(AuditEvidenceFormat.KindPolicyDecision, started.Timestamp.AddSeconds(1), ("run", run), ("step", 1), ("tool", "issue_refund"), ("verdict", "require_approval"), ("reason", "refunds over 100 need a person"),
            ("rule", "refunds-large"), ("policyVersion", 3), ("policyHash", new string('c', 64)));
        Add(AuditEvidenceFormat.KindCredential, started.Timestamp.AddSeconds(3), ("run", run), ("step", 2), ("tool", "issue_refund"), ("scopes", Scopes),
            ("expiresAt", started.Timestamp.AddSeconds(303)), ("onBehalfOf", "customer-77"));
        HistoryEvent last = history[^1];
        Add(AuditEvidenceFormat.KindChainHead, last.Timestamp, ("run", run), ("sequence", last.Sequence), ("hash", Convert.ToHexStringLower(last.Hash)), ("outcome", AuditEvidenceFormat.OutcomeOf(last)));
        Heads.Add((run, last.Sequence, Convert.ToHexStringLower(last.Hash)));
        return this;
    }

    // ---- the bundle -------------------------------------------------------------------------------------------

    /// <summary>Makes every record's link right again after a record was edited, as a writer that signs what it wrote would have.</summary>
    public void Relink()
    {
        string prev = AuditEvidenceFormat.ZeroHash;
        for (int i = 0; i < Lines.Count; i++)
        {
            int at = Lines[i].IndexOf("\"prev\":\"", StringComparison.Ordinal);
            if (at >= 0 && Lines[i].Length >= at + 8 + 64)
            {
                Lines[i] = Lines[i][..(at + 8)] + prev + Lines[i][(at + 8 + 64)..];
            }

            prev = AuditEvidenceFormat.Sha256Hex(Encoding.UTF8.GetBytes(Lines[i]));
        }
    }

    public byte[] Evidence => Encoding.UTF8.GetBytes(string.Concat(Lines.Select(l => l + "\n")));

    public byte[] Manifest(Func<string, string>? edit = null)
    {
        byte[] evidence = Evidence;
        var counts = AuditEvidenceFormat.Kinds.ToDictionary(k => k, k => Lines.Count(l => l.Contains($"\"kind\":\"{k}\"", StringComparison.Ordinal)));
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("format", AuditEvidenceFormat.Version);
            w.WriteString("exportId", ExportId);
            w.WriteString("createdAt", AuditEvidenceFormat.FormatTime(T0.AddHours(2)));
            w.WriteString("generator", "test-writer/1");
            w.WriteString("tenant", Tenant.ToString("D"));
            w.WriteString("namespace", Namespace.ToString("D"));
            w.WriteStartObject("select");
            if (From is { } f) { w.WriteString("from", AuditEvidenceFormat.FormatTime(f)); } else { w.WriteNull("from"); }
            if (To is { } t) { w.WriteString("to", AuditEvidenceFormat.FormatTime(t)); } else { w.WriteNull("to"); }
            w.WriteStartArray("runs");
            foreach (Guid r in SelectedRuns)
            {
                w.WriteStringValue(r.ToString("D"));
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteString("requestedBy", "Admin:auditor-bot");
            w.WriteString("reason", "quarterly review");
            w.WriteStartObject("signer");
            w.WriteString("kid", Kid);
            w.WriteString("publicKeySha256", AuditEvidenceFormat.Sha256Hex(PublicKey));
            w.WriteEndObject();
            w.WriteString("sealedSections", SealedSections);
            w.WriteStartObject("evidence");
            w.WriteString("file", "audit-test.jsonl");
            w.WriteNumber("bytes", evidence.Length);
            w.WriteString("sha256", AuditEvidenceFormat.Sha256Hex(evidence));
            w.WriteNumber("records", Lines.Count);
            w.WriteString("head", Lines.Count == 0 ? AuditEvidenceFormat.ZeroHash : AuditEvidenceFormat.Sha256Hex(Encoding.UTF8.GetBytes(Lines[^1])));
            w.WriteEndObject();
            w.WriteStartObject("counts");
            foreach (string kind in AuditEvidenceFormat.Kinds)
            {
                w.WriteNumber(kind, counts[kind]);
            }

            w.WriteEndObject();
            w.WriteStartArray("runs");
            foreach ((Guid run, long seq, string hash) in Heads)
            {
                w.WriteStartObject();
                w.WriteString("run", run.ToString("D"));
                w.WriteNumber("sequence", seq);
                w.WriteString("hash", hash);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        string text = Encoding.UTF8.GetString(stream.ToArray());
        return Encoding.UTF8.GetBytes(edit is null ? text : edit(text));
    }

    /// <summary>A detached compact ES256 signature over <paramref name="manifest"/>.</summary>
    public string Sign(byte[] manifest, string? header = null)
    {
        string h = AuditEvidenceFormat.Base64Url(Encoding.UTF8.GetBytes(header ?? $"{{\"alg\":\"ES256\",\"kid\":\"{Kid}\",\"typ\":\"{AuditEvidenceFormat.SignatureType}\"}}"));
        byte[] input = Encoding.ASCII.GetBytes(h + "." + AuditEvidenceFormat.Base64Url(manifest));
        byte[] signature = SigningKey.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return h + ".." + AuditEvidenceFormat.Base64Url(signature);
    }

    public AuditVerification Verify(bool requireOpened = false)
    {
        byte[] manifest = Manifest();
        return AuditEvidenceVerifier.Verify(manifest, Evidence, Sign(manifest), Key, requireOpened);
    }

    // ---- histories --------------------------------------------------------------------------------------------

    /// <summary>A small closed history for the run, chained from the genesis.</summary>
    public static List<HistoryEvent> History(Guid run, string startedBy, int tasks = 2, DateTimeOffset? start = null, Guid? tenant = null)
    {
        DateTimeOffset t = start ?? T0;
        var events = new List<HistoryEvent> { new WorkflowStarted(tenant ?? Tenant, Namespace, run, 1, t, "refunds", "b-1", [9, 9, 9]) { StartedBy = startedBy } };
        for (int i = 0; i < tasks; i++)
        {
            events.Add(new WorkflowTaskCompleted(tenant ?? Tenant, Namespace, run, events.Count + 1, t.AddSeconds(i + 1)));
        }

        events.Add(new WorkflowCompleted(tenant ?? Tenant, Namespace, run, events.Count + 1, t.AddSeconds(tasks + 1), [7]));
        var sealedHistory = new List<HistoryEvent>();
        byte[] prev = HashChain.GenesisHash.ToArray();
        foreach (HistoryEvent e in events)
        {
            HistoryEvent s = HashChain.Seal(e, prev);
            prev = s.Hash;
            sealedHistory.Add(s);
        }

        return sealedHistory;
    }
}
