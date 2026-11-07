<#
.SYNOPSIS
  Begrenzt die Mail-App per Exchange Online RBAC for Applications auf genau ein Absenderpostfach (Application Mail.Send).
.DESCRIPTION
  Ohne -Apply wird nur gelesen und angezeigt, was geschehen wuerde (Trockenlauf). Mit -Apply werden nur fehlende Objekte angelegt
  (idempotent): Service-Principal-Zeiger, Management Scope, Rollenzuweisung. Es wird nie etwas geloescht oder veraendert, und es
  wird nie eine tenantweite Graph-Berechtigung vergeben. Bricht ab, wenn der Service Principal bereits andere Exchange-Rollen hat.
  Voraussetzung: Modul ExchangeOnlineManagement, Anmeldung als Mitglied von Organization Management (Entra: Exchange Administrator).
.EXAMPLE
  ./provision-exchange-rbac.ps1 -AppId <client-id> -ServicePrincipalObjectId <sp-object-id>           # Trockenlauf
  ./provision-exchange-rbac.ps1 -AppId <client-id> -ServicePrincipalObjectId <sp-object-id> -Apply    # anlegen
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$AppId,
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$ServicePrincipalObjectId,
  [string]$Mailbox = 'learny@brors.link',
  [string]$ScopeName = 'Learny-Invite-Sender',
  [string]$DisplayName = 'Learny Invitation Mailer',
  [string[]]$AllowedRecipientTypes = @('SharedMailbox', 'UserMailbox'),
  [switch]$Apply,
  [switch]$DeviceLogin   # Anmeldung per Geraetecode (ohne Browserfenster-Handle)
)
$ErrorActionPreference = 'Stop'
Import-Module ExchangeOnlineManagement
if ($DeviceLogin) { Connect-ExchangeOnline -ShowBanner:$false -Device } else { Connect-ExchangeOnline -ShowBanner:$false }

# 1. Postfach lesen und pruefen
$mbx = Get-Mailbox -Identity $Mailbox -ErrorAction Stop
if ($mbx.PrimarySmtpAddress.ToString().ToLowerInvariant() -ne $Mailbox.ToLowerInvariant()) { throw "Primaere SMTP-Adresse ist $($mbx.PrimarySmtpAddress), nicht $Mailbox. Abbruch." }
if ($mbx.RecipientTypeDetails -notin $AllowedRecipientTypes) { throw "Postfachtyp $($mbx.RecipientTypeDetails) nicht erlaubt. Abbruch." }
Write-Host "Postfach: $($mbx.PrimarySmtpAddress) ($($mbx.RecipientTypeDetails))"

# 2. Vorhandenen Zustand lesen
$sp = Get-ServicePrincipal -Identity $ServicePrincipalObjectId -ErrorAction SilentlyContinue
$scope = Get-ManagementScope -Identity $ScopeName -ErrorAction SilentlyContinue
$assignments = @(Get-ManagementRoleAssignment -RoleAssignee $ServicePrincipalObjectId -ErrorAction SilentlyContinue)
$other = @($assignments | Where-Object { $_.Role -ne 'Application Mail.Send' })
if ($other.Count -gt 0) { throw "Der Service Principal hat bereits weitere Exchange-Rollen: $($other.Role -join ', '). Abbruch (Least Privilege)." }
$filter = "PrimarySmtpAddress -eq '$Mailbox'"
if ($scope -and $scope.RecipientFilter -ne $filter) { throw "Scope $ScopeName existiert mit anderem Filter ($($scope.RecipientFilter)). Abbruch." }
$mailSend = $assignments | Where-Object { $_.Role -eq 'Application Mail.Send' }
if ($mailSend -and $mailSend.CustomResourceScope -ne $ScopeName) { throw "Application Mail.Send ist bereits mit anderem Scope zugewiesen ($($mailSend.CustomResourceScope)). Abbruch." }

$plan = @()
if (-not $sp)       { $plan += "New-ServicePrincipal -AppId $AppId -ObjectId $ServicePrincipalObjectId -DisplayName '$DisplayName'" }
if (-not $scope)    { $plan += "New-ManagementScope -Name $ScopeName -RecipientRestrictionFilter `"$filter`"" }
if (-not $mailSend) { $plan += "New-ManagementRoleAssignment -App $ServicePrincipalObjectId -Role 'Application Mail.Send' -CustomResourceScope $ScopeName" }

if ($plan.Count -eq 0) { Write-Host 'Alles bereits eingerichtet. Nichts zu tun.'; return }
Write-Host ($(if ($Apply) { 'AUSFUEHRUNG:' } else { 'TROCKENLAUF - wuerde ausfuehren:' }))
$plan | ForEach-Object { Write-Host "  $_" }
if (-not $Apply) { return }

if (-not $sp)       { New-ServicePrincipal -AppId $AppId -ObjectId $ServicePrincipalObjectId -DisplayName $DisplayName | Out-Null }
if (-not $scope)    { New-ManagementScope -Name $ScopeName -RecipientRestrictionFilter $filter | Out-Null }
if (-not $mailSend) { New-ManagementRoleAssignment -App $ServicePrincipalObjectId -Role 'Application Mail.Send' -CustomResourceScope $ScopeName | Out-Null }
Write-Host 'Fertig. Wirksamkeit nach 30 Minuten bis 2 Stunden (Cache); danach verify-exchange-rbac.ps1 ausfuehren.'
