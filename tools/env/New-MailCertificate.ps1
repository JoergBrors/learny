<#
.SYNOPSIS
  Erzeugt das Client-Zertifikat für den Mailversand (Graph/Exchange Online) als PFX ausserhalb des Repositorys und trägt es in die lokale .env ein.
.DESCRIPTION
  ALTERNATIVE (Entwicklung/Betreiberentscheidung): Standard ist das auf der Plattform erzeugte Zertifikat (Administration → Mailversand, der private Schlüssel verlässt die Plattform nie).
  Dieses Skript erzeugt stattdessen eine PFX-Datei auf diesem Rechner (MAIL_CLIENT_CERT_PATH).
  - RSA 3072, selbstsigniert, Standard 24 Monate (wie im PoC). Der private Schlüssel verlässt den Rechner nie; nur die öffentliche .cer-Datei wird in Entra hochgeladen.
  - Das PFX bekommt ein zufälliges Passwort (nur in der .env, nie angezeigt) und Dateirechte nur für den aktuellen Benutzer. Ein vorhandenes PFX wird nie überschrieben (ausser -Force).
  - Die .env muss von Git ignoriert sein. MAIL_ENABLED wird nur mit -Enable gesetzt UND nur, wenn MAIL_ALLOWED_RECIPIENTS gesetzt ist (leer = niemand).
  - WICHTIG: Das Zertifikat wirkt erst, wenn die öffentliche .cer an der Mail-App-Registrierung im Workforce-Tenant hinterlegt ist (Ausgabe am Ende). Das Skript ändert nichts in Entra.
  - Die Ausstellung aus der Learny-PKI und die Rotation folgen in Phase D.
.EXAMPLE
  ./tools/env/New-MailCertificate.ps1 -AllowedRecipients joerg@brors.net            # erzeugen, Pfad/Passwort eintragen (MAIL_ENABLED bleibt 0)
  ./tools/env/New-MailCertificate.ps1 -AllowedRecipients joerg@brors.net -Enable    # zusätzlich MAIL_ENABLED=1 (erst nach dem Upload der .cer sinnvoll)
#>
[CmdletBinding()]
param(
  [string]$PfxPath = (Join-Path $env:USERPROFILE '.learny\mail-client.pfx'),
  [string]$EnvPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) '.env'),
  [string[]]$AllowedRecipients,
  [int]$ValidMonths = 24,
  [switch]$Enable,
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
$MailAppClientId = 'f3365162-0eda-48b3-99dd-b96d3633c40c'   # "Learny Invitation Mailer" (Workforce-Tenant db88f9b6-c89a-41a3-a21d-b61458e234ff)
if ($ValidMonths -lt 1 -or $ValidMonths -gt 27) { throw '-ValidMonths muss zwischen 1 und 27 liegen (Entra-Zertifikate: höchstens 2 Jahre empfohlen).' }

$PfxPath = [IO.Path]::GetFullPath($PfxPath); $EnvPath = [IO.Path]::GetFullPath($EnvPath)
$repo = (git -C (Split-Path $EnvPath) rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -eq 0 -and $repo) {
  if ($PfxPath.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "Das PFX darf nicht im Repository liegen: $PfxPath" }
  git -C $repo check-ignore -q -- $EnvPath 2>$null
  if ($LASTEXITCODE -ne 0) { throw "Die Datei '$EnvPath' ist NICHT von Git ignoriert. Abbruch." }
}
if ((Test-Path -LiteralPath $PfxPath) -and -not $Force) { throw "Das PFX existiert bereits: $PfxPath. Nicht überschrieben (-Force, wenn gewollt; die alte Datei geht dann verloren)." }
$recipients = @($AllowedRecipients | Where-Object { $_ } | ForEach-Object { $_.Trim().ToLowerInvariant() })
foreach ($r in $recipients) { if ($r -ne '*' -and $r -notmatch '^(@[a-z0-9.-]+\.[a-z]{2,}|[^@\s]+@[^@\s]+\.[a-z]{2,})$') { throw "Ungültiger Empfängereintrag: $r" } }

