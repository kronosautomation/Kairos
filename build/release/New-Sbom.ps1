<#
.SYNOPSIS
  Writes a CycloneDX SBOM (JSON) for every package in a directory, then checks each one (Test-Sbom.ps1).

.DESCRIPTION
  One SBOM per package, named <id>.<version>.cdx.json, describing that package: the package itself as the described component, every package it depends on (direct and transitive, resolved by the
  locked restore) and the projects of this repository it references. Development-only dependencies (analyzers, build tasks) are left out, as they are not in what a customer installs.
  The SBOM carries no serial number, so building it twice from the same inputs gives the same file apart from the time stamp.

.PARAMETER Packages  The directory Build-Packages.ps1 wrote (packages.json and the .nupkg files).
.PARAMETER Output    Where the SBOMs go.
.PARAMETER Root      The repository the packages were built from (the projects are found under src/ by package id).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Packages,
    [Parameter(Mandatory)][string]$Output,
    [string]$Root = ''
)

. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Root) { $Root = Get-RepoRoot }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$Packages = (Resolve-Path $Packages).Path

Invoke-Native dotnet @('tool', 'restore') -WorkingDirectory $Root -Quiet | Out-Null

$projects = @{}
(@(Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Recurse -Filter '*.csproj' -File | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }) + @(Get-Item -LiteralPath (Join-Path $Root 'tests/Kairos.Testing/Kairos.Testing.csproj'))) | ForEach-Object {
    $xml = [xml](Get-Content -LiteralPath $_.FullName -Raw)
    $explicit = $xml.SelectSingleNode('//PackageId')
    $projects[$(if ($explicit) { [string]$explicit.InnerText } else { $_.BaseName })] = $_.FullName
}

$manifest = Get-Content -LiteralPath (Join-Path $Packages 'packages.json') -Raw | ConvertFrom-Json
$ids = @{}
foreach ($package in $manifest) { $ids[[string]$package.id] = $true }
foreach ($package in $manifest) {
    if (-not $projects.ContainsKey($package.id)) { throw "No project under src/ produces package '$($package.id)'." }
    $file = "$($package.id).$($package.version).cdx.json"
    Invoke-Native dotnet @(
        'tool', 'run', 'dotnet-CycloneDX', '--', $projects[$package.id],
        '--output', $Output, '--filename', $file, '--output-format', 'Json',
        '--set-name', $package.id, '--set-version', $package.version, '--set-type', 'Library', '--set-nuget-purl',
        '--include-project-references', '--exclude-dev', '--no-serial-number', '--configuration', 'Release', '--disable-package-restore'
    ) -WorkingDirectory $Root -Quiet | Out-Null
    if (-not (Test-Path (Join-Path $Output $file))) { throw "The SBOM of $($package.id) was not written." }

    # A project of this repository that the package references is published in the same release: it is a package at the release version, not an assembly at 1.0.0 with no purl.
    $path = Join-Path $Output $file
    $bom = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
    $renamed = @{}
    foreach ($component in @(Get-Prop $bom 'components')) {
        if ($ids.ContainsKey([string]$component.name) -and -not (Get-Prop $component 'purl')) {
            $old = [string]$component.'bom-ref'
            $new = "pkg:nuget/$($component.name)@$($package.version)"
            $renamed[$old] = $new
            $component.version = $package.version
            $component.'bom-ref' = $new
            $component | Add-Member -NotePropertyName purl -NotePropertyValue $new
        }
    }
    foreach ($dependency in @(Get-Prop $bom 'dependencies')) {
        if ($renamed.ContainsKey([string]$dependency.ref)) { $dependency.ref = $renamed[[string]$dependency.ref] }
        if (Get-Prop $dependency 'dependsOn') { $dependency.dependsOn = @($dependency.dependsOn | ForEach-Object { if ($renamed.ContainsKey([string]$_)) { $renamed[[string]$_] } else { $_ } }) }
    }
    $bom | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
}

& (Join-Path $PSScriptRoot 'Test-Sbom.ps1') -Packages $Packages -Sboms $Output
