# Releasing Kairos

Internal. What a release is, what has to be true before the pipeline is started, and what to do when something is wrong. The pipeline is `build/azure-pipelines-release.yml` (this repository) and
`build/azure-pipelines-release.yml` in Kairos.Commercial (see its `docs/RELEASE.md` for what it adds: the container images). The scripts are in `build/release/`. What customers do with the result is
the public page *Verifying a release* (`docs-site/concepts/verify-releases.md`).

## 1. What a release produces

For every package, and every container image of the commercial repository:

| Evidence | How it is made | Where it is checked |
|---|---|---|
| Reproducible build | `Test-Reproducible.ps1`: the commit is built twice from fresh clones at different depths; every file of every package must be identical. The packages shipped must then equal the first of those builds file for file. | Pipeline stages *Reproducible* and *Package*; anyone, by rebuilding |
| NuGet author signature | `Sign-Packages.ps1`: the `sign` tool with the code-signing certificate in Key Vault (the key never leaves the vault), RFC 3161 time stamp | `Test-PackageSignatures.ps1` right after signing (`dotnet nuget verify --certificate-fingerprint`), and by consumers |
| CycloneDX SBOM | `New-Sbom.ps1` (packages: CycloneDX .NET tool, from the locked restore; images: syft) | `Test-Sbom.ps1`: the SBOM describes the package and lists every dependency its nuspec declares |
| SLSA build provenance (v1) | `New-Provenance.ps1` (commit, pipeline run, toolchain, SBOM digests), attested per artifact by cosign with the release key in Key Vault | `Attest-Packages.ps1` verifies each attestation against `build/release/cosign.pub` before the stage ends |
| Image signature | `Publish-Images.ps1` (commercial): cosign over the digest | the same script, and by consumers |

**The claim, exactly.** SLSA Build level 2: a hosted build service produced provenance, signed with a key only the release pipeline's identity can use. Not level 3: the signing runs in the
job that builds, so a compromised build step could ask for a signature. The independent check is reproducibility. Say "level 2" in every place it is stated.

**No transparency log.** Signatures and attestations are made with `--use-signing-config=false` and verified with `--insecure-ignore-tlog=true`. The keys are ours, in our vault; a public log would publish
the names and versions of unreleased builds, and gives nothing the pinned public key does not. Consumers are told so (*Verifying a release*).

## 2. The pinned toolchain

Everything that can change the bytes or the verdict is pinned, and a change is a commit:

