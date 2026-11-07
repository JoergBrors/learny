# 0044 — Exchange-Online-Einladungsversand und Versandstatus

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – im PoC mit simuliertem Graph getestet und live gegen Exchange Online bestätigt (Tests A bis E)
- **Datum:** 2026-11-07
- **Bezug:** [0005](0005-authentifizierung.md), [0009](0009-deployment-wege.md), [0026](0026-betriebsmodus-fail-closed.md), [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md), [0042](0042-einladungspflichtige-external-id-registrierung.md), [0043](0043-transaktionsmodell-einladungseinloesung.md)

## Kontext

Einladungslinks sollen per E-Mail über Exchange Online verschickt werden. Der Link ist nur im Moment der Erstellung (oder Rotation) im Klartext bekannt (ADR 0042: nur HMAC gespeichert). Ein erfolgreicher Graph-Aufruf bedeutet „zur Zustellung angenommen“, nicht „zugestellt“.

## Entscheidung

1. **Getrennte Verantwortlichkeiten:**
   - `InvitationService`: Erstellung, Prüfung, Einlösung, Widerruf, **Rotation**, Profil, Rechte.
   - `InvitationDeliveryService`: Versandzustand und Wiederholungsregeln (Administratorbestätigung, Versandslot).
   - `IInvitationMailer` / `ExchangeOnlineMailService`: nur Nachricht, Graph-Authentifizierung, Versand, Fehlerklassifikation. Keine Einladungslogik, kein Zugriff auf Datenbank.
2. **Zwei Zustandsmodelle:**
   - Einladung: `pending | accepted | expired | revoked`.
   - Versand (`invitation_deliveries`): `pending | submitted | failed | unknown`. `submitted` = von Graph angenommen (202), **keine Zustellgarantie**. `failed` = eindeutig nicht versendet (Konfiguration, 401/403/404/429/4xx). `unknown` = unklar (Timeout, Netzwerk, 5xx).
   - Ein Versandzustand ändert den Einladungsstatus nie; ein Versandfehler verbraucht keine Einladung.
3. **Token-Lebenszyklus:** Der Klartextlink wird nur im Speicher des Servers (`LinkHandover`, max. 30 min) für den bestätigten Versand gehalten und nach erfolgreicher Übergabe verworfen; nie in Datenbank, Audit, Antworten oder Logs.
4. **Wiederholung:** `failed`/`unknown` → erneuter Versand desselben Links nur mit Administratorbestätigung (kein zweiter aktiver Link). `submitted` → neuer Versand nur über **ausdrückliche Rotation** (neuer Token für dieselbe Einladung, alter Link sofort ungültig, Versandstatus zurück auf `pending`). Ist der Klartext nicht mehr im Speicher (Neustart, Ablauf), ist Rotation der einzige Weg. Rotation ist nur im Status `pending` und vor Ablauf möglich und verlängert die Frist nicht.
5. **Begrenzung:** Ein atomarer Versandslot (`lease`, 60 s) verhindert parallele Versuche; höchstens 5 Versuche je Link/Rotation.
6. **Kein Versand durch GET oder Einlösung.** Versand nur über `POST /admin/invitations/{id}/send` mit `confirm=true` durch einen Administrator.
7. **Standard aus:** Ohne `Enabled=true` und vollständige Konfiguration sendet der Dienst nicht (fail-closed).
8. **Berechtigungen:** Eigene App-Registrierung im Exchange-Tenant (Workforce), `Application Mail.Send` ausschließlich über Exchange Online **RBAC for Applications** auf das Absenderpostfach (Management Scope). **Keine** tenantweite Graph-Berechtigung `Mail.Send` in Entra, da beide Berechtigungen sich addieren und den Scope aushebeln. Authentifizierung per Client-Credentials, getrennt vom OIDC-Login der Lernenden (ADR 0005). Details und Test: `docs/entra-external-id/mail-permissions.md`.

## Konsequenzen

