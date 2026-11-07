# 0048 — Multi-Issuer-Authentifizierung und Bestandsschutz

- **Status:** Akzeptiert (Phase A umgesetzt und getestet, in Phase B präzisiert: Vertrauensregel, Vorabprüfung der Einladung)
- **Datum:** 2026-11-08
- **Bezug:** [0005](0005-authentifizierung.md), [0011](0011-mehrbenutzerarchitektur.md), [0012](0012-rollen-aus-entra-app-roles.md), [0026](0026-betriebsmodus-fail-closed.md), [0036](0036-oidc-form-post-und-https-callbacks.md), [0041](0041-entra-external-id-terraform-und-tenant-modell.md), [0042](0042-einladungspflichtige-external-id-registrierung.md), [0046](0046-administrationsportal-externe-registrierung.md), [0047](0047-token-anreicherung-mit-lernrechten.md)

## Kontext

Learny meldet bisher alle Benutzer über Entra ID des Workforce-Tenants an (ein Issuer, eine App-Registrierung, App-Rollen nach ADR 0012). Der Entra-External-ID-PoC (ADR 0041–0047) hat gezeigt, dass externe Lernende einen zweiten Anmeldeweg brauchen. Alle bestehenden Benutzer, Rollen, Besitzverhältnisse, Lernergebnisse, Zertifikate und MCP-Verknüpfungen hängen an der internen `users.id` und an der Identität `(provider, subject)` des Workforce-Issuers; sie dürfen sich nicht ändern.

## Entscheidung

1. **Zwei getrennte Identitätsanbieter.** Der Workforce-Zugang (`OIDC_*`, `OidcService`, `/auth/login`, `POST /auth/callback`) bleibt in Verhalten, Konfiguration, Pfaden und Cookies unverändert. Entra External ID ist ein zweiter, eigener Anbieter (`EXTID_*`, `ExternalOidcService`, `/auth/external/login`, `POST /auth/external/callback`) mit eigener Discovery, eigenem Schlüsselcache, eigener Client-ID, eigenem Secret, eigenem Tenant und eigenem Korrelationscookie (`lc.oidc.ext.state`, Pfad `/auth/external/callback`). Standard: aus; die Pfade existieren nur bei `EXTID_ENABLED`.
2. **Identität = (Issuer aus der Discovery, `sub`).** Der External-Issuer wird aus der Discovery übernommen (nicht aus der konfigurierten Adresse abgeleitet, kein Abgleich mit der Authority). Der Claim `tid` muss dem konfigurierten External Tenant entsprechen. `user_identities.provider` ist der jeweilige Issuer; dadurch sind Identitäten beider Anbieter nie verwechselbar, auch bei gleichem `sub` oder gleicher E-Mail.
3. **Keine Zusammenführung über die E-Mail-Adresse** — weder beim Login noch bei der Registrierung. Bestehende Identitäten werden nur durch einen gesonderten, kontrollierten Verknüpfungsprozess verbunden (eigene Entscheidung, nicht Teil dieser ADR).
4. **Bestandsschutz.** Bestehende Workforce-Benutzer behalten `users.id`, Issuer, `sub`-Zuordnung, Rollen (inkl. `granted_by`), Besitz, Ergebnisse, Zertifikate und MCP-Verknüpfungen. Es gibt keine Datenmigration auf neue IDs, keine Einladungspflicht und keinen Zwangswechsel. `OidcService` bleibt eine Klasse mit dem bisherigen öffentlichen Verhalten (`OidcProviderOptions` aus `OIDC_*`).
5. **Anmeldung am External-Pfad nur für bekannte Identitäten.** `UserService.SignInKnownIdentityAsync` legt nie einen Benutzer an, gleicht keine IdP-Rollen ab und ändert weder E-Mail noch Anzeigename; nur die Login-Zeit wird fortgeschrieben. Unbekannte Identitäten werden abgewiesen (Audit `login.external.rejected`, ohne Subject, E-Mail oder Token). Die Anlage geschieht ausschließlich über eine gültige Einladung (Phase B, ADR 0049).
6. **Rollen aus dem External-Token werden nie gelesen.** Ein Claim `roles` (z. B. `Admin.All`) ändert am External-Pfad nichts; Rollen vergibt nur ein Administrator (ADR 0011/0012). Profil (B2B/B2C) und technische Rolle bleiben getrennt.
7. **Vertrauensregel „E-Mail bestätigt“ (Einlösung, ab Phase B in `ExternalIdentityPolicy.IsEmailTrusted`):** Die E-Mail gilt **nur im ausdrücklich getesteten E-Mail-Einmalcode-Kontext** als bestätigt — und nur, wenn **alle** Bedingungen zusammen erfüllt sind: Issuer aus der Discovery, Audience = Client-ID des External-Clients, `tid` = konfigurierter External Tenant, Tokenversion `ver = 2.0`, `idp = mail` und ein Claim `email`. `idp = mail` allein ist **kein** allgemeiner Verifikationsnachweis (er gilt nur zusammen mit den übrigen Bindungen und nur, solange der User Flow ausschließlich E-Mail-Einmalcode anbietet — das ist Konfigurationsverantwortung und im Livetest zu belegen). Lokale Konten (kein `idp`) werden **nicht** als bestätigt behandelt, bis eine Vertrauensregel am Tenant belegt ist; föderierte Anbieter (Google, Microsoft, OIDC) bleiben gesperrt. `email_verified` fehlt im External-Token und wird nie als Ersatz herangezogen. (Die im PoC zusätzlich zugelassenen lokalen Konten gelten in Learny ausdrücklich nicht.)
8. **Frische Anmeldung:** Der External-Login sendet `prompt=login`; eine bestehende Entra-Sitzung gilt nie als Beleg der OTP-Prüfung. Issuer, Audience, `tid`, `exp`, `nbf`, `nonce`, Signatur (RS256) und `state` werden immer vollständig geprüft; Fehlertexte nennen keine Einzelheiten des Providers.
9. **Einmaligkeit des `state` über alle Instanzen:** Der External-Pfad verbraucht den `state` in der Datenbank (`oidc_consumed_states`, nur SHA-256-Hash, atomar per `INSERT … ON CONFLICT DO NOTHING`), nicht prozesslokal. Der Workforce-Pfad behält vorerst die prozesslokale Prüfung (unverändert); die Umstellung ist eine Empfehlung für den Mehrinstanzbetrieb (siehe Konsequenzen).
10. **Konfiguration fail-closed (ADR 0026):** Ist `EXTID_ENABLED` gesetzt, müssen `EXTID_AUTHORITY` (HTTPS; `http` nur auf Loopback in Development), `EXTID_TENANT_ID` (GUID), `EXTID_CLIENT_ID` (kein Platzhalter), `EXTID_CLIENT_SECRET` und ein Scope mit `openid` vorhanden sein, sonst Startabbruch. Der zweite Anbieter darf nicht dieselbe Authority, dieselbe Client-ID oder denselben Tenant wie der Workforce-Anbieter haben.
11. **Vorabprüfung der Einladung:** Verbindlich ist die Prüfung **in Learny** (`GET /invite/{token}` vor dem Redirect, erneute Prüfung und atomare Einlösung nach dem ID-Token). Entra legt Konten auch ohne Einladung an, wenn die Anmeldeseite direkt aufgerufen wird; ohne gültige Einladung entsteht in Learny weder Benutzer noch Zugriff (Variante A, ADR 0042). Eine Vorabprüfung im Entra-Flow über eine Custom Authentication Extension (Attributerfassung) ist eine **zusätzliche, nicht allein ausreichende** Schutzschicht (Preview, ungetestet, kein Verbrauch in der Extension); sie ersetzt die Prüfung in Learny nie. Verwaiste Entra-Konten werden überwacht und aufgeräumt.