| What | Where | Upgrade by |
|---|---|---|
| .NET SDK, exact (`rollForward: disable`) | `global.json`; `docker/ci.Dockerfile`; commercial `Dockerfile.*` (`sdk:<version>`) | Change all four together, `dotnet restore --force-evaluate` (the lock files record the SDK's implicit ILLink package; a different SDK fails a locked restore and changes compiler output), run the full tests, commit. The .NET 11 CI job drops the pin on purpose. |
| NuGet packages | `Directory.Packages.props`, `packages.lock.json` per project | the usual |
| .NET tools (CycloneDX, `sign`, DocFX) | `.config/dotnet-tools.json` (exact versions, no roll-forward) | change the version, run `Test-ReleaseTooling.ps1` and an SBOM run |
| cosign, syft | `Install-ReleaseTools.ps1`: version and SHA-256 per platform | **Upgrading a release tool:** take the version at least two weeks after its release; take the hash from the release's checksums file and compare it with the digest GitHub shows for the asset; change both in one commit; run `Test-ReleaseTooling.ps1`; run a full image publish against a local registry (commercial) |
| Container base images | digests in `Dockerfile.*`, `docker/pin-images.ps1` | `pin-images.ps1 -Refresh`, run the tests |
| Build agent | `ubuntu-latest` (Microsoft-hosted) | not pinned; the SDK, tools and hashes above are what make it irrelevant. The reproducibility check is what catches an agent change that matters. |

Paths: `Directory.Build.props` maps the repository root to `/_/` in CI builds. Without it the compiler writes the PDB's location into every assembly and the same commit built in two directories gives
two different packages (found by the first reproducibility run).

## 3. One-time setup

Do this once per organisation, in this order. Until it is done the pipeline fails at the release gate, by design.

1. **Key Vault** (`KAIROS_KEYVAULT_URL`), RBAC mode, soft delete and purge protection on, network access limited. Two objects:
   - a **code-signing certificate** (RSA 3072 or more, non-exportable, from a certificate authority that NuGet trusts; extended key usage *code signing*). Name: `KAIROS_NUGET_CERTIFICATE`. Record its
     SHA-256 fingerprint (`KAIROS_NUGET_CERTIFICATE_SHA256`) and publish it in the release notes and in `docs-site/concepts/verify-releases.md` (replacing `REPLACE-NUGET-CERTIFICATE-SHA256`).
   - a **cosign key** (EC P-256, sign only). `KAIROS_COSIGN_KEY` = `azurekms://{vault}.vault.azure.net/{key}`. Export its public half and **commit it** as `build/release/cosign.pub`
     (`cosign public-key --key azurekms://...`), in each repository. The pipeline verifies against that file and never against what the vault says on the day.
2. **Azure DevOps**: a service connection to Azure with workload identity federation (no secret), whose identity has exactly: *Key Vault Certificate User* and *Key Vault Crypto User* on the vault, and
   (commercial) *AcrPush* on the registry. A variable group `kairos-release` with the ids and names in the pipeline header (nothing in it is a secret). An **environment `kairos-release`** with
   approvals: at least two approvers, and the person who pushed the tag cannot be the only one.
3. **Who can release**: only release managers may create `v*` tags (branch/tag security on the repository); `main` has branch protection and required reviews. The gate refuses a commit that is not on
   `origin/main`.
4. **Feed** (`KAIROS_NUGET_FEED`) and, for commercial, the **registry** (`KAIROS_REGISTRY`, `KAIROS_REGISTRY_NAME`).
5. **Fill in the place-holders** (the gate fails while any `REPLACE-...` is left in published text): `SECURITY.md` (`REPLACE-SECURITY-EMAIL`; the GitHub address is already the real one), the
   fingerprint above, and on the marketing site the *Report a vulnerability* link (`REPLACE-ORG` is `kronosautomation`).
6. **GitHub**: turn on *private vulnerability reporting* for the repository (Settings, Code security), and keep a security-contact team that receives it.
7. **Servers**: set `Server:Security:*` on every node (contact, policy, expiry, at most a year ahead) so that `/.well-known/security.txt` answers. Put the date it expires in the calendar.
8. **Dashboard behind a load balancer** (optional): set `Server:KnownProxies` on the data plane to the dashboard host's address, and `Dashboard:KnownProxies`/`KnownNetworks` on the dashboard to its load
   balancer, so the server counts each person's anonymous requests separately. Off by default; see `SERVER-DESIGN.md` 4.6.

## 4. The checklist for one release

Before tagging:

- [ ] `main` is green: the per-commit pipeline and the nightly (including the soak and the benchmark gate) on the commit to release.
- [ ] No open security advisory that is fixed on `main` but unreleased, and none that should block this release.
- [ ] `dotnet list package --vulnerable --include-transitive` is clean (the gate repeats it).
- [ ] Release notes are written: what changed, what was fixed (a security fix is described only as far as its advisory allows), upgrade steps, the certificate fingerprint, the support statement.
- [ ] Every public page that changed behaviour was updated in the commit that changed it; `check-disclosure.ps1` passes.
- [ ] Version number decided (SemVer; a prerelease while a dependency is a prerelease, as the A2A packages are).
- [ ] The commercial repository is ready at the same version (its checklist), and its `kairosRef` will be this tag.
- [ ] `build/release/Test-ReleaseTooling.ps1` passes locally and in CI (it is also the first thing the pipeline does).

Releasing:

- [ ] Tag the commit on `main`: `git tag -s v1.2.3 -m "Kairos 1.2.3"` and push the tag. (A signed tag is a convenience for people; the gate does not depend on it.)
- [ ] Watch *Verify*, *Reproducible*, *Package*. A reproducibility failure is a stop: find the file that differs (the log names it), do not re-run until it is understood.
- [ ] *Sign* waits for approval. The approver reads the *Package* stage's summary (package list, SBOM check, the equality with the independent build) before approving.
- [ ] After *Sign*: download the `release` artifact and run the customer's verification on a machine that has never seen the build (`docs-site/concepts/verify-releases.md`): signature with the published fingerprint,
      both attestations with the committed public key, and one rebuild of one package.
- [ ] Re-run the pipeline with `publish = true` only after that (or set it at queue time if the approver is confident). Publish pushes the signed packages; it cannot be undone except by unlisting.
- [ ] Publish the release notes and the evidence (SBOMs, attestations, public key) next to the packages.
- [ ] Commercial: the images' digests and the registry path are in the customer-facing notes (digests, not tags).

After:

- [ ] Smoke test: install the published packages into a clean project, run the samples, start the container images with the example configuration (`/health/ready` answers).
- [ ] Update the supported-versions table in `SECURITY.md`; close the release's milestone issues; put the next `security.txt` expiry in the calendar.

## 5. When a vulnerability is reported

Policy and promises are in `SECURITY.md`. Internally:

1. **Day 0-3**: someone owns it and acknowledges the reporter (within 3 business days). Open a private GitHub security advisory (the report itself is one, if it came through GitHub). Do not discuss it in
   issues, pull requests, chat channels with guests, or commit messages.
2. **By day 10**: reproduce, rate it (CVSS v4), list the affected versions, decide the fix and the mitigation, and tell the reporter. Check the commercial repository for the same flaw (the two share code paths).
3. **Fix in private**: use the advisory's private fork/branch; a test that fails before and passes after; a cross-tenant test if the flaw touches isolation. Review by a second person.
4. **Coordinate**: agree a date with the reporter (default: when the fix is released, never later than 90 days). Prepare the advisory text, the CVE request (GitHub is a CNA) and the release notes.
5. **Release** with this process (Section 4); a security release is never exempt from the gate, the reproducibility check or the approval.
6. **Publish** the advisory after the packages and images are available; credit the reporter if they want it; tell customers of the commercial editions directly.
7. **Afterwards**: within two weeks, a short write-up of the cause and of why no test or review caught it; add what would have.

If a flaw is being exploited, skip to 5 with the smallest possible fix and publish the warning first.

## 6. When a key or certificate may be compromised

- **Certificate**: revoke it with the CA; stop the pipeline's access (remove the role assignment); issue a new one; publish the new fingerprint; re-sign and re-release the current versions; publish an advisory
  naming the affected versions and the window. Consumers who pinned the fingerprint refuse anything signed in the window by accident.
- **Cosign key**: create a new key version, commit the new `cosign.pub` (a reviewed change), re-attest the current releases, and publish an advisory saying which key signed what. Old attestations stay valid only
  for the old public key, which consumers took from the repository at the tag they installed.
- **Pipeline identity or service connection**: rotate it, review the sign-in logs and every release signed since, and treat releases signed in the suspect window as unsigned until rebuilt (reproducibility lets you
  check each one against a fresh build).

## 7. What the tooling does not do

- It does not make a compromised *build step* harmless (level 2, above). Reproducibility is the check.
- Reproducibility is for the packages' contents and for the images' digests. The `.nupkg` archive differs in zip time stamps from build to build (NuGet writes them), and signing adds a signature, so the archive is never
  byte-identical; the check compares what is inside.
- The SBOM of a library lists the closure it was built against. For analyzers and build-time packages that is wider than what lands in an application.
- The container SBOM is what syft finds in the image (OS packages and .NET assemblies). It is not a statement about vulnerabilities; scan the image separately.
- Nothing here signs the documentation site or the samples.
- The signing key is only as safe as the vault's access policy and the environment approvals. Review who holds *Key Vault Crypto User* and who approves, every quarter.
