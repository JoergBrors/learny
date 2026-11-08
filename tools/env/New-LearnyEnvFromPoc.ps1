<#
.SYNOPSIS
  Ergänzt die lokale Learny-.env um die Werte des Entra-External-ID-PoC (Phase C): External ID, Einladungen, Mailversand, OTP-Extension.
.DESCRIPTION
  Quellen: poc/.secrets/portal.env (Client-Secret der PoC-App) und die bekannten, nicht geheimen Kennungen des PoC (docs/entra-external-id).
  Die Ziel-.env muss von Git ignoriert sein (sonst Abbruch). Vorhandene Werte bleiben unverändert (ausser mit -Force). Geheimnisse werden nie ausgegeben.
  Fail-closed: Ein Bereich wird nur eingeschaltet, wenn seine Pflichtwerte vorhanden sind (sonst =0 und ein Hinweis), damit die Anwendung nicht mit unvollständiger Konfiguration abbricht.
  - EXTID_ENABLED=1 nur mit Client-Secret der PoC-App (PORTAL_ENTRA_CLIENT_SECRET in poc/.secrets/portal.env).
  - INVITE_TOKEN_KEY wird (falls fehlend) kryptografisch zufällig erzeugt (Base64, 32 Byte).
  - MAIL_ENABLED=1 nur mit Zertifikatsdatei (MAIL_CLIENT_CERT_PATH). Mit -ExportMailCert wird das PoC-Zertifikat (Thumbprint aus dem PoC) aus dem Windows-Zertifikatsspeicher
    als PFX ausserhalb des Repositorys exportiert — das gelingt nur, wenn der private Schlüssel exportierbar ist; sonst Hinweis, nichts wird umgangen.
  - EXTID_OTP_ENABLED=1 nur, wenn External ID und Mailversand aktiv sind.
  - MAIL_ALLOWED_RECIPIENTS wird nur auf angegebene Testadressen gesetzt (leer = niemand).
.EXAMPLE
  ./tools/env/New-LearnyEnvFromPoc.ps1 -AllowedRecipients joerg@brors.net -ExportMailCert
  ./tools/env/New-LearnyEnvFromPoc.ps1 -WhatIf          # zeigt nur, welche Schlüssel gesetzt würden
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$Path = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.env'),
  [string]$PocEnvPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'poc\.secrets\portal.env'),
  [string[]]$AllowedRecipients,
  [string]$PublicBaseUrl,
  [switch]$ExportMailCert,
  [string]$MailCertPath = (Join-Path $env:USERPROFILE '.learny\mail-client.pfx'),
  [switch]$Force
)
$ErrorActionPreference = 'Stop'

# Bekannte, nicht geheime Kennungen des PoC (docs/entra-external-id/poc-abschluss.md, mail-permissions.md)
$ExtTenant   = '5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63'
$ExtAuthority = "https://learnyextidpoc.ciamlogin.com/$ExtTenant/v2.0"
$ExtClientId = 'cb54157f-bcfb-4e78-9c81-b63ee7a0a03d'
$OtpApiId    = '1132ae90-fa71-4a16-b5df-15054b9e1ff0'
$OtpAudiences = "$OtpApiId,api://learny-otp.brors.date/$OtpApiId"
$MailTenant  = 'db88f9b6-c89a-41a3-a21d-b61458e234ff'
$MailClient  = 'f3365162-0eda-48b3-99dd-b96d3633c40c'
$MailThumb   = 'E9A0BAC76ABA6FC701392344A7118BCF1A841A68'
$MailSender  = 'learny@brors.link'

