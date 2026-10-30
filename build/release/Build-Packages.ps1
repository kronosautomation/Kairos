<#
.SYNOPSIS
  Builds and packs every package of the repository the way a release does: locked restore, Release, deterministic, the SDK that global.json names.

.PARAMETER Version      The package version (SemVer), e.g. 1.4.0 or 1.4.0-rc.1.
.PARAMETER Output       Where the .nupkg files and packages.json (id, version, sha256 of each) are written. Created if missing; must be empty unless -Force.
.PARAMETER Root         The repository root. Defaults to the one this script is in.
.PARAMETER Solution     The solution to restore. Defaults to the only .sln in Root.

.NOTES
  Sets CI=true, which switches on ContinuousIntegrationBuild and the path mapping of Directory.Build.props: nothing of the machine that built the package ends up in it.
  Only projects under src/ (and Kairos.Testing, which ships for customers' tests) are packed; a project that is not packable produces nothing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Output,
    [string]$Root = '',
    [string]$Solution = '',
    [string]$Configuration = 'Release',
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Root) { $Root = Get-RepoRoot }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not a SemVer version without build metadata." }
if (-not $Solution) { $Solution = (Get-ChildItem -LiteralPath $Root -Filter '*.sln' -File | Select-Object -First 1 -ExpandProperty Name) }
if (-not $Solution) { throw "No solution in $Root." }

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path $Output).Path
if ((Get-ChildItem -LiteralPath $Output -Force | Measure-Object).Count -gt 0) {
    if (-not $Force) { throw "$Output is not empty. Packages from an earlier build must not be mistaken for this one (use -Force to clear it)." }
    Get-ChildItem -LiteralPath $Output -Force | Remove-Item -Recurse -Force
}

$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Write-Host "SDK $(& dotnet --version), version $Version, $Configuration, from $Root"

Invoke-Native dotnet @('restore', $Solution, '-p:RestoreLockedMode=true', '-nologo', '-v', 'q') -WorkingDirectory $Root

# Everything under src/, plus the projects under tests/ that ship as packages for customers' own tests (docs/REPO-LAYOUT.md).
$shippedFromTests = @('Kairos.Testing')
$projects = @(Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Recurse -Filter '*.csproj' -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | Sort-Object FullName) +
    @($shippedFromTests | ForEach-Object { Join-Path $Root "tests/$_/$_.csproj" } | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ })  # the private repository has none
foreach ($project in $projects) {
    Invoke-Native dotnet @('pack', $project.FullName, '-c', $Configuration, '--no-restore', '-o', $Output, "-p:Version=$Version", '-nologo', '-v', 'q') -WorkingDirectory $Root -Quiet | Out-Null
}

$packages = @(Get-PackageFiles $Output)
if ($packages.Count -eq 0) { throw 'Nothing was packed.' }
$manifest = foreach ($package in $packages) {
    $nuspec = Read-NuspecOf $package.FullName
    $meta = $nuspec.package.metadata
    if ($meta.version -ne $Version) { throw "$($package.Name) has version $($meta.version), not $Version." }
    [ordered]@{ id = [string]$meta.id; version = [string]$meta.version; file = $package.Name; sha256 = (Get-Sha256 $package.FullName) }
}
$manifest | ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $Output 'packages.json') -Encoding utf8NoBOM
Write-Host "Packed $($packages.Count) packages into $Output"
$packages | ForEach-Object { Write-Host "  $($_.Name)" }
