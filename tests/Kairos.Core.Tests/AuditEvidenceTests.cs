using System.Security.Cryptography;
using System.Text;

namespace Kairos.Core.Tests;

/// <summary>
/// The offline verifier: a good bundle passes; every change a reader could make to any file, any key or header other than the expected, and every record-level defect a careless writer could introduce fails,
/// and history files, full or metadata only, are tied to the recorded chain heads.
/// </summary>
public sealed class AuditEvidenceTests
{
    private static readonly Guid RunA = Guid.Parse("b0000000-0000-0000-0000-00000000000a");
    private static readonly Guid RunB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private static (AuditBundleBuilder Builder, List<HistoryEvent> A, List<HistoryEvent> B) Good()
    {
        var b = new AuditBundleBuilder();
        List<HistoryEvent> a = AuditBundleBuilder.History(RunA, "Operator:ci", start: AuditBundleBuilder.T0);
        List<HistoryEvent> bb = AuditBundleBuilder.History(RunB, "Admin:amy", start: AuditBundleBuilder.T0.AddMinutes(5));
        b.AddRun(a, "Operator:ci").AddRun(bb, "Admin:amy");
        b.Add(AuditEvidenceFormat.KindHistoryExport, AuditBundleBuilder.T0.AddHours(1),
            ("run", RunA), ("actor", "Operator:ci"), ("reason", "audit evidence export test-export-1"), ("metadataOnly", false), ("head", Convert.ToHexStringLower(a[^1].Hash)));
        b.Add(AuditEvidenceFormat.KindAdminAction, AuditBundleBuilder.T0.AddHours(1),
            ("run", RunB), ("action", "CancelRun"), ("actor", "Admin:amy"), ("detail", "stop"), ("sessionUser", "kairos_dataplane"), ("clientAddress", "10.0.0.7"), ("claimedActor", null));
        return (b, a, bb);
    }

    private static string FileOf(IReadOnlyList<HistoryEvent> history, bool metadataOnly) =>
        metadataOnly ? HistoryFile.SerializeMetadataOnly(history, "test") : HistoryFile.Serialize(history, "test");

    // ---- a good bundle -----------------------------------------------------------------------------------------

    [Fact]
    public void A_good_bundle_verifies_and_every_check_is_named()
    {
        AuditVerification result = Good().Builder.Verify();

        Assert.True(result.Passed, string.Join("\n", result.Findings.Where(f => !f.Passed).Select(f => f.Detail)));
        Assert.Equal(["signature", "manifest", "evidence", "records", "chain heads", "runs", "range", "sealed sections"], result.Findings.Select(f => f.Check));
        Assert.Equal(2, result.Manifest!.Heads.Count);
        Assert.Equal(10, result.Manifest.RecordCount - 2); // two runs of five records, two audit records
    }

    [Fact]
    public void An_empty_export_is_a_valid_export()
    {
        var b = new AuditBundleBuilder();

        Assert.True(b.Verify().Passed);
    }

    // ---- tampering with the files themselves --------------------------------------------------------------------

    [Fact]
    public void Changing_any_byte_of_the_manifest_the_evidence_or_the_signature_fails()
    {
        (AuditBundleBuilder b, _, _) = Good();
        byte[] manifest = b.Manifest();
        byte[] evidence = b.Evidence;
        string signature = b.Sign(manifest);
        AuditKey key = b.Key;
        Assert.True(AuditEvidenceVerifier.Verify(manifest, evidence, signature, key).Passed);

        for (int i = 0; i < manifest.Length; i++)
        {
            byte[] changed = [.. manifest];
            changed[i] ^= 0x01;
            Assert.False(AuditEvidenceVerifier.Verify(changed, evidence, signature, key).Passed, $"manifest byte {i}");
        }

        for (int i = 0; i < evidence.Length; i++)
        {
            byte[] changed = [.. evidence];
            changed[i] ^= 0x01;
            Assert.False(AuditEvidenceVerifier.Verify(manifest, changed, signature, key).Passed, $"evidence byte {i}");
        }

        for (int i = 0; i < signature.Length; i++)
        {
            string changed = signature[..i] + (signature[i] == 'A' ? 'B' : 'A') + signature[(i + 1)..];
            if (changed == signature)
            {
                continue;
            }

            Assert.False(AuditEvidenceVerifier.Verify(manifest, evidence, changed, key).Passed, $"signature character {i}");
        }

        Assert.False(AuditEvidenceVerifier.Verify(manifest, evidence.AsSpan()[..^1], signature, key).Passed); // truncated
        Assert.False(AuditEvidenceVerifier.Verify(manifest, [.. evidence, (byte)'\n'], signature, key).Passed); // extended
    }

