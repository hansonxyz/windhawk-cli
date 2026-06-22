<#
.SYNOPSIS
  Authenticode-sign the given files with the WindhawkXYZ code-signing cert.
.DESCRIPTION
  Uses the cert from Cert:\CurrentUser\My (created by new-signing-cert.ps1) and
  timestamps the signature so it stays valid after the cert expires.
.PARAMETER Files
  Files to sign.
.PARAMETER Subject
  Signing cert subject (default CN=WindhawkXYZ Code Signing).
.PARAMETER TimestampUrl
  RFC3161 timestamp authority.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Files,
    [string]$Subject = 'CN=WindhawkXYZ Code Signing',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1
if (-not $cert) { throw "Signing cert '$Subject' not found. Run scripts\new-signing-cert.ps1 first." }

foreach ($f in $Files) {
    if (-not (Test-Path $f)) { throw "file not found: $f" }
    $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
    "{0,-9} {1}" -f $r.Status, $f
    if ($r.Status -ne 'Valid') { throw "signing failed for $f : $($r.StatusMessage)" }
}
