<#
.SYNOPSIS
  Registriert die Custom Authentication Extension "OnOtpSend" (eigener E-Mail-Versand fuer Entra-Einmalcodes) im External Tenant, gestuft.
.DESCRIPTION
  Stufen (alle ausser dem Trockenlauf nur nach ausdruecklicher Freigabe):
    (ohne Schalter)         Trockenlauf: liest den Bestand und zeigt die geplanten Graph-Aufrufe.
    -Apply                  legt die Extension an (customAuthenticationExtensions). Ohne Listener hat das KEINE Wirkung auf die Anmeldung.
    -Apply -Activate        legt zusaetzlich den Event-Listener fuer die PoC-App an. Ab hier sendet Entra Einmalcodes ueber die Extension.
    -Apply -Activate -FallbackToMicrosoft   nur falls ausdruecklich gewuenscht: bei Fehler der Extension sendet Microsoft den Code selbst.
                                            Standard ist KEIN Rueckfall (fail-closed): bei Ausfall wird kein Code gesendet.
  Rueckbau: -Apply -RemoveListener entfernt NUR den OTP-Listener der PoC-App (die registrierte Extension, andere Listener und andere Apps bleiben);
  danach sendet wieder Microsoft. Ohne -Apply ist -RemoveListener ein Trockenlauf.
  Anmeldung: Standard ist die Azure CLI (az login --tenant <TenantId>). Deren Token enthaelt die Rechte CustomAuthenticationExtension.ReadWrite.All
  und EventListener.ReadWrite.All nicht zwingend; dann -DeviceLogin verwenden (OAuth-Geraetecode mit dem Client "Microsoft Graph Command Line Tools", genau diese Rechte).
  Voraussetzung: Terraform-Stack 10-app mit gesetztem otp_extension_api_host (API-App und Zustimmung). Graph-Endpunkte sind beta (Preview).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
  [Parameter(Mandatory)][ValidatePattern('^https://')][string]$TargetUrl,
  [Parameter(Mandatory)][string]$ApiIdentifierUri,
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$PocAppClientId,
  [string]$ExtensionName = 'Learny OTP Mail (OnOtpSend)',
  [switch]$DeviceLogin,
  [switch]$Apply,
  [switch]$Activate,
  [switch]$RemoveListener,
  [switch]$FallbackToMicrosoft
)
$ErrorActionPreference = 'Stop'
if ($Activate -and -not $Apply) { throw '-Activate setzt -Apply voraus.' }
if ($FallbackToMicrosoft -and -not $Activate) { throw '-FallbackToMicrosoft setzt -Activate voraus.' }
if ($RemoveListener -and ($Activate -or $FallbackToMicrosoft)) { throw '-RemoveListener kann nicht mit -Activate/-FallbackToMicrosoft kombiniert werden.' }
if ($TargetUrl -match '[?&](code|key|token)=') { throw 'Die TargetUrl darf keine Geheimnisse (Schluessel/Token) in der Query enthalten.' }

$script:GraphToken = $null
if ($DeviceLogin) {
  # OAuth 2.0 Device Code Flow (RFC 8628) mit dem oeffentlichen Client "Microsoft Graph Command Line Tools"; das Token bleibt nur im Speicher.
  $clientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
  $scopes = 'https://graph.microsoft.com/CustomAuthenticationExtension.ReadWrite.All https://graph.microsoft.com/EventListener.ReadWrite.All'
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
  $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($script:GraphToken.Split('.')[1].Replace('-', '+').Replace('_', '/')).PadRight(((($script:GraphToken.Split('.')[1].Length + 3) / 4) -as [int]) * 4, '='))) | ConvertFrom-Json
  if ($claims.tid -ne $TenantId) { throw "Angemeldet in Tenant $($claims.tid), erwartet $TenantId (External Tenant). Abbruch." }
} else {
  $current = az account show --query tenantId -o tsv
  if ($current -ne $TenantId) { throw "Angemeldet in Tenant $current, erwartet $TenantId (External Tenant). Abbruch." }
}
$beta = 'https://graph.microsoft.com/beta/identity'

function Invoke-Graph([string]$Method, [string]$Uri, $Body = $null) {
  if ($DeviceLogin) {
    $h = @{ Authorization = "Bearer $script:GraphToken" }
    if ($null -ne $Body) { return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8))) }
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $h
  }
  if ($null -eq $Body) { return az rest --method $Method --uri $Uri -o json | ConvertFrom-Json }
  $file = New-TemporaryFile
  try {
    Set-Content -Path $file -Value ($Body | ConvertTo-Json -Depth 8) -Encoding utf8
    return az rest --method $Method --uri $Uri --headers 'Content-Type=application/json' --body "@$file" -o json | ConvertFrom-Json
  } finally { Remove-Item $file -Force }
}