    [Fact]
    public void Another_key_with_the_right_name_fails_and_the_right_key_with_another_name_fails()
    {
        (AuditBundleBuilder b, _, _) = Good();
        byte[] manifest = b.Manifest();
        string signature = b.Sign(manifest);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        AuditVerification wrongKey = AuditEvidenceVerifier.Verify(manifest, b.Evidence, signature, AuditKey.Parse(AuditBundleBuilder.KeyId, other.ExportSubjectPublicKeyInfo()));
        AuditVerification wrongName = AuditEvidenceVerifier.Verify(manifest, b.Evidence, signature, AuditKey.Parse("another-kid", b.PublicKey));

        Assert.False(wrongKey.Passed);
        Assert.False(wrongName.Passed);
        Assert.Null(wrongKey.Manifest); // nothing in the manifest is believed before the signature holds
    }

    [Theory]
    [InlineData("{\"alg\":\"none\",\"kid\":\"{kid}\",\"typ\":\"kairos-audit+jws\"}")]
    [InlineData("{\"alg\":\"HS256\",\"kid\":\"{kid}\",\"typ\":\"kairos-audit+jws\"}")]
    [InlineData("{\"alg\":\"ES384\",\"kid\":\"{kid}\",\"typ\":\"kairos-audit+jws\"}")]
    [InlineData("{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"JWT\"}")]
    [InlineData("{\"alg\":\"ES256\",\"kid\":\"{kid}\"}")]
    [InlineData("{\"alg\":\"ES256\",\"typ\":\"kairos-audit+jws\"}")]
    [InlineData("{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"kairos-audit+jws\",\"jwk\":{\"kty\":\"EC\"}}")]
    [InlineData("{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"kairos-audit+jws\",\"crit\":[\"b64\"],\"b64\":false}")]
    [InlineData("{\"alg\":\"ES256\",\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"kairos-audit+jws\"}")]
    [InlineData("[]")]
    public void A_signature_header_other_than_the_one_expected_is_refused_even_when_the_signature_is_otherwise_valid(string header)
    {
        (AuditBundleBuilder b, _, _) = Good();
        byte[] manifest = b.Manifest();
        string signature = b.Sign(manifest, header.Replace("{kid}", AuditBundleBuilder.KeyId, StringComparison.Ordinal));

        Assert.False(AuditEvidenceVerifier.Verify(manifest, b.Evidence, signature, b.Key).Passed);
    }

    [Fact]
    public void A_signature_that_carries_its_own_payload_or_is_not_compact_is_refused()
    {
        (AuditBundleBuilder b, _, _) = Good();
        byte[] manifest = b.Manifest();
        string detached = b.Sign(manifest);
        string[] parts = detached.Split('.');

        Assert.False(AuditEvidenceVerifier.Verify(manifest, b.Evidence, parts[0] + "." + AuditEvidenceFormat.Base64Url(manifest) + "." + parts[2], b.Key).Passed);
        Assert.False(AuditEvidenceVerifier.Verify(manifest, b.Evidence, parts[0] + "." + parts[2], b.Key).Passed);
        Assert.False(AuditEvidenceVerifier.Verify(manifest, b.Evidence, "", b.Key).Passed);
        Assert.False(AuditEvidenceVerifier.Verify(manifest, b.Evidence, parts[0] + ".." + parts[2][..^2], b.Key).Passed);
        Assert.True(AuditEvidenceVerifier.Verify(manifest, b.Evidence, detached + "\n", b.Key).Passed); // a trailing newline in the file is not a change
    }

