# 0047 — Token-Anreicherung mit Lernrechten (Custom Claims Provider)

- **Status:** Akzeptiert (PoC-Stand 2026-11-08; die Integration in Learny wird aus diesem Branch geplant, offene Punkte: [poc-abschluss.md](../entra-external-id/poc-abschluss.md)) – im PoC umgesetzt, per Test geprüft und live am External Tenant bestätigt (`learny_*`-Claims im ID-Token, Claims-Übersicht)
- **Datum:** 2026-11-08
- **Bezug:** [0011](0011-mehrbenutzerarchitektur.md), [0015](0015-freigaben-globale-lernmodule.md), [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md), [0042](0042-einladungspflichtige-external-id-registrierung.md), [0045](0045-external-id-branding-und-eigener-otp-mailprovider.md), [0046](0046-administrationsportal-externe-registrierung.md)

## Kontext

Nach der Anmeldung soll die Lernplattform die freigegebenen Module ohne Datenbankabfrage anzeigen können. Entra External ID kann Tokens kurz vor der Ausstellung über eine Custom Authentication Extension (Ereignis `OnTokenIssuanceStart`, Custom Claims Provider) um externe Daten ergänzen.

## Entscheidung

1. **Claims sind ein Hinweis, keine Berechtigung.** Die Zugriffsentscheidung bleibt bei `ContentAccess`/`ResolveModuleAccessAsync` gegen die Datenbank (ADR 0011, 0029); ein Entzug wirkt dadurch sofort, auch bei noch gültigem Token. Die Claims dienen Übersicht und Cache.
2. **Claims** (nur Präfix `learny_`): `learny_uid` (interne Benutzer-ID), `learny_profile`, `learny_status`, `learny_modules` (Modul-IDs, nur bis `MaxModules` = 20), `learny_modules_n` (Anzahl), `learny_grants_v` (Änderungsmarke: Hash aus Profil, Status und sortierten Modul-IDs, kein Zähler, keine Schemaänderung). Nie: Name, E-Mail, Einladungsdaten, Notizen, Administrator- oder andere Rollen.
3. **Cache-Regel für Learny:** Die Übersicht darf aus den Claims gezeigt werden, solange `learny_grants_v` zur aktuellen Marke passt; weicht sie ab (oder fehlt der Claim), kommt die Übersicht aus der Datenbank. Beim Öffnen eines Moduls wird immer gegen die Datenbank geprüft.
4. **Nie ein Anmeldefehler:** Unbekannter Benutzer (z. B. die erste Anmeldung, bevor das Portal den Benutzer kennt), Fehler oder fremde Client-App liefern `200` mit leeren Claims; nur ein ungültiges Entra-Token führt zu `401`. Beim ersten Login enthält das Token daher noch keine Claims.
5. **Zuordnung:** über die Entra-Objekt-ID (Claim `oid`, bei Registrierung und Anmeldung in `user_entra_oids` festgehalten, nie einem anderen Benutzer zugeordnet), nur ersatzweise über die Tenant-Adresse. Aufrufe für andere Apps als die Portal-App werden nicht bedient.
6. **Betrieb:** Endpunkt `POST /extensions/token-claims` auf demselben schlanken Extension-Host und derselben API-App/Audience wie `otp-send` (kein Zugriff auf Verwaltung); Token-Prüfung wie bei der OTP-Extension (Signatur, Issuer, Audience, Aufrufer `99045fe1-…`, Tenant). Standard aus (`PORTAL_CLAIMS_ENABLED=1`). Audit nur als `Status:Ergebnis`.
7. **Registrierung im Tenant** nur über `infra/entra-external-id/bootstrap/claims-extension.ps1` (Trockenlauf; `-Apply` nach Freigabe, `-Activate` für den Listener, `-RemoveListener` zum Rückbau): Extension, Claims-Mapping-Policy (nur `learny_*`), Policy am Service Principal der PoC-App, App-Manifest `acceptMappedClaims = true` und `requestedAccessTokenVersion = 2` (nur Single-Tenant-Apps).

## Erkenntnisse aus dem Live-Test (2026-11-07)