$Path = [IO.Path]::GetFullPath($Path)
$repo = (git -C (Split-Path $Path) rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -eq 0 -and $repo) {
  git -C $repo check-ignore -q -- $Path 2>$null
  if ($LASTEXITCODE -ne 0) { throw "Die Datei '$Path' ist NICHT von Git ignoriert. Abbruch (Geheimnisse gehören nie ins Repository)." }
}
function Read-EnvFile([string]$p) {
  $h = [ordered]@{}
  if (Test-Path -LiteralPath $p) { foreach ($l in Get-Content -LiteralPath $p -Encoding utf8) { if ($l -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $h[$Matches[1]] = $Matches[2].Trim() } } }
  return $h
}
$cur = Read-EnvFile $Path
$poc = Read-EnvFile $PocEnvPath
$set = [ordered]@{}; $notes = New-Object System.Collections.Generic.List[string]
function Want([string]$k, [string]$v) {
  if ($script:cur.Contains($k) -and $script:cur[$k] -ne '' -and -not $Force) { $script:notes.Add("$k : behalten"); return }
  $script:set[$k] = $v; $script:notes.Add("$k : " + $(if ($v -eq '') { 'leer gesetzt' } else { 'gesetzt' }))
}
function Value([string]$k) { if ($script:set.Contains($k)) { return $script:set[$k] } elseif ($script:cur.Contains($k)) { return $script:cur[$k] } else { return '' } }

# ── External ID ──
$secret = $poc['PORTAL_ENTRA_CLIENT_SECRET']
Want 'EXTID_AUTHORITY' $ExtAuthority; Want 'EXTID_TENANT_ID' $ExtTenant; Want 'EXTID_CLIENT_ID' $ExtClientId
if ($secret) { Want 'EXTID_CLIENT_SECRET' $secret } else { $notes.Add('EXTID_CLIENT_SECRET : FEHLT in poc/.secrets/portal.env (PORTAL_ENTRA_CLIENT_SECRET) -> New-PortalEnv.ps1 -CreateEntraSecret') }
$extOn = [bool](Value 'EXTID_CLIENT_SECRET')
Want 'EXTID_ENABLED' $(if ($extOn) { '1' } else { '0' })

# ── Einladungen ──
if (-not (Value 'INVITE_TOKEN_KEY')) {
  $b = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Fill($b); Want 'INVITE_TOKEN_KEY' ([Convert]::ToBase64String($b))
} else { $notes.Add('INVITE_TOKEN_KEY : behalten') }
if ($PublicBaseUrl) { Want 'PUBLIC_BASE_URL' $PublicBaseUrl.TrimEnd('/') }
elseif (-not (Value 'PUBLIC_BASE_URL')) { $notes.Add('PUBLIC_BASE_URL : nicht gesetzt (Development nutzt http://localhost:5000; Produktion: HTTPS-Adresse setzen)') }

# ── Mailversand ──
Want 'MAIL_TENANT_ID' $MailTenant; Want 'MAIL_CLIENT_ID' $MailClient; Want 'MAIL_SENDER' $MailSender
if ($AllowedRecipients) { Want 'MAIL_ALLOWED_RECIPIENTS' (($AllowedRecipients | ForEach-Object { $_.Trim().ToLowerInvariant() }) -join ',') }
elseif (-not (Value 'MAIL_ALLOWED_RECIPIENTS')) { $notes.Add('MAIL_ALLOWED_RECIPIENTS : leer (= niemand) -> mit -AllowedRecipients Testadressen angeben') }

