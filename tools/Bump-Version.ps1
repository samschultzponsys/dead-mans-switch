<#
.SYNOPSIS
    Moves the project to its next version and prepares the release.
.DESCRIPTION
    Versions are MAJOR.MINOR with a single-digit MINOR that rolls over after 9:
        1.1, 1.2 ... 1.9, 2.0, 2.1 ... 2.9, 3.0 ...
    Updates VERSION (the build stamps it into the exe) and turns the
    CHANGELOG's "Unreleased" section into a dated section for the new version.
    With -Tag, also commits and creates the vMAJOR.MINOR git tag; pushing that
    tag makes GitHub Actions build the exe and publish the release.
.EXAMPLE
    .\tools\Bump-Version.ps1            # 1.1 -> 1.2, edit files only
.EXAMPLE
    .\tools\Bump-Version.ps1 -Tag       # also commit + tag v1.2
    git push --follow-tags
#>
param([switch]$Tag)
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

Write-Host "Version $current -> $next"
if ($Tag) {
    git -C $root add VERSION CHANGELOG.md
    git -C $root commit -m "Release v$next"
    git -C $root tag -a "v$next" -m "v$next"   # annotated, so --follow-tags pushes it
    Write-Host "Tagged v$next. Push with: git push --follow-tags"
}