    // ---- a signed bundle that is still wrong (a careless writer, or a writer with the key) ------------------------------

    private static AuditVerification VerifySigned(AuditBundleBuilder b, Func<string, string>? editManifest = null, bool requireOpened = false)
    {
        byte[] manifest = b.Manifest(editManifest);
        return AuditEvidenceVerifier.Verify(manifest, b.Evidence, b.Sign(manifest), b.Key, requireOpened);
    }

    private static string FailedCheck(AuditVerification v) => v.Findings.First(f => !f.Passed).Check;

    [Fact]
    public void Records_that_are_deleted_duplicated_reordered_or_unlinked_fail_even_when_the_manifest_is_signed_over_them()
    {
        (AuditBundleBuilder b, _, _) = Good();
        List<string> lines = [.. b.Lines];

        void Mutate(Action<List<string>> change, string description)
        {
            (AuditBundleBuilder fresh, _, _) = Good();
            fresh.Lines.Clear();
            fresh.Lines.AddRange(lines);
            change(fresh.Lines);
            // The writer signs a manifest that matches the file it wrote, so only the records themselves can show the defect.
            AuditVerification v = VerifySigned(fresh, m => m);
            Assert.False(v.Passed, description);
        }

        Mutate(l => l.RemoveAt(3), "a record deleted");
        Mutate(l => l.Insert(3, l[3]), "a record duplicated");
        Mutate(l => (l[2], l[3]) = (l[3], l[2]), "two records swapped");
        Mutate(l => l.RemoveAt(9), "the chain head of the second run deleted");
        Mutate(l => l[4] = l[4].Replace("\"prev\":\"" + l[4].Split('"')[5], "\"prev\":\"" + new string('1', 64), StringComparison.Ordinal), "a record with another predecessor");
    }

    [Fact]
    public void A_record_of_the_wrong_shape_fails()
    {
        void Case(Func<string, string> edit, string description)
        {
            var b = new AuditBundleBuilder();
            b.AddRun(AuditBundleBuilder.History(RunA, "Operator:ci"), "Operator:ci");
            b.Lines[1] = edit(b.Lines[1]);
            b.Relink(); // the links and the manifest are made right again, so only the edit itself can be what fails

            Assert.False(VerifySigned(b).Passed, description);
        }

        Case(l => l.Replace("\"kind\":\"approval\"", "\"kind\":\"approvals\"", StringComparison.Ordinal), "an unknown kind");
        Case(l => l.Replace("\"outcome\":\"approved\"", "\"outcome\":\"maybe\"", StringComparison.Ordinal), "an outcome that is not one of the set");
        Case(l => l.Replace(",\"refused\":0", "", StringComparison.Ordinal), "a missing member");
        Case(l => l.Replace("\"refused\":0", "\"refused\":0,\"payload\":\"x\"", StringComparison.Ordinal), "an extra member");
        Case(l => l.Replace("\"refused\":0", "\"refused\":\"0\"", StringComparison.Ordinal), "a number written as text");
        Case(l => l.Replace("\"refused\":0", "\"refused\":-1", StringComparison.Ordinal), "a negative count");
        Case(l => l.Replace("\"n\":2", "\"n\":3", StringComparison.Ordinal), "a wrong record number");
        Case(l => l.Replace("a0000000-0000-0000-0000-000000000001", "a0000000-0000-0000-0000-000000000002", StringComparison.Ordinal), "another tenant");
        Case(l => l.Replace("\"at\":\"2026", "\"at\":\"2026-", StringComparison.Ordinal), "a time that is not the one spelling");
        Case(l => l.Replace("\"roles\":[\"finance\"]", "\"roles\":[1]", StringComparison.Ordinal), "a list of something else");
        Case(l => l.Replace("{\"n\"", "{\"n\":2,\"n\"", StringComparison.Ordinal), "a repeated member");
        Case(l => l.Replace("\"refused\":0", "\"n\":2", StringComparison.Ordinal), "a repeated member that stands in for a missing one (same count of members)");
        Case(l => l.Replace("\"requestedAt\":\"", "\"requestedAt\":\"x", StringComparison.Ordinal), "a time member that is not a time");
        Case(l => "[" + l + "]", "a line that is not an object");
        Case(l => "", "an empty line");
    }

