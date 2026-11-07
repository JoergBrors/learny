<#
.SYNOPSIS
  Registriert die Token-Anreicherung (Custom Claims Provider, Ereignis OnTokenIssuanceStart) fuer die PoC-App im External Tenant.
.DESCRIPTION
  Ohne -Apply nur Trockenlauf: zeigt die geplanten Graph-Aufrufe, aendert nichts. Mit -Apply (nur nach ausdruecklicher Freigabe) in dieser Reihenfolge:
    1. Custom Authentication Extension (onTokenIssuanceStartCustomExtension) mit Ziel <TargetUrl> (/extensions/token-claims) und den erwarteten Claims
    2. Claims-Mapping-Policy (nur die learny_*-Claims aus dem Custom Claims Provider)
    3. Policy am Service Principal der PoC-App
    4. App-Manifest der PoC-App: acceptMappedClaims = true, requestedAccessTokenVersion = 2 (nur diese beiden Felder)
    5. Event-Listener (onTokenIssuanceStartListener) fuer die PoC-App  <- erst ab hier wirkt es auf Tokens (nur mit -Activate)
  Mit -ExtensionId (bereits angelegte Extension) und -Activate wird NUR der Listener angelegt (Schritte 1-4 werden nicht wiederholt).
  Mit -RemoveExtensionId <id> -Apply wird genau eine Token-Claims-Extension geloescht (nur Typ onTokenIssuanceStart, nie die OTP-Extension, nie eine mit aktivem Listener).
  Ohne -Activate bleibt der Listener aus: Tokens aendern sich nicht. Rueckbau: -RemoveListener -Apply entfernt nur den Listener der PoC-App.
  Voraussetzung: az login --tenant <TenantId> --allow-no-subscriptions mit den Rechten CustomAuthenticationExtension.ReadWrite.All, EventListener.ReadWrite.All,
  Policy.ReadWrite.ApplicationConfiguration und Application.ReadWrite.All. Die Extension nutzt dieselbe API-App/Audience wie die OTP-Extension (ApiIdentifierUri).
  Anmeldung: Standard ist die Azure CLI; fehlen deren Token die Rechte (Meldung 'Forbidden ... required delegated permissions'), -DeviceLogin verwenden
  (OAuth-Geraetecode mit dem Client 'Microsoft Graph Command Line Tools', genau diese vier Rechte; das Token bleibt nur im Speicher).
  Graph-Endpunkte: beta fuer Extension/Listener (wie otp-extension.ps1), v1.0 fuer Policy und App.
  Hinweis: acceptMappedClaims nie fuer Multi-Tenant-Apps setzen (Missbrauch durch fremde Claims-Mapping-Policies); die PoC-App ist Single-Tenant.
.EXAMPLE
  ./claims-extension.ps1 -TenantId <guid> -TargetUrl https://learny-otp.brors.date/extensions/token-claims -ApiIdentifierUri api://learny-otp.brors.date/<api-client-id> -PocAppClientId <guid>
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
  [Parameter(Mandatory)][ValidatePattern('^https://')][string]$TargetUrl,
  [Parameter(Mandatory)][string]$ApiIdentifierUri,
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$PocAppClientId,
  [string]$ExtensionName = 'Learny Token Claims (OnTokenIssuanceStart)',
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$ExtensionId,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$RemoveExtensionId,
  [switch]$DeviceLogin,
  [switch]$Apply,
  [switch]$Activate,
  [switch]$RemoveListener
)
$ErrorActionPreference = 'Stop'
if ($Activate -and -not $Apply) { throw '-Activate setzt -Apply voraus.' }
if ($ExtensionId -and -not $Activate) { throw '-ExtensionId gilt nur zusammen mit -Activate (nur Listener anlegen).' }
if ($RemoveListener -and $Activate) { throw '-RemoveListener kann nicht mit -Activate kombiniert werden.' }
if ($TargetUrl -match '[?&](code|key|token)=') { throw 'Die TargetUrl darf keine Geheimnisse in der Query enthalten.' }

