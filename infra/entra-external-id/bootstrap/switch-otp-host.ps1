<#
.SYNOPSIS
  Stellt die OTP-Extension (OnOtpSend) auf einen neuen Host um: Application-ID-URI der API-App, Ziel-URL und Ressourcen-ID der Extension — ohne Device-Login.
.DESCRIPTION
  Hintergrund: Entra verlangt, dass die Ressourcen-ID der Extension in den identifierUris der API-App steht und deren Host zur Ziel-URL passt.
  Ein reines Ändern der Ziel-URL scheitert deshalb mit "ResourceIdNotFoundInIdentifierUris".
  Schritte (ohne -Apply nur Trockenlauf, liest nur):
    1. API-App (ApiClientId) bekommt die zusätzliche identifierUri api://<NewHost>/<ApiClientId> (vorhandene bleiben für den Rückweg).
    2. Die Extension erhält Ziel-URL https://<NewHost>/extensions/otp-send und resourceId api://<NewHost>/<ApiClientId>.
  Anmeldung ohne Device-Code: normale Azure-CLI-Anmeldung mit ausdrücklich angeforderten Graph-Rechten (Browser, Multi-Faktor); Tokens nur im Speicher:
    az login --tenant <TenantId> --allow-no-subscriptions --scope "https://graph.microsoft.com/Application.ReadWrite.All https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All"
  Als Global Administrator wird dabei die Zustimmung zu diesen Rechten angeboten. Der Tenant wird nur mit -Apply geändert; es wird nichts gelöscht.
.EXAMPLE
  ./switch-otp-host.ps1 -ApiClientId <Client-ID der OTP-API-App>          # Trockenlauf
  ./switch-otp-host.ps1 -ApiClientId <Client-ID der OTP-API-App> -Apply   # umstellen
#>
[CmdletBinding()]
param(
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId = '5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63',
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$ApiClientId,
  [ValidatePattern('^[a-z0-9.-]+$')][string]$NewHost = 'learny.brors.date',
  [string]$ExtensionName = 'Learny OTP Mail (OnOtpSend)',
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$loginHint = "az login --tenant $TenantId --allow-no-subscriptions --scope `"https://graph.microsoft.com/Application.ReadWrite.All https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All`""

$tok = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv 2>$null
if (-not $tok) { throw "Kein Graph-Token. Anmelden mit: $loginHint" }
$p = $tok.Split('.')[1].Replace('-', '+').Replace('_', '/'); $p = $p.PadRight($p.Length + (4 - $p.Length % 4) % 4, '=')
$claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
if ($claims.tid -ne $TenantId) { throw "Angemeldet in Tenant $($claims.tid), erwartet $TenantId (External Tenant). Abbruch." }
$scp = [string]$claims.scp
if ($scp -notmatch 'Application\.ReadWrite\.All' -or $scp -notmatch 'CustomAuthenticationExtension\.ReadWrite\.All') {
  Write-Warning "Dem Token fehlen Rechte (hat: $scp). Neu anmelden mit:`n  $loginHint"
}
$h = @{ Authorization = "Bearer $tok" }
function Graph($Method, $Uri, $Body) {
  try {
    if ($null -ne $Body) { return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8))) }
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h
  } catch { $d = $_.ErrorDetails.Message; throw "Graph-Aufruf fehlgeschlagen ($Method $Uri): $d" }
}

$newUri = "api://$NewHost/$ApiClientId"
$newTarget = "https://$NewHost/extensions/otp-send"
Write-Host ($(if ($Apply) { 'AUSFÜHRUNG' } else { 'TROCKENLAUF' }))

# 1. API-App
$app = (Graph GET "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq '$ApiClientId'&`$select=id,displayName,identifierUris").value | Select-Object -First 1
if (-not $app) { throw "App $ApiClientId nicht gefunden (Client-ID der OTP-API-App, nicht der PoC-Anmelde-App)." }
$uris = @($app.identifierUris)
Write-Host "API-App '$($app.displayName)': identifierUris = $($uris -join ', ')"
$needUri = $uris -notcontains $newUri
if ($needUri) { Write-Host "  + identifierUri $newUri" } else { Write-Host '  identifierUri bereits vorhanden' }

# 2. Extension
$beta = 'https://graph.microsoft.com/beta/identity'
$ext = @((Graph GET "$beta/customAuthenticationExtensions").value | Where-Object { $_.displayName -eq $ExtensionName }) | Select-Object -First 1
if (-not $ext) { throw "Extension '$ExtensionName' nicht gefunden." }
$curUrl = $ext.endpointConfiguration.targetUrl; $curRes = $ext.authenticationConfiguration.resourceId
Write-Host "Extension: Ziel $curUrl, resourceId $curRes"
$needExt = ($curUrl -ne $newTarget) -or ($curRes -ne $newUri)
if ($needExt) { Write-Host "  -> Ziel $newTarget, resourceId $newUri" } else { Write-Host '  Extension ist bereits umgestellt' }

if (-not $Apply) { Write-Host "`nNichts geändert. Mit -Apply umstellen."; return }

if ($needUri) {
  Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($app.id)" @{ identifierUris = @($uris + $newUri) } | Out-Null
  Write-Host 'identifierUri ergänzt. (Wird sie als "nicht verifizierte Domain" abgelehnt: Domain im Tenant verifizieren oder Weg über Cloudflare nutzen.)'
}
if ($needExt) {
  Graph PATCH "$beta/customAuthenticationExtensions/$($ext.id)" @{
    '@odata.type' = '#microsoft.graph.onOtpSendCustomExtension'
    endpointConfiguration = @{ '@odata.type' = '#microsoft.graph.httpRequestEndpoint'; targetUrl = $newTarget }
    authenticationConfiguration = @{ '@odata.type' = '#microsoft.graph.azureAdTokenAuthentication'; resourceId = $newUri }
  } | Out-Null
  Write-Host 'Extension umgestellt.'
}
Write-Host "Fertig. EXTID_OTP_AUDIENCES auf der Plattform muss die Client-ID $ApiClientId (und/oder $newUri) enthalten."
