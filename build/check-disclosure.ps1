<#
.SYNOPSIS
  Fails the build when anything public mentions what must stay internal (docs/DISCLOSURE-POLICY.md, "Rules for the docs site").

.DESCRIPTION
  Scans, for the terms below:
    1. docs-site/ and samples/        every text file except generated output (_site, obj, bin, node_modules)
    2. src/**/*.cs                    XML documentation comments only (///), because the API reference is generated from them

  A finding is any reference to the internal docs folder, to review files, or to the commercial repository, any internal invariant,
  review or research id (I1, F2, L3, E1, R2 ...), or a blocklisted word. There is deliberately no allow-list: a public page that
  needs one of these words needs a disclosure review (the owner approves), not an exception in this script.

.PARAMETER Root       Repository root. Defaults to the parent of this script's folder.
.PARAMETER PublicDirs Folders (relative to Root) scanned in full.
.PARAMETER DocCommentDirs Folders (relative to Root) whose C# XML doc comments are scanned.
.PARAMETER SelfTest   Prove the check can fail: scan a temporary tree seeded with violations, then a clean one.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string[]]$PublicDirs = @('docs-site', 'samples'),
    [string[]]$DocCommentDirs = @('src', 'tests/Kairos.Testing', 'tests/Kairos.Conformance'),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $PSCommandPath) }

# name, regex, case-sensitive?
$rules = @(
    @{ Name = 'reference to the internal docs folder'; Pattern = '(?<![\w-])docs[/\\]'; CaseSensitive = $false },
    @{ Name = 'reference to review material';          Pattern = '\breviews?[/\\]|\bcode review\b|\bsecurity review\b'; CaseSensitive = $false },
    @{ Name = 'reference to the commercial repository'; Pattern = 'Kairos\.Commercial'; CaseSensitive = $false },
    @{ Name = 'blocklisted term';                       Pattern = '\b(milestones?|roadmap|revenue|competitors?|design[- ]partners?|internal\w*|coming soon)\b'; CaseSensitive = $false },
    @{ Name = 'internal invariant, review or research id'; Pattern = '\b(I(?:[1-9]|1[01])|[FLER][1-7])\b'; CaseSensitive = $true }
)

$textExtensions = @('.md', '.yml', '.yaml', '.json', '.cs', '.csproj', '.props', '.targets', '.txt', '.html', '.htm', '.xml', '.config', '.ps1', '.sh', '.toml', '.liquid', '.tmpl', '.css', '.js')
$skipDirs = @('_site', 'obj', 'bin', 'node_modules', '.git', '.vs')
# DocFX writes the API reference here from the XML comments, which are scanned at their source. Everything else under docs-site/api is hand-written and scanned.
$generatedApi = '[\\/]docs-site[\\/]api[\\/][^\\/]*\.(yml|manifest)$'

