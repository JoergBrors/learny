# Registrierungs-PoC: Portal (Administration und Lernende)

Entscheidung: [ADR 0046](../adr/0046-administrationsportal-externe-registrierung.md). Projekt: `poc/InviteRegistration.Portal` (nicht Teil von `LearnCards.slnx`, keine NuGet-Pakete, kein Eingriff in den Learny-Code).

## Aufbau

```
Browser ──► Portal (ASP.NET Core, Razor statisch)  ──► SQLite (portal.db, eigene Datei)
              ├─ /admin …  Administration (Cookie "admin", Rolle je Aufruf gegen die DB)
              ├─ /dashboard, /modules/{id}, /profile  Lernende (Cookie "user")
              ├─ /invite/{token}, /invite-start, /login, POST /auth/callback  (gemeinsamer Ablauf InviteFlow)
              ├─ /admin/login, POST /admin/callback  Workforce-OIDC für Administratoren
              └─ ExchangeOnlineMailService (Einladungsmail) und InvitationService/ContentAccess (wiederverwendet)
Getrennter Port 5080: nur POST /extensions/otp-send  (OTP-Extension, optional)
```

## Seiten und Endpunkte

| Bereich | Route | Inhalt |
|---|---|---|
| öffentlich | `/`, `/admin/signin`, `/access-denied`, `/admin/denied`, `/invite-failed` | Start, Anmeldung, Fehlerseiten (einheitlich) |
| Administration | `/admin`, `/admin/invitations` | Kennzahlen (gesamt, offen, versendet, eingelöst, abgelaufen, widerrufen, Versand fehlgeschlagen, aktive/deaktivierte Benutzer), filterbare Tabelle (Suche, Einladungs-, Versandstatus) |
| | `/admin/invitations/new` | Einladung erstellen (E-Mail, Profil, Gültigkeit 1–720 h, Module, optional Name/Beschreibung/Notiz) |
| | `/admin/invitations/{id}` | Detail, Versand, Neuausstellung, Widerruf, Timeline |
| | `/admin/users`, `/admin/users/{id}` | Benutzer, Rechte zuweisen/entziehen, deaktivieren/reaktivieren |
| | `/admin/assignments`, `/admin/events` | Zuweisungsmatrix, Ereignisprotokoll |
| Aktionen (nur POST, CSRF, Administrator) | `/admin/actions/invitations`, `…/{id}/send`, `…/rotate`, `…/revoke`, `…/users/{id}/assign`, `…/revoke`, `…/deactivate`, `…/reactivate` | Versand, Neuausstellung, Widerruf und Deaktivierung brauchen ein Bestätigungsfeld |
| Lernende | `/dashboard`, `/profile`, `/modules/{id}` | nur zugewiesene Module; direkter Zugriff auf andere Module → 403 |

Es gibt **keine** Endpunkte für Profilwechsel, Rollenvergabe oder Benutzerlöschung.

## Registrierungs- und Mailablauf

1. Administrator erstellt die Einladung (Profil, Gültigkeit, Module). Status: Einladung `pending`, Versand `pending`; der Klartextlink liegt nur im Speicher.
2. Administrator bestätigt den Versand → `ExchangeOnlineMailService` sendet „Einladung zu Learny“ über `learny@brors.link` (Versand `submitted` = von Graph angenommen, **keine** Zustellgarantie; Fehler → `failed`/`unknown`, Einladung bleibt gültig).
3. Empfänger öffnet den Link → Prüfung, Cookie, Weiterleitung zu Entra mit `prompt=login` (Ereignis „Link aufgerufen“ ist nur ein beobachteter Serveraufruf).
4. Entra: Registrierung im gebrandeten User Flow, OTP-Mail (von Microsoft oder, falls aktiviert, über die Extension von `learny@brors.link`), Entra validiert den Code.
5. Callback (form_post, PKCE): Issuer, Audience, `tid`, state, nonce → E-Mail-Bindung (`idp = mail`) → Identität darf nicht existieren → atomarer Verbrauch → Benutzer, Profil, Rechte → Dashboard.
6. Jeder Fehlerfall endet auf derselben Seite („Diese Einladung ist ungültig, abgelaufen oder wurde bereits verwendet.“).

