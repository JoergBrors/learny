# 0046 — Administrationsportal für externe Registrierung und Benutzer-Lebenszyklus

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – im PoC umgesetzt und per HTTP-Tests geprüft; Administratoranmeldung live über die Learny-Entra-App (Workforce, Rolle Admin.All) bestätigt
- **Datum:** 2026-11-08
- **Bezug:** [0002](0002-keine-nuget-pakete.md), [0003](0003-eine-deploybare-einheit.md), [0004](0004-datenbank-abstraktion.md), [0005](0005-authentifizierung.md), [0011](0011-mehrbenutzerarchitektur.md), [0012](0012-rollen-aus-entra-app-roles.md), [0014](0014-sitzungs-und-betriebsmodus-haertung.md), [0015](0015-freigaben-globale-lernmodule.md), [0026](0026-betriebsmodus-fail-closed.md), [0036](0036-oidc-form-post-und-https-callbacks.md), [0042](0042-einladungspflichtige-external-id-registrierung.md), [0043](0043-transaktionsmodell-einladungseinloesung.md), [0044](0044-exchange-online-einladungsversand-und-versandstatus.md), [0045](0045-external-id-branding-und-eigener-otp-mailprovider.md)

## Kontext

Der PoC kann Einladungen versenden, einlösen und Inhalte freigeben, aber nur per Skript und ohne Verwaltungsoberfläche. Administratoren sollen Einladungen erstellen, versenden, widerrufen und verfolgen, Lernrechte nachträglich ändern und Benutzer deaktivieren können. Lernende registrieren sich über den vorhandenen External-ID-Ablauf und sehen nur zugewiesene Inhalte.

## Entscheidung

