# Shared helpers of the release scripts (dot-source it). PowerShell 7.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Native {
    <# Runs a program, shows its output, and throws if it fails. Native commands do not stop a PowerShell script by themselves. #>
    param([Parameter(Mandatory)][string]$File, [string[]]$Arguments = @(), [string]$WorkingDirectory = (Get-Location).Path, [switch]$Quiet)
    Push-Location $WorkingDirectory
    try {
        $out = & $File @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    if (-not $Quiet) { $out | ForEach-Object { Write-Host $_ } }
    if ($code -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $code" }
    return $out
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-RepoRoot {
    param([string]$From = $PSScriptRoot)
    $dir = Resolve-Path $From
    while ($dir -and -not (Test-Path (Join-Path $dir 'global.json'))) {
        $parent = Split-Path -Parent $dir
        if ($parent -eq $dir.Path -or -not $parent) { throw "No global.json above ${From}: this does not look like a repository root." }
        $dir = Resolve-Path $parent
    }
    return $dir.Path
}

function Get-PackageFiles {
    param([Parameter(Mandatory)][string]$Directory)
    Get-ChildItem -LiteralPath $Directory -Filter '*.nupkg' -File | Where-Object { $_.Name -notlike '*.symbols.nupkg' } | Sort-Object Name
}

function Read-NupkgEntries {
    <# name -> sha256 of every entry of a package (zip metadata such as time stamps is not part of what a package contains). #>
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $result = [ordered]@{}
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
    try {
        foreach ($entry in ($zip.Entries | Sort-Object FullName)) {
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                $result[$entry.FullName] = ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $result
}

function Read-NuspecOf {
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -notlike '*/*' } | Select-Object -First 1
        if (-not $entry) { throw "$Path has no nuspec." }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Get-Prop {
    <# $object.name, or $null when the object has no such property (strict mode makes the plain form an error). #>
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Assert-FileHash {
    <# Throws, and deletes the file, unless it has exactly the SHA-256 that was pinned for it. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sha256)
    $actual = Get-Sha256 $Path
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item -Force -LiteralPath $Path
        throw "$(Split-Path -Leaf $Path) does not match the pinned SHA-256 (expected $Sha256, got $actual). It was deleted and nothing was installed."
    }
}

function Compare-PackageSets {
    <# What differs between two directories of packages: a package in one only, an entry in one only, an entry with other content. Time stamps in the zip headers are not compared. #>
    param([Parameter(Mandatory)][string]$First, [Parameter(Mandatory)][string]$Second)
    $problems = New-Object System.Collections.Generic.List[string]
    $a = @{}; Get-PackageFiles $First | ForEach-Object { $a[$_.Name] = $_.FullName }
    $b = @{}; Get-PackageFiles $Second | ForEach-Object { $b[$_.Name] = $_.FullName }
    foreach ($name in (@($a.Keys) + @($b.Keys) | Sort-Object -Unique)) {
        if (-not $a.ContainsKey($name)) { $problems.Add("$name was built only the second time"); continue }
        if (-not $b.ContainsKey($name)) { $problems.Add("$name was built only the first time"); continue }
        $x = Read-NupkgEntries $a[$name]
        $y = Read-NupkgEntries $b[$name]
        foreach ($entry in (@($x.Keys) + @($y.Keys) | Sort-Object -Unique)) {
            if (-not $x.Contains($entry) -or -not $y.Contains($entry)) { $problems.Add("$name : $entry exists in one build only") }
            elseif ($x[$entry] -ne $y[$entry]) { $problems.Add("$name : $entry differs ($($x[$entry].Substring(0, 12)) vs $($y[$entry].Substring(0, 12)))") }
        }
    }
    return ,$problems
}
