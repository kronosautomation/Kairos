<#
.SYNOPSIS
  Runs the Kairos CI stages locally in Docker, matching azure-pipelines.yml.
.EXAMPLE
  .\ci-local.ps1                       # Fast stage on .NET 10 (what every PR runs)
  .\ci-local.ps1 -Tfm net11.0          # Fast stage on .NET 11 / Runtime Async
  .\ci-local.ps1 -Stage Integration -PgImage postgres:15
  .\ci-local.ps1 -Stage Simulation -SimMinutes 30
  .\ci-local.ps1 -Stage All
#>
param(
  [ValidateSet('Fast','Integration','Simulation','Mutation','Supply','All')] [string]$Stage = 'Fast',
  [ValidateSet('net10.0','net11.0')] [string]$Tfm = 'net10.0',
  [int]$SimSeeds = 1000,
  [int]$SimMinutes = 0,
  [string]$PgImage = 'postgres:17',
  [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force -Path .ci-local | Out-Null
docker info *> $null; if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop is not running.' }

$env:KAIROS_SIM_SEEDS = if ($Stage -eq 'Simulation' -and $SimMinutes -gt 0) { '0' } else { "$SimSeeds" }
$env:KAIROS_SIM_DURATION_MINUTES = "$SimMinutes"
$env:KAIROS_SIM_START_SEED = if ($Stage -eq 'Simulation') { "$(Get-Random -Minimum 1 -Maximum 2147483647)" } else { '1' }
$env:KAIROS_PG_IMAGE = $PgImage

$compose = @('compose','-f','docker/compose.yml','--profile','ci')
if ($Rebuild) { docker @compose build --no-cache ci }
docker @compose run --rm ci $Stage $Tfm
if ($LASTEXITCODE -ne 0) { Write-Host "FAILED. See .ci-local for results and failing seeds." -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host "Passed. Results in .ci-local" -ForegroundColor Green