## Statusmodell und Ereignisse

Einladung `pending | accepted | expired | revoked`, Versand `pending | submitted | failed | unknown` (getrennt). Ereignistypen: `invitation.created`, `send_requested`, `submitted`, `send_failed`, `opened`, `registration.started`, `authentication.completed`, `registration.rejected`, `invitation.accepted`, `user.created`, `profile.assigned`, `content.assigned`, `content.revoked`, `invitation.revoked`, `invitation.rotated`, `user.deactivated`, `user.reactivated`, `user.signed_in`, `admin.signed_in`, `admin.login_rejected`. Jedes Ereignis hat Zeitpunkt, Einladungs-ID, optional Benutzer-ID, Kategorie und optional den Administrator als Akteur.

## Sicherer lokaler Start

Alle Einstellungen über Umgebungsvariablen. Außerhalb von Development gilt Fail-Closed: HTTPS, Token-Schlüssel und Workforce-OIDC sind Pflicht, sonst Startabbruch.

| Variable | Bedeutung |
|---|---|
| `PORTAL_MODE=development` | lokaler Modus (HTTP erlaubt, Dev-Login möglich, Token-Schlüssel wird lokal erzeugt) |
| `PORTAL_URL`, `PORTAL_PUBLIC_BASE_URL` | Adresse (Standard dev `https://localhost:5001`); für Einladungslinks |
| `PORTAL_DATA_DIR` | Daten: `portal.db`, `keys/` (Data Protection), `invite-token.key` (nur dev) |
| `PORTAL_INVITE_TOKEN_KEY` | Base64, ≥ 32 Byte, Pflicht außerhalb von dev, über Neustarts gleich |
| `PORTAL_ENTRA_TENANT_ID`, `PORTAL_ENTRA_AUTHORITY`, `PORTAL_ENTRA_CLIENT_ID`, `PORTAL_ENTRA_CLIENT_SECRET`, `PORTAL_ENTRA_REDIRECT` | External-ID-Anmeldung der Lernenden (`https://<host>/auth/callback`) |
| `PORTAL_ADMIN_AUTHORITY`, `PORTAL_ADMIN_ISSUER` (Standard = Authority), `PORTAL_ADMIN_TENANT_ID`, `PORTAL_ADMIN_CLIENT_ID`, `PORTAL_ADMIN_CLIENT_SECRET`, `PORTAL_ADMIN_REDIRECT` | Workforce-OIDC der Administratoren. Ohne Secret ist die Microsoft-Anmeldung in Development aus (nur Dev-Zugang), außerhalb Pflicht |
| `PORTAL_ADMIN_ROLES` (z. B. `Admin.All`), `PORTAL_ADMIN_SUBJECTS` | Wer Administrator ist: App-Rolle im ID-Token des vertrauten Issuers und/oder Subject-Liste (Bootstrap). Mindestens eines von beiden ist Pflicht |
| `PORTAL_DEV_ADMIN_KEY` | nur dev und Loopback: lokaler Administratorzugang |
| `PORTAL_MAIL_ENABLED=1`, `MAIL_TENANT_ID`, `MAIL_CLIENT_ID`, `MAIL_CERT_THUMBPRINT`, `MAIL_SENDER`, `PORTAL_MAIL_ALLOWED_RECIPIENTS` | Exchange-Versand (sonst „Versand aus“, fail-closed) |
| `PORTAL_OTP_ENABLED=1`, `OTP_EXT_AUDIENCES`, `PORTAL_OTP_URL` | OTP-Extension auf eigenem Port (setzt Mail voraus) |
| `PORTAL_SEED_DEMO=1` | Testmodule A (global), B (global), C (freigegeben) |
| `PORTAL_FORCE_REAUTH=0` | schaltet `prompt=login` ab (Standard an) |

