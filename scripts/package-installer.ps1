<#
.SYNOPSIS
  Build the single, self-contained, signed windhawk-cli installer.
.DESCRIPTION
  Produces ONE file — dist\windhawk-cli-<version>-installer.exe — a self-extracting,
  self-installing whsetup.exe with the entire runtime payload embedded. Signing that one
  file therefore covers the whole installer + payload (tamper-evident as a unit).

  Steps: assemble payload (rebranded windhawk.exe + whcli.exe over the portable runtime),
  sign the payload binaries, zip + embed them into whsetup, build whsetup, sign whsetup.

  Prereqs (build these first): Release\windhawk.exe (app) and dist\whcli.exe.
#>
[CmdletBinding()]
param([string]$Version = '1.0.0', [switch]$NoSign)

$ErrorActionPreference = 'Stop'
$Repo   = Split-Path -Parent $PSScriptRoot
$port   = Join-Path $Repo 'windhawk-portable'
$relExe = Join-Path $Repo 'windhawk\src\windhawk\Release\windhawk.exe'
$whcli  = Join-Path $Repo 'dist\whcli.exe'
$sign   = Join-Path $PSScriptRoot 'sign.ps1'

foreach ($f in @($relExe, $whcli, (Join-Path $port 'windhawk.ini'))) {
    if (-not (Test-Path $f)) { throw "missing prerequisite: $f (build it first)" }
}

# 1. Assemble the payload (runtime minus per-install data, with our rebuilt binaries).
$stage = Join-Path $Repo 'dist\payload-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host "Assembling payload..."
# Lean payload: exclude the 543MB Compiler and 236MB Electron UI — mods come precompiled.
& robocopy $port $stage /E /XD (Join-Path $port 'AppData') (Join-Path $port 'Compiler') (Join-Path $port 'UI') /XF 'windhawk.ini' '*.orig' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
Copy-Item $relExe (Join-Path $stage 'windhawk.exe') -Force
Copy-Item $whcli  (Join-Path $stage 'whcli.exe')   -Force
$ver = (Get-ChildItem (Join-Path $stage 'Engine') -Directory)[0].Name

# Bundle ONLY the tiny mod runtime libs (precompiled mods link against these at load).
$libMap = @{ 'i686-w64-mingw32' = '32'; 'x86_64-w64-mingw32' = '64'; 'aarch64-w64-mingw32' = 'arm64' }
foreach ($t in $libMap.Keys) {
    $libsrc = Join-Path $port "Compiler\$t\bin"
    if (-not (Test-Path $libsrc)) { continue }
    $libdst = Join-Path $stage "RuntimeLibs\$($libMap[$t])"
    New-Item -ItemType Directory -Force $libdst | Out-Null
    Copy-Item (Join-Path $libsrc 'libc++.dll')            (Join-Path $libdst 'libc++.whl')            -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $libsrc 'libunwind.dll')         (Join-Path $libdst 'libunwind.whl')         -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $libsrc 'windhawk-mod-shim.dll') (Join-Path $libdst 'windhawk-mod-shim.dll') -Force -ErrorAction SilentlyContinue
}

# 2. Sign the payload binaries (so each is individually trusted at runtime).
if (-not $NoSign) {
    try {
        & $sign -Files @(
            (Join-Path $stage 'windhawk.exe'),
            (Join-Path $stage 'whcli.exe'),
            (Join-Path $stage "Engine\$ver\32\windhawk.dll"),
            (Join-Path $stage "Engine\$ver\64\windhawk.dll"),
            (Join-Path $stage "Engine\$ver\arm64\windhawk.dll")
        )
    } catch { Write-Warning "payload signing skipped/failed: $($_.Exception.Message)" }
}

# 3. Zip the payload and place it for embedding into whsetup.
$payloadZip = Join-Path $Repo 'whsetup\payload.zip'
Remove-Item $payloadZip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "Embedding payload: $([math]::Round((Get-Item $payloadZip).Length/1MB)) MB"

# 4. Build whsetup (the csproj embeds payload.zip -> single self-contained exe).
Write-Host "Building self-contained installer..."
& (Join-Path $Repo 'whsetup\build.ps1')

# 5. Sign the single installer and name it by version.
$built     = Join-Path $Repo 'dist\whsetup.exe'   # whsetup\build.ps1 stages it here
$installer = Join-Path $Repo "dist\windhawk-cli-$Version-installer.exe"
Copy-Item $built $installer -Force
if (-not $NoSign) {
    try { & $sign -Files @($installer) } catch { Write-Warning "installer signing failed: $($_.Exception.Message)" }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Installer ready: $installer ($([math]::Round((Get-Item $installer).Length/1MB)) MB)"
