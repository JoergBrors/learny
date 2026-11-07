<#
.SYNOPSIS
  Aendert die bei der Registrierung abgefragten Attribute des User Flows "LearnySignUpSignIn" (External Tenant).
.DESCRIPTION
  Standard (-Collect Email): Entra fragt nach dem Einmalcode nichts mehr ab ("Details hinzufuegen" entfaellt);
  der Anzeigename kommt dann aus der Einladung (Portal). -Collect EmailAndDisplayName stellt den frueheren Zustand wieder her.
  Aendert nichts ohne -Apply; loescht den Flow nie. Voraussetzung: az login --tenant <TenantId> --allow-no-subscriptions
  als "External ID User Flow Administrator".
.EXAMPLE
  ./userflow-attributes.ps1 -TenantId <guid>                 # Trockenlauf
  ./userflow-attributes.ps1 -TenantId <guid> -Apply          # Anzeigename nicht mehr abfragen
  ./userflow-attributes.ps1 -TenantId <guid> -Collect EmailAndDisplayName -Apply   # zurueck
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
  [string]$FlowName = 'LearnySignUpSignIn',
  [ValidateSet('Email', 'EmailAndDisplayName')][string]$Collect = 'Email',
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'

$current = az account show --query tenantId -o tsv
if ($current -ne $TenantId) { throw "Angemeldet in Tenant $current, erwartet $TenantId (External Tenant). Abbruch." }

$base = 'https://graph.microsoft.com/v1.0/identity/authenticationEventsFlows'
$id = az rest --method GET --uri $base --query "value[?displayName=='$FlowName'].id" -o tsv
if (-not $id) { throw "User Flow '$FlowName' nicht gefunden." }

$attributes = @(@{ id = 'email'; displayName = 'E-Mail-Adresse'; description = 'E-Mail-Adresse'; userFlowAttributeType = 'builtIn'; dataType = 'string' })
$inputs = @(@{ attribute = 'email'; label = 'E-Mail-Adresse'; inputType = 'text'; hidden = $true; editable = $false; writeToDirectory = $true; required = $true; validationRegEx = '^[^@\s]+@[^@\s]+$' })
if ($Collect -eq 'EmailAndDisplayName') {
  $attributes += @{ id = 'displayName'; displayName = 'Anzeigename'; description = 'Anzeigename'; userFlowAttributeType = 'builtIn'; dataType = 'string' }
  $inputs += @{ attribute = 'displayName'; label = 'Anzeigename'; inputType = 'text'; hidden = $false; editable = $true; writeToDirectory = $true; required = $false; validationRegEx = '^.{1,64}$' }
}
$body = @{
  '@odata.type' = '#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow'
  onAttributeCollection = @{
    '@odata.type' = '#microsoft.graph.onAttributeCollectionExternalUsersSelfServiceSignUp'
    attributes = $attributes
    attributeCollectionPage = @{ views = @(@{ inputs = $inputs }) }
  }
} | ConvertTo-Json -Depth 12

$uri = "$base/$id"
if (-not $Apply) { Write-Host "TROCKENLAUF - Request an PATCH $uri :"; Write-Host $body; return }

$file = New-TemporaryFile
try {
  Set-Content -Path $file -Value $body -Encoding utf8
  az rest --method PATCH --uri $uri --headers 'Content-Type=application/json' --body "@$file"
  Write-Host 'Stand danach:'
  az rest --method GET --uri $uri --query "onAttributeCollection.attributeCollectionPage.views[0].inputs[].attribute" -o tsv
} finally { Remove-Item $file -Force }
