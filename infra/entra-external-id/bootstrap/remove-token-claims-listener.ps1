<#
.SYNOPSIS
  Entfernt genau den Listener "OnTokenIssuanceStart" (Token-Anreicherung) der Learny-PoC-App im External Tenant — die Ursache des Anmeldefehlers 1100001.
.DESCRIPTION
  Hintergrund: Learny nutzt keine Token-Claims (ADR 0047). Ruft Entra beim Token-Ausstellen die Extension /extensions/token-claims auf und diese ist nicht erreichbar, scheitert die Anmeldung.
  Ohne -Apply nur Trockenlauf: zeigt den Listener, löscht nichts. Mit -Apply wird NUR dieser eine Listener gelöscht (Typ onTokenIssuanceStartListener, App cb54157f-…).
  Nicht angefasst: der OTP-Listener (onEmailOtpSend), alle anderen Listener, die Extensions selbst, die Claims-Mapping-Policy und die App. Rückbau jederzeit mit claims-extension.ps1 -ExtensionId <id> -Activate.
  Anmeldung: Azure CLI mit Anmeldung im External Tenant (az login --tenant <TenantId> --allow-no-subscriptions --scope https://graph.microsoft.com//.default); Recht EventListener.ReadWrite.All
  nötig (sonst 403 -> Graph Explorer verwenden: DELETE https://graph.microsoft.com/beta/identity/authenticationEventListeners/<id>). Das Skript prüft am Ende, ob der Listener weg ist.
.EXAMPLE
  ./remove-token-claims-listener.ps1            # Trockenlauf
  ./remove-token-claims-listener.ps1 -Apply     # löscht den Listener
#>
[CmdletBinding()]
param(
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId = '5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63',
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$PocAppClientId = 'cb54157f-bcfb-4e78-9c81-b63ee7a0a03d',
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$uri = 'https://graph.microsoft.com/beta/identity/authenticationEventListeners'

$cur = az account get-access-token --resource https://graph.microsoft.com --query tenant -o tsv 2>$null
if ($LASTEXITCODE -ne 0 -or -not $cur) { throw "Kein Graph-Token. Anmelden: az login --tenant $TenantId --allow-no-subscriptions --scope https://graph.microsoft.com//.default" }
if ($cur -ne $TenantId) { throw "Angemeldet in Tenant $cur, erwartet $TenantId (External Tenant). Abbruch." }

function Get-Listeners {
  $r = az rest --method GET --uri $uri -o json
  if ($LASTEXITCODE -ne 0) { throw 'Listener konnten nicht gelesen werden (Rechte/MFA).' }
  return @(($r | ConvertFrom-Json).value)
}
$mine = @(Get-Listeners | Where-Object {
  $_.'@odata.type' -eq '#microsoft.graph.onTokenIssuanceStartListener' -and
  @($_.conditions.applications.includeApplications | ForEach-Object { $_.appId }) -contains $PocAppClientId })

if ($mine.Count -eq 0) { Write-Host "Kein Token-Anreicherungs-Listener für die App $PocAppClientId vorhanden. Nichts zu tun."; return }
Write-Host ($(if ($Apply) { 'AUSFÜHRUNG' } else { 'TROCKENLAUF' }) + ' — zu löschen:')
$mine | ForEach-Object { Write-Host "  DELETE $uri/$($_.id)   (onTokenIssuanceStartListener, App $PocAppClientId)" }
Write-Host 'Bleibt unverändert: OTP-Listener, andere Listener, Extensions, Claims-Mapping-Policy, App.'
if (-not $Apply) { Write-Host "`nNichts geändert. Mit -Apply löschen."; return }

foreach ($l in $mine) {
  az rest --method DELETE --uri "$uri/$($l.id)" | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Löschen fehlgeschlagen (vermutlich fehlt EventListener.ReadWrite.All). Alternative: Graph Explorer, DELETE $uri/$($l.id)" }
}
$left = @(Get-Listeners | Where-Object { $_.'@odata.type' -eq '#microsoft.graph.onTokenIssuanceStartListener' -and @($_.conditions.applications.includeApplications | ForEach-Object { $_.appId }) -contains $PocAppClientId })
$otp = @(Get-Listeners | Where-Object { $_.'@odata.type' -eq '#microsoft.graph.onEmailOtpSendListener' })
Write-Host "Verbleibende Token-Anreicherungs-Listener der App: $($left.Count) (erwartet 0). OTP-Listener unverändert vorhanden: $($otp.Count)."
if ($left.Count -gt 0) { throw 'Der Listener ist noch vorhanden.' }
Write-Host 'Fertig. Link neu ausstellen und die externe Anmeldung erneut versuchen.'
