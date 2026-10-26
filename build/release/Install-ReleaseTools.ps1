<#
.SYNOPSIS
  Downloads cosign and syft at the versions pinned here, checks each download against the SHA-256 pinned here, and puts them in a directory (and on PATH for the rest of an Azure Pipelines job).

.DESCRIPTION
  Nothing is fetched "latest", and nothing that does not match its pinned hash is run. To upgrade a tool, change the version and both hashes in one commit, taken from the release's own checksums
  file and cross-checked against the hash GitHub reports for the asset, and run the signing and verification tests again (docs/RELEASE.md, "Upgrading a release tool").
  A pinned release is at least two weeks old when it is pinned: a compromised release is usually noticed within days.

  The .NET tools of the release (CycloneDX, sign) are not here: they are pinned in .config/dotnet-tools.json and installed by `dotnet tool restore`.
#>
[CmdletBinding()]
param([string]$Directory = '')

. (Join-Path $PSScriptRoot 'common.ps1')

$Tools = @{
    cosign = @{
        Version = '3.1.3'
        Url     = 'https://github.com/sigstore/cosign/releases/download/v{0}/{1}'
        Assets  = @{
            'linux-amd64'   = @{ File = 'cosign-linux-amd64'; Sha256 = '4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71'; Binary = 'cosign' }
            'windows-amd64' = @{ File = 'cosign-windows-amd64.exe'; Sha256 = '9fe59be0eca1271873ce019061335eb1ac419b7059202e797828467ddabe33be'; Binary = 'cosign.exe' }
        }
    }
    syft   = @{
        Version = '1.54.1'
        Url     = 'https://github.com/anchore/syft/releases/download/v{0}/{1}'
        Assets  = @{
            'linux-amd64'   = @{ File = 'syft_{0}_linux_amd64.tar.gz'; Sha256 = 'c069905b391cc4c20a5ba65ad5c10be2a7ba074f8ea6ad203e24d14e303dad47'; Binary = 'syft'; Archive = 'tar' }
            'windows-amd64' = @{ File = 'syft_{0}_windows_amd64.zip'; Sha256 = '8b56e8285e295e0bbed26eeea9b16ed51c493be97ccdf42dae6326c84fe8e19f'; Binary = 'syft.exe'; Archive = 'zip' }
        }
    }
}

$platform = if ($IsWindows) { 'windows-amd64' } elseif ($IsLinux) { 'linux-amd64' } else { throw 'The release tools are pinned for Linux and Windows on x64 only.' }
if (-not $Directory) { $Directory = Join-Path ([IO.Path]::GetTempPath()) 'kairos-release-tools' }
New-Item -ItemType Directory -Force -Path $Directory | Out-Null
$Directory = (Resolve-Path $Directory).Path

foreach ($name in $Tools.Keys) {
    $tool = $Tools[$name]
    $asset = $tool.Assets[$platform]
    $file = $asset.File -f $tool.Version
    $download = Join-Path $Directory $file
    $url = $tool.Url -f $tool.Version, $file
    Write-Host "$name $($tool.Version): $url"
    Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing
    Assert-FileHash -Path $download -Sha256 $asset.Sha256

    $target = Join-Path $Directory $asset.Binary
    switch ($(if ($asset.ContainsKey('Archive')) { $asset['Archive'] } else { 'none' })) {
        'tar' { Invoke-Native tar @('-xzf', $download, '-C', $Directory, $asset.Binary) -Quiet | Out-Null }
        'zip' { Expand-Archive -LiteralPath $download -DestinationPath $Directory -Force }
        default { Move-Item -Force $download $target }
    }
    if (-not $IsWindows) { Invoke-Native chmod @('+x', $target) -Quiet | Out-Null }
    Write-Host "  verified and installed: $target"
}

if ($env:TF_BUILD) { Write-Host "##vso[task.prependpath]$Directory" }
else { Write-Host "Add $Directory to PATH to use them." }
$Directory
