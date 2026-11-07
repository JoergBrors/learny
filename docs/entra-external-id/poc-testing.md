# PoC-Tests: Einladung, Versand und HTTP

Start (paketfrei, ohne Netzwerk, temporäre SQLite-Dateien, lokaler Kestrel auf Loopback):

```
dotnet build poc/InviteRegistration.Tests
dotnet run --project poc/InviteRegistration.Tests --no-build
```

Exit-Code 0 = alles bestanden. Letzter Lauf: **425 bestanden, 0 fehlgeschlagen** (inklusive Portal, Administration, Mail-Einrichtung, Token-Anreicherung und Claims-Übersicht). Zusätzlich: `dotnet build LearnCards.slnx` fehlerfrei (das PoC ist nicht Teil der Lösung, Learny-Code unverändert) und `terraform validate` erfolgreich in `00-tenant` und `10-app`.

## Teststrategie

- **Einladungslogik** (`Program.cs`): Dienst gegen echte SQLite-Dateien, inkl. Parallelität über zwei Datenbankverbindungen und Fehlerinjektion.
- **Versand** (`ExtendedTests.DeliveryTests`): Zustandsmodell mit simuliertem Mailer (Annahme, Ablehnung, unklar, Ausnahme, Verzögerung).
- **Exchange-Mailservice** (`ExchangeTests`): Graph und Tokenendpunkt über einen Stub-`HttpMessageHandler`; prüft Anfrageform, Fehlerklassifikation, Token-Cache, Geheimnisfreiheit.
- **HTTP** (`HttpTests`): echter Kestrel-Server, „Browser“ mit manueller Cookie-Verwaltung, Fake-Identitätsanbieter (PKCE und nonce werden geprüft, Code nur einmal).
- **Tokenprüfung** (`OidcTests`): lokal signierte Tokens gegen simulierte Discovery/JWKS (Signatur, Issuer, Audience, exp/nbf, nonce, Algorithmus, manipulierter Payload, E-Mail-Richtlinie).
- **Nicht** automatisiert: echte Graph-/Exchange-Aufrufe (vorbereitet: `poc/InviteRegistration.MailLiveTest`, Test A–D in [mail-permissions.md](mail-permissions.md)); die Entra-Anmeldung wurde manuell mit `poc/InviteRegistration.LiveProbe` geprüft (siehe unten).

## Abgedeckte Szenarien