    [Fact]
    public void A_manifest_that_disagrees_with_the_records_fails_even_when_signed()
    {
        Assert.Equal("records", FailedCheck(VerifySigned(Good().Builder, m => m.Replace("\"records\":12", "\"records\":13", StringComparison.Ordinal))));
        Assert.Equal("records", FailedCheck(VerifySigned(Good().Builder, m => m.Replace("\"run\":2", "\"run\":3", StringComparison.Ordinal))));
        Assert.Equal("evidence", FailedCheck(VerifySigned(Good().Builder, m => m.Replace("\"bytes\":", "\"bytes\":1", StringComparison.Ordinal))));
        Assert.Contains(VerifySigned(Good().Builder, m => m.Replace("\"format\":\"kairos-audit-evidence/1\"", "\"format\":\"kairos-audit-evidence/2\"", StringComparison.Ordinal)).Findings, f => f is { Check: "manifest", Passed: false });
        Assert.Contains(VerifySigned(Good().Builder, m => m.Replace("\"generator\"", "\"generated\"", StringComparison.Ordinal)).Findings, f => f is { Check: "manifest", Passed: false });
        Assert.Contains(VerifySigned(Good().Builder, m => m.Replace("\"sealedSections\":\"opened\"", "\"sealedSections\":\"maybe\"", StringComparison.Ordinal)).Findings, f => f is { Check: "manifest", Passed: false });
        Assert.Contains(VerifySigned(Good().Builder, m => m.Replace("\"file\":\"audit-test.jsonl\"", "\"file\":\"../audit-test.jsonl\"", StringComparison.Ordinal)).Findings, f => f is { Check: "manifest", Passed: false });
    }

