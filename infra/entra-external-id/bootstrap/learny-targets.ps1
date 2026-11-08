<#
.SYNOPSIS
  Stellt Entra External ID (PoC-App und OTP-Extension) auf die Learny-Adressen um: Redirect-URIs für Debug und Plattform, Ziel-URL der OTP-Extension.
.DESCRIPTION
  Ohne -Apply nur Trockenlauf (liest den Bestand, zeigt die geplanten Änderungen; ändert nichts). Es wird nichts gelöscht:
    1. Redirect-URIs der PoC-App: <DebugBaseUrl>/auth/external/callback und <PlatformBaseUrl>/auth/external/callback werden ZUSÄTZLICH zu den vorhandenen eingetragen.
    2. Ziel-URL der OTP-Extension (OnOtpSend): https://<OtpPublicHost>/extensions/otp-send. Bezeichner der API-App (Audience) und Listener bleiben unverändert.
       -OtpTarget Debug    (Standard) Der öffentliche Host (Cloudflare-Tunnel) zeigt auf die lokale Debug-Instanz (http://localhost:5000). OTP-Tests laufen lokal.
       -OtpTarget Platform  Die Extension ruft die Plattform direkt (<PlatformBaseUrl>/extensions/otp-send).
  Anmeldung: Azure CLI (az login --tenant <TenantId>) oder -DeviceLogin (Graph-Rechte Application.ReadWrite.All, CustomAuthenticationExtension.ReadWrite.All). Tokens nur im Speicher.
  Danach zeigt das Skript, was in Cloudflare einzutragen ist (-ShowCloudflare auch ohne Anmeldung). Graph-Endpunkte der Extension sind beta (Preview).
.EXAMPLE
  ./learny-targets.ps1 -ShowCloudflare                         # nur die Cloudflare-Angaben anzeigen
  ./learny-targets.ps1                                         # Trockenlauf
  ./learny-targets.ps1 -Apply -DeviceLogin                     # umstellen (OTP-Test lokal über den Tunnel)
  ./learny-targets.ps1 -Apply -DeviceLogin -OtpTarget Platform # OTP direkt gegen die Plattform
#>
[CmdletBinding()]
param(
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId = '5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63',
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$PocAppClientId = 'cb54157f-bcfb-4e78-9c81-b63ee7a0a03d',
  [ValidatePattern('^http://localhost(:[0-9]+)?$')][string]$DebugBaseUrl = 'http://localhost:5000',
  [ValidatePattern('^https://[^/]+$')][string]$PlatformBaseUrl = 'https://learny.brors.date',
  [ValidatePattern('^[a-z0-9.-]+$')][string]$OtpPublicHost = 'learny-otp.brors.date',
  [ValidateSet('Debug', 'Platform')][string]$OtpTarget = 'Debug',
  [string]$ExtensionName = 'Learny OTP Mail (OnOtpSend)',
  [switch]$DeviceLogin,
  [switch]$Apply,
  [switch]$ShowCloudflare
)
$ErrorActionPreference = 'Stop'
$otpPath = '/extensions/otp-send'
$targetUrl = if ($OtpTarget -eq 'Platform') { "$PlatformBaseUrl$otpPath" } else { "https://$OtpPublicHost$otpPath" }
$localPort = ([Uri]$DebugBaseUrl).Port

function Show-Cloudflare {
  Write-Host @"

=== Cloudflare (Zero Trust → Networks → Tunnels → <dein Tunnel> → Public Hostname) ===
Nur für -OtpTarget Debug nötig (OTP-Test lokal über den Tunnel):
  Subdomain/Domain : $($OtpPublicHost.Split('.')[0]) / $($OtpPublicHost.Substring($OtpPublicHost.IndexOf('.') + 1))      (= $OtpPublicHost)
  Path             : extensions/otp-send                (nur dieser Pfad; alles andere bleibt unveröffentlicht)
  Service Type     : HTTP
  URL              : localhost:$localPort                    (früher localhost:5080 = PoC-Portal; jetzt die Learny-Debug-Instanz)
  Zusätzlich       : eine zweite Regel für denselben Host ohne Pfad mit Service 'HTTP Status 404' (Catch-all), damit /invite, /admin usw. über diesen Host nie erreichbar sind.
  Access           : KEINE Cloudflare-Access-Richtlinie auf diesem Host (Entra kann sich dort nicht anmelden; Learny prüft das Bearer-Token der Extension selbst).
  WAF/Bot          : POST von Microsoft nicht blockieren. Optional: Rate-Limiting-Regel für /extensions/otp-send.

Bei lokal verwaltetem Tunnel (config.yml) stattdessen:
  ingress:
    - hostname: $OtpPublicHost
      path: ^/extensions/otp-send$
      service: http://localhost:$localPort
    - hostname: $OtpPublicHost
      service: http_status:404
    - service: http_status:404

Plattform ($PlatformBaseUrl): kein Cloudflare-Eintrag für die OTP-Extension nötig, wenn die Domain bereits auf den Server zeigt.
Dort darf /extensions/otp-send, /invite*, /invite.js und /auth/* nicht durch Access/WAF blockiert sein.

Lokal in der .env (Debug): EXTID_OTP_ENABLED=1, EXTID_OTP_AUDIENCES=<API-App-Id>,api://learny-otp.brors.date/<API-App-Id>, MAIL_ENABLED=1 (Zertifikat + MAIL_ALLOWED_RECIPIENTS), EXTID_ENABLED=1.
"@
}
if ($ShowCloudflare) { Show-Cloudflare; return }

# ── Anmeldung ──
$script:Token = $null
if ($DeviceLogin) {
  $cid = '14d82eec-204b-4c2f-b7e8-296a70dab67e'   # öffentlicher Client "Microsoft Graph Command Line Tools"
  $scopes = 'https://graph.microsoft.com/Application.ReadWrite.All https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All'
  $dc = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode" -Body @{ client_id = $cid; scope = $scopes }
  Write-Host $dc.message
  $deadline = (Get-Date).AddSeconds([int]$dc.expires_in)
  while (-not $script:Token) {
    if ((Get-Date) -gt $deadline) { throw 'Gerätecode abgelaufen. Abbruch.' }
    Start-Sleep -Seconds ([int]$dc.interval)
    try {
      $t = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body @{ grant_type = 'urn:ietf:params:oauth:grant-type:device_code'; client_id = $cid; device_code = $dc.device_code }
      $script:Token = $t.access_token
    } catch {
      $err = $null; try { $err = ($_.ErrorDetails.Message | ConvertFrom-Json).error } catch { }
      if ($err -ne 'authorization_pending' -and $err -ne 'slow_down') { throw "Anmeldung fehlgeschlagen: $err" }
    }
  }
  $p = $script:Token.Split('.')[1].Replace('-', '+').Replace('_', '/'); $p = $p.PadRight($p.Length + (4 - $p.Length % 4) % 4, '=')
  $tid = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json).tid
  if ($tid -ne $TenantId) { throw "Angemeldet in Tenant $tid, erwartet $TenantId (External Tenant). Abbruch." }
} else {
  # Der External Tenant hat kein Azure-Abonnement: der Tenant wird aus dem Graph-Token gelesen (nicht aus "az account show").
  $cur = az account get-access-token --resource https://graph.microsoft.com --query tenant -o tsv 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $cur) { throw "Kein Graph-Token. Anmelden mit: az login --tenant $TenantId --allow-no-subscriptions --scope https://graph.microsoft.com//.default  (bei AADSTS50076: MFA im Browser abschließen) — oder -DeviceLogin verwenden." }
  if ($cur -ne $TenantId) { throw "Angemeldet in Tenant $cur, erwartet $TenantId (External Tenant). Abbruch (az login --tenant $TenantId --allow-no-subscriptions)." }
}
function Invoke-Graph([string]$Method, [string]$Uri, $Body = $null) {
  if ($DeviceLogin) {
    $h = @{ Authorization = "Bearer $script:Token" }
    if ($null -ne $Body) { return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8))) }
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h
  }
  $hint = "Graph-Aufruf fehlgeschlagen (Details siehe oben). Bei AADSTS50076 (MFA): az logout; az login --tenant $TenantId --scope https://graph.microsoft.com//.default - oder das Skript mit -DeviceLogin starten."
  if ($null -eq $Body) { $r = az rest --method $Method --uri $Uri -o json; if ($LASTEXITCODE -ne 0) { throw $hint }; return $r | ConvertFrom-Json }
  $f = New-TemporaryFile
  try {
    Set-Content -Path $f -Value ($Body | ConvertTo-Json -Depth 8) -Encoding utf8
    $r = az rest --method $Method --uri $Uri --headers 'Content-Type=application/json' --body "@$f" -o json
    if ($LASTEXITCODE -ne 0) { throw $hint }
    return $r | ConvertFrom-Json
  } finally { Remove-Item $f -Force }
}

# ── 1. Redirect-URIs der PoC-App (zusätzlich, nichts entfernen) ──
$app = (Invoke-Graph GET "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq '$PocAppClientId'&`$select=id,displayName,web").value | Select-Object -First 1
if (-not $app) { throw "App $PocAppClientId im Tenant nicht gefunden." }
$have = @($app.web.redirectUris)
$want = @("$DebugBaseUrl/auth/external/callback", "$PlatformBaseUrl/auth/external/callback")
$add = @($want | Where-Object { $have -notcontains $_ })
Write-Host ($(if ($Apply) { 'AUSFÜHRUNG' } else { 'TROCKENLAUF' }) + " — App '$($app.displayName)'")
Write-Host "  vorhandene Redirect-URIs: $($have.Count)"
if ($add.Count -eq 0) { Write-Host '  Redirect-URIs: nichts zu tun' } else { $add | ForEach-Object { Write-Host "  + Redirect-URI $_" } }

# ── 2. Ziel-URL der OTP-Extension ──
$beta = 'https://graph.microsoft.com/beta/identity'
$ext = @((Invoke-Graph GET "$beta/customAuthenticationExtensions").value | Where-Object { $_.displayName -eq $ExtensionName }) | Select-Object -First 1
if (-not $ext) { Write-Warning "Extension '$ExtensionName' nicht vorhanden — zuerst otp-extension.ps1 ausführen." }
else {
  $curUrl = $ext.endpointConfiguration.targetUrl
  if ($curUrl -eq $targetUrl) { Write-Host "  OTP-Ziel-URL: nichts zu tun ($targetUrl)" } else { Write-Host "  OTP-Ziel-URL: $curUrl  ->  $targetUrl" }
}

if ($Apply) {
  if ($add.Count -gt 0) {
    Invoke-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($app.id)" @{ web = @{ redirectUris = @($have + $add) } } | Out-Null
    Write-Host '  Redirect-URIs ergänzt.'
  }
  if ($ext -and $ext.endpointConfiguration.targetUrl -ne $targetUrl) {
    Invoke-Graph PATCH "$beta/customAuthenticationExtensions/$($ext.id)" @{ '@odata.type' = '#microsoft.graph.onOtpSendCustomExtension'; endpointConfiguration = @{ '@odata.type' = '#microsoft.graph.httpRequestEndpoint'; targetUrl = $targetUrl } } | Out-Null
    Write-Host '  OTP-Ziel-URL aktualisiert.'
  }
} else { Write-Host "`nNichts geändert. Mit -Apply umstellen." }

if ($OtpTarget -eq 'Debug') { Show-Cloudflare }
