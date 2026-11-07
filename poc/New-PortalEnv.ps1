<#
.SYNOPSIS
  Füllt poc/.secrets/portal.env für den lokalen Start (und das Debuggen) des Registrierungs-PoC-Portals.
.DESCRIPTION
  Die Datei wird von der VS-Code-Startkonfiguration (envFile) gelesen und enthält Geheimnisse; sie liegt in einem git-ignorierten Ordner.
  Das Skript ist idempotent: Vorhandene Werte bleiben unverändert (ausser mit -Force beim Entwicklungsschlüssel). Werte von Geheimnissen werden nie ausgegeben.

  Standard (ohne Schalter):  erzeugt nur den lokalen Entwicklungsschlüssel PORTAL_DEV_ADMIN_KEY (kryptografisch zufällig). Es werden keine Cloud-Aufrufe gemacht.
  -CreateEntraSecret:        legt am External-ID-App-Objekt ein neues Client-Secret "portal-local" an (Azure CLI, Anmeldung im External Tenant nötig) und trägt es ein.
  -RemoveEntraSecret:        löscht die Secrets "portal-local" der App und leert den Eintrag.
  -AdminFromLearnyEnv:       übernimmt die Administratoranmeldung von Learnys Entra-App (OIDC_ISSUER, OIDC_CLIENT_ID, OIDC_CLIENT_SECRET aus der Learny-.env) in die portal.env.
                             Administrator ist dann, wer die App-Rolle hat, die in Learny auf admin abgebildet ist (Standard Admin.All). Das Secret wird nur kopiert, nie angezeigt.
  -WithMail:                 trägt die Exchange-Versandangaben ein (nur Kennungen, keine Geheimnisse; das Zertifikat bleibt im Windows-Zertifikatsspeicher).
.EXAMPLE
  ./poc/New-PortalEnv.ps1                                         # Entwicklungsschlüssel
  ./poc/New-PortalEnv.ps1 -CreateEntraSecret                      # zusätzlich echtes Entra-Secret (30 Tage)
  ./poc/New-PortalEnv.ps1 -AdminFromLearnyEnv                     # Administratoranmeldung mit derselben Entra-App wie Learny
  ./poc/New-PortalEnv.ps1 -WithMail -AllowedRecipients joerg@brors.net
  ./poc/New-PortalEnv.ps1 -RemoveEntraSecret                      # Aufräumen
#>
[CmdletBinding()]
param(
  [string]$Path = (Join-Path $PSScriptRoot '.secrets\portal.env'),
  [switch]$CreateEntraSecret,
  [switch]$RemoveEntraSecret,
  [switch]$WithMail,
  [switch]$AdminFromLearnyEnv,
  [string]$LearnyEnvPath = (Join-Path (Split-Path $PSScriptRoot) '.env'),
  [string[]]$AllowedRecipients,
  [int]$SecretValidDays = 30,
  [switch]$Force,
  [switch]$SkipGitCheck
)
$ErrorActionPreference = 'Stop'

# Bekannte, nicht geheime Kennungen dieses PoC (siehe docs/entra-external-id)
$ExternalTenantId = '5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63'
$PocAppClientId   = 'cb54157f-bcfb-4e78-9c81-b63ee7a0a03d'
$SecretName       = 'portal-local'
$Mail = [ordered]@{
  PORTAL_MAIL_ENABLED = '1'
  MAIL_TENANT_ID      = 'db88f9b6-c89a-41a3-a21d-b61458e234ff'
  MAIL_CLIENT_ID      = 'f3365162-0eda-48b3-99dd-b96d3633c40c'
  MAIL_CERT_THUMBPRINT = 'E9A0BAC76ABA6FC701392344A7118BCF1A841A68'
  MAIL_SENDER         = 'learny@brors.link'
}

if ($CreateEntraSecret -and $RemoveEntraSecret) { throw '-CreateEntraSecret und -RemoveEntraSecret schließen sich aus.' }
if ($SecretValidDays -lt 1 -or $SecretValidDays -gt 90) { throw '-SecretValidDays muss zwischen 1 und 90 liegen.' }

$Path = [IO.Path]::GetFullPath($Path)
New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null

