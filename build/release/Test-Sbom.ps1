<#
.SYNOPSIS
  Checks that every package has an SBOM and that the SBOM is about that package and complete.

.DESCRIPTION
  For each package in packages.json:
    - <id>.<version>.cdx.json exists and is CycloneDX JSON of a spec version this check knows;
    - the described component is that package (name, version, a pkg:nuget purl);
    - every component has a name, a version and a purl, and no bom-ref appears twice;
    - every dependency the package's nuspec declares (for any target framework) is a component of the SBOM, so the file lists at least what a consumer is told to install.
  Exits with an error listing everything wrong.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Packages,
    [Parameter(Mandatory)][string]$Sboms
)

. (Join-Path $PSScriptRoot 'common.ps1')
$manifest = Get-Content -LiteralPath (Join-Path $Packages 'packages.json') -Raw | ConvertFrom-Json
$problems = New-Object System.Collections.Generic.List[string]

foreach ($package in $manifest) {
    $path = Join-Path $Sboms "$($package.id).$($package.version).cdx.json"
    if (-not (Test-Path -LiteralPath $path)) { $problems.Add("$($package.id): no SBOM at $path"); continue }
    try { $bom = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $problems.Add("$($package.id): the SBOM is not JSON"); continue }

    if ((Get-Prop $bom 'bomFormat') -ne 'CycloneDX') { $problems.Add("$($package.id): bomFormat is '$(Get-Prop $bom 'bomFormat')'") }
    if ("$(Get-Prop $bom 'specVersion')" -notmatch '^1\.[4-9]$') { $problems.Add("$($package.id): specVersion '$(Get-Prop $bom 'specVersion')' is not one this check knows") }
    $described = Get-Prop (Get-Prop $bom 'metadata') 'component'
    if (-not $described -or (Get-Prop $described 'name') -ne $package.id -or (Get-Prop $described 'version') -ne $package.version) {
        $problems.Add("$($package.id): the described component is '$(Get-Prop $described 'name') $(Get-Prop $described 'version')', not the package")
    }
    elseif ("$(Get-Prop $described 'purl')" -notlike 'pkg:nuget/*') { $problems.Add("$($package.id): the described component has no pkg:nuget purl") }

    $components = @(Get-Prop $bom 'components')
    $refs = @{}
    foreach ($component in $components) {
        if (-not (Get-Prop $component 'name') -or -not (Get-Prop $component 'version') -or -not (Get-Prop $component 'purl')) { $problems.Add("$($package.id): component '$(Get-Prop $component 'name')' lacks a name, version or purl") }
        $ref = Get-Prop $component 'bom-ref'
        if ($ref) {
            if ($refs.ContainsKey($ref)) { $problems.Add("$($package.id): bom-ref '$ref' appears twice") }
            $refs[$ref] = $true
        }
    }

    $names = @{}
    foreach ($component in $components) { if ($component.name) { $names[([string]$component.name).ToLowerInvariant()] = $true } }
    $nuspec = Read-NuspecOf (Join-Path $Packages $package.file)
    $declared = @($nuspec.SelectNodes('//*[local-name()="dependency"]') | ForEach-Object { [string]$_.id } | Sort-Object -Unique)
    foreach ($dependency in $declared) {
        if (-not $names.ContainsKey($dependency.ToLowerInvariant())) { $problems.Add("$($package.id): depends on $dependency, which the SBOM does not list") }
    }

    Write-Host ("{0,-40} {1,3} components, {2} declared dependencies" -f $package.id, $components.Count, $declared.Count)
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "SBOM: $_" -ForegroundColor Red }
    throw "$($problems.Count) problem(s) in the SBOMs."
}
Write-Host "SBOMs: $($manifest.Count) checked, all describe their package and list what it depends on." -ForegroundColor Green
