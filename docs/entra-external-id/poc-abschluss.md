# Abschluss des Entra-External-ID-PoC (Stand 2026-11-08)

Dieser Branch (`feature/entra-external-id`) bleibt unverändert als Referenz. Die echte Implementierung in Learny wird aus diesem Stand geplant ([learny-integration.md](learny-integration.md)). Die ADRs 0041 bis 0047 sind für den PoC **akzeptiert**; sie beschreiben, was im PoC gilt und live bestätigt wurde, nicht schon den Umbau von Learny.

## 1. Was der PoC leistet

| Bereich | Ergebnis | ADR |
|---|---|---|
| Infrastruktur | Isolierter External Tenant, PoC-App, User Flow `LearnySignUpSignIn`, Mail-App (Workforce-Tenant, Exchange-RBAC), Terraform und Bootstrap-Skripte mit Trockenlauf | [0041](../adr/0041-entra-external-id-terraform-und-tenant-modell.md) |
| Registrierung nur per Einladung | Einmaliger HMAC-Link, Profile B2B/B2C, Module, atomare Einlösung, Kompensation, Rotation | [0042](../adr/0042-einladungspflichtige-external-id-registrierung.md), [0043](../adr/0043-transaktionsmodell-einladungseinloesung.md) |
| Versand | Graph `sendMail` über Exchange Online, Versandstatus getrennt vom Einladungsstatus, Zertifikat im Portal einrichtbar | [0044](../adr/0044-exchange-online-einladungsversand-und-versandstatus.md) |
| Anmeldung und Branding | Eigener Einmalcode über die Custom Authentication Extension `OnOtpSend`, Learny-Branding bei Entra | [0045](../adr/0045-external-id-branding-und-eigener-otp-mailprovider.md) |
| Administration | Portal für Einladungen, Versand, Benutzer, Lernrechte, Ereignisprotokoll, Mail-Einrichtung; Admin-Anmeldung über die Learny-Entra-App (Rolle `Admin.All`) | [0046](../adr/0046-administrationsportal-externe-registrierung.md) |
| Token-Anreicherung | `learny_*`-Claims im ID-Token (Custom Claims Provider), Modulübersicht aus den Claims mit Marke und Ungültigmachen | [0047](../adr/0047-token-anreicherung-mit-lernrechten.md) |

## 2. Tests

- `dotnet run --project poc/InviteRegistration.Tests`: **425 bestanden, 0 fehlgeschlagen** (eigenes Konsolenprogramm, Kestrel in-process, simulierte Anbieter; keine NuGet-Pakete). Das PoC ist nicht Teil von `LearnCards.slnx`, Learny-Code ist unverändert.
- Live am Tenant bestätigt: Registrierung per Einmalcode mit eigener OTP-Mail, einmalige Einlösung, Weiterleitung nach Entra mit Branding, Versand der Einladung über Exchange Online, Admin-Anmeldung über Workforce-OIDC, lokales Konto wird akzeptiert, `learny_*`-Claims im Token, Ungültigmachen der Claims-Übersicht nach Administratoraktion.
- Nicht live geprüft: Passkeys, Entra-Föderation (B2B), soziale Anmeldung, mehrere Portalinstanzen, PostgreSQL, Betrieb hinter einer festen Produktions-URL.

## 3. Stand im Tenant (nach dem PoC)