- **Nur Text und Text-Listen:** Eine Zahl (`learny_modules_n` als JSON-Zahl) ließ die Tokenausstellung scheitern (Token-Endpunkt `AADSTS1100001`, `token_http_400`); als Text geliefert funktioniert es. Boolean und JSON sind laut Dokumentation ebenfalls nicht unterstützt. Gesamtgröße der Claims höchstens 3 KB.
- **Eine Liste mit einem Eintrag** erscheint im Token als einfacher Text, nicht als Array: `learny_modules` ist für Verbraucher „Text oder Text-Liste“.
- **Scope bleibt `openid profile email`;** `<client-id>/.default` wird von Entra abgelehnt (`AADSTS1100001`).
- **Erste Anmeldung nach dem Einschalten:** Das Portal kennt die Entra-Objekt-ID (`oid`) erst nach einer Anmeldung; davor `200:unknown_user` und leere Claims (lokale Konten haben keine Adresse im Verzeichnis, `mail` ist leer).
- Diagnose im Portal: Ereignisse „Token-Anreicherung“ (`Status:Ergebnis`) und Kurzcodes bei Token-Fehlern (`oidc:token_http_…_aadsts…`).

## Nutzung im PoC-Portal (Referenz für Learny)

- Beim Login übernimmt die Sitzung `learny_grants_v` und `learny_modules` (nur wenn `learny_uid` zum angemeldeten Benutzer gehört) als Sitzungsclaims `lv`/`lm`.
- Die Übersicht (`/dashboard`) zeigt die Module aus `lm`, solange `lv` der aktuellen Marke entspricht; die aktuelle Marke liegt je Benutzer im Speicher (`LearnerOverview`) und wird bei jeder Administratoraktion (Zuweisen, Entziehen, Deaktivieren, Reaktivieren) ungültig gemacht. Ohne Claims oder bei abweichender Marke kommt die Übersicht aus der Datenbank; die Seite nennt die Quelle.
- Modulnamen kommen aus einem Katalog-Cache; das Öffnen eines Moduls prüft immer `ContentAccess` gegen die Datenbank.
- Mehrere Portalinstanzen brauchen für die aktuelle Marke einen gemeinsamen Speicher oder einen kurzen Ablauf (der PoC hält sie im Prozess).

## Konsequenzen

- Token wächst um die Claims (Größenbegrenzung über `MaxModules`); mehr Module → nur die Anzahl, die Liste kommt aus der Datenbank.
- Neue Zuweisungen erscheinen im Token erst nach der nächsten Anmeldung; bis dahin trägt die Marke `learny_grants_v` die Erkennung.
- Der Aufruf erfolgt bei jeder Tokenausstellung (Latenz, Verfügbarkeit des Extension-Hosts); Ausfall führt laut Entra zu einem Fehler bei der Anmeldung der OIDC-App — Zeitlimit 2 s, ein Wiederholungsversuch.
- Das Skript wurde nur als Trockenlauf geprüft; die Graph-Aufrufe (beta) und die Wirkung auf ID-Tokens externer Tenants sind live zu bestätigen.

## Leitplanken für Änderungen

- Keine neuen Claims ohne Eintrag in dieser ADR; nie personenbezogene Daten, nie Rechte, die allein über den Zugriff entscheiden.
- Berechtigungsprüfung bei Zugriff bleibt in der Datenbank; Claims nur für Übersicht und Cache.
- Leere Claims statt Fehler für alles außer einem ungültigen Entra-Token.

## Betroffener Code

- `poc/InviteRegistration/TokenClaims.cs`, `InviteHttp.cs` (`MapTokenClaims`), `InviteSchema.cs` (`user_entra_oids`), `InvitationService.cs` (`RecordObjectIdAsync`), `EntraOidcFlow.cs` (`oid`)
- `poc/InviteRegistration.Portal/Program.cs`, `PortalHost.cs`
- `poc/InviteRegistration.Tests/TokenClaimsTests.cs`, `infra/entra-external-id/bootstrap/claims-extension.ps1`

## Nachtrag 2026-11-08 (Learny-Integration, Phase B)

In Learny sind `learny_*`-Claims nie die Rechtequelle: Zugriffsentscheidungen kommen aus der Datenbank (`ResolveModuleAccessAsync`, Themenpfade, [0049](0049-einladungs-und-lernberechtigungsmodell.md)); die Token-Anreicherung ist ein späterer Hinweis ohne prozesslokalen Berechtigungszustand als Sicherheitsquelle. Sie ist nicht Teil von Phase B.