| Bereich | Szenarien |
|---|---|
| Einlösung (68) | gültig; fehlend/unbekannt/überlang; abgelaufen (72 h, konfigurierbar); widerrufen; verbraucht (auch nach Deaktivierung/Identitätsänderung); bestehende Identität; falsche/fehlende/unbestätigte E-Mail; gleiche E-Mail andere Identität; 24 parallele Einlösungen; gleiche Identität parallel; Fehler bei Anlage/Profil/Rechten; fehlgeschlagene Kompensation; keine Eskalation; Module A/B/C; Konfiguration fail-closed |
| Versandzustände | pending ohne Versand; ohne Bestätigung kein Versand; submitted ≠ eingelöst; failed → Wiederholung gleicher Link; unknown → Wiederholung; Mailer-Ausnahme; zweiter Versand nach submitted verweigert; Rotation (neuer Link, alter ungültig, eine Einladung, Status zurückgesetzt); Rotation nur Admin/nur pending; Versuchsgrenze; abgelaufen/widerrufen/eingelöst nicht sendbar |
| Parallelität | 10 parallele Versände → 1 Mailer-Aufruf; 15 Runden Rotation/Widerruf gegen Einlösung → nie zwei Gewinner |
| Mail-Härtung (`MailTests`) | External-ID-Tenant als Mail-Tenant verboten (`wrong_tenant`); Workforce-Endpunkt und Absender `learny@brors.link`; ungültiger Absender; kein `from` aus Anfragedaten; Empfänger-Allowlist (Adresse, Domain, leer, Fremddomain mit gleichem Suffix); Zertifikatsauthentifizierung (Assertion RS256, `x5t#S256`, `aud`, `iss=sub`, ≤ 10 min, verifizierbar; Zertifikat ohne privaten Schlüssel; Secret und Zertifikat gleichzeitig); Timeout beim Senden (`unknown`) und beim Token (`auth_timeout`); `Retry-After` (429, 503, Begrenzung, Sperre des Versandslots); keine Geheimnisse in Konsole/Fehlercodes |
| OTP-Extension (`OtpExtensionTests`) | gültiger Aufruf (200 + continue, Absender `learny@brors.link`, Betreff, Empfänger, Code unverändert, keine Bilder/Links, Workforce-Tenant); Token: fehlend, ohne Präfix, falscher Issuer/Audience/azp/tid, abgelaufen, falsche Signatur, HS256, Müll (alle 401), V1-Token mit appid; Schema: falscher Typ, kein JSON, otpContext fehlt, ungültige Adresse, Code mit Markup/zu kurz/fehlender Feldname (400), Body über 8 KB (413), beide Feldschreibweisen; Empfängerregel: Einladung, aktiver Benutzer, unbekannt, deaktiviert, widerrufen, abgelaufen, Groß-/Kleinschreibung; Rate Limit (429 mit Retry-After, danach wieder erlaubt); Graph 403/401/429/5xx, Timeout, Mailservice deaktiviert (keiner meldet "continue"); Duplikat nach Erfolg (eine Mail), Wiederholung nach Fehler wird versendet; keine Codes in Konsole, Audit-Haken, Antworten, Datenbank; HTTP-Endpunkt (401, 200, 413, 405) und ohne Handler 404; Einladungsmail unverändert an Allowlist gebunden |
| Branding und Mailvorlage (`BrandingTests`) | Zielmarke Learny; Farben aus `app.css`; Kontraste ≥ 4,5:1 (Text, Akzent, Mailkopf, Code); Assets aus dem Bestand (Favicon), Bannerlogo PNG ≤ 36 × 245 px; deutsche Texte, Längengrenzen; Skripte mit Trockenlauf als Standard; Terraform-OTP-API standardmäßig aus; Mailtext deutsch vollständig (HTML und Text), Gültigkeit konfigurierbar, Kodierung des Codes, Codeprüfung |
| Exchange-Mailservice | deaktiviert/unvollständig → kein HTTP; Erfolg (202, Anfrageform, nur konfiguriertes Postfach, `saveToSentItems=false`, Token-Cache); 403 (fehlende Mail-Berechtigung), 401 (Token verworfen), 404, 429, 400 → Rejected; 500/503/Timeout → Ambiguous; Tokenfehler; Header-Injektion; keine Geheimnisse in Fehlercodes |
| HTTP | Admin ohne Schlüssel 401; Profil ADMIN 400; Erstellung ohne Link in Antwort/ohne Mail; Versand ohne Bestätigung 400; bestätigter Versand genau eine Mail; zweiter Versand 409; Status ohne Token; GET Link ohne Versand/Verbrauch; Weiterleitung ohne Token; Sicherheitsheader; Cookie-Attribute; Callback → Dashboard; B2C sieht nur A/C (B → 403); ohne/gefälschte Sitzung 401; Callback-Wiederholung; zweiter Browser; falscher state; Providerfehler; ungültiger Code; falsche/unbestätigte E-Mail; bestehende Identität; zwei Browser parallel → ein Erfolg; `link_unavailable` → Rotation; Widerruf; Versandfehler 502 und Wiederholung |


## Manueller Livetest gegen den External Tenant (2026-11-07)

`poc/InviteRegistration.LiveProbe` (HTTPS `https://localhost:5001`, temporäre Datenbank, kein PoC-Mailversand). Ergebnis mit einem echten Konto (E-Mail-Einmalcode):