- Ein Administrator sieht beide Zustände; „submitted“ und „gelesen“ sind verschiedene Dinge.
- Nach `submitted` ist ein erneuter Versand mit **neuem** Link nötig; ein bereits zugestellter alter Link wird dadurch ungültig (bewusst).
- Rotation und Einlösung konkurrieren atomar (ADR 0043).
- Das Client-Geheimnis steckt in der Umgebung; ein Zertifikat ist für Produktion vorzuziehen (im PoC nicht umgesetzt).
- Schulungsbenachrichtigungen (ADR 0029) können denselben `IInvitationMailer` nutzen, brauchen aber eigene Vorlagen und Empfängerlogik; nicht Teil dieses PoC.

## Leitplanken für Änderungen

- Keine Einladungslogik im Mail-Service; keine Mail-Abhängigkeit im `InvitationService`.
- Keine Klartextlinks in Datenbank/Audit/Logs/Statusantworten; Mailtext und Link nie protokollieren.
- Absender kommt ausschließlich aus der Konfiguration (nicht aus Anfragen).
- Neue Graph-Berechtigungen nur nach Ergänzung von `mail-permissions.md`; kein `Mail.Send` ohne RBAC-Scope.

## Betroffener Code

- `poc/InviteRegistration/Mail.cs`, `InvitationDelivery.cs`, `InviteHttp.cs`, `InvitationService.cs` (`RotateAsync`), `poc/InviteRegistration.Tests/ExtendedTests.cs`

## Nachtrag 2026-11-08 (Vorbereitung des Livetests)

Status unverändert („Vorgeschlagen“). Ergänzungen im PoC, nicht live geprüft:

- **Absender:** Shared Mailbox `learny@brors.link` (Workforce-Tenant `db88f9b6-…`); kein neues Postfach.
- **Authentifizierung:** bevorzugt Zertifikat (Client-Assertion), Client-Secret nur als Alternative (genau eines von beiden); kein Secret im Terraform-State. Mail-App per Terraform-Stack `20-mail`, RBAC per kontrolliertem Skript (Trockenlauf als Standard).
- **Tenant-Schutz:** `ForbiddenTenantIds` verhindert die Nutzung des External-ID-Tenants als Mail-Tenant.
- **Empfänger-Allowlist:** nur ausdrücklich zugelassene Adressen/Domains; leer = niemand.
- **Timeouts und Retry-After:** Zeitüberschreitung beim Tokenabruf = nichts gesendet (`failed`), beim Senden = `unknown`. `Retry-After` (429, 5xx; auf 1 h begrenzt) sperrt den Versandslot bis zum Zeitpunkt.
- **Befund:** Mehrere andere Apps im Workforce-Tenant haben tenantweites `Mail.Send` und könnten über dasselbe Postfach senden; der RBAC-Scope der Mail-App schützt davor nicht (siehe `mail-permissions.md`).

## Nachtrag 2026-11-08 (OTP-Versand)

Status unverändert. `ExchangeOnlineMailService` sendet zusätzlich die von Entra erzeugten Einmalcodes (`IOtpMailer`, ADR [0045](0045-external-id-branding-und-eigener-otp-mailprovider.md)) mit derselben Mail-App, demselben Zertifikat, demselben Postfachscope und festem Absender `learny@brors.link`. Die statische Empfänger-Allowlist gilt weiter nur für Einladungsmails; OTP-Empfänger werden im Extension-Handler gegen Einladungen und aktive Benutzer geprüft. Keine neuen Mail-Rechte.

## Nachtrag 2026-11-08 (Einladungsmail)

Status unverändert. Die Einladungsmail ist jetzt gebrandetes HTML (`InvitationMailTemplate`: Learny-Kopf, persönliche Begrüßung falls ein Anzeigename vorliegt, Link als Schaltfläche und sichtbar, Gültigkeit, Hinweis auf einmalige Nutzung, Sicherheitshinweis; keine Bilder, kein Tracking). Absender, Betreff, Mail-App, Zertifikat und Postfachscope bleiben unverändert; keine zusätzlichen Rechte. Das Portal löst den Versand nur nach ausdrücklicher Administratorbestätigung aus.