$script:GraphToken = $null
if ($DeviceLogin) {
  $clientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
  $scopes = 'https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All https://graph.microsoft.com/EventListener.ReadWrite.All https://graph.microsoft.com/Policy.ReadWrite.ApplicationConfiguration https://graph.microsoft.com/Application.ReadWrite.All'
  $dc = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode" -Body @{ client_id = $clientId; scope = $scopes }
  Write-Host $dc.message
  $deadline = (Get-Date).AddSeconds([int]$dc.expires_in)
  while (-not $script:GraphToken) {
    if ((Get-Date) -gt $deadline) { throw 'Geraetecode abgelaufen. Abbruch.' }
    Start-Sleep -Seconds ([int]$dc.interval)
    try {
      $t = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body @{ grant_type = 'urn:ietf:params:oauth:grant-type:device_code'; client_id = $clientId; device_code = $dc.device_code }
      $script:GraphToken = $t.access_token
    } catch {
      $err = $null; try { $err = ($_.ErrorDetails.Message | ConvertFrom-Json).error } catch { }
      if ($err -ne 'authorization_pending' -and $err -ne 'slow_down') { throw "Anmeldung fehlgeschlagen: $err" }
    }
  }
  $payload = $script:GraphToken.Split('.')[1].Replace('-', '+').Replace('_', '/')
  $tc = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.PadRight([int]([math]::Ceiling($payload.Length / 4.0) * 4), '='))) | ConvertFrom-Json
  if ($tc.tid -ne $TenantId) { throw "Angemeldet in Tenant $($tc.tid), erwartet $TenantId (External Tenant). Abbruch." }
} else {
  $current = az account show --query tenantId -o tsv
  if ($current -ne $TenantId) { throw "Angemeldet in Tenant $current, erwartet $TenantId (External Tenant). Abbruch." }
}

$claimNames = 'learny_uid', 'learny_profile', 'learny_status', 'learny_modules', 'learny_modules_n', 'learny_grants_v'
$beta = 'https://graph.microsoft.com/beta/identity'
$v1 = 'https://graph.microsoft.com/v1.0'

$ext = @{
  '@odata.type' = '#microsoft.graph.onTokenIssuanceStartCustomExtension'
  displayName = $ExtensionName
  description = 'Learny: ergaenzt das Token um learny_*-Claims (Profil, Module, Berechtigungsversion). Nur ein Hinweis, der Zugriff wird in Learny weiter geprueft.'
  endpointConfiguration = @{ '@odata.type' = '#microsoft.graph.httpRequestEndpoint'; targetUrl = $TargetUrl }
  authenticationConfiguration = @{ '@odata.type' = '#microsoft.graph.azureAdTokenAuthentication'; resourceId = $ApiIdentifierUri }
  clientConfiguration = @{ timeoutInMilliseconds = 2000; maximumRetries = 1 }
  claimsForTokenConfiguration = @($claimNames | ForEach-Object { @{ claimIdInApiResponse = $_ } })
}
$definition = @{ ClaimsMappingPolicy = @{ Version = 1; IncludeBasicClaimSet = 'true'
  ClaimsSchema = @($claimNames | ForEach-Object { @{ Source = 'CustomClaimsProvider'; ID = $_; JwtClaimType = $_ } }) } } | ConvertTo-Json -Depth 8 -Compress
$policy = @{ definition = @($definition); displayName = 'Learny Token Claims'; isOrganizationDefault = $false }

function Invoke-Graph([string]$Method, [string]$Uri, $Body = $null) {
  # Jeder Fehler bricht ab (keine Teilanwendung): 4xx/5xx werfen, bei az zusaetzlich der Exitcode.
  if ($DeviceLogin) {
    $h = @{ Authorization = "Bearer $script:GraphToken" }
    if ($null -ne $Body) { return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10))) }
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h
  }
  if ($null -eq $Body) { $out = az rest --method $Method --uri $Uri -o json } else {
    $file = New-TemporaryFile
    try { Set-Content -Path $file -Value ($Body | ConvertTo-Json -Depth 10) -Encoding utf8; $out = az rest --method $Method --uri $Uri --headers 'Content-Type=application/json' --body "@$file" -o json }
    finally { Remove-Item $file -Force }
  }
  if ($LASTEXITCODE -ne 0) { throw "Graph-Aufruf fehlgeschlagen: $Method $Uri" }
  if ($out) { return ($out -join "`n") | ConvertFrom-Json }
}

if ($RemoveExtensionId) {
  $e = Invoke-Graph GET "$beta/customAuthenticationExtensions/$RemoveExtensionId"
  if ($e.'@odata.type' -ne '#microsoft.graph.onTokenIssuanceStartCustomExtension') { throw "Die Extension $RemoveExtensionId ist keine Token-Claims-Extension. Abbruch." }
  $used = (Invoke-Graph GET "$beta/authenticationEventListeners").value | Where-Object { $_.handler.customExtension.id -eq $RemoveExtensionId }
  if ($used) { throw "Die Extension $RemoveExtensionId wird von einem Listener verwendet. Abbruch." }
  if ($Apply) { Invoke-Graph DELETE "$beta/customAuthenticationExtensions/$RemoveExtensionId" | Out-Null; Write-Host "Extension $RemoveExtensionId ($($e.displayName)) geloescht." }
  else { Write-Host "TROCKENLAUF: wuerde Extension $RemoveExtensionId ($($e.displayName)) loeschen." }
  return
}