1. **Ein eigenständiges Portal im PoC** (`poc/InviteRegistration.Portal`, ASP.NET Core, Razor-Komponenten im statischen Server-Rendering, kein SignalR, keine NuGet-Pakete). Bereiche: `/admin` (Administration) und `/dashboard` (Lernende). Es nutzt `InvitationService`, `InvitationDeliveryService`, `ContentAccess`, `ExchangeOnlineMailService`, `EntraOidcFlow` und den OTP-Handler weiter; der Einladungs-/OIDC-Ablauf wurde dafür aus `InviteApp` in die gemeinsame Komponente `InviteFlow`/`OidcCookieFlow` ausgelagert (keine zweite Einladungslogik, keine zweite Persistenz).
2. **Administratoranmeldung** über einen **getrennten, ausdrücklich vertrauten Workforce-Issuer** (`EntraOidcFlow` mit eigener Authority). Administrator ist nur, wessen **Subject** (nicht die E-Mail) in der Bootstrap-Konfiguration `PORTAL_ADMIN_SUBJECTS` steht **und** dessen Issuer dem konfigurierten Administrator-Issuer entspricht. Der Administrator-Issuer darf nie der External-ID-Issuer sein. Die Rolle `admin` wird ausschließlich hier (`AdminBootstrap`) vergeben, nie über Einladungen oder Entra-Claims. Bei **jedem** Aufruf wird die Rolle gegen die Datenbank geprüft (ADR 0011/0012). Außerhalb von Development ist ohne Workforce-OIDC kein Start möglich (ADR 0026); ein lokaler Entwicklungszugang mit Schlüssel existiert nur in Development und nur auf Loopback.
3. **Profil und Rollen getrennt:** Das Lernprofil (`B2B_LEARNER`, `B2C_LEARNER`) steht in `user_profiles` und stammt ausschließlich aus dem Einladungsdatensatz. Technische Rollen (`user_roles`) erhalten Eingeladene nie. Ein Profilwechsel ist nicht vorgesehen (der Dienst lehnt ihn ausdrücklich ab) und braucht eine eigene Entscheidung mit erneuter Rechteprüfung.
4. **Lernrechte** (`module_grants`) können Administratoren nach der Registrierung zuweisen und entziehen; die Wirkung ist sofort, weil `ContentAccess` bei jedem Zugriff prüft (auch direkter Modulzugriff, Dashboard). Benutzer-Deaktivierung sperrt Sitzung und Zugriff sofort (Cookie-Validierung und `ContentAccess` prüfen `users.status`). Löschen ist nicht Teil des PoC.
5. **Zustandsmodelle bleiben getrennt:** Einladung `pending|accepted|expired|revoked`, Versand `pending|submitted|failed|unknown`, dazu ein **Ereignisprotokoll** (`invitation_events`) mit festem Typkatalog und eng begrenzter Kategorie (kein Freitext), nie Codes, Links oder Tokens. „Link aufgerufen“ ist nur ein beobachteter Serveraufruf (Scanner/Linkvorschau möglich); Fortschritt innerhalb des Microsoft-Ablaufs wird nicht behauptet. Die Timeline zeigt nur technisch beobachtete Ereignisse.
6. **Versand nur nach ausdrücklicher Administratorbestätigung** (Formular mit Bestätigungsfeld und CSRF-Schutz). Der Klartextlink liegt nur im Speicher (`LinkHandover`, 30 min). Ist er nicht mehr verfügbar (Neustart), ist die ausdrücklich bestätigte **Neuausstellung (Rotation)** der einzige Weg; sie macht den alten Link ungültig und erzeugt keine zweite Einladung. Die Einladungsmail ist gebrandetes HTML (ohne Bilder/Tracking).
7. **Persistenz:** Alle Daten (Einladungen, Benutzer, Identitäten, Profile, Rechte, Versandstatus, Ereignisse) liegen in der PoC-Datenbank (SQLite lokal, keine Learny-Datenbank); Data-Protection-Schlüssel werden im Dateisystem gehalten, der Token-HMAC-Schlüssel bleibt über Neustarts gleich (`PORTAL_INVITE_TOKEN_KEY`). Mehrere Portalinstanzen auf derselben Datenbankdatei lösen höchstens einmal ein (ADR 0043). PostgreSQL ist **nicht verifiziert**.
8. **Frische Authentifizierung:** Der Registrierungsablauf sendet `prompt=login`; eine bestehende Entra-Sitzung gilt nie als Beleg einer frischen OTP-Verifikation. Unabhängig davon werden Issuer, Audience, `tid`, state, nonce, E-Mail-Bindung, Identitätsausschluss und Einladung immer vollständig geprüft; die E-Mail gilt nur im getesteten OTP-Kontext (`idp = mail`, `ver = 2.0`) als bestätigt. Die Wirkung von `prompt=login` bei External ID ist live zu bestätigen.
9. **OTP-Extension technisch getrennt:** Sie läuft (falls aktiviert) auf einem **eigenen Port** mit nur dem Pfad `POST /extensions/otp-send`, ohne Zugriff auf Verwaltung; das Hauptportal hat diesen Pfad nicht.
10. **Anwendungssicherheit:** Cookies `HttpOnly`/`Secure` (außer lokal über HTTP), SameSite Lax (Sitzung) bzw. None nur für das einmalige OIDC-Korrelationscookie, CSRF-Token für alle Formulare, Rate Limit für Einladungs-/Anmeldepfade, einheitliche Einladungsfehler, kein offener Redirect, CSP/`no-store`/`X-Frame-Options`, kein Logging von URLs, keine öffentliche Administrator-API.

## Konsequenzen

- Das Portal ist ein PoC und kein Learny-Produktcode; für die Integration sind die Schnittstellen und das Datenmodell zu übernehmen (siehe `learny-integration.md`).
- Die Administratoranmeldung gegen echtes Workforce-Entra ist **nicht live getestet** (Workforce-App-Registrierung fehlt); Tests nutzen einen simulierten Identitätsanbieter.
- Ein Benutzerwechsel B2C ↔ B2B erfordert künftig eine eigene Entscheidung; bis dahin ist nur Deaktivierung und neue Einladung möglich.
- Das Ereignisprotokoll ist kein manipulationssicheres Audit (keine Signatur, keine Aufbewahrungsregeln).

## Leitplanken für Änderungen

