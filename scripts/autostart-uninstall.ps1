<#
.SYNOPSIS
  Remove the logon Scheduled Task created by autostart-install.ps1.
#>
[CmdletBinding()]
param([string]$TaskName = 'WindhawkCustom')

$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed logon task '$TaskName'."
} else {
    Write-Host "No logon task '$TaskName' found."
}