function Get-Findings {
    param([string]$BaseDir, [string[]]$Dirs, [bool]$DocCommentsOnly)

    $found = New-Object System.Collections.Generic.List[object]
    foreach ($dir in $Dirs) {
        $path = Join-Path $BaseDir $dir
        if (-not (Test-Path $path)) { continue }

        $files = Get-ChildItem -Path $path -Recurse -File -Force | Where-Object {
            $full = $_.FullName
            $ext = $_.Extension.ToLowerInvariant()
            $skip = $full -match $generatedApi
            foreach ($d in $skipDirs) {
                if ($full -match ([regex]::Escape([IO.Path]::DirectorySeparatorChar + $d + [IO.Path]::DirectorySeparatorChar))) { $skip = $true; break }
            }
            (-not $skip) -and ($textExtensions -contains $ext) -and ((-not $DocCommentsOnly) -or $ext -eq '.cs')
        }

        foreach ($file in $files) {
            $lineNumber = 0
            foreach ($line in [IO.File]::ReadLines($file.FullName)) {
                $lineNumber++
                $candidate = $line
                if ($DocCommentsOnly) {
                    if ($line -notmatch '^\s*///') { continue }
                }
                elseif ($file.Extension -eq '.cs' -and $line -notmatch '^\s*//') {
                    # In C# code, `internal` is the access modifier, not the word the policy is about. Comments are still checked.
                    $candidate = [regex]::Replace($line, '\binternal(?=\s+(static|sealed|abstract|partial|readonly|class|record|struct|interface|enum|const|void|async|new|unsafe|delegate|virtual|override|[A-Z]\w*))', 'access_modifier')
                }
                foreach ($rule in $rules) {
                    $options = if ($rule.CaseSensitive) { [Text.RegularExpressions.RegexOptions]::None } else { [Text.RegularExpressions.RegexOptions]::IgnoreCase }
                    $match = [regex]::Match($candidate, $rule.Pattern, $options)
                    if ($match.Success) {
                        $relative = $file.FullName.Substring($BaseDir.Length).TrimStart('\', '/')
                        $found.Add([pscustomobject]@{ File = $relative; Line = $lineNumber; Rule = $rule.Name; Text = $match.Value; Source = $(if ($DocCommentsOnly) { 'xml-doc comment' } else { 'public file' }) })
                    }
                }
            }
        }
    }
    return $found
}

function Invoke-Check {
    param([string]$BaseDir, [string[]]$PublicFolders, [string[]]$CommentFolders)

    $findings = @()
    $findings += Get-Findings -BaseDir $BaseDir -Dirs $PublicFolders -DocCommentsOnly $false
    $findings += Get-Findings -BaseDir $BaseDir -Dirs $CommentFolders -DocCommentsOnly $true
    return , $findings
}

if ($SelfTest) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('kairos-disclosure-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path "$temp/docs-site", "$temp/samples", "$temp/src/Pkg" -Force | Out-Null
    try {
        Set-Content "$temp/docs-site/clean.md" "# Replay`nA run resumes after a crash."
        Set-Content "$temp/src/Pkg/Clean.cs" "/// <summary>Starts a run.</summary>`npublic class Clean { }"
        $clean = Invoke-Check -BaseDir $temp -PublicFolders @('docs-site', 'samples') -CommentFolders @('src')
        if ($clean.Count -ne 0) { Write-Error "Self-test: a clean tree produced $($clean.Count) finding(s)."; exit 1 }

        $bad = @{
            'docs-site/a.md'     = 'See [notes](../docs/SECURITY-ARCHITECTURE.md).'
            'docs-site/b.md'     = 'Planned for the next milestone.'
            'docs-site/c.md'     = 'We beat the competitor on price.'
            'docs-site/d.md'     = 'Guarantee I3 holds.'
            'docs-site/e.md'     = 'Fixed after review F2.'
            'samples/f.cs'       = '// uses Kairos.Commercial licence checks'
            'docs-site/g.md'     = 'This is coming soon.'
            'docs-site/h.md'     = 'Our design partner asked for it.'
            'docs-site/i.md'     = 'Based on the research reading list R3.'
            'src/Pkg/Leak.cs'    = '/// <summary>See docs/RESEARCH.md for the plan.</summary>'
        }
        foreach ($entry in $bad.GetEnumerator()) {
            Set-Content (Join-Path $temp $entry.Key) $entry.Value
        }

        $dirty = Invoke-Check -BaseDir $temp -PublicFolders @('docs-site', 'samples') -CommentFolders @('src')
        $files = $dirty | ForEach-Object { $_.File.Replace('\', '/') } | Sort-Object -Unique
        $missed = $bad.Keys | Where-Object { $files -notcontains $_ }
        if ($missed) { Write-Error "Self-test: violations not detected in: $($missed -join ', ')"; exit 1 }

        # Ordinary code and prose must not trigger it: a non-doc comment in src, generated output, look-alike words, the C# access modifier.
        Set-Content "$temp/src/Pkg/Plain.cs" "// this is an internal note, not published`npublic class Plain { }"
        Set-Content "$temp/samples/Modifier.cs" "internal sealed class Helper { internal static int X; }"
        New-Item -ItemType Directory -Path "$temp/docs-site/_site" -Force | Out-Null
        Set-Content "$temp/docs-site/_site/generated.html" 'internal roadmap milestone'
        foreach ($name in @('a', 'b', 'c', 'd', 'e', 'g', 'h', 'i')) { Remove-Item "$temp/docs-site/$name.md" }
        Remove-Item "$temp/samples/f.cs", "$temp/src/Pkg/Leak.cs"
        Set-Content "$temp/docs-site/ok.md" 'Documents are processed in order; see docs-site conventions and the I/O section.'
        $after = Invoke-Check -BaseDir $temp -PublicFolders @('docs-site', 'samples') -CommentFolders @('src')
        if ($after.Count -ne 0) { Write-Error "Self-test: false positives: $($after | Format-Table | Out-String)"; exit 1 }
        Write-Host 'Self-test passed: violations are detected, clean and generated content is not flagged.'
        exit 0
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$all = Invoke-Check -BaseDir $Root -PublicFolders $PublicDirs -CommentFolders $DocCommentDirs
if ($all.Count -gt 0) {
    foreach ($f in $all) {
        $message = "Disclosure policy: $($f.Rule) '$($f.Text)' ($($f.Source))"
        Write-Host "##vso[task.logissue type=error;sourcepath=$($f.File);linenumber=$($f.Line)]$message"
        Write-Host "$($f.File):$($f.Line): $message"
    }

    Write-Host "`n$($all.Count) disclosure finding(s). Rewrite the text for customers (docs/DISCLOSURE-POLICY.md); do not allow-list."
    exit 1
}

Write-Host "Disclosure check passed ($($PublicDirs -join ', ') in full; XML doc comments under $($DocCommentDirs -join ', '))."
exit 0
