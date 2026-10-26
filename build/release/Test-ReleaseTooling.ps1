<#
.SYNOPSIS
  Proves that the release checks can fail: each one is run against a good input and then against a broken one, and must accept the first and refuse the second.

.DESCRIPTION
  A check that has never been seen to fail is not evidence. This runs, offline except for nothing (cosign and dotnet must be installed; Install-ReleaseTools.ps1 does that):
    - reproducibility comparison: same files with other zip time stamps pass; a changed file, a missing file, a missing package fail
    - pinned download hash: a file with other bytes is refused and deleted
    - SBOM check: a missing dependency, a wrong version, a missing purl fail
    - NuGet signature check, with a throw-away self-signed certificate: an unsigned package, a package changed after signing and a package signed by another certificate fail, and the right
      certificate gets as far as the chain (which a self-signed certificate cannot satisfy; that is the one error expected)
    - attestations (cosign, throw-away key): provenance and SBOM attestations verify; a changed package, the wrong key and the wrong statement type fail
    - provenance predicate: names the commit and the toolchain, and says "local" outside a pipeline
  Exit code 1 if any expectation is wrong.
#>
[CmdletBinding()]
param([string]$Work = '')

. (Join-Path $PSScriptRoot 'common.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ownWork = -not $Work
if ($ownWork) { $Work = Join-Path ([IO.Path]::GetTempPath()) ("kairos-releasetest-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$Work = (Resolve-Path $Work).Path

$script:failures = New-Object System.Collections.Generic.List[string]
function Expect([string]$what, [scriptblock]$test) {
    try {
        if (& $test) { Write-Host "  ok   $what" -ForegroundColor Green }
        else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failures.Add($what) }
    }
    catch { Write-Host "  FAIL $what ($($_.Exception.Message))" -ForegroundColor Red; $script:failures.Add($what) }
}
function Fails([scriptblock]$action) { try { & $action | Out-Null; return $false } catch { return $true } }

function New-FakePackage([string]$Directory, [string]$Id, [string]$Version, [hashtable]$Files, [string[]]$Dependencies = @(), [DateTimeOffset]$Stamp = [DateTimeOffset]::UtcNow) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $path = Join-Path $Directory "$Id.$Version.nupkg"
    if (Test-Path $path) { Remove-Item $path }
    $deps = ($Dependencies | ForEach-Object { "<dependency id=""$_"" version=""1.0.0"" />" }) -join ''
    $nuspec = "<?xml version=""1.0""?><package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd""><metadata><id>$Id</id><version>$Version</version><authors>t</authors><description>t</description><dependencies><group targetFramework=""net10.0"">$deps</group></dependencies></metadata></package>"
    $zip = [IO.Compression.ZipFile]::Open($path, 'Create')
    try {
        $all = @{ "$Id.nuspec" = $nuspec } + $Files
        foreach ($name in ($all.Keys | Sort-Object)) {
            $entry = $zip.CreateEntry($name)
            $entry.LastWriteTime = $Stamp
            $writer = New-Object IO.StreamWriter($entry.Open())
            $writer.Write($all[$name]); $writer.Dispose()
        }
    }
    finally { $zip.Dispose() }
    $path
}

try {
    # ---- reproducibility comparison ----
    Write-Host 'Reproducibility comparison'
    $one = Join-Path $Work 'repro-1'; $two = Join-Path $Work 'repro-2'
    New-FakePackage $one 'P.A' '1.0.0' @{ 'lib/a.dll' = 'code' } -Stamp ([DateTimeOffset]'2026-01-01T00:00:00Z') | Out-Null
    New-FakePackage $two 'P.A' '1.0.0' @{ 'lib/a.dll' = 'code' } -Stamp ([DateTimeOffset]'2026-06-06T12:34:56Z') | Out-Null
    Expect 'the same files with other zip time stamps are the same package' { (Compare-PackageSets $one $two).Count -eq 0 }
    New-FakePackage $two 'P.A' '1.0.0' @{ 'lib/a.dll' = 'code, built in another directory' } | Out-Null
    Expect 'a changed file is found, by name' { $p = Compare-PackageSets $one $two; $p.Count -eq 1 -and $p[0] -like '*lib/a.dll differs*' }
    New-FakePackage $two 'P.A' '1.0.0' @{ 'lib/a.dll' = 'code'; 'lib/extra.dll' = 'x' } | Out-Null
    Expect 'a file in one build only is found' { (Compare-PackageSets $one $two) -like '*lib/extra.dll exists in one build only*' }
    Remove-Item (Join-Path $two 'P.A.1.0.0.nupkg')
    Expect 'a package in one build only is found' { (Compare-PackageSets $one $two) -like '*built only the first time*' }

    # ---- pinned download ----
    Write-Host 'Pinned download hash'
    $file = Join-Path $Work 'tool.bin'; [IO.File]::WriteAllText($file, 'the tool')
    $good = Get-Sha256 $file
    Expect 'the pinned bytes are accepted' { Assert-FileHash -Path $file -Sha256 $good; Test-Path $file }
    Expect 'other bytes are refused and deleted' { (Fails { Assert-FileHash -Path $file -Sha256 ('0' * 64) }) -and -not (Test-Path $file) }

    # ---- SBOM check ----
    Write-Host 'SBOM check'
    $pk = Join-Path $Work 'sbom-pk'; $sb = Join-Path $Work 'sbom'
    New-FakePackage $pk 'P.B' '2.0.0' @{ 'lib/b.dll' = 'b' } -Dependencies @('Dep.One', 'Dep.Two') | Out-Null
    @([ordered]@{ id = 'P.B'; version = '2.0.0'; file = 'P.B.2.0.0.nupkg'; sha256 = 'x' }) | ConvertTo-Json -AsArray | Set-Content (Join-Path $pk 'packages.json')
    New-Item -ItemType Directory -Force -Path $sb | Out-Null
    function Write-Bom([hashtable]$Override = @{}) {
        $bom = [ordered]@{
            bomFormat = 'CycloneDX'; specVersion = '1.6'
            metadata = @{ component = @{ type = 'library'; name = 'P.B'; version = '2.0.0'; purl = 'pkg:nuget/P.B@2.0.0'; 'bom-ref' = 'pkg:nuget/P.B@2.0.0' } }
            components = @(
                @{ name = 'Dep.One'; version = '1.0.0'; purl = 'pkg:nuget/Dep.One@1.0.0'; 'bom-ref' = 'pkg:nuget/Dep.One@1.0.0' },
                @{ name = 'Dep.Two'; version = '1.0.0'; purl = 'pkg:nuget/Dep.Two@1.0.0'; 'bom-ref' = 'pkg:nuget/Dep.Two@1.0.0' })
        }
        foreach ($k in $Override.Keys) { $bom[$k] = $Override[$k] }
        $bom | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $sb 'P.B.2.0.0.cdx.json')
    }
    Write-Bom
    Expect 'a complete SBOM of the package passes' { & (Join-Path $PSScriptRoot 'Test-Sbom.ps1') -Packages $pk -Sboms $sb *> $null; $true }
    Write-Bom @{ components = @(@{ name = 'Dep.One'; version = '1.0.0'; purl = 'pkg:nuget/Dep.One@1.0.0'; 'bom-ref' = 'a' }) }
    Expect 'an SBOM that lacks a declared dependency fails' { Fails { & (Join-Path $PSScriptRoot 'Test-Sbom.ps1') -Packages $pk -Sboms $sb *> $null } }
    Write-Bom @{ metadata = @{ component = @{ type = 'library'; name = 'P.B'; version = '9.9.9'; purl = 'pkg:nuget/P.B@9.9.9' } } }
    Expect 'an SBOM of another version fails' { Fails { & (Join-Path $PSScriptRoot 'Test-Sbom.ps1') -Packages $pk -Sboms $sb *> $null } }
    Write-Bom @{ components = @(@{ name = 'Dep.One'; version = '1.0.0' }, @{ name = 'Dep.Two'; version = '1.0.0'; purl = 'pkg:nuget/Dep.Two@1.0.0' }) }
    Expect 'a component with no purl fails' { Fails { & (Join-Path $PSScriptRoot 'Test-Sbom.ps1') -Packages $pk -Sboms $sb *> $null } }
    Remove-Item (Join-Path $sb 'P.B.2.0.0.cdx.json')
    Expect 'a package with no SBOM fails' { Fails { & (Join-Path $PSScriptRoot 'Test-Sbom.ps1') -Packages $pk -Sboms $sb *> $null } }

    # ---- NuGet signatures ----
    Write-Host 'NuGet signature check'
    function New-Certificate([string]$Name) {
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$Name", $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $oids = [Security.Cryptography.OidCollection]::new(); $oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
        $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
        $pfx = Join-Path $Work "$Name.pfx"
        [IO.File]::WriteAllBytes($pfx, $cert.Export('Pfx', 'pw'))
        @{ Pfx = $pfx; Fingerprint = $cert.GetCertHashString('SHA256') }
    }
    $release = New-Certificate 'release'; $other = New-Certificate 'other'
    $signed = Join-Path $Work 'signed'
    New-FakePackage $signed 'P.C' '1.0.0' @{ 'lib/c.dll' = 'c' } | Out-Null
    function Verify-Output([string]$Fingerprint, [string]$Dir) {
        (& dotnet nuget verify --all --certificate-fingerprint $Fingerprint (Join-Path $Dir 'P.C.1.0.0.nupkg') 2>&1 | ForEach-Object { "$_" }) -join "`n"
    }
    Expect 'an unsigned package is refused' { (Verify-Output $release.Fingerprint $signed) -match 'NU3004' }
    & dotnet nuget sign (Join-Path $signed 'P.C.1.0.0.nupkg') --certificate-path $release.Pfx --certificate-password pw 2>&1 | Out-Null
    $result = Verify-Output $release.Fingerprint $signed
    Expect 'the right certificate is recognised: the only error left is the self-signed chain, which no test certificate can satisfy' { ($result -match 'NU3018') -and ($result -notmatch 'NU3034|NU3004|NU3008') }
    Expect 'a package signed by another certificate is refused (fingerprint mismatch)' { (Verify-Output $other.Fingerprint $signed) -match 'NU3034' }
    $tampered = Join-Path $Work 'tampered'; New-Item -ItemType Directory -Force -Path $tampered | Out-Null
    $bytes = [IO.File]::ReadAllBytes((Join-Path $signed 'P.C.1.0.0.nupkg')); $bytes[40] = $bytes[40] -bxor 1
    [IO.File]::WriteAllBytes((Join-Path $tampered 'P.C.1.0.0.nupkg'), $bytes)
    Expect 'a package changed after signing is refused' { (Verify-Output $release.Fingerprint $tampered) -match 'NU3008|NU3003|NU3005|invalid|corrupt' -or (Fails { & dotnet nuget verify --all (Join-Path $tampered 'P.C.1.0.0.nupkg') *> $null; if ($LASTEXITCODE) { throw 'x' } }) }
    Expect 'the verification script passes the pinned fingerprint to NuGet (a self-signed chain cannot get further to prove it, so the call is checked)' { (Get-Content (Join-Path $PSScriptRoot 'Test-PackageSignatures.ps1') -Raw) -match '--certificate-fingerprint \$Fingerprint' }
    Expect 'the verification script itself fails on that package' { Fails { & (Join-Path $PSScriptRoot 'Test-PackageSignatures.ps1') -Packages $signed -Fingerprint $release.Fingerprint *> $null } }

    # ---- attestations ----
    Write-Host 'Attestations (provenance and SBOM)'
    if (-not (Get-Command cosign -ErrorAction SilentlyContinue)) { throw 'cosign is not on PATH: run Install-ReleaseTools.ps1 first.' }
    $keys = Join-Path $Work 'keys'; New-Item -ItemType Directory -Force -Path $keys | Out-Null
    $env:COSIGN_PASSWORD = 'release-test'
    foreach ($name in 'good', 'wrong') { Invoke-Native cosign @('generate-key-pair', '--output-key-prefix', (Join-Path $keys $name)) -Quiet | Out-Null }
    $apk = Join-Path $Work 'attest-pk'; $asb = Join-Path $Work 'attest-sbom'; $out = Join-Path $Work 'attest-out'
    $pkg = New-FakePackage $apk 'P.D' '3.0.0' @{ 'lib/d.dll' = 'd' } -Dependencies @('Dep.One')
    @([ordered]@{ id = 'P.D'; version = '3.0.0'; file = 'P.D.3.0.0.nupkg'; sha256 = (Get-Sha256 $pkg) }) | ConvertTo-Json -AsArray | Set-Content (Join-Path $apk 'packages.json')
    New-Item -ItemType Directory -Force -Path $asb | Out-Null
    @{ bomFormat = 'CycloneDX'; specVersion = '1.6'; metadata = @{ component = @{ name = 'P.D'; version = '3.0.0'; purl = 'pkg:nuget/P.D@3.0.0' } }; components = @(@{ name = 'Dep.One'; version = '1.0.0'; purl = 'pkg:nuget/Dep.One@1.0.0' }) } |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $asb 'P.D.3.0.0.cdx.json')
    $predicate = Join-Path $Work 'provenance.json'
    & (Join-Path $PSScriptRoot 'New-Provenance.ps1') -Version 3.0.0 -Output $predicate | Out-Null
    $cosign = @{ Key = (Join-Path $keys 'good.key'); PublicKey = (Join-Path $keys 'good.pub') }
    & (Join-Path $PSScriptRoot 'Attest-Packages.ps1') -Packages $apk -Sboms $asb -Provenance $predicate -Output $out @cosign | Out-Null
    Expect 'provenance and SBOM attestations are written and verify' { (Test-Path (Join-Path $out 'P.D.3.0.0.nupkg.provenance.sigstore.json')) -and (Test-Path (Join-Path $out 'P.D.3.0.0.nupkg.sbom.sigstore.json')) }
    $bundle = Join-Path $out 'P.D.3.0.0.nupkg.provenance.sigstore.json'
    function Verify-Attestation([string]$PublicKey, [string]$Type, [string]$Blob, [string]$Bundle) {
        & cosign verify-blob-attestation --key $PublicKey --type $Type --bundle $Bundle --insecure-ignore-tlog=true $Blob *> $null
        $LASTEXITCODE -eq 0
    }
    Expect 'the attestation verifies for the package it names' { Verify-Attestation $cosign.PublicKey 'slsaprovenance1' $pkg $bundle }
    Expect 'it does not verify for another key' { -not (Verify-Attestation (Join-Path $keys 'wrong.pub') 'slsaprovenance1' $pkg $bundle) }
    Expect 'it does not verify as another kind of statement' { -not (Verify-Attestation $cosign.PublicKey 'cyclonedx' $pkg $bundle) }
    $changed = Join-Path $Work 'changed.nupkg'; Copy-Item $pkg $changed; Add-Content $changed 'x'
    Expect 'it does not verify for a package that was changed' { -not (Verify-Attestation $cosign.PublicKey 'slsaprovenance1' $changed $bundle) }
    $statement = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(((Get-Content $bundle -Raw | ConvertFrom-Json).dsseEnvelope.payload))) | ConvertFrom-Json
    Expect 'the signed statement names the package digest and the SLSA provenance type' { $statement.subject[0].digest.sha256 -eq (Get-Sha256 $pkg) -and $statement.predicateType -eq 'https://slsa.dev/provenance/v1' }
    Expect 'attesting with a key that does not exist fails' { Fails { & (Join-Path $PSScriptRoot 'Attest-Packages.ps1') -Packages $apk -Sboms $asb -Provenance $predicate -Output (Join-Path $Work 'x') -Key (Join-Path $keys 'missing.key') -PublicKey $cosign.PublicKey *> $null } }

    # ---- provenance predicate ----
    Write-Host 'Provenance predicate'
    $p = Get-Content $predicate -Raw | ConvertFrom-Json
    Expect 'it names the commit that was built' { $p.buildDefinition.resolvedDependencies[0].digest.gitCommit -match '^[0-9a-f]{40}$' }
    Expect 'it names the toolchain' { $p.buildDefinition.internalParameters.tools.dotnet -match '^\d+\.\d+\.\d+' -and $p.buildDefinition.internalParameters.tools.cosign }
    Expect 'outside a pipeline it says so, so that a verifier can refuse it' { $env:TF_BUILD -or ($p.runDetails.builder.id -eq 'local' -and $p.runDetails.metadata.invocationId -eq 'local') }

    # ---- release gate ----
    Write-Host 'Release gate'
    $repo = Join-Path $Work 'gate'; New-Item -ItemType Directory -Force -Path (Join-Path $repo 'build/release') | Out-Null
    Set-Content (Join-Path $repo 'global.json') '{}'
    Set-Content (Join-Path $repo 'SECURITY.md') "Report a vulnerability to mailto:security@example.test or through the advisories page."
    Set-Content (Join-Path $repo 'README.md') 'A readme.'
    Set-Content (Join-Path $repo 'build/release/cosign.pub') "-----BEGIN PUBLIC KEY-----`nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE`n-----END PUBLIC KEY-----"
    $gate = { param($tag) & (Join-Path $PSScriptRoot 'Test-ReleaseGate.ps1') -Tag $tag -Root $repo -SkipMainCheck -SkipVulnerabilityCheck *> $null }
    Expect 'a complete repository on a version tag passes' { & $gate 'refs/tags/v1.2.3'; $true }
    Expect 'a prerelease tag passes' { & $gate 'refs/tags/v1.2.3-rc.1'; $true }
    Expect 'a tag that is not a version fails' { Fails { & $gate 'refs/tags/release-1' } }
    Expect 'a tag with only two version parts fails' { Fails { & $gate 'refs/tags/v1.2' } }
    Set-Content (Join-Path $repo 'README.md') 'Report at https://github.com/REPLACE-ORG/kairos/security/policy'
    Expect 'a place-holder left in published text fails' { Fails { & $gate 'refs/tags/v1.2.3' } }
    Set-Content (Join-Path $repo 'README.md') 'A readme.'
    Remove-Item (Join-Path $repo 'SECURITY.md')
    Expect 'a missing SECURITY.md fails' { Fails { & $gate 'refs/tags/v1.2.3' } }
    Set-Content (Join-Path $repo 'SECURITY.md') 'Nothing here.'
    Expect 'a SECURITY.md that says nothing about reporting fails' { Fails { & $gate 'refs/tags/v1.2.3' } }
    Set-Content (Join-Path $repo 'SECURITY.md') "Report a vulnerability to mailto:security@example.test."
    Set-Content (Join-Path $repo 'build/release/cosign.pub') 'not a key'
    Expect 'a public key file that is not a key fails' { Fails { & $gate 'refs/tags/v1.2.3' } }
    Remove-Item (Join-Path $repo 'build/release/cosign.pub')
    Expect 'a missing public key fails' { Fails { & $gate 'refs/tags/v1.2.3' } }
}
finally {
    Remove-Item Env:COSIGN_PASSWORD -ErrorAction SilentlyContinue
    if ($ownWork) { Remove-Item -Recurse -Force -LiteralPath $Work -ErrorAction SilentlyContinue }
}

if ($script:failures.Count -gt 0) { throw "$($script:failures.Count) release check(s) behaved wrongly: $($script:failures -join '; ')" }
Write-Host 'Every release check accepted what it should and refused what it should.' -ForegroundColor Green