- Registrierung im User Flow `LearnySignUpSignIn`, Code-Austausch mit PKCE, ID-Token-Prüfung gegen die echte JWKS: **bestanden**.
- Echte Claims: `email`, `idp = mail`, `iss = https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0`, `sub`, `oid`, `tid`, `ver = 2.0`; **`email_verified` fehlt**.
- Ohne Richtlinie: Einlösung abgelehnt (`EmailNotVerified`, fail-closed). Mit `EmailVerificationPolicies.OtpIdpOnly`: Einlösung erfolgreich, Einladung `accepted`, Dashboard zeigt nur Modul A.
- Gefundener Fehler: Das Token-Cookie hatte den Pfad `/invite` und wurde nicht an `/invite-start` gesendet (RFC-6265-Pfadabgleich). Behoben (Pfad `/`); der Test-Browser beachtet Cookie-Pfade jetzt.
- Der Einladungslink wurde einmal per Outlook-Connector an das Testpostfach gesendet (nicht über den Exchange-Mailservice des PoC).
- Das Test-Client-Secret wurde nach dem Test aus der App gelöscht.
- **Ende-zu-Ende mit echter Mail (2026-11-08):** Einladung über Exchange Online (`learny@brors.link`, Zertifikat) versendet, per Link registriert (genau ein Modul sichtbar), Link danach ungültig. Details in [mail-permissions.md](mail-permissions.md) (Test A bis D).


## Live-Testplan Branding und eigener OTP-Versand (vorbereitet, nicht ausgeführt)

Voraussetzungen und Freigaben je Schritt: [ADR 0045](../adr/0045-external-id-branding-und-eigener-otp-mailprovider.md). Reihenfolge: Branding zuerst (rückholbar), Extension zuletzt.

| Test | Vorbereitung (Freigabe nötig) | Erwartung |
|---|---|---|
| **A** Registrierungsseite zeigt Learny-Branding | Branding ist angewendet (2026-11-08); optische Prüfung steht aus | Logo, Farben, deutscher Hinweistext auf der Konto-erstellen-Seite |
| **B** OTP-Seite zeigt Learny-Branding | wie A | gleiches Branding auf der Code-Seite (Seiten im Flow durchlaufen) |
| **C** OTP-Mail kommt von `learny@brors.link` | öffentliche HTTPS-Adresse bereitstellen; `terraform apply` in `10-app` mit `otp_extension_api_host`; `otp-extension.ps1 -Apply -Activate` | Absender `learny@brors.link`, Betreff „Ihr Learny-Anmeldecode“ |
| **D** OTP-Code funktioniert bei Entra | wie C | Eingabe des Codes führt zur Anmeldung (Entra validiert) |
| **E** Einladung wird genau einmal eingelöst | Einladung per Exchange senden, Link öffnen | Einladung `accepted`, ein Benutzer |
| **F** Dashboard zeigt nur Modul A | wie E | Modul A sichtbar, Modul B nicht, auch nicht per direktem Aufruf (403) |
| **G** eingelöster Link ungültig | Link erneut öffnen | „Die Einladung ist ungültig oder nicht einlösbar.“ |
| **H** Ausfall des eigenen OTP-Dienstes | Endpunkt kurz abschalten (oder Mailservice deaktivieren), Code anfordern | Es wird **kein** Code gesendet (Rückfall aus); keine Anmeldung, keine Einlösung |
| **I** Rückfall auf Microsoft | Listener löschen | Microsoft sendet wieder den Code |

Zusätzlich vor C: Prüfen der echten Nutzlast (Feldname des Codes, `locale`) und des Timeout-/Wiederholungsverhaltens von Entra gegen den PoC-Endpunkt.

## Branding angewendet (2026-11-08)

`bootstrap/branding.ps1 -Apply` gegen den External Tenant: Standard-Branding (`0`) und Lokalisierung `de-DE` angelegt (Hintergrundfarbe `#F7F5F0`, Hinweistext, Eingabehinweis „E-Mail-Adresse“, Bannerlogo, Favicon). Erkenntnisse aus dem ersten Lauf:

- Bilder des Standard-Brandings lassen sich nur unter `/branding/localizations/0/{bild}` hochladen; `PUT /branding/{bild}` liefert HTTP 405. Das Skript ist angepasst.
- Das Anlegen des Standard-Brandings per `PATCH /branding` funktioniert.
- Der Umlaut-Test (Codepoints im Tenant) bestätigt korrekte UTF-8-Speicherung; das Skript sendet Bodys zusätzlich ausdrücklich als UTF-8-Bytes.
- Ein erneuter Trockenlauf meldet „Branding entspricht bereits der Konfiguration“. Die optische Prüfung (Test A/B) hat noch niemand durchgeführt.

## OTP-Extension: Stand der Vorbereitung (2026-11-08)

