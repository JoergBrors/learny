<#
.SYNOPSIS
  Liest das Branding des External Tenants, vergleicht es mit branding/branding.json und wendet es nur mit -Apply an.
.DESCRIPTION
  Ohne -Apply wird ausschliesslich gelesen (Trockenlauf mit Differenz). Mit -Apply werden nur abweichende Werte geschrieben (idempotent):
    PATCH/POST /organization/{id}/branding[/localizations/{locale}]   (Texte, Farben)
    PUT    .../bannerLogo, .../favicon                                  (Bilder)
  Es wird nie etwas geloescht, der Tenant-Anzeigename wird nicht geaendert. Voraussetzung: az login --tenant <TenantId>, Rolle
  Organizational Branding Administrator (Graph: OrganizationalBranding.ReadWrite.All). Branding erfordert laut Graph-Dokumentation eine
  Entra-ID-P1/P2-Lizenz; im External Tenant ist die Verfuegbarkeit vor dem Apply zu pruefen.
  Hinweis: Das Anlegen des Standard-Brandings per PATCH ist nach der Dokumentation vorgesehen, im Tenant aber noch nicht erprobt.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
  [string]$ConfigPath = (Join-Path $PSScriptRoot '..\branding\branding.json'),
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$cfg = Get-Content -Raw -Encoding utf8 $ConfigPath | ConvertFrom-Json

$current = az account show --query tenantId -o tsv
if ($current -ne $TenantId) { throw "Angemeldet in Tenant $current, erwartet $TenantId (External Tenant). Abbruch." }
$token = az account get-access-token --tenant $TenantId --resource https://graph.microsoft.com --query accessToken -o tsv
$h = @{ Authorization = "Bearer $token" }
$base = "https://graph.microsoft.com/v1.0/organization/$TenantId/branding"

# Bodys immer als UTF-8-Bytes senden (sonst werden Umlaute zerstoert)
function ConvertTo-Utf8Json($obj) { [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json)) }

function Get-Json($uri) { try { Invoke-RestMethod -Method Get -Uri $uri -Headers $h } catch { if ($_.Exception.Response.StatusCode.value__ -eq 404) { $null } else { throw } } }
function Get-ImageHash($uri) {
  try { $r = Invoke-WebRequest -Method Get -Uri $uri -Headers $h; return (Get-FileHash -InputStream ([IO.MemoryStream]::new($r.Content)) -Algorithm SHA256).Hash }
  catch { return $null }
}

$changes = @()
function Compare-Props($label, $uri, $desired, $existing) {
  foreach ($k in 'backgroundColor', 'signInPageText', 'usernameHintText') {
    $want = $desired.$k; $have = if ($existing) { $existing.$k } else { $null }
    if ($want -ne $have) { $script:changes += [pscustomobject]@{ Ziel = $label; Eigenschaft = $k; Ist = $have; Soll = $want; Typ = 'text'; Uri = $uri } }
  }
  foreach ($img in 'bannerLogo', 'favicon') {
    $file = Join-Path $repoRoot $cfg.assets.$img
    $wantHash = (Get-FileHash $file -Algorithm SHA256).Hash
    # Bilder des Standard-Brandings liegen unter localizations/0 (der direkte Pfad /branding/{bild} ist fuer PUT/GET von Streams nicht erlaubt)
    $imgUri = if ($label -eq 'default') { "$base/localizations/0/$img" } else { "$uri/$img" }
    $haveHash = if ($existing) { Get-ImageHash $imgUri } else { $null }
    if ($wantHash -ne $haveHash) { $script:changes += [pscustomobject]@{ Ziel = $label; Eigenschaft = $img; Ist = $haveHash; Soll = $wantHash; Typ = 'image'; Uri = $imgUri; File = $file } }
  }
}

$existingDefault = Get-Json $base
Compare-Props 'default' $base $cfg.default $existingDefault
foreach ($loc in $cfg.localizations.PSObject.Properties) {
  $uri = "$base/localizations/$($loc.Name)"
  Compare-Props $loc.Name $uri $loc.Value (Get-Json $uri)
}

if ($changes.Count -eq 0) { Write-Host 'Branding entspricht bereits der Konfiguration. Nichts zu tun.'; return }
Write-Host ($(if ($Apply) { 'AUSFUEHRUNG - Aenderungen:' } else { 'TROCKENLAUF - Differenz (Ist -> Soll):' }))
$changes | Select-Object Ziel, Eigenschaft, Typ, Ist, Soll | Format-Table -AutoSize -Wrap | Out-String | Write-Host
if (-not $Apply) { return }

foreach ($grp in ($changes | Group-Object Ziel)) {
  $uri = ($grp.Group | Select-Object -First 1).Uri
  $isDefault = $grp.Name -eq 'default'
  $targetUri = if ($isDefault) { $base } else { "$base/localizations/$($grp.Name)" }
  $textChanges = @($grp.Group | Where-Object Typ -eq 'text')
  if ($textChanges.Count -gt 0) {
    $body = @{}; foreach ($c in $textChanges) { $body[$c.Eigenschaft] = $c.Soll }
    $exists = Get-Json $targetUri
    if ($isDefault -or $exists) { Invoke-RestMethod -Method Patch -Uri $targetUri -Headers $h -ContentType 'application/json; charset=utf-8' -Body (ConvertTo-Utf8Json $body) | Out-Null }
    else { $body['id'] = $grp.Name; Invoke-RestMethod -Method Post -Uri "$base/localizations" -Headers $h -ContentType 'application/json; charset=utf-8' -Body (ConvertTo-Utf8Json $body) | Out-Null }
  }
  foreach ($c in ($grp.Group | Where-Object Typ -eq 'image')) {
    Invoke-RestMethod -Method Put -Uri $c.Uri -Headers $h -ContentType 'image/png' -InFile $c.File | Out-Null
  }
}
Write-Host 'Fertig. Branding kann bis zu einigen Minuten bis zur Anzeige brauchen.'
