using System.Security.Cryptography;
using Kairos.Cli;
using Kairos.Core.Tests;

namespace Kairos.Cli.Tests;

/// <summary><c>kairos audit verify</c> as an auditor runs it: files in, exit code and text out, nothing from a record on the screen.</summary>
public sealed class AuditVerifyCliTests : IDisposable
{
    private static readonly Guid Run = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-audit-cli-" + Guid.NewGuid().ToString("N"));

    public AuditVerifyCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (AuditBundleBuilder Builder, List<HistoryEvent> History, string Key) Write()
    {
        var b = new AuditBundleBuilder();
        List<HistoryEvent> history = AuditBundleBuilder.History(Run, "Operator:ci");
        b.AddRun(history, "Operator:ci");
        byte[] manifest = b.Manifest();
        File.WriteAllBytes(Path.Combine(_dir, "audit-test.manifest.json"), manifest);
        File.WriteAllBytes(Path.Combine(_dir, "audit-test.jsonl"), b.Evidence);
        File.WriteAllText(Path.Combine(_dir, "audit-test.sig"), b.Sign(manifest));
        string key = Path.Combine(_dir, "key.pem");
        File.WriteAllText(key, b.SigningKey.ExportSubjectPublicKeyInfoPem());
        return (b, history, key);
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = await Cli.RunAsync(args, o, e, default);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task A_good_bundle_is_verified_from_its_directory_its_base_name_or_its_manifest()
    {
        (_, _, string key) = Write();

        foreach (string bundle in new[] { _dir, Path.Combine(_dir, "audit-test"), Path.Combine(_dir, "audit-test.manifest.json") })
        {
            (int code, string output, string error) = await RunAsync("audit", "verify", bundle, "--key", key);

            Assert.Equal(ExitCode.Passed, code);
            Assert.Contains("VERIFIED", output, StringComparison.Ordinal);
            Assert.Contains("signature:", output, StringComparison.Ordinal);
            Assert.Equal("", error);
        }
    }

    [Fact]
    public async Task A_history_file_full_or_metadata_only_is_checked_against_the_recorded_head()
    {
        (_, List<HistoryEvent> history, string key) = Write();
        string full = Path.Combine(_dir, "full.khist");
        string meta = Path.Combine(_dir, "meta.khist");
        File.WriteAllText(full, HistoryFile.Serialize(history, "test"));
        File.WriteAllText(meta, HistoryFile.SerializeMetadataOnly(history, "test"));

        (int code, string output, _) = await RunAsync("audit", "verify", _dir, "--key", key, "--history", full, "--history", meta);
        Assert.Equal(ExitCode.Passed, code);
        Assert.Contains("history full.khist", output, StringComparison.Ordinal);
        Assert.Contains("history meta.khist", output, StringComparison.Ordinal);

        File.WriteAllText(meta, HistoryFile.SerializeMetadataOnly(history.Take(2).ToList(), "test"));
        (int early, _, string error) = await RunAsync("audit", "verify", _dir, "--key", key, "--history", meta);
        Assert.Equal(ExitCode.Failed, early);
        Assert.Contains("NOT VERIFIED", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_file_or_another_key_is_not_verified_and_exits_1()
    {
        (_, _, string key) = Write();
        string evidence = Path.Combine(_dir, "audit-test.jsonl");
        byte[] original = File.ReadAllBytes(evidence);
        original[10] ^= 1;
        File.WriteAllBytes(evidence, original);
        (int changed, _, string error) = await RunAsync("audit", "verify", _dir, "--key", key);
        original[10] ^= 1;
        File.WriteAllBytes(evidence, original);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string otherKey = Path.Combine(_dir, "other.pem");
        File.WriteAllText(otherKey, other.ExportSubjectPublicKeyInfoPem());
        (int wrongKey, _, string wrongError) = await RunAsync("audit", "verify", _dir, "--key", otherKey);

        Assert.Equal(ExitCode.Failed, changed);
        Assert.Contains("FAIL evidence", error, StringComparison.Ordinal);
        Assert.Equal(ExitCode.Failed, wrongKey);
        Assert.Contains("FAIL signature", wrongError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_mistakes_exit_2_and_a_private_key_is_never_accepted()
    {
        (AuditBundleBuilder b, _, string key) = Write();
        string privateKey = Path.Combine(_dir, "private.pem");
        File.WriteAllText(privateKey, b.SigningKey.ExportPkcs8PrivateKeyPem());

        Assert.Equal(ExitCode.Usage, (await RunAsync("audit", "verify", _dir)).Code); // no key
        Assert.Equal(ExitCode.Usage, (await RunAsync("audit", "verify", "--key", key)).Code); // no bundle
        Assert.Equal(ExitCode.Usage, (await RunAsync("audit", "verify", Path.Combine(_dir, "nothing"), "--key", key)).Code);
        Assert.Equal(ExitCode.Usage, (await RunAsync("audit", "verify", _dir, "--key", Path.Combine(_dir, "missing.pem"))).Code);
        Assert.Equal(ExitCode.Usage, (await RunAsync("audit", "verify", _dir, "--key", key, "--history", Path.Combine(_dir, "missing.khist"))).Code);
        Assert.Equal(ExitCode.Usage, (await RunAsync("audit", "verify", _dir, "--key", key, "--bogus")).Code);
        Assert.Equal(ExitCode.Usage, (await RunAsync("audit")).Code);
        (int code, _, string error) = await RunAsync("audit", "verify", _dir, "--key", privateKey);
        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("private key", error, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_a_record_says_reaches_the_screen()
    {
        (_, _, string key) = Write();
        (_, string output, string error) = await RunAsync("audit", "verify", _dir, "--key", key);

        foreach (string content in new[] { "olivia@example.test", "refunds over 100", "customer-77", "issue_refund", "Operator:ci" })
        {
            Assert.DoesNotContain(content, output + error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_bundle_written_without_the_sealed_steps_is_refused_when_they_are_required()
    {
        var b = new AuditBundleBuilder { SealedSections = AuditEvidenceFormat.NotOpened };
        b.Add(AuditEvidenceFormat.KindRun, AuditBundleBuilder.T0, ("run", Run), ("workflowType", "w"), ("buildId", "b"), ("startedBy", null), ("previousRun", null));
        b.Add(AuditEvidenceFormat.KindChainHead, AuditBundleBuilder.T0, ("run", Run), ("sequence", 1), ("hash", new string('c', 64)), ("outcome", "open"));
        b.Heads.Add((Run, 1, new string('c', 64)));
        byte[] manifest = b.Manifest();
        File.WriteAllBytes(Path.Combine(_dir, "audit-test.manifest.json"), manifest);
        File.WriteAllBytes(Path.Combine(_dir, "audit-test.jsonl"), b.Evidence);
        File.WriteAllText(Path.Combine(_dir, "audit-test.sig"), b.Sign(manifest));
        string key = Path.Combine(_dir, "key.der");
        File.WriteAllBytes(key, b.PublicKey);

        Assert.Equal(ExitCode.Passed, (await RunAsync("audit", "verify", _dir, "--key", key)).Code);
        Assert.Equal(ExitCode.Failed, (await RunAsync("audit", "verify", _dir, "--key", key, "--require-opened")).Code);
        Assert.Equal(ExitCode.Failed, (await RunAsync("audit", "verify", _dir, "--key", key, "--kid", "a-different-key-id")).Code);
    }
}