# Zertifikat erzeugen
$rsa = [Security.Cryptography.RSA]::Create(3072)
$req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Learny-Mail-Client', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$req.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
$now = [DateTimeOffset]::UtcNow
$cert = $req.CreateSelfSigned($now.AddMinutes(-5), $now.AddMonths($ValidMonths))
$pwBytes = [byte[]]::new(24); [Security.Cryptography.RandomNumberGenerator]::Fill($pwBytes); $pw = [Convert]::ToBase64String($pwBytes)
$dir = Split-Path $PfxPath; New-Item -ItemType Directory -Force -Path $dir | Out-Null
[IO.File]::WriteAllBytes($PfxPath, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $pw))
$cerPath = [IO.Path]::ChangeExtension($PfxPath, '.cer')
[IO.File]::WriteAllBytes($cerPath, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
try { icacls $PfxPath /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null } catch { Write-Warning 'Dateirechte konnten nicht eingeschränkt werden.' }

# .env aktualisieren (Kommentare bleiben, Sicherung .env.bak)
$set = [ordered]@{ MAIL_CLIENT_CERT_PATH = $PfxPath; MAIL_CLIENT_CERT_PASSWORD = $pw; MAIL_CLIENT_SECRET = '' }
if ($recipients.Count -gt 0) { $set['MAIL_ALLOWED_RECIPIENTS'] = ($recipients -join ',') }
$lines = New-Object 'System.Collections.Generic.List[string]'
if (Test-Path -LiteralPath $EnvPath) { foreach ($x in (Get-Content -LiteralPath $EnvPath -Encoding utf8)) { $lines.Add([string]$x) }; Copy-Item -LiteralPath $EnvPath -Destination ($EnvPath + '.bak') -Force }
$have = @{}; foreach ($l in $lines) { if ($l -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $have[$Matches[1]] = $Matches[2].Trim() } }
$allowed = if ($set.Contains('MAIL_ALLOWED_RECIPIENTS')) { $set['MAIL_ALLOWED_RECIPIENTS'] } else { $have['MAIL_ALLOWED_RECIPIENTS'] }
if ($Enable) {
  if ($allowed) { $set['MAIL_ENABLED'] = '1' } else { Write-Warning 'MAIL_ENABLED bleibt 0: MAIL_ALLOWED_RECIPIENTS ist leer (= niemand). Mit -AllowedRecipients Testadressen angeben.' }
}
$written = @{}
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=') { $k = $Matches[1]; if ($set.Contains($k)) { $lines[$i] = "$k=$($set[$k])"; $written[$k] = $true } } }
$new = @($set.Keys | Where-Object { -not $written.ContainsKey($_) })
if ($new.Count -gt 0) { $lines.Add(''); $lines.Add('# ─── Mail-Zertifikat (tools/env/New-MailCertificate.ps1; Geheimnis, nie committen) ───'); foreach ($k in $new) { $lines.Add("$k=$($set[$k])") } }
Set-Content -LiteralPath $EnvPath -Value $lines -Encoding utf8

Write-Host @"

Zertifikat erzeugt (Passwort nur in der .env, nie angezeigt):
  PFX (privat, bleibt hier) : $PfxPath
  CER (öffentlich, Upload)  : $cerPath
  Thumbprint                : $($cert.Thumbprint)
  gültig bis                : $($cert.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd')) UTC
  .env                      : $($set.Keys -join ', ') gesetzt$(if ($set['MAIL_ENABLED']) { '' } else { ' (MAIL_ENABLED unverändert)' })

NÄCHSTER SCHRITT (nur du, im Workforce-Tenant db88f9b6-c89a-41a3-a21d-b61458e234ff):
  Entra Admin Center → App-Registrierungen → "Learny Invitation Mailer" ($MailAppClientId)
  → Zertifikate & Geheimnisse → Zertifikate → Zertifikat hochladen → $cerPath
  Das bestehende PoC-Zertifikat bleibt eingetragen (nichts löschen). Der Exchange-RBAC-Scope ändert sich nicht.
  Alternativ per CLI (Workforce-Tenant angemeldet):
    az ad app credential reset --id $MailAppClientId --cert "@$cerPath" --append
  Wirksam nach wenigen Minuten. Danach Learny neu starten; MAIL_ENABLED=1 und MAIL_ALLOWED_RECIPIENTS in der .env prüfen.
"@
