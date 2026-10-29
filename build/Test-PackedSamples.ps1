<#
.SYNOPSIS
  Proves the packages work as packages: packs every package to a local folder, then restores, builds and runs the samples from that folder only (no project references).

.DESCRIPTION
  1. Packs the repository (build/release/Build-Packages.ps1) into a fresh folder feed.
  2. Checks that every Kairos package the documentation tells a reader to install (docs-site: `dotnet add package`, `dotnet tool install`, <PackageReference>) is in the feed.
  3. Installs the `kairos` tool from the feed, then provisions and migrates a PostgreSQL database with it (`kairos db provision`, `kairos db migrate`, twice).
  4. Copies each sample outside the repository, replaces its project references with package references to the feed's version, restores with a nuget.config whose Kairos.* source is the
     feed alone (third-party packages come from nuget.org), an empty global packages folder and --no-cache, builds, and runs the ones that have a main program.

  PostgreSQL: set KAIROS_SAMPLE_ADMIN_CONNECTION to an administrator connection string of a throwaway server (the Linux CI job starts a postgres container), or let the script start a throwaway
  cluster from the PostgreSQL binaries it finds (-PostgresBin, PATH, or the usual install folders), as the Windows CI job does. Works on Linux and Windows. PowerShell 7.

.PARAMETER Version   Package version for the pack. Default 1.0.0-ci.local.
.PARAMETER Work      Working folder (feed, sample copies, packages). Default: a new, short-named folder under the temp directory (Windows paths stop at 260 characters and package folders are deep; keep it short). Removed at the end unless -Keep.
.PARAMETER Samples   Sample folders under samples/ to restore and build from the feed. Those with a main program (GettingStarted) are also run.
.PARAMETER PostgresBin  Folder with initdb and pg_ctl, when no admin connection is given and they are not on PATH.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0-ci.local',
    [string]$Work = '',
    [string[]]$Samples = @('GettingStarted', 'Concepts', 'AgentSamples'),
    [string]$PostgresBin = '',
    [switch]$Keep
)