- Öffentliche Adresse: eigener Cloudflare-Tunnel, veröffentlichte Anwendung `learny-otp.brors.date` → `http://localhost:5080` (nur der schlanke OTP-Host mit einem Pfad; ohne laufenden Host antwortet Cloudflare mit 502).
- `10-app` angewendet (API-App der Extension), Extension per `otp-extension.ps1 -DeviceLogin -Apply` registriert, **ohne Listener**.
- Erkenntnisse: Das Token der Azure CLI enthält `CustomAuthenticationExtension.ReadWrite.All` nicht (HTTP 403); `Connect-MgGraph -UseDeviceAuthentication` brach im Modul ab. Das Skript nutzt deshalb mit `-DeviceLogin` einen eigenen OAuth-Gerätecode-Ablauf (Client „Microsoft Graph Command Line Tools“, Token nur im Speicher).
- Der OTP-Host läuft über `poc/InviteRegistration.LiveProbe --send-mail --otp-extension` (Umgebung `OTP_EXT_AUDIENCES` = Client-ID und Identifier-URI der API-App). Der Endpunkt ist noch nicht gegen echte Entra-Aufrufe getestet.
- **Offen:** Listener aktivieren (`-Activate`, eigene Freigabe) und die Livetests C bis I.

## Portal-Tests (`PortalTests`, `BrandingTests`)

Echtes Kestrel, Fake-Identitätsanbieter (Lernende und Workforce), Fake-Mailer, SQLite-Dateien. Abgedeckt: Zugang ohne Anmeldung gesperrt; Administratoranmeldung nur mit zugelassenem Subject und Workforce-Issuer (gleiche E-Mail, anderes Subject, External-ID-Issuer abgewiesen); Dev-Login nur Development/Loopback, Startabbruch außerhalb von Development (kein Dev-Login, kein HTTP, kein Admin-OIDC); Einladung erstellen (CSRF, Validierung, kein Profil `ADMIN`, unbekannte Module, Gültigkeit), kodierte Ausgabe, kein Token/Link in Seiten; Versand ohne Bestätigung, Versandfehler (Einladung bleibt offen), Wiederholung, Neuausstellung (alter Link ungültig); Registrierung über die simulierte External-ID-Anmeldung inkl. Ereignisreihenfolge (ohne Link/Code); falsche und nicht bestätigte E-Mail, bestehende Identität, Anmeldung bekannter und unbekannter Benutzer, kein offener Redirect; Dashboard nur Modul A, direkter Zugriff auf B/C → 403; Administratorseiten für Lernende gesperrt (IDOR), keine Endpunkte für Profilwechsel/Rollen; Zuweisen (Modul C), Entziehen (Modul A) und Deaktivieren/Reaktivieren wirken sofort; Widerruf; Filter/Suche; Sicherheitsheader; Neustart (Sitzungen, Daten, Ereignisse, versendeter Link bleiben; Klartextlink weg → Neuausstellung); zwei Portalinstanzen auf einer Datenbank, 12 parallele Einlösungen → ein Erfolg; Rate Limit (429); fehlender und getrennter OTP-Endpunkt; Einladungsmail (Branding, Begrüßung, Link, Gültigkeit, Hinweis, ohne Bilder).

**Nicht automatisiert / nicht geprüft:** echte Workforce-Anmeldung der Administratoren (keine App-Registrierung), echter Browser- und Mobiltest der Oberfläche, `prompt=login` gegen External ID, PostgreSQL, Last, echte Mail-Zustellung über das Portal. Der manuelle Ende-zu-Ende-Test (17 Schritte) steht in [poc-portal.md](poc-portal.md).

## Livetest eigener OTP-Versand (2026-11-07/08)

Listener `onEmailOtpSendListener` (ID `e8d1e5fa-c2d6-417e-a97c-fdb5954f8cf4`) für die PoC-App aktiv, **ohne** Rückfall auf Microsoft. Host: `LiveProbe --send-mail --otp-extension` hinter `learny-otp.brors.date` (Cloudflare-Tunnel, nur der OTP-Pfad). Ergebnis mit `joerg@brors.net`:

