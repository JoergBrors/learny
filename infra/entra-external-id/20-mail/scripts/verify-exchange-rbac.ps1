<#
.SYNOPSIS
  Rein lesende Pruefung der Mail-Berechtigung (statische RBAC-Pruefung). Aendert nichts.
.DESCRIPTION
  1. Entra: Die Mail-App hat KEINE Graph-Anwendungsberechtigung (insbesondere kein tenantweites Mail.Send) und keine delegierten Zustimmungen.
  2. Exchange: Genau die Rolle "Application Mail.Send", Scope = Absenderpostfach.
  3. Exchange: Test-ServicePrincipalAuthorization: Absenderpostfach InScope=True, ein anderes Postfach InScope=False.
  Exit-Code 0 nur, wenn alles erfuellt ist. Voraussetzung: az login im Workforce-Tenant, ExchangeOnlineManagement.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$ServicePrincipalObjectId,
  [string]$Mailbox = 'learny@brors.link',
  [Parameter(Mandatory)][string]$OtherMailbox,
  [string]$ScopeName = 'Learny-Invite-Sender',
  [switch]$DeviceLogin
)
$ErrorActionPreference = 'Stop'
$failures = @()
function Check($name, $ok, $detail = '') { if ($ok) { Write-Host "  ok   $name" } else { Write-Host "  FAIL $name $detail"; $script:failures += $name } }

Write-Host 'Entra (Microsoft Graph, nur Lesen):'
$grants = az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ServicePrincipalObjectId/appRoleAssignments" --query 'value[].appRoleId' -o tsv
Check 'keine Graph-Anwendungsberechtigungen (kein tenantweites Mail.Send)' ([string]::IsNullOrWhiteSpace($grants)) $grants
$oauth = az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ServicePrincipalObjectId/oauth2PermissionGrants" --query 'value[].scope' -o tsv
Check 'keine delegierten Zustimmungen' ([string]::IsNullOrWhiteSpace($oauth)) $oauth
$app = az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ServicePrincipalObjectId" --query 'appId' -o tsv
$reg = az rest --method GET --uri "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq '$app'&`$select=web,spa,publicClient,requiredResourceAccess" -o json | ConvertFrom-Json
$a = $reg.value[0]
Check 'keine Redirect-URIs / keine Plattformen' (($a.web.redirectUris.Count -eq 0) -and ($a.spa.redirectUris.Count -eq 0) -and ($a.publicClient.redirectUris.Count -eq 0))
Check 'keine requiredResourceAccess (keine API-Berechtigungen deklariert)' ($a.requiredResourceAccess.Count -eq 0)

Write-Host 'Exchange Online:'
Import-Module ExchangeOnlineManagement
if ($DeviceLogin) { Connect-ExchangeOnline -ShowBanner:$false -Device } else { Connect-ExchangeOnline -ShowBanner:$false }
$assign = @(Get-ManagementRoleAssignment -RoleAssignee $ServicePrincipalObjectId)
Check 'genau eine Rollenzuweisung: Application Mail.Send' (($assign.Count -eq 1) -and ($assign[0].Role -eq 'Application Mail.Send')) (($assign.Role -join ','))
Check "Scope = $ScopeName" ($assign.Count -eq 1 -and $assign[0].CustomResourceScope -eq $ScopeName)
$scope = Get-ManagementScope -Identity $ScopeName
Check 'Scope-Filter = nur Absenderpostfach' ($scope.RecipientFilter -eq "PrimarySmtpAddress -eq '$Mailbox'") $scope.RecipientFilter
$ok = Test-ServicePrincipalAuthorization -Identity $ServicePrincipalObjectId -Resource $Mailbox | Where-Object { $_.RoleName -eq 'Application Mail.Send' }
Check "Absenderpostfach $Mailbox InScope = True" ($ok.InScope -eq $true)
$no = Test-ServicePrincipalAuthorization -Identity $ServicePrincipalObjectId -Resource $OtherMailbox | Where-Object { $_.RoleName -eq 'Application Mail.Send' }
Check "anderes Postfach $OtherMailbox InScope = False" ($no.InScope -eq $false)

if ($failures.Count -gt 0) { Write-Host "`n$($failures.Count) Pruefung(en) fehlgeschlagen."; exit 1 }
Write-Host "`nAlle Pruefungen bestanden."
