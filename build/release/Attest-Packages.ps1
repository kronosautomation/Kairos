<#
.SYNOPSIS
  Signs a provenance statement and an SBOM statement for every package with the release key (cosign), writes them next to the packages, and verifies each one straight away.

.DESCRIPTION
  For <package>.nupkg this writes
    <package>.nupkg.provenance.sigstore.json   an in-toto statement (subject = the package's SHA-256, predicate = the SLSA provenance) signed with the release key
    <package>.nupkg.sbom.sigstore.json         the same for the package's CycloneDX SBOM
  and verifies both against the public key, so a signing that did not work stops the release here and not at a customer's.
  Run it on the packages as they will be published: after NuGet signing (the signature is part of the package, and the attestation names the signed file's digest).

.PARAMETER Key        The signing key: azurekms://{vault}.vault.azure.net/{key} in a release (the private key never leaves Key Vault), or a cosign key file in tests.
.PARAMETER PublicKey  What to verify with: a public key file, or the same KMS reference.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Packages,
    [Parameter(Mandatory)][string]$Sboms,
    [Parameter(Mandatory)][string]$Provenance,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][string]$PublicKey
)

. (Join-Path $PSScriptRoot 'common.ps1')
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$files = @(Get-PackageFiles $Packages)
if ($files.Count -eq 0) { throw "No packages in $Packages." }
$manifest = Get-Content -LiteralPath (Join-Path $Packages 'packages.json') -Raw | ConvertFrom-Json

foreach ($file in $files) {
    $entry = $manifest | Where-Object { $_.file -eq $file.Name } | Select-Object -First 1
    if (-not $entry) { throw "$($file.Name) is not in packages.json." }
    $sbom = Join-Path $Sboms "$($entry.id).$($entry.version).cdx.json"
    if (-not (Test-Path -LiteralPath $sbom)) { throw "No SBOM for $($file.Name)." }

    foreach ($kind in @(@{ Name = 'provenance'; Predicate = $Provenance; Type = 'slsaprovenance1' }, @{ Name = 'sbom'; Predicate = $sbom; Type = 'cyclonedx' })) {
        $bundle = Join-Path $Output "$($file.Name).$($kind.Name).sigstore.json"
        # --use-signing-config=false: the key is ours; nothing is uploaded to a public log (the package names and versions of a private release are not for one).
        Invoke-Native cosign @('attest-blob', '--key', $Key, '--predicate', $kind.Predicate, '--type', $kind.Type, '--bundle', $bundle, '--use-signing-config=false', '--yes', $file.FullName) -Quiet | Out-Null
        Invoke-Native cosign @('verify-blob-attestation', '--key', $PublicKey, '--type', $kind.Type, '--bundle', $bundle, '--insecure-ignore-tlog=true', $file.FullName) -Quiet | Out-Null
    }
    Write-Host "attested and verified: $($file.Name)"
}
Write-Host "Attested $($files.Count) packages: provenance and SBOM, each verified against the public key." -ForegroundColor Green
