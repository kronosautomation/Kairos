<#
.SYNOPSIS
  Author-signs every NuGet package in a directory with the release certificate in Azure Key Vault, time-stamps the signature, and verifies the result (Test-PackageSignatures.ps1).

.DESCRIPTION
  The certificate's private key stays in Key Vault: the `sign` tool (Microsoft's, pinned in .config/dotnet-tools.json) sends each digest to the vault and puts the signature into the package. The identity that runs
  this needs, on that vault, only the permissions to read the certificate and to sign with its key (roles "Key Vault Certificate User" and "Key Vault Crypto User"); the pipeline gets it from a workload
  identity federation, not a secret. Packages are signed in place. Run it before Attest-Packages.ps1 (the attestations name the signed file).

.PARAMETER KeyVaultUrl        https://{vault}.vault.azure.net
.PARAMETER Certificate        The certificate's name in the vault (a code-signing certificate, RSA 3072 or more, from a CA NuGet trusts).
.PARAMETER Fingerprint        The SHA-256 fingerprint the signature must come from: what a consumer would pin. Verification fails for any other certificate.
.PARAMETER CredentialType     azure-cli (a pipeline's AzureCLI task), workload-identity, managed-identity.
.PARAMETER Timestamper        An RFC 3161 time-stamp authority. Without a time stamp the signature stops being valid when the certificate expires.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Packages,
    [Parameter(Mandatory)][string]$KeyVaultUrl,
    [Parameter(Mandatory)][string]$Certificate,
    [Parameter(Mandatory)][string]$Fingerprint,
    [string]$CredentialType = 'azure-cli',
    [string]$Timestamper = 'http://timestamp.acs.microsoft.com/',
    [string]$Root = ''
)

. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Root) { $Root = Get-RepoRoot }
if ($KeyVaultUrl -notmatch '^https://[a-z0-9-]+\.vault\.azure\.net/?$') { throw "KeyVaultUrl '$KeyVaultUrl' is not a Key Vault address." }
if ($Fingerprint -notmatch '^[0-9a-fA-F]{64}$') { throw 'Fingerprint must be the 64 hex characters of the certificate''s SHA-256.' }
$Packages = (Resolve-Path $Packages).Path
$files = @(Get-PackageFiles $Packages)
if ($files.Count -eq 0) { throw "No packages in $Packages." }

Invoke-Native dotnet @('tool', 'restore') -WorkingDirectory $Root -Quiet | Out-Null
$arguments = @(
    'tool', 'run', 'sign', '--', 'code', 'azure-key-vault') + @($files | ForEach-Object { $_.Name }) + @(
    '--base-directory', $Packages,
    '--azure-key-vault-url', $KeyVaultUrl, '--azure-key-vault-certificate', $Certificate, '--azure-credential-type', $CredentialType,
    '--file-digest', 'sha256', '--timestamp-url', $Timestamper, '--timestamp-digest', 'sha256',
    '--description', 'Kairos', '--verbosity', 'Information')
Invoke-Native dotnet $arguments -WorkingDirectory $Root | Out-Null

& (Join-Path $PSScriptRoot 'Test-PackageSignatures.ps1') -Packages $Packages -Fingerprint $Fingerprint
