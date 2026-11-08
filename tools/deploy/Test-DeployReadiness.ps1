<#
.SYNOPSIS
  Schreibgeschützte Vorprüfung vor dem Produktions-Deployment (https://learny.brors.date): .env-Konsistenz, GitHub-Variablen/-Secrets (nur Namen), Entra (nur lesen).
.DESCRIPTION
  Ändert nichts, zeigt nie Werte von Secrets. Exit-Code 1 bei Fehlern.
    1. .env: Pflichtschlüssel, Trennung der Tenants (MAIL_TENANT_ID ≠ EXTID_TENANT_ID), Authority passend zum Tenant, kein Dev-Login.
    2. GitHub (gh CLI): fehlende Secrets/Variablen des Workflows deploy-iis-on-tag.yml.
    3. Entra (optional, -CheckEntra; vorher az login --tenant <EXTID_TENANT_ID>): Redirect-URIs der External-ID-App, OTP-Extension-Ziel.
.EXAMPLE
  ./tools/deploy/Test-DeployReadiness.ps1
  ./tools/deploy/Test-DeployReadiness.ps1 -CheckEntra
#>
[CmdletBinding()]
param(
  [string]$EnvPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.env'),
  [string]$PlatformBaseUrl = 'https://learny.brors.date',
  [string]$OtpPublicHost = 'learny-otp.brors.date',
  [switch]$CheckEntra
)
$script:bad = 0
function Ok($m)   { Write-Host "  OK    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  HINW  $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  FEHL  $m" -ForegroundColor Red; $script:bad++ }
$repo = Split-Path (Split-Path $PSScriptRoot)

Write-Host '1. .env'
$envv = @{}
if (Test-Path $EnvPath) {
  Get-Content $EnvPath | ForEach-Object { if ($_ -match '^\s*([A-Z0-9_]+)\s*=\s*(.*)$') { $envv[$Matches[1]] = $Matches[2].Trim().Trim('"') } }
  Ok ".env gelesen ($($envv.Count) Schlüssel)"
} else { Warn ".env nicht gefunden ($EnvPath) — nur GitHub/Entra-Prüfung" }
function Need($k) { if ($envv.ContainsKey($k) -and $envv[$k]) { Ok "$k gesetzt" } else { Fail "$k fehlt" } }
if ($envv.Count) {
  foreach ($k in 'OIDC_ISSUER', 'OIDC_CLIENT_ID', 'OIDC_CLIENT_SECRET', 'MCP_TOKEN_KEY', 'PUBLIC_BASE_URL') { Need $k }
  if ($envv['PUBLIC_BASE_URL'] -and $envv['PUBLIC_BASE_URL'] -ne $PlatformBaseUrl) { Warn "PUBLIC_BASE_URL ist '$($envv['PUBLIC_BASE_URL'])', für die Plattform erwartet: $PlatformBaseUrl (lokal ist http://localhost:5000 richtig)" }
  if ($envv['AUTH_MODE'] -eq 'dev') { Fail 'AUTH_MODE=dev ist nie für Produktion (ADR 0026)' }
  if ($envv['EXTID_ENABLED'] -in '1', 'true') {
    foreach ($k in 'EXTID_AUTHORITY', 'EXTID_TENANT_ID', 'EXTID_CLIENT_ID', 'EXTID_CLIENT_SECRET', 'INVITE_TOKEN_KEY') { Need $k }
    if ($envv['EXTID_AUTHORITY'] -and $envv['EXTID_TENANT_ID'] -and $envv['EXTID_AUTHORITY'] -notmatch [regex]::Escape($envv['EXTID_TENANT_ID'])) { Fail 'EXTID_AUTHORITY passt nicht zu EXTID_TENANT_ID' }
    if ($envv['EXTID_AUTHORITY'] -and $envv['EXTID_AUTHORITY'] -notmatch '\.ciamlogin\.com/') { Warn 'EXTID_AUTHORITY ist keine ciamlogin.com-Adresse' }
  } else { Warn 'EXTID_ENABLED nicht gesetzt: External-ID-Anmeldung aus' }
  if ($envv['MAIL_ENABLED'] -in '1', 'true') {
    foreach ($k in 'MAIL_TENANT_ID', 'MAIL_CLIENT_ID', 'MAIL_SENDER') { Need $k }
    if ($envv['MAIL_TENANT_ID'] -and $envv['MAIL_TENANT_ID'] -eq $envv['EXTID_TENANT_ID']) { Fail 'MAIL_TENANT_ID darf nicht der External-ID-Tenant sein (ADR 0050)' }
    if (-not $envv['MAIL_CLIENT_CERT_PATH']) { Warn 'Kein MAIL_CLIENT_CERT_PATH: Zertifikat wird auf der Plattform erzeugt (Administration → Mailversand); die .cer danach in Entra hochladen' }
    if ($envv['MAIL_ALLOWED_RECIPIENTS'] -eq '*') { Warn 'MAIL_ALLOWED_RECIPIENTS=* erlaubt alle Empfänger' }
  }
  if ($envv['EXTID_OTP_ENABLED'] -in '1', 'true') { Need 'EXTID_OTP_AUDIENCES' }
}

Write-Host '2. GitHub (nur Namen)'
if (Get-Command gh -ErrorAction SilentlyContinue) {
  $wf = Get-Content (Join-Path $repo '.github/workflows/deploy-iis-on-tag.yml') -Raw
  $needVars = [regex]::Matches($wf, 'vars\.([A-Z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
  $needSec = [regex]::Matches($wf, 'secrets\.([A-Z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne 'GITHUB_TOKEN' } | Sort-Object -Unique
  $haveVars = @(gh variable list --json name --jq '.[].name' 2>$null)
  $haveSec = @(gh secret list --json name --jq '.[].name' 2>$null)
  if (-not $haveVars.Count -and -not $haveSec.Count) { Warn 'gh nicht angemeldet oder kein Zugriff (gh auth login)' }
  else {
    $mv = @($needVars | Where-Object { $_ -notin $haveVars }); $ms = @($needSec | Where-Object { $_ -notin $haveSec })
    $hard = @($ms | Where-Object { $_ -in 'MCP_API_KEY', 'MCP_TOKEN_KEY' })   # der Workflow bricht ohne diese beiden ab
    if ($hard.Count) { Fail "Workflow bricht ab ohne Secret(s): $($hard -join ', ') (MCP_TOKEN_KEY: openssl rand -base64 32)" }
    if ($ms.Count) { Warn "Weitere nicht gesetzte Secrets (leere Werte werden in die web.env übernommen): $(($ms | Where-Object { $_ -notin $hard }) -join ', ')" } if (-not $ms.Count) { Ok 'Alle Secrets des Workflows vorhanden' }
    if ($mv.Count) { Warn "Nicht gesetzte Variablen (Vorgabewerte des Workflows gelten, falls vorhanden): $($mv -join ', ')" } else { Ok 'Alle Variablen vorhanden' }
  }
} else { Warn 'gh CLI nicht installiert' }

if ($CheckEntra) {
  Write-Host '3. Entra External ID (nur lesen)'
  $tid = $envv['EXTID_TENANT_ID']; $cid = $envv['EXTID_CLIENT_ID']
  if (-not $tid -or -not $cid) { Fail 'EXTID_TENANT_ID/EXTID_CLIENT_ID fehlen in .env' }
  else {
    $tok = az account get-access-token --tenant $tid --resource https://graph.microsoft.com --query accessToken -o tsv 2>$null
    if (-not $tok) { Fail "Kein Graph-Token: az login --tenant $tid" }
    else {
      $h = @{ Authorization = "Bearer $tok" }
      $app = (Invoke-RestMethod -Headers $h -Uri "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq '$cid'&`$select=displayName,web").value | Select-Object -First 1
      if (-not $app) { Fail 'App mit EXTID_CLIENT_ID nicht im Tenant gefunden' }
      else {
        $want = "$PlatformBaseUrl/auth/external/callback"
        if ($app.web.redirectUris -contains $want) { Ok "Redirect-URI vorhanden: $want" } else { Fail "Redirect-URI fehlt: $want (infra/entra-external-id/bootstrap/learny-targets.ps1 -Apply)" }
        if ($app.web.redirectUris | Where-Object { $_ -match '^http://(?!localhost)' }) { Fail 'Unsichere http-Redirect-URI (außer localhost)' }
        # Hinweis: Learny nutzt Authorization Code + PKCE mit form_post — implizite ID-Token-Ausstellung ist NICHT nötig und sollte aus bleiben.
      }
      try {
        $ext = (Invoke-RestMethod -Headers $h -Uri 'https://graph.microsoft.com/beta/identity/customAuthenticationExtensions').value
        $otp = @($ext | Where-Object { $_.displayName -match 'OTP' })
        if ($otp.Count) {
          foreach ($o in $otp) {
            $u = $o.endpointConfiguration.targetUrl
            if ($u -eq "https://$OtpPublicHost/extensions/otp-send" -or $u -eq "$PlatformBaseUrl/extensions/otp-send") { Ok "OTP-Extension-Ziel: $u" }
            else { Fail "OTP-Extension-Ziel unerwartet: $u (Plattform: $PlatformBaseUrl/extensions/otp-send)" }
          }
        } else { Warn 'Keine OTP-Extension gefunden' }
      } catch { Warn "OTP-Extension nicht lesbar (CustomAuthenticationExtension.Read.All?): $($_.Exception.Message)" }
    }
  }
}
Write-Host ''
if ($script:bad) { Write-Host "$script:bad Fehler — Deployment erst nach Behebung." -ForegroundColor Red; exit 1 } else { Write-Host 'Keine Fehler.' -ForegroundColor Green }
