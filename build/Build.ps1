<#
.SYNOPSIS
    Compiles src\DeadMansSwitch.ps1 into dist\DeadMansSwitch.exe with ps2exe,
    stamping the version from the VERSION file into the exe's file properties.
.EXAMPLE
    .\build\Build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Install-Module ps2exe -Scope CurrentUser -Force
}
Import-Module ps2exe

$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d$') { throw "VERSION '$version' is not MAJOR.MINOR" }

# The script and VERSION must agree, or the updater would compare against the wrong number.
$src = Join-Path $root 'src\DeadMansSwitch.ps1'
if ((Get-Content $src -Raw) -notmatch "\`$AppVersion\s*=\s*'$([regex]::Escape($version))'") {
    throw "`$AppVersion in src\DeadMansSwitch.ps1 does not match VERSION ($version). Run tools\Bump-Version.ps1."
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$out = Join-Path $dist 'DeadMansSwitch.exe'

Invoke-ps2exe -inputFile $src -outputFile $out -noConsole `
    -title 'Dead Man''s Switch' -product 'Dead Man''s Switch' -description 'Recurring, extendable shutdown timer' `
    -iconFile (Join-Path $root 'assets\DeadMansSwitch.ico') -version "$version.0.0"

Write-Host "Built $out (v$version)"
