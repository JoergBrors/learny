<#
.SYNOPSIS
  Hinterlegt die Phase-C-Konfiguration aus der lokalen .env als GitHub Variables und Secrets (gh CLI) für den IIS-Deploy-Workflow.
.DESCRIPTION
  Gelesen wird nur eine feste Liste von Schlüsseln (siehe unten). Ohne -Apply passiert NICHTS ausser einer Anzeige (nur Namen, nie Werte).
  Secrets werden über Standardeingabe an gh übergeben (nie als Kommandozeilenargument, nie in der Ausgabe). Leere Werte werden übersprungen.
  Das Mail-Zertifikat (PFX) wird NICHT übertragen: MAIL_CLIENT_CERT_PATH muss der Pfad auf dem Server sein (-ServerMailCertPath); Datei und Passwort legt der Betreiber dort ab.
  Voraussetzung: gh CLI installiert und angemeldet (gh auth login) mit Recht auf Repository-Variablen/-Secrets.
.EXAMPLE
  ./tools/env/Publish-GitHubConfig.ps1 -ServerMailCertPath 'D:\learny\keys\mail-client.pfx'            # Vorschau
  ./tools/env/Publish-GitHubConfig.ps1 -ServerMailCertPath 'D:\learny\keys\mail-client.pfx' -Apply     # schreibt nach GitHub
#>
[CmdletBinding()]
param(
  [string]$EnvPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.env'),
  [string]$Repo,
  [string]$ServerMailCertPath,
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'

# Namen müssen zu .github/workflows/deploy-iis-on-tag.yml (vars./secrets.) passen
$Variables = 'EXTID_ENABLED','EXTID_AUTHORITY','EXTID_TENANT_ID','EXTID_CLIENT_ID','INVITE_VALIDITY_HOURS','MAIL_ENABLED','MAIL_TENANT_ID','MAIL_CLIENT_ID','MAIL_SENDER',
             'MAIL_CLIENT_CERT_PATH','MAIL_ALLOWED_RECIPIENTS','EXTID_OTP_ENABLED','EXTID_OTP_AUDIENCES'
$Secrets   = 'EXTID_CLIENT_SECRET','INVITE_TOKEN_KEY','MAIL_CLIENT_CERT_PASSWORD'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh CLI nicht gefunden (https://cli.github.com).' }
if (-not (Test-Path -LiteralPath $EnvPath)) { throw "Datei nicht gefunden: $EnvPath" }
$e = @{}
foreach ($l in Get-Content -LiteralPath $EnvPath -Encoding utf8) { if ($l -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $e[$Matches[1]] = $Matches[2].Trim() } }
if ($ServerMailCertPath) { $e['MAIL_CLIENT_CERT_PATH'] = $ServerMailCertPath } else { $e.Remove('MAIL_CLIENT_CERT_PATH') }   # lokaler Pfad gilt nicht auf dem Server

$repoArgs = @(); if ($Repo) { $repoArgs = @('--repo', $Repo) }
if ($Apply) { & gh auth status 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'gh ist nicht angemeldet (gh auth login).' } }

function Invoke-GhWithStdin([string[]]$ghArgs, [string]$value) {
  $psi = [Diagnostics.ProcessStartInfo]::new('gh')
  foreach ($a in $ghArgs) { $psi.ArgumentList.Add($a) }
  $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
  $p = [Diagnostics.Process]::Start($psi)
  $p.StandardInput.Write($value); $p.StandardInput.Close()   # ohne Zeilenumbruch am Ende
  $null = $p.StandardOutput.ReadToEnd(); $err = $p.StandardError.ReadToEnd(); $p.WaitForExit()
  if ($p.ExitCode -ne 0) { throw "gh $($ghArgs[0..1] -join ' ') $($ghArgs[2]) fehlgeschlagen: $($err.Trim())" }
}

$plan = @()
foreach ($n in $Variables) { $plan += [pscustomobject]@{ Art = 'Variable'; Name = $n; Wert = [bool]$e[$n] } }
foreach ($n in $Secrets)   { $plan += [pscustomobject]@{ Art = 'Secret';   Name = $n; Wert = [bool]$e[$n] } }
$plan | ForEach-Object { Write-Host ("{0,-9} {1,-28} {2}" -f $_.Art, $_.Name, $(if ($_.Wert) { 'Wert vorhanden' } else { 'leer -> übersprungen' })) }

if ($e['EXTID_ENABLED'] -in '1','true' -and -not $e['INVITE_TOKEN_KEY']) { Write-Warning 'EXTID_ENABLED ist gesetzt, aber INVITE_TOKEN_KEY fehlt: der Deploy-Workflow bricht dann ab.' }
if ($e['MAIL_ENABLED'] -in '1','true' -and -not $e['MAIL_CLIENT_CERT_PATH']) { Write-Host 'Hinweis: ohne -ServerMailCertPath erzeugt die Plattform das Mail-Zertifikat selbst (Administration -> Mailversand); die .cer danach in Entra hochladen.' }

Write-Host "`nHinweis: Diese Werte gelten für die Plattform https://learny.brors.date (GitHub Variable PUBLIC_BASE_URL dort prüfen); der Debug-Betrieb nutzt http://localhost:5000 aus der lokalen .env und wird hier nicht übertragen."

if (-not $Apply) { Write-Host "`nVorschau. Mit -Apply werden die Werte nach GitHub geschrieben (Secrets verschlüsselt, nie angezeigt)."; return }
foreach ($n in $Variables) { if ($e[$n]) { Invoke-GhWithStdin (@('variable', 'set', $n) + $repoArgs) $e[$n]; Write-Host "Variable $n gesetzt" } }
foreach ($n in $Secrets)   { if ($e[$n]) { Invoke-GhWithStdin (@('secret', 'set', $n) + $repoArgs) $e[$n];   Write-Host "Secret   $n gesetzt" } }
Write-Host 'Fertig. Es wurde kein Deployment ausgelöst.'