if ($ExportMailCert -and -not (Value 'MAIL_CLIENT_CERT_PATH')) {
  $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $MailThumb } | Select-Object -First 1
  if (-not $cert) { $notes.Add("Mail-Zertifikat $MailThumb : nicht im Windows-Zertifikatsspeicher gefunden") }
  elseif (-not $cert.HasPrivateKey) { $notes.Add('Mail-Zertifikat : ohne privaten Schlüssel, kein Export möglich') }
  elseif ($PSCmdlet.ShouldProcess($MailCertPath, 'PFX exportieren')) {
    try {
      $pwBytes = [byte[]]::new(24); [Security.Cryptography.RandomNumberGenerator]::Fill($pwBytes)
      $pw = [Convert]::ToBase64String($pwBytes)
      New-Item -ItemType Directory -Force -Path (Split-Path $MailCertPath) | Out-Null
      Export-PfxCertificate -Cert $cert -FilePath $MailCertPath -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
      Want 'MAIL_CLIENT_CERT_PATH' $MailCertPath; Want 'MAIL_CLIENT_CERT_PASSWORD' $pw
    } catch { $notes.Add('Mail-Zertifikat : Export nicht möglich (privater Schlüssel vermutlich nicht exportierbar) -> Zertifikat für Phase D neu ausstellen') }
  }
}
$certOk = (Value 'MAIL_CLIENT_CERT_PATH') -and (Test-Path -LiteralPath (Value 'MAIL_CLIENT_CERT_PATH'))
$mailOn = [bool](Value 'MAIL_ALLOWED_RECIPIENTS')   # ohne Zertifikatsdatei erzeugt die Plattform das Zertifikat (Administration -> Mailversand)
if (-not $certOk) { $notes.Add('Mail-Zertifikat : keine Datei -> wird auf der Plattform erzeugt (Administration -> Mailversand), danach die .cer in Entra hochladen') }
if (-not $mailOn) { $notes.Add('MAIL_ENABLED=0 : MAIL_ALLOWED_RECIPIENTS ist leer') }
Want 'MAIL_ENABLED' $(if ($mailOn) { '1' } else { '0' })

# ── OTP-Extension ──
Want 'EXTID_OTP_AUDIENCES' $OtpAudiences
Want 'EXTID_OTP_ENABLED' $(if ($extOn -and $mailOn) { '1' } else { '0' })

# Umgebungen: Debug = http://localhost:5000 (PUBLIC_BASE_URL bleibt leer, Development nutzt diese Adresse), Plattform = https://learny.brors.date (dort PUBLIC_BASE_URL als GitHub Variable).
$notes.Add('Entra-App (External Tenant) braucht beide Redirect-URIs: http://localhost:5000/auth/external/callback (Debug) und https://learny.brors.date/auth/external/callback (Plattform)')
$notes.Add('OTP-Extension (Entra) ruft nur die Plattform auf: https://learny.brors.date/extensions/otp-send; lokal ist sie nur über die Tests erreichbar')

# ── schreiben (vorhandene Zeilen aktualisieren, neue ans Ende; Kommentare bleiben) ──
Write-Host "Ziel: $Path"
foreach ($n in $notes) { Write-Host "  $n" }
if ($set.Count -eq 0) { Write-Host 'Nichts zu ändern.'; return }
if ($PSCmdlet.ShouldProcess($Path, "$($set.Count) Schlüssel schreiben")) {
  $lines = New-Object 'System.Collections.Generic.List[string]'
  if (Test-Path -LiteralPath $Path) { foreach ($x in (Get-Content -LiteralPath $Path -Encoding utf8)) { $lines.Add([string]$x) } }
  if (Test-Path -LiteralPath $Path) { Copy-Item -LiteralPath $Path -Destination ($Path + '.bak') -Force }
  $written = @{}
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=') { $k = $Matches[1]; if ($set.Contains($k)) { $lines[$i] = "$k=$($set[$k])"; $written[$k] = $true } }
  }
  $new = @($set.Keys | Where-Object { -not $written.ContainsKey($_) })
  if ($new.Count -gt 0) {
    $lines.Add(''); $lines.Add('# ─── Phase C: aus dem Entra-PoC übernommen (tools/env/New-LearnyEnvFromPoc.ps1; Geheimnisse, nie committen) ───')
    foreach ($k in $new) { $lines.Add("$k=$($set[$k])") }
  }
  Set-Content -LiteralPath $Path -Value $lines -Encoding utf8
  Write-Host "Geschrieben: $($set.Count) Schlüssel (Sicherung: $Path.bak). Werte von Geheimnissen werden nie angezeigt."
}