## Konsequenzen

- Die Anmeldeseite zeigt bei aktivem External ID zusätzlich „Externe Anmeldung“; der Workforce-Button bleibt unverändert.
- `/auth/logout` meldet weiterhin am Workforce-Anbieter ab (Cookie-Abmeldung greift für alle); die Abmeldung an der External-Sitzung ist nicht Teil von Phase A.
- Im Mehrinstanzbetrieb gilt für den Workforce-Rücksprung weiter die prozesslokale `state`-Prüfung; der Schutz stützt sich dort auf das einmalige Korrelationscookie. Empfehlung: `OidcStateStore` künftig auch für den Workforce-Pfad verwenden (Änderung am Bestandspfad, daher nicht ohne Freigabe).
- PostgreSQL: Die neue Tabelle nutzt nur portable Typen und Anweisungen (`TEXT`, `ON CONFLICT … DO NOTHING`); die Prüfung auf einer echten PostgreSQL-Instanz steht aus (Testumgebung, Phase F).

## Leitplanken für Änderungen

- Der Workforce-Pfad (`/auth/login`, `/auth/callback`, `OidcService`, `OIDC_*`) wird nur durch eine eigene ADR geändert; Bestandsschutz-Tests dürfen nicht entfallen.
- Identitäten nie über E-Mail oder `sub` allein zuordnen; der Schlüssel ist `(Issuer, sub)`.
- Rollen nie aus dem External-Token übernehmen; keine Benutzeranlage außerhalb der Einladungseinlösung.
- Fehlermeldungen und Logs ohne Codes, Tokens, Secrets, Subjects oder E-Mail-Adressen.

## Betroffener Code

- `Auth/OidcService.cs` (`OidcProviderOptions`, `ExternalOidcService`), `Auth/ExternalAuthEndpoints.cs`, `Auth/OidcStateStore.cs`, `Auth/OidcCallback.cs`, `Auth/AuthEndpoints.cs`
- `Infrastructure/ExternalIdSettings.cs`, `Infrastructure/AppConfig.cs`, `Services/UserService.cs` (`SignInKnownIdentityAsync`), `Data/Schema.cs` (`oidc_consumed_states`), `Components/Pages/Login.razor`, `Program.cs`
- `tests/LearnCards.IntegrationTests/ExternalIdUnitTests.cs`, `OidcFlowTests.cs`

## Präzisierung 2026-11-08 (Phase B)

- Identität ausschließlich über **Issuer und `sub`**; keine Zusammenführung über E-Mail (auch nicht bei der Einlösung: ein inzwischen vorhandenes Konto mit derselben Adresse verhindert die Registrierung, es verknüpft nichts).
- **Keine Administrator-, Auditor- oder Themen-Admin-Rolle aus dem External-Token**; Einladungen können nie Rollen vergeben, Lernprofile sind keine Rollen (ADR 0049).
- Der Workforce-Login bleibt unverändert; eine Identität der Art `external` meldet sich nie über den Workforce-Pfad an und umgekehrt (anderer Issuer, `kind`).
- Die Registrierung (Einladung, Einlösung, Profil, Zuweisungen) ist in [ADR 0049](0049-einladungs-und-lernberechtigungsmodell.md) entschieden.
