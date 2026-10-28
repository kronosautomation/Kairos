<#
.SYNOPSIS
  The checks that must hold before a release is built at all. Fails, listing everything wrong, so that a missing piece is found in the first minute and not after an hour of signing.

.DESCRIPTION
  - The tag is a version: v<major>.<minor>.<patch> with an optional prerelease, and nothing else.
  - The released commit is on the main branch (a release is never cut from a side branch).
  - SECURITY.md exists and says where to report a vulnerability.
  - build/release/cosign.pub exists and is a public key: the key attestations are verified against. The pipeline does not trust a key it finds in the vault on the day; it trusts this file, which a
    reviewed commit put here.
  - No review with open findings is in the tree (docs/reviews): a review that lists what is still unfixed is internal until a fix has shipped (docs/DISCLOSURE-POLICY.md). A review whose open items are all
    closed may stay; a file under docs/reviews with an "Open items" or "Open findings" heading, a "Not started" status or a "Tracked" row fails the gate.
  - Nothing that is published still has a place-holder to fill in ("REPLACE-" followed by a name): the docs site, samples, README, SECURITY.md, package metadata, security.txt settings.
  - No known high or critical vulnerability in what ships (the same check the per-commit pipeline makes).

.PARAMETER Tag          refs/tags/v1.2.3 or v1.2.3.
.PARAMETER Root         The repository.
.PARAMETER SkipMainCheck, SkipVulnerabilityCheck  For tests, and for a repository with no origin.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Root = '',
    [string]$MainBranch = 'origin/main',
    [string[]]$PublishedPaths = @('README.md', 'SECURITY.md', 'docs-site', 'samples', 'src', 'Directory.Build.props', 'build/release/cosign.pub'),
    [switch]$SkipMainCheck,
    [switch]$SkipVulnerabilityCheck
)

. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Root) { $Root = Get-RepoRoot }
$PublishedPaths = @($PublishedPaths | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) # "a,b,c" arrives as one string when the script is started with -File
$problems = New-Object System.Collections.Generic.List[string]

$name = $Tag -replace '^refs/tags/', ''
if ($name -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { $problems.Add("'$name' is not a release tag (v1.2.3 or v1.2.3-rc.1).") }

if (-not $SkipMainCheck) {
    & git -C $Root merge-base --is-ancestor HEAD $MainBranch 2>$null
    if ($LASTEXITCODE -ne 0) { $problems.Add("The released commit is not on $MainBranch. A release is cut from main.") }
}

$security = Join-Path $Root 'SECURITY.md'
if (-not (Test-Path -LiteralPath $security)) { $problems.Add('SECURITY.md is missing.') }
else {
    $text = Get-Content -LiteralPath $security -Raw
    if ($text -notmatch '(?i)report') { $problems.Add('SECURITY.md does not say how to report a vulnerability.') }
    if ($text -notmatch '(?i)mailto:|https://\S+/(security|advisories)|@') { $problems.Add('SECURITY.md has no contact to report to.') }
}

$pub = Join-Path $Root 'build/release/cosign.pub'
if (-not (Test-Path -LiteralPath $pub)) { $problems.Add('build/release/cosign.pub is missing: export the release key''s public half from Key Vault (cosign public-key --key azurekms://...) and commit it (docs/RELEASE.md, one-time setup).') }
elseif ((Get-Content -LiteralPath $pub -Raw) -notmatch '-----BEGIN PUBLIC KEY-----[\s\S]+-----END PUBLIC KEY-----') { $problems.Add('build/release/cosign.pub is not a PEM public key.') }

$reviews = Join-Path $Root 'docs/reviews'
if (Test-Path -LiteralPath $reviews) {
    foreach ($file in Get-ChildItem -LiteralPath $reviews -Recurse -File -Filter '*.md') {
        $hit = Select-String -LiteralPath $file.FullName -Pattern '^#{1,6}\s.*\bopen (items|findings)\b', '\bNot started\b', '\|\s*Tracked\s*\|' -List
        if ($hit) { $problems.Add("A review with open findings is in the tree and would be published: $($file.FullName.Substring($Root.Length).TrimStart('', '/')) (line $($hit.LineNumber)). Move it to the private repository, or close the findings.") }
    }
}

$left = @()
foreach ($path in $PublishedPaths) {
    $full = Join-Path $Root $path
    if (-not (Test-Path -LiteralPath $full)) { continue }
    $files = if ((Get-Item -LiteralPath $full).PSIsContainer) {
        Get-ChildItem -LiteralPath $full -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](obj|bin|node_modules|_site)[\\/]' -and $_.Extension -in '.md', '.cs', '.csproj', '.props', '.json', '.yml', '.yaml', '.html', '.txt', '.xml', '.toml' }
    } else { Get-Item -LiteralPath $full }
    foreach ($file in $files) {
        $hits = Select-String -LiteralPath $file.FullName -Pattern 'REPLACE-[A-Za-z0-9_-]+' -AllMatches -ErrorAction SilentlyContinue
        foreach ($hit in $hits) { $left += "$($file.FullName.Substring($Root.Length).TrimStart('\', '/')):$($hit.LineNumber) $($hit.Matches[0].Value)" }
    }
}
foreach ($item in ($left | Select-Object -First 20)) { $problems.Add("A place-holder is left in published text: $item") }
if ($left.Count -gt 20) { $problems.Add("... and $($left.Count - 20) more place-holders.") }

if (-not $SkipVulnerabilityCheck) {
    $solution = Get-ChildItem -LiteralPath $Root -Filter '*.sln' -File | Select-Object -First 1
    if ($solution) {
        $report = (& dotnet list $solution.FullName package --vulnerable --include-transitive 2>&1 | ForEach-Object { "$_" }) -join "`n"
        if ($report -match '(?m)>\s.*\b(High|Critical)\b') { $problems.Add("A package with a high or critical advisory ships:`n$report") }
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "RELEASE GATE: $_" -ForegroundColor Red }
    throw "$($problems.Count) thing(s) stand between this commit and a release."
}
Write-Host "Release gate passed for $name." -ForegroundColor Green
