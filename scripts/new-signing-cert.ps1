<#
.SYNOPSIS
  One-time: create the WindhawkXYZ self-signed code-signing certificate.
.DESCRIPTION
  The private key stays in the current user's certificate store (Cert:\CurrentUser\My)
  — it is NOT written to disk. Only the PUBLIC certificate (.cer) is exported, for
  distributing trust to your managed workstations (see docs/fleet-cert-trust.md).

  Keep the private key safe: whoever holds it can sign code as "WindhawkXYZ". Back it
  up via certmgr/Export-PfxCertificate to a protected location if needed; never commit it.
.PARAMETER Subject
  Certificate subject. Default: CN=WindhawkXYZ Code Signing.
.PARAMETER Years
  Validity in years. Default 100 (effectively non-expiring) by request.
  SECURITY NOTE: a long-lived, fleet-trusted self-signed root has no practical
  expiry to limit damage and no CRL/OCSP revocation — if the private key leaks,
  the only remedy is removing it from every machine's trust stores. Guard the key.
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=WindhawkXYZ Code Signing',
    [int]$Years = 100
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
$certDir = Join-Path $Repo 'certs'
New-Item -ItemType Directory -Force $certDir | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject }
if ($existing) {
    Write-Host "A code-signing cert with subject '$Subject' already exists:"
    $existing | ForEach-Object { "  Thumbprint $($_.Thumbprint)  NotAfter $($_.NotAfter)" }
    Write-Host "Delete it first if you want to regenerate."
    return
}

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($Years)

$cer = Join-Path $certDir 'windhawkxyz-codesign.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

Write-Host "Created code-signing certificate:"
Write-Host "  Subject:    $Subject"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Valid to:   $($cert.NotAfter)"
Write-Host "  Public cert (distribute to fleet): $cer"
Write-Host ""
Write-Host "Next: sign binaries with scripts\sign.ps1; trust the .cer on workstations per docs\fleet-cert-trust.md"
