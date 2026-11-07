<#
.SYNOPSIS
  Einmaliger Bootstrap: legt den User Flow "LearnySignUpSignIn" im External Tenant an und verknuepft die PoC-App.
.DESCRIPTION
  User Flows (Graph v1.0 authenticationEventsFlows) sind nicht ueber die in ADR 0041 freigegebenen
  Terraform-Provider verwaltbar. Das Skript ist bewusst NICHT an Terraform gekoppelt, aendert nichts
  ohne -Apply, ist idempotent (existiert der Flow, wird nichts angelegt) und loescht nie.
  Voraussetzung: az login --tenant <TenantId> --allow-no-subscriptions als "External ID User Flow Administrator".
.EXAMPLE
  ./create-userflow.ps1 -TenantId <guid> -AppClientId <guid>          # Trockenlauf: zeigt den Request
  ./create-userflow.ps1 -TenantId <guid> -AppClientId <guid> -Apply   # legt an
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$TenantId,
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$AppClientId,
  [string]$FlowName = 'LearnySignUpSignIn',
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'

$current = az account show --query tenantId -o tsv
if ($current -ne $TenantId) { throw "Angemeldet in Tenant $current, erwartet $TenantId (External Tenant). Abbruch." }

$uri = 'https://graph.microsoft.com/v1.0/identity/authenticationEventsFlows'
$existing = az rest --method GET --uri $uri --query "value[?displayName=='$FlowName'].id" -o tsv
if ($existing) { Write-Host "User Flow '$FlowName' existiert bereits ($existing). Nichts zu tun."; return }

$body = @{
  '@odata.type' = '#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow'
  displayName   = $FlowName
  description   = 'Learny PoC: Registrierung und Anmeldung per E-Mail-Einmalcode'
  onAuthenticationMethodLoadStart = @{
    '@odata.type'     = '#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp'
    identityProviders = @(@{ id = 'EmailOtpSignup-OAUTH' })
  }
  onInteractiveAuthFlowStart = @{
    '@odata.type'   = '#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp'
    isSignUpAllowed = $true
  }
  onAttributeCollection = @{
    '@odata.type' = '#microsoft.graph.onAttributeCollectionExternalUsersSelfServiceSignUp'
    attributes = @(
      @{ id = 'email'; displayName = 'E-Mail-Adresse'; description = 'E-Mail-Adresse'; userFlowAttributeType = 'builtIn'; dataType = 'string' },
      @{ id = 'displayName'; displayName = 'Anzeigename'; description = 'Anzeigename'; userFlowAttributeType = 'builtIn'; dataType = 'string' }
    )
    attributeCollectionPage = @{ views = @(@{ inputs = @(
      @{ attribute = 'email'; label = 'E-Mail-Adresse'; inputType = 'text'; hidden = $true; editable = $false; writeToDirectory = $true; required = $true; validationRegEx = '^[^@\s]+@[^@\s]+$' },
      @{ attribute = 'displayName'; label = 'Anzeigename'; inputType = 'text'; hidden = $false; editable = $true; writeToDirectory = $true; required = $false; validationRegEx = '^.{1,64}$' }
    ) }) }
  }
  conditions = @{ applications = @{ includeApplications = @(@{ appId = $AppClientId }) } }
} | ConvertTo-Json -Depth 12

if (-not $Apply) { Write-Host "TROCKENLAUF - Request an POST $uri :"; Write-Host $body; return }

$file = New-TemporaryFile
try {
  Set-Content -Path $file -Value $body -Encoding utf8
  az rest --method POST --uri $uri --headers 'Content-Type=application/json' --body "@$file" --query "{id:id,displayName:displayName}" -o json
} finally { Remove-Item $file -Force }
