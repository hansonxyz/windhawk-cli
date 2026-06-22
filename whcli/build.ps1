# Builds whcli as a single self-contained Native AOT exe.
#
# Native AOT links with MSVC (link.exe) and needs the VC environment on PATH,
# INCLUDING the VS Installer dir (the ILCompiler target shells out to vswhere.exe,
# which BuildTools' vcvars64 does NOT add to PATH). We set both up here.
#
# Output: whcli\bin\x64\Release\net8.0-windows\win-x64\publish\whcli.exe

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$vcvars   = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
$installer = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"

if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars (install VS Build Tools 2022 + C++ workload)" }

cmd /c "set `"PATH=$installer;%PATH%`" && `"$vcvars`" >nul 2>&1 && cd /d `"$here`" && dotnet publish -c Release -r win-x64"
if ($LASTEXITCODE -ne 0) { throw "whcli AOT publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $here "bin\x64\Release\net8.0-windows\win-x64\publish\whcli.exe"

# Copy to a stable location the orchestration scripts (and packaging) can rely on.
$dist = Join-Path (Split-Path -Parent $here) "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $exe (Join-Path $dist "whcli.exe") -Force

Write-Host "Built: $exe ($([math]::Round((Get-Item $exe).Length/1KB)) KB)"
Write-Host "Staged: $(Join-Path $dist 'whcli.exe')"
