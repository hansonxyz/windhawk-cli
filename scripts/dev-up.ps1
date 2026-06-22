<#
.SYNOPSIS
  Switch the machine from the official Windhawk to our custom portable build (dev).
.DESCRIPTION
  Stops the official Windhawk service and tray, then launches our portable build
  from windhawk-portable\ with the tray icon enabled for testing. Reversible with
  dev-down.ps1. Requires Administrator (to control the service).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Repo     = Split-Path -Parent $PSScriptRoot
$Portable = Join-Path $Repo 'windhawk-portable'
$Official = Join-Path $env:ProgramFiles 'Windhawk\windhawk.exe'

function Assert-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "Run this as Administrator (needed to stop the Windhawk service)."
    }
}

Assert-Admin

if (-not (Test-Path (Join-Path $Portable 'windhawk.exe'))) {
    throw "Custom build not found at $Portable. Build/assemble it first (see CLAUDE.md)."
}

# 1) Stop the official service.
$svc = Get-Service -Name 'Windhawk' -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Write-Host "Stopping official Windhawk service..."
    Stop-Service -Name 'Windhawk' -Force
}

# 2) Ask the official tray/UI to exit (best-effort; ours isn't running yet).
if (Test-Path $Official) {
    Write-Host "Closing official Windhawk tray/UI..."
    & $Official -exit -wait 2>$null
}

# 3) Launch our portable build with the tray enabled.
Write-Host "Starting custom Windhawk (portable, tray on)..."
Start-Process -FilePath (Join-Path $Portable 'windhawk.exe') -ArgumentList '-tray-only'

Write-Host "Custom Windhawk is now running from $Portable."
Write-Host "Run scripts\dev-down.ps1 -RestoreOfficial to switch back."