if ($RemoveListener) {
  $listeners = (Invoke-Graph GET "$beta/authenticationEventListeners").value | Where-Object { $_.'@odata.type' -eq '#microsoft.graph.onTokenIssuanceStartListener' -and $_.conditions.applications.includeApplications.appId -contains $PocAppClientId }
  if (-not $listeners) { Write-Host 'Kein Token-Claims-Listener fuer die PoC-App gefunden. Nichts zu tun.'; return }
  foreach ($l in $listeners) {
    if ($Apply) { Invoke-Graph DELETE "$beta/authenticationEventListeners/$($l.id)" | Out-Null; Write-Host "Listener $($l.id) entfernt." }
    else { Write-Host "TROCKENLAUF: wuerde Listener $($l.id) entfernen." }
  }
  return
}

$listener = @{
  '@odata.type' = '#microsoft.graph.onTokenIssuanceStartListener'
  conditions = @{ applications = @{ includeAllApplications = $false; includeApplications = @(@{ appId = $PocAppClientId }) } }
  priority = 500
  handler = @{ '@odata.type' = '#microsoft.graph.onTokenIssuanceStartCustomExtensionHandler'; customExtension = @{ id = '<ID der Extension aus Schritt 1>' } }
}
$manifest = @{ api = @{ acceptMappedClaims = $true; requestedAccessTokenVersion = 2 } }

if (-not $Apply) {
  Write-Host 'TROCKENLAUF - geplante Aufrufe (nichts wird geaendert):'
  Write-Host "1. POST $beta/customAuthenticationExtensions"; Write-Host ($ext | ConvertTo-Json -Depth 8)
  Write-Host "2. POST $v1/policies/claimsMappingPolicies"; Write-Host ($policy | ConvertTo-Json -Depth 6)
  Write-Host "3. POST $v1/servicePrincipals/{SP der PoC-App}/claimsMappingPolicies/`$ref"
  Write-Host "4. PATCH $v1/applications(appId='$PocAppClientId')  (nur diese Felder)"; Write-Host ($manifest | ConvertTo-Json -Depth 4)
  Write-Host "5. (nur mit -Activate) POST $beta/authenticationEventListeners"; Write-Host ($listener | ConvertTo-Json -Depth 8)
  return
}

if ($ExtensionId) {
  $listener.handler.customExtension.id = $ExtensionId
  $l = Invoke-Graph POST "$beta/authenticationEventListeners" $listener
  Write-Host "Listener angelegt: $($l.id). Ab jetzt ergaenzt Entra die Tokens der PoC-App."
  return
}
# Nicht doppelt anlegen: gibt es schon eine Extension/Policy dieses Namens, bricht das Skript ab (Listener nur mit -ExtensionId anlegen).
$dupExt = (Invoke-Graph GET "$beta/customAuthenticationExtensions").value | Where-Object { $_.displayName -eq $ExtensionName }
if ($dupExt) { throw "Extension '$ExtensionName' existiert bereits ($($dupExt.id -join ', ')). Nichts angelegt. Nur den Listener anlegen: -ExtensionId <id> -Apply -Activate." }
$dupPol = (Invoke-Graph GET "$v1/policies/claimsMappingPolicies").value | Where-Object { $_.displayName -eq $policy.displayName }
if ($dupPol) { throw "Claims-Mapping-Policy '$($policy.displayName)' existiert bereits ($($dupPol.id -join ', ')). Nichts angelegt." }
$created = Invoke-Graph POST "$beta/customAuthenticationExtensions" $ext
if (-not $created.id) { throw 'Extension wurde nicht angelegt. Abbruch (nichts weiter geaendert).' }
Write-Host "Extension angelegt: $($created.id)"
$pol = Invoke-Graph POST "$v1/policies/claimsMappingPolicies" $policy
Write-Host "Claims-Mapping-Policy angelegt: $($pol.id)"
$sp = Invoke-Graph GET "$v1/servicePrincipals(appId='$PocAppClientId')"
Invoke-Graph POST "$v1/servicePrincipals/$($sp.id)/claimsMappingPolicies/`$ref" @{ '@odata.id' = "$v1/policies/claimsMappingPolicies/$($pol.id)" } | Out-Null
Write-Host 'Policy an den Service Principal der PoC-App gebunden.'
Invoke-Graph PATCH "$v1/applications(appId='$PocAppClientId')" $manifest | Out-Null
Write-Host 'App-Manifest: acceptMappedClaims = true, requestedAccessTokenVersion = 2.'
if ($Activate) {
  $listener.handler.customExtension.id = $created.id
  $l = Invoke-Graph POST "$beta/authenticationEventListeners" $listener
  Write-Host "Listener angelegt: $($l.id). Ab jetzt ergaenzt Entra die Tokens der PoC-App."
} else { Write-Host 'Listener NICHT angelegt (-Activate fehlt): Tokens bleiben unveraendert.' }