    [Fact]
    public void A_manifest_whose_record_head_is_not_the_last_record_fails_even_when_the_file_digest_is_right()
    {
        (AuditBundleBuilder b, _, _) = Good();
        byte[] real = b.Manifest();
        string text = Encoding.UTF8.GetString(real);
        int at = text.IndexOf("\"head\":\"", StringComparison.Ordinal) + 8;
        string wrong = text[..at] + new string('e', 64) + text[(at + 64)..];

        AuditVerification v = VerifySigned(b, _ => wrong);

        Assert.Equal("records", FailedCheck(v));
        Assert.Contains("head of the chain", v.Findings[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chain_head_in_the_manifest_that_is_not_the_one_in_the_records_fails()
    {
        (AuditBundleBuilder b, _, _) = Good();
        b.Heads[0] = (b.Heads[0].Run, b.Heads[0].Sequence, new string('a', 64));
        Assert.Equal("chain heads", FailedCheck(VerifySigned(b)));

        (AuditBundleBuilder c, _, _) = Good();
        c.Heads.RemoveAt(1);
        Assert.Equal("chain heads", FailedCheck(VerifySigned(c)));

        (AuditBundleBuilder d, _, _) = Good();
        d.Heads.Add((Guid.NewGuid(), 4, new string('b', 64)));
        Assert.Equal("chain heads", FailedCheck(VerifySigned(d)));
    }

    [Fact]
    public void A_record_about_a_run_the_bundle_does_not_cover_fails()
    {
        var b = new AuditBundleBuilder();
        b.AddRun(AuditBundleBuilder.History(RunA, "Operator:ci"), "Operator:ci");
        b.Add(AuditEvidenceFormat.KindApproval, AuditBundleBuilder.T0,
            ("run", RunB), ("approvalId", "x"), ("tool", null), ("requestedAt", null), ("decidedAt", null), ("outcome", "unresolved"), ("approver", null), ("roles", Array.Empty<string>()), ("refused", 0));

        Assert.Equal("runs", FailedCheck(VerifySigned(b)));
    }

    [Fact]
    public void A_bundle_holds_only_what_its_range_allows()
    {
        (AuditBundleBuilder b, _, _) = Good();
        b.From = AuditBundleBuilder.T0.AddMinutes(-1);
        b.To = AuditBundleBuilder.T0.AddHours(2);
        Assert.True(VerifySigned(b).Passed);

        b.To = AuditBundleBuilder.T0.AddMinutes(3); // run B started at +5 and is now outside the range
        Assert.Equal("range", FailedCheck(VerifySigned(b)));

        b.SelectedRuns.Add(RunB); // naming the run explains it; the entries at +60 are about runs the bundle covers, so they are allowed
        Assert.True(VerifySigned(b).Passed);

        // An audit entry that is outside the range and about no covered run is not.
        b.Add(AuditEvidenceFormat.KindAdminAction, AuditBundleBuilder.T0.AddHours(1),
            ("run", null), ("action", "ResumeWorkflowTask"), ("actor", "Admin:amy"), ("detail", "x"), ("sessionUser", null), ("clientAddress", null), ("claimedActor", null));
        Assert.Equal("range", FailedCheck(VerifySigned(b)));
        b.Lines.RemoveAt(b.Lines.Count - 1);

        b.Add(AuditEvidenceFormat.KindAdminAction, AuditBundleBuilder.T0.AddMinutes(1),
            ("run", null), ("action", "ResumeWorkflowTask"), ("actor", "Admin:amy"), ("detail", "x"), ("sessionUser", null), ("clientAddress", null), ("claimedActor", null));
        b.Relink();
        Assert.True(VerifySigned(b).Passed); // inside the range, about no run in particular
    }

    [Fact]
    public void A_bundle_written_without_opening_the_sealed_steps_cannot_claim_what_only_they_say()
    {
        (AuditBundleBuilder b, _, _) = Good();
        b.SealedSections = AuditEvidenceFormat.NotOpened;
        Assert.Equal("sealed sections", FailedCheck(VerifySigned(b))); // it contains policy decisions and an approver

        var honest = new AuditBundleBuilder { SealedSections = AuditEvidenceFormat.NotOpened };
        honest.Add(AuditEvidenceFormat.KindRun, AuditBundleBuilder.T0, ("run", RunA), ("workflowType", "w"), ("buildId", "b"), ("startedBy", null), ("previousRun", null));
        honest.Add(AuditEvidenceFormat.KindApproval, AuditBundleBuilder.T0,
            ("run", RunA), ("approvalId", "a"), ("tool", null), ("requestedAt", null), ("decidedAt", null), ("outcome", "sealed"), ("approver", null), ("roles", Array.Empty<string>()), ("refused", 0));
        honest.Add(AuditEvidenceFormat.KindChainHead, AuditBundleBuilder.T0, ("run", RunA), ("sequence", 1), ("hash", new string('c', 64)), ("outcome", "open"));
        honest.Heads.Add((RunA, 1, new string('c', 64)));
        Assert.True(VerifySigned(honest).Passed);
        Assert.False(VerifySigned(honest, requireOpened: true).Passed);
        Assert.True(VerifySigned(Good().Builder, requireOpened: true).Passed);
    }

    // ---- findings never quote the bundle -----------------------------------------------------------------------------

    [Fact]
    public void Findings_do_not_repeat_what_a_hostile_bundle_says()
    {
        var b = new AuditBundleBuilder { Kid = "evil\u001b[31m-key-" + new string('x', 500) };
        byte[] manifest = b.Manifest();
        AuditVerification v = AuditEvidenceVerifier.Verify(manifest, b.Evidence, b.Sign(manifest), AuditKey.Parse("expected", b.PublicKey));

        Assert.False(v.Passed);
        string text = string.Join("\n", v.Findings.Select(f => f.Detail));
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.True(text.Length < 400);
    }

    // ---- history files against the recorded heads ----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_history_file_full_or_metadata_only_is_tied_to_the_recorded_head(bool metadataOnly)
    {
        (AuditBundleBuilder b, List<HistoryEvent> a, List<HistoryEvent> bb) = Good();
        AuditVerification v = b.Verify();

        Assert.True(AuditEvidenceVerifier.VerifyHistory(v, FileOf(a, metadataOnly), "a.khist"));
        Assert.True(AuditEvidenceVerifier.VerifyHistory(v, FileOf(bb, metadataOnly), "b.khist"));
        Assert.True(v.Passed);
        Assert.Contains("export record", v.Findings.First(f => f.Check == "history a.khist").Detail, StringComparison.Ordinal); // run A has an export record ending at that head
        Assert.Contains("No export record", v.Findings.First(f => f.Check == "history b.khist").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_history_file_that_is_not_the_recorded_history_fails_and_says_why()
    {
        (AuditBundleBuilder b, List<HistoryEvent> a, _) = Good();
        AuditVerification v = b.Verify();

        // An earlier copy: the file ends before the recorded head.
        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(a.Take(2).ToList(), false), "early.khist"));
        Assert.Contains("before the recorded head", v.Findings[^1].Detail, StringComparison.Ordinal);

        // A fork: the same run, a different event 4.
        var forked = new List<HistoryEvent>(a.Take(3));
        forked.Add(HashChain.Seal(new WorkflowCompleted(AuditBundleBuilder.Tenant, AuditBundleBuilder.Namespace, RunA, 4, a[3].Timestamp, [8]), forked[^1].Hash));
        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(forked, false), "fork.khist"));
        Assert.Contains("different event", v.Findings[^1].Detail, StringComparison.Ordinal);

        // A run the bundle does not cover.
        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(AuditBundleBuilder.History(Guid.NewGuid(), "Operator:ci"), false), "other.khist"));
        Assert.Contains("does not cover", v.Findings[^1].Detail, StringComparison.Ordinal);