Beispiel (Windows PowerShell, lokal ohne Live-Anbindung):

```
$env:PORTAL_MODE='development'; $env:PORTAL_URL='http://127.0.0.1:5090'; $env:PORTAL_SEED_DEMO='1'; $env:PORTAL_DEV_ADMIN_KEY='<lokaler-schluessel>'
$env:PORTAL_ENTRA_TENANT_ID='5fce2b3c-…'; $env:PORTAL_ENTRA_AUTHORITY='https://learnyextidpoc.ciamlogin.com/5fce2b3c-…/v2.0'
$env:PORTAL_ENTRA_CLIENT_ID='cb54157f-…'; $env:PORTAL_ENTRA_CLIENT_SECRET='<aus der Secret-Verwaltung>'
dotnet run --project poc/InviteRegistration.Portal
```

**Administratoranmeldung mit derselben Entra-App wie Learny:** Das Portal nutzt die Workforce-App `Learny-oid` (`89e199e0-…`) als Anmelde-App. Administrator ist, wer im ID-Token die App-Rolle **`Admin.All`** hat (wie in Learny, ADR 0012); hat jemand die Rolle bei einer späteren Anmeldung nicht mehr, verliert er die Portal-Administration sofort (Rolle in der Datenbank entfernt, bestehende Sitzungen werden bei jedem Aufruf geprüft). Adminrechte aus dem External Tenant gibt es nie (anderer Issuer). **Änderung in Entra (2026-11-08, auf deine Freigabe):** An `Learny-oid` wurde die Redirect-URI `http://localhost:5090/admin/callback` ergänzt (nur die Liste der Redirect-URIs; die bestehenden `https://learny.brors.date/auth/callback` und `http://localhost:5000/auth/callback`, Rollen, Secrets und übrige Einstellungen blieben unverändert; Sicherung des Vorher-Stands im Arbeitsordner der Sitzung). Das Client-Secret von Learny kommt aus deiner Learny-`.env` (`OIDC_CLIENT_SECRET`) und wird mit `pwsh poc/New-PortalEnv.ps1 -AdminFromLearnyEnv` nur lokal in die `portal.env` kopiert, nie angezeigt. Für eine Produktivnutzung des Portals gehört eine eigene Redirect-URI und ein eigenes Secret dazu; eine eigene App-Registrierung für das Portal wäre die sauberere Trennung.

**Debuggen in VS Code:** In `.vscode/launch.json` gibt es die Konfiguration „Registrierungs-PoC Portal (lokal)“ (mit dem Build-Task „build: poc portal“ in `tasks.json`). F5 startet das Portal auf `http://127.0.0.1:5090` und öffnet `/admin/signin`. Die Daten liegen in `poc/.secrets/portal-data` (git-ignoriert, bleiben zwischen Läufen erhalten); Geheimnisse kommen aus `poc/.secrets/portal.env`. Im Development-Modus startet das Portal auch ohne `PORTAL_ENTRA_CLIENT_SECRET`: Administration, Einladungen und Benutzerverwaltung funktionieren, die echte Entra-Anmeldung scheitert dann fail-closed beim Code-Austausch.

**Lokale Geheimnisdatei per Skript:** `pwsh poc/New-PortalEnv.ps1` füllt `poc/.secrets/portal.env` (git-ignoriert) idempotent: lokaler Entwicklungsschlüssel (zufällig), mit `-CreateEntraSecret` ein Client-Secret „portal-local“ für die PoC-App (30 Tage, Azure CLI im External Tenant), mit `-WithMail -AllowedRecipients <adresse>` die Exchange-Kennungen, mit `-RemoveEntraSecret` Aufräumen. Vorhandene Werte werden nie überschrieben, Geheimnisse nie ausgegeben, und das Skript bricht ab, wenn die Zieldatei nicht von Git ignoriert ist.

