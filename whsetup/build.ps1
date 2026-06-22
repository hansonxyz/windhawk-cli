# Builds whsetup as a single self-contained Native AOT exe.
# Like whcli, AOT linking needs the VC env + the VS Installer dir on PATH.
# Output: whsetup\bin\x64\Release\net8.0-windows\win-x64\publish\whsetup.exe (also staged to ..\dist\whsetup.exe)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$vcvars    = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
$installer = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

cmd /c "set `"PATH=$installer;%PATH%`" && `"$vcvars`" >nul 2>&1 && cd /d `"$here`" && dotnet publish -c Release -r win-x64"
if ($LASTEXITCODE -ne 0) { throw "whsetup AOT publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $here "bin\x64\Release\net8.0-windows\win-x64\publish\whsetup.exe"
$dist = Join-Path (Split-Path -Parent $here) "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $exe (Join-Path $dist "whsetup.exe") -Force
Write-Host "Built: $exe ($([math]::Round((Get-Item $exe).Length/1KB)) KB)"
