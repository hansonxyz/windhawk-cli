<#
.SYNOPSIS
  Apply a whcli profile (mods + settings) to a target Windhawk install.
.DESCRIPTION
  The deployable building block: drives dist\whcli.exe to install+compile+configure
  every mod in a profile into a target portable Windhawk root. Optionally stages the
  portable runtime to the target first. Your external provisioning can call this, or
  call whcli.exe directly.
.PARAMETER Root
  Target Windhawk root (a dir containing windhawk.ini). Defaults to the repo's
  windhawk-portable.
.PARAMETER ProfilePath
  Profile JSON to apply. Defaults to repo profile.json.
.PARAMETER Bundle
  Folder of pinned mod sources. Defaults to repo mods\.
.PARAMETER StageFrom
  Optional: mirror a portable runtime template into -Root before applying.
.PARAMETER AppSettings
  Also apply the profile's app settings (e.g. HideTrayIcon).
.PARAMETER NoFetch
  Fail instead of fetching a missing mod source from windhawk-mods.
#>
[CmdletBinding()]
param(
    [string]$Root,
    [string]$ProfilePath,
    [string]$Bundle,
    [string]$StageFrom,
    [switch]$AppSettings,
    [switch]$NoFetch
)

$ErrorActionPreference = 'Stop'
$Repo  = Split-Path -Parent $PSScriptRoot
$Whcli = Join-Path $Repo 'dist\whcli.exe'

if (-not $Root)        { $Root        = Join-Path $Repo 'windhawk-portable' }
if (-not $ProfilePath) { $ProfilePath = Join-Path $Repo 'profile.json' }
if (-not $Bundle)      { $Bundle      = Join-Path $Repo 'mods' }

if (-not (Test-Path $Whcli)) { throw "whcli not built. Run whcli\build.ps1 first." }
if (-not (Test-Path $ProfilePath)) { throw "Profile not found: $ProfilePath" }

if ($StageFrom) {
    Write-Host "Staging portable runtime: $StageFrom -> $Root"
    & robocopy $StageFrom $Root /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $Root 'windhawk.ini'))) {
    throw "No windhawk.ini in $Root. Stage the portable runtime there first (-StageFrom)."
}

$whArgs = @('apply', $ProfilePath, '--root', $Root, '--bundle', $Bundle)
if ($AppSettings) { $whArgs += '--app-settings' }
if ($NoFetch)     { $whArgs += '--no-fetch' }

Write-Host "Applying $ProfilePath -> $Root"
& $Whcli @whArgs
exit $LASTEXITCODE