| Test | Ergebnis |
|---|---|
| C OTP-Mail von `learny@brors.link` | **bestanden**: Mail „Ihr Learny-Anmeldecode“ von „Learny-Lernplattform“, genau eine Mail, keine zusätzliche Microsoft-Mail; Text enthält „Der Code ist 30 Minuten gültig“ |
| D OTP-Code funktioniert bei Entra | **bestanden**: Entra rief die Extension auf (`[OTP] 200:accepted`) und validierte den eingegebenen Code selbst (Token mit `idp = mail`) |
| E Einladung genau einmal eingelöst | **bestanden**: Einladung `accepted` (`redeemed ok`) |
| F/G Dashboard nur Modul A, Link danach ungültig | durch den Gesamtablauf („funktioniert“) und die früheren Livetests abgedeckt; im letzten Lauf nicht einzeln gemessen |
| A/B Branding auf Registrierungs-/OTP-Seite | vom Tester nur pauschal bestätigt („funktioniert“), nicht einzeln dokumentiert |
| H Ausfall des OTP-Dienstes | **nicht ausgeführt** |
| I Rückfall auf Microsoft (Listener löschen) | **nicht ausgeführt** |

Erkenntnisse aus dem Test:

- Eine **bestehende Entra-Browser-Sitzung** führt ohne Code zur Anmeldung. Das erschien zunächst als „Extension wird nicht aufgerufen“. Zum Testen immer ein neues privates Fenster oder zuerst den Logout-Endpunkt aufrufen.
- Der Host-Neustart macht frühere Einladungslinks ungültig (temporäre Datenbank der Probe); „ungültig“ beim Öffnen eines älteren Links ist dann erwartetes Verhalten.
- Ein Anmeldeversuch mit einer anderen Adresse als der eingeladenen (gmx) scheiterte wie vorgesehen mit `EmailMismatch`, ohne Benutzer und ohne Verbrauch der Einladung.
- Der Aufruf mit Tokenprüfung (Aufrufer, Audience, Tenant) ist gegen echte Entra-Aufrufe bestätigt; das echte Request-Schema und das Timeout-/Wiederholungsverhalten von Entra sind weiter nicht im Detail ausgewertet.

## Nicht geprüft / Live-Voraussetzungen

- **Entra-Anmeldung:** Tenant, App, User Flow und Tokenprüfung sind live geprüft. Offen: Learnys `OidcService` statt `EntraOidcFlow`, Verhalten bei Google/Microsoft-Konto/Föderation (andere `idp`-Werte).
- **Exchange:** App im Workforce-Tenant, RBAC-Scope, Positiv-/Negativtest laut [mail-permissions.md](mail-permissions.md), SPF/DKIM/DMARC, Testpostfach.
- PostgreSQL; Last; Ratenbegrenzung; Mehrinstanzbetrieb (die Handover-Ablage ist prozesslokal).

## Abnahme (Soll → Beleg)

| Nr. | Kriterium | Beleg |
|---|---|---|
| 1 | gleiche Einladungs-/Berechtigungslogik | Mailer/Delivery haben keinen Zugriff auf Einlösung; HTTP ruft `InvitationService` |
| 2 | getrennte Verantwortlichkeiten | [poc-architecture.md](poc-architecture.md), Tabelle „Verantwortlichkeiten“ |
| 3 | getrennte Zustandsmodelle | Versandtests, `invitation_deliveries` |
| 4 | Versandfehler verbraucht nichts | failed/unknown/HTTP-502-Tests |
| 5 | Klartext nicht dauerhaft gespeichert | DB-/Audit-/Antwort-Prüfungen |
| 6 | Wiederholung erzeugt keine zweite aktive Einladung | Wiederholungs- und Rotationstests (1 Einladung, 1 gültiger Link) |
| 7 | höchstens einmal einlösbar | Parallel-, Replay-, Rotationsrennen |
| 8 | bestehende Identität abgelehnt | Einlösungs- und HTTP-Test |
| 9 | Mailrechte auf Postfach begrenzt | Konzept + Prüfschritte; **Live-Test offen** |
| 10 | B2C explizit berechtigt | Module A/B/C (Service und HTTP) |
| 11 | alle Tests bestehen | 357/357 |
| 12 | `dotnet build LearnCards.slnx` | fehlerfrei |
| 13 | Terraform-Validierung | `validate` in beiden Stacks erfolgreich |
| 14 | keine Produktionsressource verändert | kein Apply, kein Commit, `git status` nur neue/PoC-/Dokudateien |