# Sicherheit: Die Datei darf nie ins Repository gelangen.
if (-not $SkipGitCheck) {
  $repo = (git -C (Split-Path $Path) rev-parse --show-toplevel 2>$null)
  if ($LASTEXITCODE -eq 0 -and $repo) {
    git -C $repo check-ignore -q -- $Path 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Die Datei '$Path' ist NICHT von Git ignoriert. Abbruch (Geheimnisse gehören nie ins Repository)." }
  }
}

# vorhandene Datei lesen (KEY=VALUE, Kommentare und Leerzeilen bleiben unberücksichtigt)
$env_ = [ordered]@{}
if (Test-Path $Path) {
  foreach ($line in Get-Content -Path $Path -Encoding utf8) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $env_[$Matches[1]] = $Matches[2].Trim() }
  }
}
$report = [ordered]@{}
function Set-Key([string]$k, [string]$v, [string]$how) { $script:env_[$k] = $v; $script:report[$k] = $how }
function Keep-Key([string]$k) { if (-not $script:report.Contains($k)) { $script:report[$k] = if ($script:env_[$k]) { 'behalten' } else { 'leer' } } }

# 1) lokaler Entwicklungsschlüssel (nur Development, nur Loopback)
if (-not $env_['PORTAL_DEV_ADMIN_KEY'] -or $Force) {
  $bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
  Set-Key 'PORTAL_DEV_ADMIN_KEY' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')) $(if ($Force -and $env_['PORTAL_DEV_ADMIN_KEY']) { 'neu erzeugt (Force)' } else { 'neu erzeugt' })
} else { Keep-Key 'PORTAL_DEV_ADMIN_KEY' }

# 2) Entra-Client-Secret der PoC-App (nur auf ausdrücklichen Schalter)
if ($CreateEntraSecret) {
  $tenant = az account show --query tenantId -o tsv 2>$null
  if ($tenant -ne $ExternalTenantId) { throw "Die Azure CLI ist in Tenant '$tenant' angemeldet, erwartet wird der External Tenant $ExternalTenantId. Bitte: az login --tenant $ExternalTenantId --allow-no-subscriptions" }
  $end = (Get-Date).AddDays($SecretValidDays).ToString('yyyy-MM-dd')
  $secret = az ad app credential reset --id $PocAppClientId --append --display-name $SecretName --end-date $end --query password -o tsv 2>$null
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($secret)) { throw 'Das Client-Secret konnte nicht angelegt werden (Berechtigung für die App-Registrierung prüfen).' }
  Set-Key 'PORTAL_ENTRA_CLIENT_SECRET' $secret.Trim() "neu angelegt (Name '$SecretName', gültig bis $end)"
  Remove-Variable secret
} elseif ($RemoveEntraSecret) {
  $tenant = az account show --query tenantId -o tsv 2>$null
  if ($tenant -ne $ExternalTenantId) { throw "Die Azure CLI ist in Tenant '$tenant' angemeldet, erwartet wird der External Tenant $ExternalTenantId." }
  $ids = @(az ad app credential list --id $PocAppClientId --query "[?displayName=='$SecretName'].keyId" -o tsv)
  foreach ($id in $ids | Where-Object { $_ }) { az ad app credential delete --id $PocAppClientId --key-id $id | Out-Null }
  Set-Key 'PORTAL_ENTRA_CLIENT_SECRET' '' "geleert; $($ids.Count) Secret(s) '$SecretName' in Entra gelöscht"
} else { Keep-Key 'PORTAL_ENTRA_CLIENT_SECRET' }

