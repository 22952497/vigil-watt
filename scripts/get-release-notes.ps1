# Shared helper for the release pipeline: pulls one version's section out of
# docs/project/release-notes.md. The format contract lives in that file's header and must stay
# in sync with Services/ReleaseNotes.cs (the app parses the same file at runtime).
# Dot-source this file, then call Get-ReleaseNotes / Write-ReleaseNotesFile.

function Get-ReleaseNotes {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Version
    )
    $path = Join-Path $Root "docs\project\release-notes.md"
    if (-not (Test-Path -LiteralPath $path)) { return $null }

    $capturing = $false
    $buffer = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8)) {
        if ($line -match '^##\s+(\S+)\s*$') {
            if ($capturing) { break }
            $found = $Matches[1] -replace '^[vV]', ''
            if ($found -eq $Version) { $capturing = $true }
            continue
        }
        if ($capturing) { $buffer.Add($line) }
    }
    if (-not $capturing) { return $null }

    $text = ($buffer -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text
}

# UTF-8 without BOM: a BOM would render as a stray character at the top of the release notes.
function Write-ReleaseNotesFile {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
    return $Path
}
