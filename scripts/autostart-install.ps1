<#
.SYNOPSIS
  Make our custom portable Windhawk start at logon (replaces official autostart).
.DESCRIPTION
  Registers a per-user logon Scheduled Task that launches our portable build with
  the tray enabled. To fully replace the official install, also disable its service:
      Set-Service Windhawk -StartupType Disabled
  (shown, not done automatically). Requires Administrator for a highest-privilege task.
.PARAMETER TaskName
  Scheduled task name. Default: WindhawkCustom.
#>
[CmdletBinding()]
param([string]$TaskName = 'WindhawkCustom')

$ErrorActionPreference = 'Stop'
$Repo     = Split-Path -Parent $PSScriptRoot
$Portable = Join-Path $Repo 'windhawk-portable'
$exe      = Join-Path $Portable 'windhawk.exe'

if (-not (Test-Path $exe)) { throw "Custom build not found at $exe." }

$action    = New-ScheduledTaskAction -Execute $exe -Argument '-tray-only'
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Registered logon task '$TaskName' -> $exe -tray-only"
Write-Host "To fully replace the official install, disable its service:"
Write-Host "    Set-Service Windhawk -StartupType Disabled" -ForegroundColor Yellow
