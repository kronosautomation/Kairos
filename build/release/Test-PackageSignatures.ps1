<#
.SYNOPSIS
  Fails unless every package in the directory carries a valid author signature from the certificate with the given SHA-256 fingerprint.

.DESCRIPTION
  This is the check a consumer can run too: `dotnet nuget verify --all --certificate-fingerprint <sha256> <package>`. It rejects an unsigned package, a package changed after it was signed, and a package
  signed by any other certificate, even a valid one.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Packages,
    [Parameter(Mandatory)][string]$Fingerprint
)

. (Join-Path $PSScriptRoot 'common.ps1')
if ($Fingerprint -notmatch '^[0-9a-fA-F]{64}$') { throw 'Fingerprint must be the 64 hex characters of the certificate''s SHA-256.' }
$files = @(Get-PackageFiles $Packages)
if ($files.Count -eq 0) { throw "No packages in $Packages." }

$failed = @()
foreach ($file in $files) {
    $out = & dotnet nuget verify --all --certificate-fingerprint $Fingerprint $file.FullName 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        $failed += $file.Name
        Write-Host "NOT VERIFIED: $($file.Name)" -ForegroundColor Red
        $out | Where-Object { $_ -match 'NU3\d+|error|Error' } | ForEach-Object { Write-Host "  $_" }
    }
}
if ($failed.Count -gt 0) { throw "$($failed.Count) of $($files.Count) packages do not carry a valid signature from the release certificate." }
Write-Host "Signatures: all $($files.Count) packages are signed by the certificate with fingerprint $($Fingerprint.Substring(0, 16))..." -ForegroundColor Green