$ext = @{
  '@odata.type' = '#microsoft.graph.OnOtpSendCustomExtension'
  displayName = $ExtensionName
  description = 'Learny: Versand der Entra-Einmalcodes ueber Exchange Online (learny@brors.link)'
  authenticationConfiguration = @{ '@odata.type' = '#microsoft.graph.azureAdTokenAuthentication'; resourceId = $ApiIdentifierUri }
  endpointConfiguration = @{ '@odata.type' = '#microsoft.graph.httpRequestEndpoint'; targetUrl = $TargetUrl }
}

$existingExt = @((Invoke-Graph GET "$beta/customAuthenticationExtensions").value | Where-Object { $_.displayName -eq $ExtensionName })
$existingListeners = @((Invoke-Graph GET "$beta/authenticationEventListeners").value | Where-Object { $_.'@odata.type' -eq '#microsoft.graph.onEmailOtpSendListener' })
Write-Host "Vorhandene Extension '$ExtensionName': $($existingExt.Count); vorhandene OTP-Listener: $($existingListeners.Count)"
if ($existingListeners.Count -gt 0) { Write-Host 'Hinweis: Es existiert bereits ein OTP-Listener. Dieses Skript aendert ihn nicht.' }

# Rueckbau: nur der OTP-Listener der PoC-App; die Extension bleibt bestehen.
if ($RemoveListener) {
  $mine = @($existingListeners | Where-Object { @($_.conditions.applications.includeApplications | ForEach-Object { $_.appId }) -contains $PocAppClientId })
  if ($mine.Count -eq 0) { Write-Host "Kein OTP-Listener fuer die App $PocAppClientId vorhanden. Nichts zu tun."; return }
  Write-Host ($(if ($Apply) { 'AUSFUEHRUNG:' } else { 'TROCKENLAUF - wuerde ausfuehren:' }))
  $mine | ForEach-Object { Write-Host "  DELETE $beta/authenticationEventListeners/$($_.id)   (OTP-Listener der App $PocAppClientId; Extension $ExtensionName bleibt)" }
  if (-not $Apply) { return }
  foreach ($l in $mine) { Invoke-Graph DELETE "$beta/authenticationEventListeners/$($l.id)" | Out-Null }
  $after = @((Invoke-Graph GET "$beta/authenticationEventListeners").value | Where-Object { $_.'@odata.type' -eq '#microsoft.graph.onEmailOtpSendListener' -and @($_.conditions.applications.includeApplications | ForEach-Object { $_.appId }) -contains $PocAppClientId })
  Write-Host "Verbleibende OTP-Listener fuer die App: $($after.Count). Microsoft sendet die Einmalcodes wieder selbst."
  $extAfter = @((Invoke-Graph GET "$beta/customAuthenticationExtensions").value | Where-Object { $_.displayName -eq $ExtensionName })
  Write-Host "Extension '$ExtensionName' weiterhin vorhanden: $($extAfter.Count)"
  return
}

$plan = @()
if ($existingExt.Count -eq 0) { $plan += "POST $beta/customAuthenticationExtensions   (OnOtpSendCustomExtension -> $TargetUrl, Audience $ApiIdentifierUri)" }
if ($Activate -and $existingListeners.Count -eq 0) {
  $plan += "POST $beta/authenticationEventListeners   (onEmailOtpSendListener fuer App $PocAppClientId$(if ($FallbackToMicrosoft) { ', MIT Rueckfall auf Microsoft' } else { ', ohne Rueckfall (fail-closed)' }))"
}
if ($plan.Count -eq 0) { Write-Host 'Nichts zu tun.'; return }
Write-Host ($(if ($Apply) { 'AUSFUEHRUNG:' } else { 'TROCKENLAUF - wuerde ausfuehren:' }))
$plan | ForEach-Object { Write-Host "  $_" }
if (-not $Apply) { return }

$extId = if ($existingExt.Count -gt 0) { $existingExt[0].id } else { (Invoke-Graph POST "$beta/customAuthenticationExtensions" $ext).id }
Write-Host "Extension: $extId"
if ($Activate -and $existingListeners.Count -eq 0) {
  $handler = @{ '@odata.type' = '#microsoft.graph.onOtpSendCustomExtensionHandler'; customExtension = @{ id = $extId } }
  if ($FallbackToMicrosoft) { $handler.configuration = @{ behaviorOnError = @{ '@odata.type' = '#microsoft.graph.fallbackToMicrosoftProviderOnError' } } }
  $listener = @{
    '@odata.type' = '#microsoft.graph.onEmailOtpSendListener'
    priority = 500
    conditions = @{ applications = @{ includeAllApplications = $false; includeApplications = @(@{ appId = $PocAppClientId }) } }
    handler = $handler
  }
  $created = Invoke-Graph POST "$beta/authenticationEventListeners" $listener
  Write-Host "Listener angelegt: $($created.id)"
  Write-Host 'Listener aktiv: Entra sendet Einmalcodes ab jetzt ueber die Extension.'
}
