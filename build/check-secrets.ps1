<#
.SYNOPSIS
  Fails the build when a file tracked by git contains something shaped like a real secret.

.DESCRIPTION
  Scans every tracked text file for credentials by their shape, not by the word "password": a private key block, a cloud access key id, a storage account key or shared access signature,
  a bearer or JWT token, a source-control token, a client secret given a literal value. A connection string that names a password for a throw-away test database is not a secret in this sense
  (the fixture roles of the tests are created fresh for every run), so the plain word is not enough to fail the build. There is deliberately no allow-list file: a value that looks like a real
  secret and is not one is changed so that it does not look like one.

.PARAMETER Root       Repository root. Defaults to the parent of this script's folder.
.PARAMETER SelfTest   Prove the check can fail: scan a temporary repository seeded with one secret of each shape, then a clean one.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $PSCommandPath) }

# name, regex
$rules = @(
    @{ Name = 'private key block';                  Pattern = '-----BEGIN (RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY( BLOCK)?-----' },
    @{ Name = 'AWS access key id';                  Pattern = '\b(AKIA|ASIA)[0-9A-Z]{16}\b' },
    @{ Name = 'storage account key';                Pattern = '(AccountKey|SharedAccessKey)\s*=\s*[A-Za-z0-9+/]{40,}={0,2}' },
    @{ Name = 'shared access signature';            Pattern = '[?&]sig=[A-Za-z0-9%+/]{30,}' },
    @{ Name = 'source-control or chat token';       Pattern = '\b(gh[pousr]_[A-Za-z0-9]{36,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{20,})\b' },
    @{ Name = 'JSON web token';                     Pattern = '\beyJ[A-Za-z0-9_-]{15,}\.eyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{10,}' },
    @{ Name = 'client secret with a literal value'; Pattern = '(?i)client_?secret["'']?\s*[=:]\s*["''][^"''\s]{12,}["'']' },
    @{ Name = 'password with a long literal value'; Pattern = '(?i)(password|pwd)["'']?\s*[=:]\s*["''][A-Za-z0-9+/_\-]{24,}["'']' }
)

# This file holds the patterns above, and a pattern is shaped like what it looks for. Only its rule-definition lines (those that begin with an at-brace and `Name =`) are skipped, by name, here and nowhere
# else; every other line of this file is scanned like any other, so a secret pasted into the script still fails the build. The self-test fixtures below are assembled from pieces for the same reason.
$selfPath = 'build/check-secrets.ps1'
$ruleDefinition = '^\s*@\{ Name = '

$binary = '\.(png|jpg|jpeg|gif|ico|woff2?|ttf|otf|zip|nupkg|dll|exe|pdf|snk|pfx|jar)$'

function Get-Findings {
    param([string]$BaseDir)
    $found = New-Object System.Collections.Generic.List[object]
    $files = & git -C $BaseDir ls-files -z | ForEach-Object { $_ -split "`0" } | Where-Object { $_ -and $_ -notmatch $binary }
    foreach ($relative in $files) {
        $path = Join-Path $BaseDir $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $path).Length -gt 2MB) { continue }
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadLines($path)) {
            $lineNumber++
            if ($relative -eq $selfPath -and $line -match $ruleDefinition) { continue }
            foreach ($rule in $rules) {
                if ([regex]::IsMatch($line, $rule.Pattern)) {
                    $found.Add([pscustomobject]@{ File = $relative; Line = $lineNumber; Rule = $rule.Name })
                }
            }
        }
    }
    return , $found
}

if ($SelfTest) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('kairos-secrets-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try {
        & git -C $temp init -q
        Set-Content "$temp/clean.cs" 'var cs = new NpgsqlConnectionStringBuilder { Username = "u", Password = "m" };'
        Set-Content "$temp/clean.md" 'Set the client secret in the key vault; never write client_secret = <value> here.'
        & git -C $temp add -A
        $clean = Get-Findings -BaseDir $temp
        if ($clean.Count -ne 0) { Write-Error "Self-test: a clean tree produced $($clean.Count) finding(s): $($clean | Format-Table | Out-String)"; exit 1 }

        $dash = '-' * 5
        $bad = @{
            'a.txt' = "${dash}BEGIN EC PRIVATE KEY${dash}"
            'b.txt' = 'key = AKIA' + ('A' * 16)
            'c.txt' = 'DefaultEndpointsProtocol=https;AccountKey=' + ('A' * 44) + '=='
            'd.txt' = 'https://x.blob.core.windows.net/c?sv=2024&sig=' + ('a' * 40)
            'e.txt' = 'token: ghp_' + ('a' * 36)
            'f.txt' = 'Authorization: ey' + ('J' * 20) + '.ey' + ('J' * 20) + '.' + ('A' * 20)
            'g.json' = '{ "client_' + 'secret": "' + ('a' * 16) + '" }'
            'h.cs' = 'var p = "Server=db;Password=' + ('Z' * 30) + '";'.Replace('"', '')
        }
        $bad['h.cs'] = 'const string P = "Password=' + ('Z' * 30) + '"; var q = new { Password = "' + ('Z' * 30) + '" };'
        # The script's own rule lines are skipped and nothing else in it is: a rule-shaped line passes, a secret on any other line of the same path fails.
        New-Item -ItemType Directory -Path "$temp/build" -Force | Out-Null
        Set-Content "$temp/build/check-secrets.ps1" ('    @{ Name = ''x''; Pattern = ''' + "${dash}BEGIN PRIVATE KEY${dash}" + ''' },')
        & git -C $temp add -A
        $own = Get-Findings -BaseDir $temp
        if ($own.Count -ne 0) { Write-Error "Self-test: the script's own rule definition was flagged: $($own | Format-Table | Out-String)"; exit 1 }
        $bad['build/check-secrets.ps1'] = '$token = ''ghp_' + ('a' * 36) + ''''
        foreach ($entry in $bad.GetEnumerator()) { Set-Content (Join-Path $temp $entry.Key) $entry.Value }
        & git -C $temp add -A
        $dirty = Get-Findings -BaseDir $temp
        $files = $dirty | ForEach-Object { $_.File } | Sort-Object -Unique
        $missed = $bad.Keys | Where-Object { $files -notcontains $_ }
        if ($missed) { Write-Error "Self-test: secrets not detected in: $($missed -join ', ')"; exit 1 }
        Write-Host 'Self-test passed: every secret shape is detected, and ordinary test passwords and prose are not flagged.'
        exit 0
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$all = Get-Findings -BaseDir $Root
if ($all.Count -gt 0) {
    foreach ($f in $all) {
        Write-Host "##vso[task.logissue type=error;sourcepath=$($f.File);linenumber=$($f.Line)]Possible secret ($($f.Rule))"
        Write-Host "$($f.File):$($f.Line): possible secret ($($f.Rule))"
    }

    Write-Host "`n$($all.Count) possible secret(s) in tracked files. Remove the value and rotate it if it was real; do not allow-list."
    exit 1
}

Write-Host 'Secret scan passed (every tracked text file, by the shape of the secret).'
exit 0
