<#
.SYNOPSIS
  Stop our custom portable Windhawk; optionally restore the official service.
.PARAMETER RestoreOfficial
  Also start the official Windhawk service again (requires Administrator).
#>
[CmdletBinding()]
param([switch]$RestoreOfficial)

$ErrorActionPreference = 'Stop'
$Repo     = Split-Path -Parent $PSScriptRoot
$Portable = Join-Path $Repo 'windhawk-portable'

# Ask our instance to exit.
$exe = Join-Path $Portable 'windhawk.exe'
if (Test-Path $exe) {
    Write-Host "Stopping custom Windhawk..."
    & $exe -exit -wait 2>$null
}

if ($RestoreOfficial) {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "Run as Administrator to restore the official service (or omit -RestoreOfficial)."
    }
    $svc = Get-Service -Name 'Windhawk' -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host "Starting official Windhawk service..."
        Start-Service -Name 'Windhawk'
    } else {
        Write-Warning "Official Windhawk service not found."
    }
}

Write-Host "Done."