- Die Rolle `admin` nur über `AdminBootstrap`; nie aus Einladungen, Anfragedaten oder External-ID-Claims; Administrator-Issuer ≠ External-ID-Issuer.
- Profile und Rechte nur serverseitig aus Einladung bzw. Administratoraktion; Rechteprüfung zentral in `ContentAccess`.
- Keine Codes, Links, Tokens oder Freitext in Ereignissen; neue Ereignistypen nur im Katalog.
- Versand nur nach Bestätigung; Linktoken nie persistieren; Neuausstellung ersetzt den alten Link atomar.
- Kein stiller Rückfall außerhalb von Development; Dev-Login nie außerhalb von Development/Loopback.

## Betroffener Code

- `poc/InviteRegistration.Portal/*` (`PortalHost`, Razor-Seiten, `Program.cs`), `poc/InviteRegistration/AdminService.cs`, `InviteEvents.cs`, `InviteFlow.cs`, `InviteSchema.cs`, `InvitationService.cs`, `OtpExtension.cs` (`InvitationMailTemplate`)
- `poc/InviteRegistration.Tests/PortalTests.cs`, `docs/entra-external-id/poc-portal.md`

## Nachtrag 2026-11-08 (Administratoranmeldung mit der Learny-App)

Status unverändert. Die Administratoranmeldung nutzt dieselbe Workforce-App wie Learny (`Learny-oid`). Administrator ist, wer im ID-Token des vertrauten Issuers eine konfigurierte App-Rolle (`PORTAL_ADMIN_ROLES`, z. B. `Admin.All`) hat oder dessen Subject in `PORTAL_ADMIN_SUBJECTS` steht. Fällt die Rolle bei einer späteren Anmeldung weg, wird die Rolle `admin` in der Datenbank entzogen (laufende Sitzungen enden sofort). An der App wurde die Redirect-URI `http://localhost:5090/admin/callback` ergänzt; die Mitbenutzung einer Produktions-App-Registrierung (gemeinsames Secret) ist ein Kompromiss für den lokalen Test und für die Produktion durch eine eigene App-Registrierung zu ersetzen.

## Nachtrag 2026-11-08 (Mail-Einrichtung im Portal)

Status unverändert. Administratoren richten den Mailversand unter `/admin/mail` ein: App-Registrierung (Tenant, Client-ID, Absender, erlaubte Empfänger; vorausgewählt aus `MAIL_*`), Client-Zertifikat und Aktivierung. Das Zertifikat (RSA 3072, selbstsigniert, 24 Monate) entsteht im Portal; der private Schlüssel liegt nur AES-256-GCM-verschlüsselt (Schlüssel per HKDF aus `PORTAL_INVITE_TOKEN_KEY`) im Datenverzeichnis, heruntergeladen wird ausschließlich das öffentliche `.cer`. Aktivieren geht nur mit Zertifikat und mindestens einem erlaubten Empfänger; ein neues Zertifikat deaktiviert den Versand, bis es in Entra hinterlegt und erneut aktiviert wurde; der External-ID-Tenant wird abgelehnt. Änderungen stehen im Ereignisprotokoll (`mail.settings_changed`, `mail.certificate_created`, ohne Schlüssel/Freitext). `ConfigurableMailer` liest die Einrichtung bei jedem Versand (kein Neustart); `PORTAL_MAIL_ENABLED=1` bleibt als Rückfall. Das Zertifikat in Entra hochzuladen und die Exchange-RBAC-Begrenzung bleibt ein manueller Schritt (Anleitung auf der Seite).

## Nachtrag 2026-11-08 (Learny-Integration, Phase B)

Die fachlichen Operationen des Portals (Einladungen verwalten, Module/Themen zuweisen und widerrufen, Status) stehen in Learny als `InvitationService` und `LearnerAdminService` bereit ([0049](0049-einladungs-und-lernberechtigungsmodell.md)); die Oberfläche folgt in Phase C im vorhandenen `Admin.razor`. Es gibt keine zweite Admin-App und keinen Administrator aus dem External-Token.
