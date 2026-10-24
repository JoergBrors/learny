# 0014 — Sitzungs- und Betriebsmodus-Härtung

- **Status:** Vorgeschlagen (nicht umgesetzt, benötigt Freigabe)
- **Datum:** 2026-10-24
- **Anlass:** [Prüfbericht Mehrbenutzerfähigkeit](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md), Feststellungen F1–F6
- **Würde ergänzen:** [0005](0005-authentifizierung.md), [0011](0011-mehrbenutzerarchitektur.md)

## Kontext

Die Prüfung zeigte: Die Datentrennung zwischen Benutzern ist wirksam, aber (F1) eine unvollständige Produktivkonfiguration öffnet stillschweigend den Dev-Login mit Administratorrechten,
(F2) Session-Cookies lassen sich nach dem Logout weiterverwenden, (F3) offene Blazor-Circuits übernehmen Sperren nicht sofort, (F4) Upserts sind nicht atomar,
(F5/F6) Provider-Bindung und vertrauenswürdige Proxys sind nicht erzwungen.

## Vorgeschlagene Entscheidung

1. **Betriebsmodus fail closed:** Der Dev-Login ist nur aktiv, wenn `AUTH_MODE=dev` ausdrücklich gesetzt **und** `ASPNETCORE_ENVIRONMENT=Development` ist. `AUTH_MODE=auto` entfällt oder wird zu
   „OIDC, sonst Start verweigern“. `devUser`/`devRoles` existieren nur in `Development`.
2. **Serverseitige Sitzungsversion:** `users.session_version` wird als Claim ins Cookie geschrieben und in `OnValidatePrincipal` verglichen. Logout, „überall abmelden“, Sperren und Rollenentzug erhöhen sie.
   Zusätzlich eine absolute Höchstdauer der Sitzung. Logout per POST mit Antiforgery.
3. **Circuits revalidieren:** `RevalidatingServerAuthenticationStateProvider` (Framework, kein NuGet) prüft Benutzerstatus und Sitzungsversion in kurzen Abständen.
4. **Atomare Upserts:** `INSERT … ON CONFLICT … DO UPDATE` für `user_preferences` und `user_card_states` auf beiden Providern.
5. **Provider-Bindung:** `ADMIN_SUBJECTS` und die Altdaten-Migration gelten nur für `provider = OIDC_ISSUER`, nie für `dev`.
6. **Proxys:** erlaubte Forwarded-Header-Quellen konfigurierbar (`KNOWN_PROXIES`); in Produktion `Secure`-Cookies erzwingen.

## Konsequenzen (bei Annahme)

- Lokale Entwicklung braucht künftig `AUTH_MODE=dev` explizit (VS-Code-Starts und Tests setzen es bereits bzw. sind anzupassen).
- Eine zusätzliche DB-Spalte und eine Migration (`ALTER … ADD COLUMN session_version`), kleiner Mehraufwand pro Anfrage (Prüfung existiert bereits).
- Bestehende Sitzungen werden einmalig ungültig.

## Leitplanken für Änderungen

- Kein Weg darf aus einer Fehlkonfiguration einen Administratorzugang ohne Anmeldung machen.
- Tests: Betriebsmodus (Production ohne OIDC startet nicht), Cookie-Replay nach Logout → 401, parallele Erstspeicherung ohne 500 (siehe `tests/LearnCards.IntegrationTests`).
