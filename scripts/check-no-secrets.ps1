<#
.SYNOPSIS
    Refuses to let identifiers you have chosen to keep private reach a commit or a push.

.DESCRIPTION
    Reads search patterns from `.secret-patterns` in the repository root — one per line, `#` for
    comments. That file is git-ignored on purpose: writing your own SIM's number into a tracked
    file would publish the very thing this script exists to protect.

    Typical contents are your own number, the numbers of people you have exchanged messages with,
    and the SIM's IMEI, IMSI, ICCID and service centre address. Any of those can end up in source
    by accident — a phone number makes a natural-looking test case, and that is exactly how one
    got in here once.

    Exits non-zero if any pattern is found, so it can be used as a git hook.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\check-no-secrets.ps1
#>
[CmdletBinding()]
param(
    [string] $PatternFile
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $scriptDirectory '..')).Path

if (-not $PatternFile) {
    $PatternFile = Join-Path $repo '.secret-patterns'
}

if (-not (Test-Path $PatternFile)) {
    Write-Host "No $PatternFile - nothing to check against."
    Write-Host 'Create it (one identifier per line) to have this check do something.'
    exit 0
}

$patterns = Get-Content $PatternFile |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') }

if (-not $patterns) {
    Write-Host "$PatternFile is empty - nothing to check against."
    exit 0
}

Push-Location $repo
try {
    # Everything git would actually publish: tracked files plus new files that are not ignored.
    $files = @(git ls-files) + @(git ls-files --others --exclude-standard) |
        Sort-Object -Unique
}
finally {
    Pop-Location
}

$binary = '\.(png|jpg|jpeg|gif|ico|db|dll|exe|pdb|pri|zip|msix|appx|woff2?)$'
$findings = @()

foreach ($relative in $files) {
    $path = Join-Path $repo $relative
    if (-not (Test-Path $path -PathType Leaf)) { continue }
    if ($relative -match $binary) { continue }

    $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }

    foreach ($pattern in $patterns) {
        if ($text.Contains($pattern)) {
            # The pattern itself is not printed: this output may be pasted into a bug report.
            $findings += [PSCustomObject]@{
                File   = $relative
                Digits = $pattern.Length
            }
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host ''
    Write-Host 'BLOCKED: a private identifier appears in files that would be published.' -ForegroundColor Red
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host 'Remove it and run this again. Note that rewriting a commit is not enough once it'
    Write-Host 'has been pushed - the old object stays reachable on the server.'
    exit 1
}

Write-Host "Clean: none of the $($patterns.Count) private pattern(s) appear in $($files.Count) publishable file(s)."
exit 0
