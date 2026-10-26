namespace Kairos.Cli;

/// <summary>The options of <c>kairos audit verify</c>.</summary>
internal sealed record AuditVerifyArguments
{
    /// <summary>The bundle: its base name, its manifest file, or the directory that holds one bundle.</summary>
    public string Bundle { get; init; } = "";

    /// <summary>The public key the verifier trusts (PEM, DER or base64 of a P-256 SubjectPublicKeyInfo).</summary>
    public string KeyFile { get; init; } = "";

    /// <summary>The key identifier the signature must carry. Defaults to the one the signature names; the trust is in the key, not in this label.</summary>
    public string? KeyId { get; init; }

    /// <summary>History files (<c>kairos history export</c>, full or metadata only) to check against the recorded chain heads.</summary>
    public IReadOnlyList<string> Histories { get; init; } = [];

    /// <summary>Fail a bundle written without opening the sealed steps.</summary>
    public bool RequireOpened { get; init; }

    public static AuditVerifyArguments Parse(IReadOnlyList<string> args)
    {
        var result = new AuditVerifyArguments();
        var histories = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--key": result = result with { KeyFile = Value() }; break;
                case "--kid": result = result with { KeyId = Value() }; break;
                case "--history": histories.Add(Value()); break;
                case "--require-opened": result = result with { RequireOpened = true }; break;
                default:
                    if (name.StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{name}'.");
                    }

                    result = result.Bundle.Length == 0 ? result with { Bundle = name } : throw new ArgumentException("Give one bundle to verify.");
                    break;
            }
        }

        if (result.Bundle.Length == 0)
        {
            throw new ArgumentException("audit verify needs the bundle (its base name, its manifest file, or its directory).");
        }

        if (result.KeyFile.Length == 0)
        {
            throw new ArgumentException("audit verify needs --key <public key file>: the key you trust, fetched from the key service of the customer who signed, not taken from the bundle.");
        }

        return result with { Histories = histories };
    }
}

internal static class AuditVerifyCommand
{
    private const string ManifestSuffix = ".manifest.json";

    public static async Task<int> RunAsync(AuditVerifyArguments args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string? manifestPath = Locate(args.Bundle);
        if (manifestPath is null)
        {
            await stderr.WriteLineAsync($"No audit bundle found at '{args.Bundle}' (looked for <name>{ManifestSuffix} with <name>.jsonl and <name>.sig beside it).").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        string basePath = manifestPath[..^ManifestSuffix.Length];
        string evidencePath = basePath + ".jsonl";
        string signaturePath = basePath + ".sig";
        foreach (string path in new[] { evidencePath, signaturePath, args.KeyFile }.Concat(args.Histories))
        {
            if (!File.Exists(path))
            {
                await stderr.WriteLineAsync($"'{path}' does not exist.").ConfigureAwait(false);
                return ExitCode.Usage;
            }
        }

        byte[] manifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        byte[] evidence = await File.ReadAllBytesAsync(evidencePath, cancellationToken).ConfigureAwait(false);
        string signature = await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false);

        AuditKey key;
        try
        {
            string kid = args.KeyId ?? AuditEvidenceVerifier.KeyIdOf(signature) ?? "";
            if (kid.Length == 0)
            {
                await stderr.WriteLineAsync("The signature does not name a key identifier; pass --kid.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            key = AuditKey.Parse(kid, await File.ReadAllBytesAsync(args.KeyFile, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCode.Usage;
        }

        AuditVerification result = AuditEvidenceVerifier.Verify(manifest, evidence, signature, key, args.RequireOpened);
        if (result.Manifest is { } m && m.EvidenceFile != Path.GetFileName(evidencePath))
        {
            result.AddFinding("evidence file", false, "The signed manifest names a different evidence file than the one beside it.");
        }

        foreach (string history in args.Histories)
        {
            AuditEvidenceVerifier.VerifyHistory(result, await File.ReadAllTextAsync(history, cancellationToken).ConfigureAwait(false), Path.GetFileName(history));
        }

        foreach (AuditFinding f in result.Findings)
        {
            await (f.Passed ? stdout : stderr).WriteLineAsync($"{(f.Passed ? "ok  " : "FAIL")} {f.Check}: {f.Detail}").ConfigureAwait(false);
        }

        if (!result.Passed)
        {
            await stderr.WriteLineAsync("NOT VERIFIED: do not rely on this bundle.").ConfigureAwait(false);
            return ExitCode.Failed;
        }

        AuditManifest verified = result.Manifest!;
        await stdout.WriteLineAsync($"VERIFIED: export {verified.ExportId}, {verified.RecordCount} records, {verified.Heads.Count} runs, signed with the key you supplied.").ConfigureAwait(false);
        await stdout.WriteLineAsync("This proves the bundle is the one the key's owner signed and that nothing in it was changed since. It does not prove the system was honest before the export.").ConfigureAwait(false);
        return ExitCode.Passed;
    }

    private static string? Locate(string bundle)
    {
        if (Directory.Exists(bundle))
        {
            string[] manifests = Directory.GetFiles(bundle, "*" + ManifestSuffix);
            return manifests.Length == 1 ? manifests[0] : null;
        }

        if (bundle.EndsWith(ManifestSuffix, StringComparison.Ordinal))
        {
            return File.Exists(bundle) ? bundle : null;
        }

        string withSuffix = bundle + ManifestSuffix;
        return File.Exists(withSuffix) ? withSuffix : null;
    }
}
