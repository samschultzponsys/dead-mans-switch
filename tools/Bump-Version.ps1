<#
.SYNOPSIS
    Moves the project to its next version and prepares the release.
.DESCRIPTION
    Versions are MAJOR.MINOR with a single-digit MINOR that rolls over after 9:
        1.1, 1.2 ... 1.9, 2.0, 2.1 ... 2.9, 3.0 ...
    Updates VERSION (the build stamps it into the exe) and turns the
    CHANGELOG's "Unreleased" section into a dated section for the new version.
    Pushing the result to main releases it: GitHub Actions sees a VERSION
    with no release yet, tags it vMAJOR.MINOR and publishes the exe.
.EXAMPLE
    .\tools\Bump-Version.ps1            # 1.1 -> 1.2
    git commit -am "Release v1.2"; git push
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-NextVersion([string]$current) {
    if ($current -notmatch '^(\d+)\.(\d)$') { throw "VERSION '$current' is not MAJOR.MINOR" }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2] + 1
    if ($minor -gt 9) { $major++; $minor = 0 }
    "$major.$minor"
}

$versionFile = Join-Path $root 'VERSION'
$current = (Get-Content $versionFile -Raw).Trim()
$next = Get-NextVersion $current

$changelog = Join-Path $root 'CHANGELOG.md'
$text = Get-Content $changelog -Raw
$unreleased = '(?m)^## \[Unreleased\][ \t]*(?=\r?$)'
if ($text -notmatch $unreleased) { throw 'CHANGELOG.md has no "## [Unreleased]" section.' }
$date = Get-Date -Format 'yyyy-MM-dd'
$text = $text -replace $unreleased, "## [Unreleased]`n`n## [$next] - $date"
Set-Content -Path $changelog -Value $text -NoNewline

Set-Content -Path $versionFile -Value $next

Write-Host "Version $current -> $next. Commit and push to main to release it."
