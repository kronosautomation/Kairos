<#
.SYNOPSIS
  Builds the committed state twice, from two fresh clones in two different directories, and fails unless every package contains exactly the same files.

.DESCRIPTION
  What it proves: the packages are a function of the commit and the pinned toolchain, not of the machine, the directory or the moment they were built.
  - Each build starts from a fresh clone of HEAD (uncommitted changes are not built, and the script refuses to run with any), at a different depth, so a path that leaks into an assembly is found.
  - Each package of one build is compared with its twin: the same entries, each with the same SHA-256. (A .nupkg is a zip, and NuGet writes the time of packing into the zip headers; the bytes of the
    archive differ for that reason alone. What a package contains is what is compared, and what is signed afterwards is a package, so a signature does not hide a difference.)
  - Exit code 1 and a list of the differing entries when anything differs.

.PARAMETER Root      The repository to build (its HEAD).
.PARAMETER Siblings  Other repositories the projects reference by relative path (the commercial repository needs the public one), cloned next to it.
.PARAMETER Version   The version both builds use. Default 0.0.0-reproducibility.
.PARAMETER Work      Scratch directory. Default: a new directory under the system temp directory, removed when done unless -Keep.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string[]]$Siblings = @(),
    [string]$Version = '0.0.0-reproducibility',
    [string]$Work = '',
    [switch]$Keep,
    [switch]$AllowDirty
)

. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Root) { $Root = Get-RepoRoot }
$repos = @($Root) + $Siblings | ForEach-Object { (Resolve-Path $_).Path }

foreach ($repo in $repos) {
    $dirty = & git -C $repo status --porcelain
    if ($dirty -and -not $AllowDirty) { throw "$repo has uncommitted changes. The check builds what is committed, so commit first (or -AllowDirty to build HEAD anyway)." }
}

$ownWork = -not $Work
if ($ownWork) { $Work = Join-Path ([IO.Path]::GetTempPath()) ("kairos-repro-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# Two different places: a different directory depth changes every absolute path the compiler could have written down.
$places = @(@('one', 'a'), @('two', 'deeper', 'still', 'b'))
$builds = @()
try {
    for ($i = 0; $i -lt 2; $i++) {
        $base = Join-Path $Work (($places[$i]) -join [IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Force -Path $base | Out-Null
        foreach ($repo in $repos) {
            $name = Split-Path -Leaf $repo
            $commit = (& git -C $repo rev-parse HEAD).Trim()
            Invoke-Native git @('clone', '--quiet', '--no-checkout', $repo, (Join-Path $base $name)) -Quiet | Out-Null
            Invoke-Native git @('-C', (Join-Path $base $name), 'checkout', '--quiet', $commit) -Quiet | Out-Null
            Write-Host "build $($i + 1): $name at $commit in $base"
        }

        $out = Join-Path $Work "packages-$($i + 1)"
        & (Join-Path $PSScriptRoot 'Build-Packages.ps1') -Version $Version -Output $out -Root (Join-Path $base (Split-Path -Leaf $Root)) | Out-Host
        $builds += $out
    }

    $problems = Compare-PackageSets $builds[0] $builds[1]
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "NOT REPRODUCIBLE: $_" -ForegroundColor Red }
        throw "$($problems.Count) difference(s) between two builds of the same commit."
    }

    $first = @{}
    Get-PackageFiles $builds[0] | ForEach-Object { $first[$_.Name] = $_.FullName }
    $second = @{}
    Get-PackageFiles $builds[1] | ForEach-Object { $second[$_.Name] = $_.FullName }
    $count = $first.Count
    $identical = @($first.Keys | Where-Object { (Get-Sha256 $first[$_]) -eq (Get-Sha256 $second[$_]) }).Count
    Write-Host "Reproducible: $count packages, every entry of every package identical across two builds ($identical of them byte-identical as archives; the others differ only in the zip headers' time stamps)." -ForegroundColor Green
}
finally {
    if ($ownWork -and -not $Keep) { Remove-Item -Recurse -Force -LiteralPath $Work -ErrorAction SilentlyContinue }
    elseif ($Keep) { Write-Host "Kept $Work" }
}