. (Join-Path $PSScriptRoot 'release/common.ps1')
$root = Get-RepoRoot
if (-not $Work) { $Work = Join-Path ([IO.Path]::GetTempPath()) ("kp-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$Work = (Resolve-Path $Work).Path
$feed = Join-Path $Work 'feed'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

function Set-Login([string]$Connection, [string]$User, [string]$Password) {
    <# The connection string with another login: user and password keys are dropped and replaced. #>
    $kept = $Connection.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -notmatch '^\s*(Username|User ID|UserID|User Name|Password|Pwd)\s*=' }
    return (($kept + "Username=$User" + "Password=$Password") -join ';')
}

$postgres = $null   # a throwaway cluster this script started, to stop at the end
try {
    # 1. Pack ---------------------------------------------------------------------------------------------------------------------------------------------------
    Write-Step "Pack $Version into $feed"
    & (Join-Path $root 'build/release/Build-Packages.ps1') -Version $Version -Output $feed -Force
    $packed = @(Get-Content -LiteralPath (Join-Path $feed 'packages.json') -Raw | ConvertFrom-Json)
    $ids = $packed | ForEach-Object { $_.id }

    # 2. Every package the documentation names is in the feed ----------------------------------------------------------------------------------------------------
    Write-Step 'Packages named in docs-site are in the feed'
    $named = [System.Collections.Generic.SortedSet[string]]::new()
    foreach ($page in Get-ChildItem -LiteralPath (Join-Path $root 'docs-site') -Recurse -Filter '*.md' -File) {
        $text = Get-Content -LiteralPath $page.FullName -Raw
        foreach ($m in [regex]::Matches($text, 'dotnet add (?:\S+ )?package (Kairos\.[A-Za-z0-9.]+)')) { [void]$named.Add($m.Groups[1].Value) }
        foreach ($m in [regex]::Matches($text, 'dotnet tool install[^\r\n]*?\b(Kairos\.[A-Za-z0-9.]+)')) { [void]$named.Add($m.Groups[1].Value) }
        foreach ($m in [regex]::Matches($text, '<PackageReference Include="(Kairos\.[A-Za-z0-9.]+)"')) { [void]$named.Add($m.Groups[1].Value) }
    }
    $missing = @($named | Where-Object { $_ -notin $ids })
    if ($missing.Count -gt 0) { throw "The documentation names packages that are not in the feed: $($missing -join ', ')" }
    Write-Host "  $($named.Count) named, all present: $($named -join ', ')"

    # nuget.config shared by everything below: Kairos.* from the feed alone, the rest from nuget.org. An empty packages folder means nothing resolves from a machine's cache.
    $packages = Join-Path $Work 'pkg'
    $nugetConfig = Join-Path $Work 'nuget.config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="kairos-feed" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="kairos-feed"><package pattern="Kairos.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8NoBOM

    # PostgreSQL --------------------------------------------------------------------------------------------------------------------------------------------------
    $adminConnection = $env:KAIROS_SAMPLE_ADMIN_CONNECTION
    if (-not $adminConnection) {
        Write-Step 'Start a throwaway PostgreSQL cluster'
        $candidates = @($PostgresBin) + @(Get-ChildItem -Path '/usr/lib/postgresql/*/bin', 'C:\Program Files\PostgreSQL\*\bin' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | ForEach-Object FullName)
        $bin = $candidates | Where-Object { $_ -and (Get-ChildItem -LiteralPath $_ -Filter 'initdb*' -ErrorAction SilentlyContinue) } | Select-Object -First 1
        if (-not $bin) {
            $onPath = Get-Command initdb -ErrorAction SilentlyContinue
            if ($onPath) { $bin = Split-Path -Parent $onPath.Source }
        }
        if (-not $bin) { throw 'No PostgreSQL to use: set KAIROS_SAMPLE_ADMIN_CONNECTION, pass -PostgresBin, or put initdb on PATH.' }
        $data = Join-Path $Work 'pgdata'
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        # Trust authentication on the loopback interface of a cluster that lives for this run only; the roles and what they may do are still the real ones.
        Invoke-Native (Join-Path $bin 'initdb') @('-D', $data, '-U', 'postgres', '--auth=trust', '-E', 'UTF8') | Out-Null
        $options = "-p $port -c listen_addresses=127.0.0.1"
        if (-not $IsWindows) { $options += " -c unix_socket_directories=$Work" }
        Invoke-Native (Join-Path $bin 'pg_ctl') @('-D', $data, '-o', $options, '-l', (Join-Path $Work 'postgres.log'), '-w', 'start') | Out-Null
        $postgres = @{ Bin = $bin; Data = $data }
        $adminConnection = "Host=127.0.0.1;Port=$port;Username=postgres;Database=postgres"
    }

    # 3. The packed tool provisions and migrates ------------------------------------------------------------------------------------------------------------------
    Write-Step 'Install kairos from the feed; db provision, db migrate (twice)'
    $tools = Join-Path $Work 'tools'
    Invoke-Native dotnet @('tool', 'install', 'Kairos.Cli', '--version', $Version, '--tool-path', $tools, '--configfile', $nugetConfig, '--no-http-cache') -WorkingDirectory $Work | Out-Null
    $kairos = Join-Path $tools ($IsWindows ? 'kairos.exe' : 'kairos')

    $migratorPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    $dataPlanePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    $otherPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    $env:KAIROS_ADMIN_CONNECTION = $adminConnection
    $env:KAIROS_MIGRATOR_PASSWORD = $migratorPassword
    $env:KAIROS_DATAPLANE_PASSWORD = $dataPlanePassword
    $env:KAIROS_CONTROLPLANE_PASSWORD = $otherPassword
    $env:KAIROS_READONLY_PASSWORD = $otherPassword
    Invoke-Native $kairos @('db', 'provision') | Out-Null
    $env:KAIROS_CONNECTION = Set-Login $adminConnection 'kairos_migrator' $migratorPassword
    $first = (Invoke-Native $kairos @('db', 'migrate')) -join "`n"
    if ($first -notmatch 'Applied \d+ migration') { throw "The first db migrate applied nothing: $first" }
    $second = (Invoke-Native $kairos @('db', 'migrate')) -join "`n"
    if ($second -notmatch 'nothing to apply') { throw "The second db migrate was not a no-op: $second" }
    $dataPlaneConnection = Set-Login $adminConnection 'kairos_dataplane' $dataPlanePassword

    # 4. Samples from the feed --------------------------------------------------------------------------------------------------------------------------------------
    [xml]$central = Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw
    $thirdParty = @{}
    foreach ($v in $central.SelectNodes('//PackageVersion')) { $thirdParty[$v.GetAttribute('Include')] = $v.GetAttribute('Version') }

    @'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $Work 'Directory.Build.props') -Encoding utf8NoBOM

    foreach ($sample in $Samples) {
        Write-Step "Sample $sample from the feed"
        $source = Join-Path $root "samples/$sample"
        $dest = Join-Path $Work "samples/$sample"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Get-ChildItem -LiteralPath $source -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -ne 'packages.lock.json' } |
            ForEach-Object {
                $target = Join-Path $dest ([IO.Path]::GetRelativePath($source, $_.FullName))
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target
            }

        $project = Join-Path $dest "$sample.csproj"
        [xml]$xml = Get-Content -LiteralPath $project -Raw
        foreach ($ref in @($xml.SelectNodes('//ProjectReference'))) {
            $id = [IO.Path]::GetFileNameWithoutExtension(($ref.GetAttribute('Include') -replace '\\', '/'))
            if ($id -notin $ids) { throw "$sample references project $id, which is not a packed package." }
            $package = $xml.CreateElement('PackageReference')
            $package.SetAttribute('Include', $id)
            $package.SetAttribute('Version', $Version)
            if ($ref.GetAttribute('OutputItemType') -eq 'Analyzer') { $package.SetAttribute('PrivateAssets', 'all') }
            [void]$ref.ParentNode.ReplaceChild($package, $ref)
        }
        foreach ($ref in @($xml.SelectNodes('//PackageReference[not(@Version) and not(@VersionOverride)]'))) {
            $name = $ref.GetAttribute('Include')
            if (-not $thirdParty.ContainsKey($name)) { throw "$sample uses $name, which Directory.Packages.props has no version for." }
            $ref.SetAttribute('Version', $thirdParty[$name])
        }
        $xml.Save($project)

        Copy-Item -LiteralPath $nugetConfig -Destination (Join-Path $dest 'nuget.config')
        Invoke-Native dotnet @('restore', $project, '--packages', $packages, '--no-cache', '--force', '-p:RestorePackagesWithLockFile=false', '-nologo', '-v', 'q') -WorkingDirectory $dest | Out-Null
        Invoke-Native dotnet @('build', $project, '-c', 'Release', '--no-restore', '-nologo', '-v', 'q') -WorkingDirectory $dest | Out-Null

        # Every Kairos package this sample resolved came from the feed (a project reference, or a package from somewhere else, would show here).
        $assets = Get-Content -LiteralPath (Join-Path $dest 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $kairosLibs = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Kairos.*' })
        if ($kairosLibs.Count -eq 0) { throw "$sample resolved no Kairos package." }
        $notPackages = @($kairosLibs | Where-Object { $_.Value.type -ne 'package' })
        if ($notPackages.Count -gt 0) { throw "$sample did not take $($notPackages.Name -join ', ') from a package." }
        Write-Host "  restored $($kairosLibs.Count) Kairos packages: $(($kairosLibs.Name | Sort-Object) -join ', ')"

        if ($sample -eq 'GettingStarted') {
            $env:ConnectionStrings__Kairos = $dataPlaneConnection
            $run = (Invoke-Native dotnet @('run', '--project', $project, '-c', 'Release', '--no-build') -WorkingDirectory $dest) -join "`n"
            foreach ($expected in 'Charging order-1001', 'Shipping order-1001', 'receipt') {
                if ($run -notlike "*$expected*") { throw "Running $sample from the feed did not print '$expected'. Output:`n$run" }
            }
            Remove-Item Env:ConnectionStrings__Kairos
        }
    }

    Write-Host "`nThe packages restore, build and run from the feed alone." -ForegroundColor Green
}
finally {
    foreach ($name in 'KAIROS_ADMIN_CONNECTION', 'KAIROS_MIGRATOR_PASSWORD', 'KAIROS_DATAPLANE_PASSWORD', 'KAIROS_CONTROLPLANE_PASSWORD', 'KAIROS_READONLY_PASSWORD', 'KAIROS_CONNECTION', 'ConnectionStrings__Kairos') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
    if ($postgres) {
        & (Join-Path $postgres.Bin 'pg_ctl') -D $postgres.Data -m immediate stop 2>&1 | Out-Null
    }
    if (-not $Keep) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Kept $Work" }
}