| Objekt | Tenant | Kennung |
|---|---|---|
| External Tenant (PoC) | External | `5fce2b3c-8e95-49b5-9cfd-fc223b7e8f63` |
| PoC-App „Learny External Auth PoC“ | External | `cb54157f-bcfb-4e78-9c81-b63ee7a0a03d` (`acceptMappedClaims = true`, `requestedAccessTokenVersion = 2`) |
| User Flow `LearnySignUpSignIn` | External | `842b1348-22d6-4c65-9ad3-59cd61278779` (Anzeigename wird noch abgefragt, siehe Punkt 5) |
| OTP-API-App | External | `1132ae90-fa71-4a16-b5df-15054b9e1ff0`, Identifier `api://learny-otp.brors.date/1132ae90-…` |
| Extension `OnOtpSend` + Listener | External | Extension `f931a363-…`, Listener `e8d1e5fa-…` (ohne Rückfall auf Microsoft) |
| Extension `OnTokenIssuanceStart` + Listener | External | Extension `de9ddba2-…`, Listener `82f0c024-…`, Claims-Mapping-Policy `b8c8503b-…` an der PoC-App |
| Mail-App | Workforce | `f3365162-0eda-48b3-99dd-b96d3633c40c`, Absender `learny@brors.link`, Exchange-RBAC auf dieses Postfach |
| Learny-Entra-App (Admin-Anmeldung) | Workforce | `Learny-oid` `89e199e0-…`, zusätzliche Redirect-URI `http://localhost:5090/admin/callback` |
| Öffentlicher Zugang der Extensions | Cloudflare | Tunnel `learny-otp.brors.date` → `http://localhost:5080` (nur `/extensions/otp-send` und `/extensions/token-claims`) |

Rückbau der einzelnen Teile: `otp-extension.ps1 -RemoveListener`, `claims-extension.ps1 -RemoveListener` bzw. `-RemoveExtensionId`, `userflow-attributes.ps1`, Terraform-Stacks (nur nach geprüftem Plan). Nichts wurde gelöscht, was nicht ausdrücklich beauftragt war.

## 4. Betrieb des PoC

- Start in VS Code: Konfiguration „Registrierungs-PoC Portal (lokal)“ (`.vscode/launch.json`), Umgebung aus `poc/.secrets/portal.env` (Skript `poc/New-PortalEnv.ps1`, Geheimnisse nie in Git, `poc/.secrets/` ist ignoriert). Details und alle Variablen: [poc-portal.md](poc-portal.md).
- Wichtige Schalter: `PORTAL_OTP_ENABLED=1`, `OTP_EXT_AUDIENCES`, `PORTAL_CLAIMS_ENABLED=1`, `PORTAL_SHOW_TOKEN` (nur Development; zeigt das eigene ID-Token auf `/profile`), `PORTAL_ENTRA_SCOPE` (Standard `openid profile email`).
- Diagnose: Ereignisprotokoll und Seite „Mail-Einrichtung“ im Portal (Statuscodes der Extension-Aufrufe, Kurzcodes bei Tokenfehlern, lesbare Ablehnungsgründe), Entra-Anmeldeprotokoll (`auditLogs/signIns`, beta).

## 5. Bekannte Grenzen und Erkenntnisse

1. **E-Mail-Bestätigung:** Als bestätigt gelten Anmeldungen per Einmalcode (`idp = mail`) und **lokale Konten** des External Tenants (kein `idp`, `ver = 2.0`, Adresse in `emails`/`preferred_username`). Die Annahme, dass Entra lokale Adressen bei der Registrierung per Code bestätigt, stammt aus der Microsoft-Dokumentation und ist am Tenant nicht belegt.
2. **Registrierung außerhalb der Einladung:** Entra legt Konten auch an, wenn jemand die Anmeldeseite direkt aufruft; das Portal verweigert dann nur den Zugriff. Eine Sperre bräuchte eine Prüfung vor dem Anlegen (z. B. Attributerfassung-Extension) und ist nicht umgesetzt.
3. **Claims:** Nur Text und Text-Listen, höchstens 3 KB; eine Zahl ließ die Tokenausstellung scheitern (`AADSTS1100001`); ein einzelnes Modul erscheint als Text statt Liste; der Scope `<client-id>/.default` wird abgelehnt. Die erste Anmeldung nach dem Einschalten liefert leere Claims (die Entra-Objekt-ID ist noch unbekannt; lokale Konten haben keine Adresse im Verzeichnis).
4. **Übersicht aus Claims:** Die aktuelle Marke liegt im Prozessspeicher; mehrere Instanzen brauchen einen gemeinsamen Speicher oder einen kurzen Ablauf. Der Zugriff auf Module wird immer gegen die Datenbank geprüft.
5. **Anzeigename:** Der User Flow fragt den Anzeigenamen noch ab. `userflow-attributes.ps1` (Trockenlauf geprüft) entfernt die Abfrage; der Name kommt dann aus der Einladung. Nicht angewendet.
6. **Passkeys:** In External-ID-Tenants nur für Konten mit E-Mail + Passwort, nicht mit Einmalcode; Registrierung nur über eine eigene Verwaltungsseite der Anwendung; Löschen per Graph möglich (nicht getestet). Siehe Abschnitt 7.
7. **Preview-Endpunkte:** Extension und Listener nutzen Graph `beta`; Verhalten kann sich ändern.

