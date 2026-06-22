<#
.SYNOPSIS
  Show which Windhawk is active (official service vs our custom build) and list our mods.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'SilentlyContinue'
$Repo     = Split-Path -Parent $PSScriptRoot
$Portable = Join-Path $Repo 'windhawk-portable'
$Whcli    = Join-Path $Repo 'dist\whcli.exe'

Write-Host "=== Official service ===" -ForegroundColor Cyan
$svc = Get-Service -Name 'Windhawk'
if ($svc) { "{0}  (startup: {1})" -f $svc.Status, $svc.StartType } else { "not installed" }

Write-Host "`n=== Running windhawk.exe processes ===" -ForegroundColor Cyan
Get-CimInstance Win32_Process -Filter "Name='windhawk.exe'" |
    ForEach-Object { "{0}  PID {1}" -f $_.ExecutablePath, $_.ProcessId }

Write-Host "`n=== Custom build mods ($Portable) ===" -ForegroundColor Cyan
if ((Test-Path $Whcli) -and (Test-Path (Join-Path $Portable 'windhawk.ini'))) {
    & $Whcli list --root $Portable
} else {
    Write-Host "whcli or portable build not found (run whcli\build.ps1 and assemble the portable build)."
}