        // Not a history file at all, and a file edited after export.
        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, "hello", "junk.khist"));
        string text = FileOf(a, false);
        int at = text.LastIndexOf("event: ", StringComparison.Ordinal) + 7;
        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, text[..at] + (text[at] == 'A' ? 'B' : 'A') + text[(at + 1)..], "edited.khist"));
    }

    [Fact]
    public void A_history_from_another_tenant_fails_even_with_the_same_run_id()
    {
        (AuditBundleBuilder b, List<HistoryEvent> a, _) = Good();
        AuditVerification v = b.Verify();
        List<HistoryEvent> foreign = AuditBundleBuilder.History(RunA, "Operator:ci", start: AuditBundleBuilder.T0, tenant: Guid.Parse("f0000000-0000-0000-0000-000000000009"));

        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(foreign, false), "foreign.khist"));
        Assert.Contains("another tenant", v.Findings[^1].Detail, StringComparison.Ordinal);
        Assert.True(AuditEvidenceVerifier.VerifyHistory(v, FileOf(a, false), "own.khist"));
    }

    [Fact]
    public void A_history_whose_start_disagrees_with_the_start_record_fails()
    {
        var b = new AuditBundleBuilder();
        List<HistoryEvent> history = AuditBundleBuilder.History(RunA, "Operator:ci");
        b.AddRun(history, "Admin:someone-else"); // the bundle says another person started it
        AuditVerification v = b.Verify();
        Assert.True(v.Passed);

        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(history, true), "a.khist"));
        Assert.Contains("starter", v.Findings[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void History_files_are_not_checked_against_a_bundle_that_failed_only_at_the_last_check()
    {
        // Every record parses and the heads agree; only the demand that the sealed steps were opened is not met.
        List<HistoryEvent> history = AuditBundleBuilder.History(RunA, "Operator:ci");
        var b = new AuditBundleBuilder { SealedSections = AuditEvidenceFormat.NotOpened };
        b.Add(AuditEvidenceFormat.KindRun, history[0].Timestamp, ("run", RunA), ("workflowType", "refunds"), ("buildId", "b-1"), ("startedBy", "Operator:ci"), ("previousRun", null));
        b.Add(AuditEvidenceFormat.KindChainHead, history[^1].Timestamp, ("run", RunA), ("sequence", history[^1].Sequence), ("hash", Convert.ToHexStringLower(history[^1].Hash)), ("outcome", "completed"));
        b.Heads.Add((RunA, history[^1].Sequence, Convert.ToHexStringLower(history[^1].Hash)));
        byte[] manifest = b.Manifest();
        AuditVerification v = AuditEvidenceVerifier.Verify(manifest, b.Evidence, b.Sign(manifest), b.Key, requireOpened: true);
        Assert.False(v.Passed);
        Assert.NotEmpty(v.Records);

        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(history, false), "a.khist"));
        Assert.Contains("did not verify", v.Findings[^1].Detail, StringComparison.Ordinal);

        AuditVerification fine = AuditEvidenceVerifier.Verify(manifest, b.Evidence, b.Sign(manifest), b.Key);
        Assert.True(AuditEvidenceVerifier.VerifyHistory(fine, FileOf(history, false), "a.khist")); // the same bundle without the demand: the file is accepted
    }

    [Fact]
    public void History_files_are_not_checked_against_a_bundle_that_did_not_verify()
    {
        (AuditBundleBuilder b, List<HistoryEvent> a, _) = Good();
        byte[] manifest = b.Manifest();
        AuditVerification v = AuditEvidenceVerifier.Verify(manifest, b.Evidence.AsSpan()[..^1], b.Sign(manifest), b.Key);

        Assert.False(AuditEvidenceVerifier.VerifyHistory(v, FileOf(a, false), "a.khist"));
    }

    // ---- keys -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_key_may_be_given_as_pem_der_or_base64_and_only_if_it_is_a_p256_public_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.ExportSubjectPublicKeyInfo();
        string pem = key.ExportSubjectPublicKeyInfoPem();

        Assert.Equal(der, AuditKey.Parse("k", der).SubjectPublicKeyInfo);
        Assert.Equal(der, AuditKey.Parse("k", Encoding.UTF8.GetBytes(pem)).SubjectPublicKeyInfo);
        Assert.Equal(der, AuditKey.Parse("k", Encoding.UTF8.GetBytes(Convert.ToBase64String(der))).SubjectPublicKeyInfo);
        Assert.Equal(AuditEvidenceFormat.Sha256Hex(der), AuditKey.Parse("k", der).Fingerprint);

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        Assert.Throws<FormatException>(() => AuditKey.Parse("k", p384.ExportSubjectPublicKeyInfo()));
        Assert.Throws<FormatException>(() => AuditKey.Parse("k", rsa.ExportSubjectPublicKeyInfo()));
        Assert.Contains("private key", Assert.Throws<FormatException>(() => AuditKey.Parse("k", Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()))).Message, StringComparison.Ordinal); // a private key is refused, not used
        Assert.Throws<FormatException>(() => AuditKey.Parse("k", [.. der, 0]));
        Assert.Throws<FormatException>(() => AuditKey.Parse("k", "not a key"u8));
        Assert.Throws<FormatException>(() => AuditKey.Parse("k", ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_key_id_a_signature_names_can_be_read_but_only_as_a_label()
    {
        (AuditBundleBuilder b, _, _) = Good();
        string signature = b.Sign(b.Manifest());

        Assert.Equal(AuditBundleBuilder.KeyId, AuditEvidenceVerifier.KeyIdOf(signature));
        Assert.Null(AuditEvidenceVerifier.KeyIdOf("nonsense"));
        Assert.Null(AuditEvidenceVerifier.KeyIdOf("a.b.c"));
    }
}
