<#
.SYNOPSIS
  Pins every container image used by Kairos to an immutable digest ("tag@sha256:..."), so nothing changes until we
  re-pin and re-test on purpose. Covers docker/compose.yml and the FROM lines of every Dockerfile under docker/.
.EXAMPLE
  .\docker\pin-images.ps1          # pull each unpinned image and write its digest into the files
  .\docker\pin-images.ps1 -Check   # report unpinned images and exit 1 if any (for CI)
  .\docker\pin-images.ps1 -Refresh # re-resolve already pinned images too (deliberate upgrade; re-run the tests after)
#>
param([switch]$Check, [switch]$Refresh)
$ErrorActionPreference = 'Continue'   # docker writes progress to stderr; rely on exit codes
Set-Location (Split-Path $PSScriptRoot -Parent)

$files = @('docker/compose.yml') + (Get-ChildItem docker -Recurse -Filter '*Dockerfile*' | ForEach-Object { $_.FullName })
$patterns = @(
  @{ Regex = 'image:\s*"(?<ref>[^"]+)"'; Make = { param($r) "image: `"$r`"" } },
  @{ Regex = '(?m)^FROM\s+(?<ref>\S+)';   Make = { param($r) "FROM $r" } }
)
$cache = @{}
$unpinned = New-Object System.Collections.Generic.List[string]

function Resolve-Digest([string]$ref) {
  if ($cache.ContainsKey($ref)) { return $cache[$ref] }
  Write-Host "pulling $ref"
  docker pull --quiet $ref | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Host "  could not pull $ref, left unchanged" -ForegroundColor Yellow; $cache[$ref] = $null; return $null }
  $repoDigest = docker image inspect $ref --format '{{index .RepoDigests 0}}' 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $repoDigest) { $cache[$ref] = $null; return $null }
  $digest = ($repoDigest -split '@')[1]
  $cache[$ref] = $digest
  return $digest
}

foreach ($file in $files) {
  $text = Get-Content $file -Raw
  $orig = $text
  foreach ($p in $patterns) {
    $text = [regex]::Replace($text, $p.Regex, {
      param($m)
      $ref = $m.Groups['ref'].Value
      $base = ($ref -split '@')[0]
      $pinned = $ref -match '@sha256:'
      if ($pinned -and -not $Refresh) { return $m.Value }
      if ($Check) { $unpinned.Add("$file -> $ref"); return $m.Value }
      $digest = Resolve-Digest $base
      if (-not $digest) { return $m.Value }
      return (& $p.Make "$base@$digest")
    })
  }
  if (-not $Check -and $text -ne $orig) { Set-Content -Path $file -Value $text -NoNewline; Write-Host "updated $file" -ForegroundColor Green }
}

if ($Check) {
  if ($unpinned.Count -gt 0) { $unpinned | ForEach-Object { Write-Host "unpinned: $_" -ForegroundColor Red }; exit 1 }
  Write-Host 'All images are pinned.' -ForegroundColor Green
}
