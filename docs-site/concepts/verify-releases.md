# Verifying a release

Every release of Kairos is built from a tagged commit by a pipeline, and ships with evidence you can check yourself before you install it. This page says what the evidence is,
how to check each piece, and what it does and does not prove.

| Evidence | What it tells you | Where it is |
|---|---|---|
| **NuGet author signature** | The package is byte-for-byte what we signed, with our code-signing certificate. | Inside every `.nupkg`. |
| **SBOM** (CycloneDX) | Every component the package depends on, with versions and package URLs. | `<package>.<version>.cdx.json`, and signed as an attestation. |
| **Build provenance** (SLSA v1) | Which commit, which pipeline run and which toolchain produced the package. | `<package>.nupkg.provenance.sigstore.json`. |
| **Reproducible build** | Anyone can build the same commit and get the same package contents. | The build instructions below. |
| **Image signature** | A container image is the one we built, identified by digest. | In the registry, next to the image. |

The public key that signs attestations and images is `build/release/cosign.pub` in the repository, committed at the release's tag. The SHA-256 fingerprint of the package-signing
certificate is published in the release notes of every release and is:

```text
REPLACE-NUGET-CERTIFICATE-SHA256
```

Take both from the repository at the tag you are installing, over HTTPS, and not from the place you downloaded the package from.

## Check the package signature

```text
dotnet nuget verify --all --certificate-fingerprint <the fingerprint above> Kairos.Core.<version>.nupkg
```

This fails for a package that is unsigned, that was changed after it was signed, or that was signed by any other certificate, including a valid one. NuGet can also enforce it for every
restore: add our certificate to the `trustedSigners` of your `nuget.config` and set `signatureValidationMode` to `require`.

## Check the attestations and read the SBOM

Install [cosign](https://github.com/sigstore/cosign) and run, for each package:

```text
cosign verify-blob-attestation --key cosign.pub --type slsaprovenance1 \
  --bundle Kairos.Core.<version>.nupkg.provenance.sigstore.json --insecure-ignore-tlog=true Kairos.Core.<version>.nupkg

cosign verify-blob-attestation --key cosign.pub --type cyclonedx \
  --bundle Kairos.Core.<version>.nupkg.sbom.sigstore.json --insecure-ignore-tlog=true Kairos.Core.<version>.nupkg
```

A successful run prints `Verified OK`: the statement names exactly this package's SHA-256 and was signed by the release key. The flag `--insecure-ignore-tlog` is there because our releases do not
write to a public transparency log; the trust is in the key, which is why you take it from the repository and not from the download. The statement itself is the payload of the bundle (base64 in
`dsseEnvelope.payload`); the SBOM is the same document as the `.cdx.json` file.

## Check a container image

Images are referred to by digest. Check the signature and the two attestations before you run one:

```text
cosign verify --key cosign.pub <registry>/<image>@sha256:<digest>
cosign verify-attestation --key cosign.pub --type slsaprovenance1 --insecure-ignore-tlog=true <registry>/<image>@sha256:<digest>
cosign verify-attestation --key cosign.pub --type cyclonedx --insecure-ignore-tlog=true <registry>/<image>@sha256:<digest>
```

Pin the digest in your deployment (not the tag), so that what you verified is what runs.

## Rebuild it yourself

A package is a function of the commit and the toolchain. The toolchain is pinned: the .NET SDK in `global.json` (exactly that version), locked package versions in each project's
`packages.lock.json`, and the release tools listed in `.config/dotnet-tools.json`.

1. Check out the release's tag and install the SDK that `global.json` names.
2. Run `CI=true dotnet pack src/Kairos.Core/Kairos.Core.csproj -c Release -p:Version=<version>` (or `build/release/Build-Packages.ps1` for all packages).
3. Compare what the package contains. A `.nupkg` is a zip, and NuGet records the time of packing in it, so compare the files inside and not the archive: every file in the package we
   shipped (apart from the signature, which is added afterwards) has the same SHA-256 as the file you built.

If a file differs, that is worth a report: see `SECURITY.md`.

## What this proves, and what it does not

- The signature proves the package was signed with our certificate, and that nothing changed after. It does not prove the code is free of defects.
- The provenance names the commit and the pipeline run. It is **SLSA Build level 2**: a hosted pipeline produced it and signed it with a key that is held in a key vault and usable only
  by the release pipeline's identity. It is not level 3, because the signing runs in the same job as the build, so a compromised build step could in principle ask for a signature. The
  reproducible build is how you check it independently.
- The SBOM lists the dependency closure the package was built against, which for a build-time package (analyzers, for instance) can be wider than what the package places in your application.
- None of these replaces your own review of what you run. They tell you that what you run is what we released, and where it came from.
