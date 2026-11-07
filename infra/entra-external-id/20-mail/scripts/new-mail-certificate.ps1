<#
.SYNOPSIS
  Erzeugt das Zertifikat der Mail-App: privater Schluessel nicht exportierbar im Zertifikatsspeicher, nur der oeffentliche Teil als PEM-Datei.
.DESCRIPTION
  Ohne -Apply wird nur angezeigt, was geschehen wuerde. Der private Schluessel verlaesst den Speicher nie und gehoert nie in Terraform,
  Repository, Logs oder .env. Die PEM-Datei (oeffentlich) wird in terraform.tfvars als mail_certificate_pem referenziert.
  Fuer Produktion: Zertifikat aus Key Vault/PKI verwenden; dies ist die einfache Variante fuer den PoC.
#>
[CmdletBinding()]
param(
  [string]$Subject = 'CN=Learny Invitation Mailer',
  [int]$ValidDays = 365,
  [string]$PublicPemPath = (Join-Path $PSScriptRoot '..\mail-app-public.pem'),
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'
if (-not $Apply) { Write-Host "TROCKENLAUF: wuerde ein nicht exportierbares Zertifikat '$Subject' ($ValidDays Tage) in Cert:\CurrentUser\My erzeugen und den oeffentlichen Teil nach '$PublicPemPath' schreiben."; return }
$cert = New-SelfSignedCertificate -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -KeySpec Signature -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddDays($ValidDays)
$pem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($cert.RawData, 'InsertLineBreaks') + "`n-----END CERTIFICATE-----`n"
Set-Content -Path $PublicPemPath -Value $pem -Encoding ascii
Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "Oeffentliches Zertifikat: $PublicPemPath (privater Schluessel bleibt im Speicher)"