# 2b) Administratoranmeldung mit derselben Entra-App wie Learny (aus der Learny-.env; nur kopieren, nie ausgeben)
if ($AdminFromLearnyEnv) {
  if (-not (Test-Path $LearnyEnvPath)) { throw "Die Learny-.env wurde nicht gefunden: $LearnyEnvPath" }
  $le = @{}
  foreach ($line in Get-Content -Path $LearnyEnvPath -Encoding utf8) { if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $le[$Matches[1]] = $Matches[2].Trim() } }
  foreach ($k in 'OIDC_ISSUER', 'OIDC_CLIENT_ID', 'OIDC_CLIENT_SECRET') { if (-not $le[$k] -or $le[$k] -like 'YOUR_*') { throw "In der Learny-.env fehlt ein gültiger Wert für $k." } }
  if ($le['OIDC_ISSUER'] -notmatch '^https://login\.microsoftonline\.com/([0-9a-fA-F-]{36})/v2\.0$') { throw 'OIDC_ISSUER ist kein Workforce-v2-Issuer (https://login.microsoftonline.com/<tenant>/v2.0).' }
  $learnyTenantId = $Matches[1]   # sofort sichern: $Matches wird von späteren Vergleichen überschrieben
  if ($le['OIDC_ISSUER'] -like "*$ExternalTenantId*") { throw 'OIDC_ISSUER zeigt auf den External-ID-Tenant; die Administration braucht den Workforce-Tenant.' }
  $roles = @('Admin.All')
  if ($le['OIDC_ROLE_MAP']) { $mapped = @($le['OIDC_ROLE_MAP'].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^(.+)=admin$' } | ForEach-Object { ($_ -split '=')[0] }); if ($mapped.Count -gt 0) { $roles = $mapped } }
  Set-Key 'PORTAL_ADMIN_AUTHORITY' $le['OIDC_ISSUER'] 'aus Learny-.env'
  Set-Key 'PORTAL_ADMIN_TENANT_ID' $learnyTenantId 'aus Learny-.env'
  Set-Key 'PORTAL_ADMIN_CLIENT_ID' $le['OIDC_CLIENT_ID'] 'aus Learny-.env'
  Set-Key 'PORTAL_ADMIN_CLIENT_SECRET' $le['OIDC_CLIENT_SECRET'] 'aus Learny-.env kopiert'
  Set-Key 'PORTAL_ADMIN_ROLES' ($roles -join ',') 'Rolle(n), die in Learny auf admin abgebildet sind'
  if (-not $env_['PORTAL_ADMIN_REDIRECT']) { Set-Key 'PORTAL_ADMIN_REDIRECT' 'http://localhost:5090/admin/callback' 'gesetzt (muss an der Entra-App eingetragen sein)' }
  Write-Warning 'Das Secret der Learny-App liegt jetzt zusätzlich in der portal.env (git-ignoriert). Nach dem Test nicht weitergeben; Aufräumen: Datei löschen.'
}

# 3) Exchange-Versand (nur Kennungen)
if ($WithMail) {
  foreach ($k in $Mail.Keys) { if ($env_[$k] -ne $Mail[$k]) { Set-Key $k $Mail[$k] 'gesetzt' } else { Keep-Key $k } }
  if ($AllowedRecipients) { Set-Key 'PORTAL_MAIL_ALLOWED_RECIPIENTS' ($AllowedRecipients -join ',') 'gesetzt' } else { Keep-Key 'PORTAL_MAIL_ALLOWED_RECIPIENTS' }
  if (-not $env_['PORTAL_MAIL_ALLOWED_RECIPIENTS']) { Write-Warning 'PORTAL_MAIL_ALLOWED_RECIPIENTS ist leer: Ohne erlaubte Empfänger sendet der Mailservice nichts (fail-closed). Mit -AllowedRecipients <adresse> ergänzen.' }
  if (-not (Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object Thumbprint -eq $Mail.MAIL_CERT_THUMBPRINT)) { Write-Warning 'Das Mail-Zertifikat (Thumbprint) wurde in Cert:\CurrentUser\My nicht gefunden.' }
}

# Datei schreiben (UTF-8 ohne BOM), Reihenfolge stabil
$lines = @('# Lokale Portal-Konfiguration (Geheimnisse, nie committen). Erzeugt/ergänzt durch poc/New-PortalEnv.ps1') + ($env_.Keys | ForEach-Object { "$_=$($env_[$_])" })
[IO.File]::WriteAllText($Path, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

Write-Host "Datei: $Path"
$report.GetEnumerator() | ForEach-Object {
  $isSecret = $_.Key -match 'KEY|SECRET'
  $shown = if ($isSecret) { if ($env_[$_.Key]) { "gesetzt ($($env_[$_.Key].Length) Zeichen)" } else { 'nicht gesetzt' } } else { $env_[$_.Key] }
  Write-Host ("  {0,-34} {1,-52} {2}" -f $_.Key, $_.Value, $shown)
}
if (-not $env_['PORTAL_ENTRA_CLIENT_SECRET']) { Write-Host "`nHinweis: PORTAL_ENTRA_CLIENT_SECRET ist leer. Ohne Secret funktionieren Administration, Einladungen und Benutzerverwaltung, aber keine echte Entra-Registrierung (-CreateEntraSecret)." }