## 6. Offene Punkte (nicht erledigt, nur auf ausdrücklichen Auftrag)

- Test I: Listener `e8d1e5fa-…` der OTP-Extension entfernen (Rückfall auf Microsofts Mail testen) und anschließend wieder aktivieren oder zurückbauen.
- Rückbau des Test-Secrets `livetest-otp` in Entra und der lokalen Secret-Dateien unter `poc/.secrets/`.
- User-Flow-Änderung (Anzeigenamen-Feld) anwenden oder verwerfen.
- Branding-Prüfung A/B je Seite dokumentieren; Cloudflare-Route/Tunnel bewerten.
- Nichts committet: Der Stand liegt als Datei-Änderungen im Arbeitsverzeichnis des Branches.

## 7. Vorschlag für die Planung der echten Implementierung

Reihenfolge (jeweils mit eigener ADR gegenüber den Learny-ADRs 0005, 0011, 0012, 0026, 0036):

1. **Anmeldung:** `OidcService` von Learny um External ID als zweiten Issuer erweitern (Authority, `tid`, Roles getrennt vom Workforce-Issuer); Ersatz von `EntraOidcFlow`. E-Mail-Bestätigung nach Abschnitt 5.1 festlegen.
2. **Datenmodell:** Tabellen `invitations*`, `user_profiles`, `module_grants`, `user_entra_oids`, `invitation_events`, `mail_settings` nach `Data/Schema.cs` portieren (SQLite und PostgreSQL, gebundene Parameter); `poc_modules` entfällt zugunsten der Learny-Module/Themen (`ResolveModuleAccessAsync`).
3. **Einladung und Versand:** `InvitationService`/`InvitationDeliveryService` und Mail-Einrichtung als Learny-Dienste; Mail-App- und Zertifikatsverwaltung in die Administration; keine Geheimnisse in der Datenbank.
4. **Extensions:** `otp-send` und `token-claims` als eigene Pfade im Learny-Web (bzw. schlanker Host) hinter öffentlichem HTTPS; Konfiguration über `.env`/`AppConfig` und Deployment-Dokumentation.
5. **Claims und Rechte:** `learny_*` aus den Learny-Berechtigungen (Module/Themen/Zuweisungen) ableiten; Marke und Ungültigmachen über gemeinsamen Speicher; Zugriffsprüfung bleibt in der Datenbank.
6. **Entscheidungen vorab:** Passkeys (Passwortkonten ja/nein), B2B über Entra-Föderation, Sperre der Registrierung außerhalb der Einladung, eigene Anmeldedomäne statt `ciamlogin.com`, Rolle `themen_admin`/Auditor für Externe, Aufbewahrung des Ereignisprotokolls.
7. **Betrieb:** Terraform-Stacks und Bootstrap-Skripte aus `infra/entra-external-id/` als Grundlage für Test-/Produktionstenant übernehmen; kein `apply` ohne geprüften Plan.
