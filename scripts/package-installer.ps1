<#
.SYNOPSIS
  Assemble the offline installer package: dist\installer\{whsetup.exe, payload\}.
.DESCRIPTION
  payload\ = the runtime (Compiler/Engine/UI + helper DLLs) from windhawk-portable,
  with the freshly-built rebranded windhawk.exe and whcli.exe overlaid, and the
  per-install AppData\ + windhawk.ini excluded (the installer writes windhawk.ini
  and the engine creates AppData at runtime).

  Prereqs: build the rebranded app (Release\windhawk.exe), whcli (dist\whcli.exe),
  and whsetup (dist\whsetup.exe) first (this script copies, it doesn't build them).
#>
[CmdletBinding()]
param([switch]$NoSign)

$ErrorActionPreference = 'Stop'
$Repo  = Split-Path -Parent $PSScriptRoot
$port  = Join-Path $Repo 'windhawk-portable'
$relExe = Join-Path $Repo 'windhawk\src\windhawk\Release\windhawk.exe'
$whcli = Join-Path $Repo 'dist\whcli.exe'
$whsetup = Join-Path $Repo 'dist\whsetup.exe'

foreach ($f in @($relExe, $whcli, $whsetup, (Join-Path $port 'windhawk.ini'))) {
    if (-not (Test-Path $f)) { throw "missing prerequisite: $f" }
}

$out     = Join-Path $Repo 'dist\installer'
$payload = Join-Path $out 'payload'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $payload | Out-Null

Write-Host "Copying runtime payload (excluding AppData, windhawk.ini)..."
& robocopy $port $payload /E /XD (Join-Path $port 'AppData') /XF 'windhawk.ini' '*.orig' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

Write-Host "Overlaying rebranded windhawk.exe + whcli.exe..."
Copy-Item $relExe (Join-Path $payload 'windhawk.exe') -Force
Copy-Item $whcli  (Join-Path $payload 'whcli.exe')   -Force

Write-Host "Placing whsetup.exe..."
Copy-Item $whsetup (Join-Path $out 'whsetup.exe') -Force

if (-not $NoSign) {
    $ver = (Get-ChildItem (Join-Path $payload 'Engine') -Directory)[0].Name
    $toSign = @(
        (Join-Path $payload 'windhawk.exe'),
        (Join-Path $payload 'whcli.exe'),
        (Join-Path $payload "Engine\$ver\32\windhawk.dll"),
        (Join-Path $payload "Engine\$ver\64\windhawk.dll"),
        (Join-Path $payload "Engine\$ver\arm64\windhawk.dll"),
        (Join-Path $out 'whsetup.exe')
    )
    try {
        Write-Host "Signing our binaries..."
        & (Join-Path $PSScriptRoot 'sign.ps1') -Files $toSign
    } catch {
        Write-Warning "Signing skipped/failed: $($_.Exception.Message)"
        Write-Warning "Run scripts\new-signing-cert.ps1 once, then re-run with signing (or pass -NoSign to suppress)."
    }
}

$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Installer package ready: $out ($size MB)"
Write-Host "Distribute by zipping the '$out' folder."