Geheimnisse gehören nie in Quelltext, Doku oder Git; `PORTAL_DATA_DIR` liegt außerhalb des Repositorys oder unter `bin/` (git-ignoriert).

## Live-Testanleitung (Ende-zu-Ende)

Voraussetzungen (offen): Workforce-App-Registrierung für die Administratoranmeldung (Redirect `https://<host>/admin/callback`) oder lokal der Dev-Zugang; External-ID-App-Redirect auf die Portal-Adresse (`/auth/callback`); Mail-Konfiguration; optional OTP-Listener (ADR 0045).

1. Als Administrator anmelden. 2. Neue Einladung (B2C, Modul A). 3. Versand bestätigen (Absender `learny@brors.link`). 4. Versandstatus prüfen (angenommen). 5. Link in **neuem privaten Fenster** öffnen. 6. Registrieren (E-Mail-OTP). 7. OTP-Mail von `learny@brors.link` (nur mit aktivem Listener, sonst Microsoft). 8. Dashboard zeigt nur Modul A. 9. Admin sieht Fortschritt und Ereignisse. 10. Derselbe Link ist ungültig. 11. Modul C zuweisen, Dashboard aktualisieren. 12. Modul A entziehen, direkter Aufruf ist gesperrt. 13. Host neu starten: Benutzer, Status und Ereignisse bleiben erhalten; ein vor dem Neustart versendeter Link bleibt gültig, eine Neuausstellung ist für einen erneuten Versand nötig.

## Mail-Einrichtung (`/admin/mail`)

Administratoren hinterlegen hier die Mail-App, ohne `portal.env` zu bearbeiten: Verzeichnis-ID, Anwendungs-ID, Absenderpostfach und erlaubte Empfänger (vorausgewählt aus `MAIL_TENANT_ID`/`MAIL_CLIENT_ID`/`MAIL_SENDER`). „Zertifikat erzeugen“ legt den privaten Schlüssel verschlüsselt unter `<PORTAL_DATA_DIR>/mail/` ab; das öffentliche `.cer` wird über „Öffentliches Zertifikat herunterladen“ geholt und in der App-Registrierung unter *Zertifikate & Geheimnisse* hochgeladen (oder `az ad app credential reset --id <Client-ID> --cert "@learny-poc-mail.cer" --append`). Der in Entra angezeigte Fingerabdruck muss mit dem im Portal übereinstimmen. Danach „Versand aktivieren“ ankreuzen; die Änderung gilt sofort. Fehler „mail_disabled“ im Versandstatus bedeutet: nichts aktiviert. Der Schlüssel hängt an `PORTAL_INVITE_TOKEN_KEY`; ändert sich dieser, muss das Zertifikat neu erzeugt werden.

## Token-Anreicherung (ADR 0047)

Mit `PORTAL_CLAIMS_ENABLED=1` bedient der Extension-Host zusätzlich `POST /extensions/token-claims` (Custom Claims Provider, `OnTokenIssuanceStart`): Entra ergänzt das Token um `learny_uid`, `learny_profile`, `learny_status`, `learny_modules` (nur bis 20), `learny_modules_n` und `learny_grants_v`. Die Claims sind nur ein Hinweis; der Zugriff wird weiter gegen die Datenbank geprüft. Beim ersten Login sind sie noch leer (das Portal kennt den Benutzer erst nach der Registrierung). Im Tenant registrieren: `infra/entra-external-id/bootstrap/claims-extension.ps1` (Trockenlauf, `-Apply` nach Freigabe, `-Activate` aktiviert den Listener). Auf `/profile` zeigt „Mein Anmelde-Token“ (nur Development) die ergänzten Claims.

Die Modulübersicht (`/dashboard`) nutzt die Claims der Anmeldung, solange `learny_grants_v` zur aktuellen Marke passt (Quelle steht auf der Seite); Administratoraktionen machen die Marke sofort ungültig, sodass danach die Datenbank gilt. Das Öffnen eines Moduls prüft immer gegen die Datenbank.
