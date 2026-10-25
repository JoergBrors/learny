# 0026 — Betriebsmodus fail closed: kein stiller Dev-Login, ENV_FILE ist verbindlich

- **Status:** Akzeptiert (setzt Punkt 1 von [0014](0014-sitzungs-und-betriebsmodus-haertung.md) teilweise um)
- **Datum:** 2026-10-26
- **Anlass:** Produktionsvorfall: Nach dem Umzug der `.env` aus dem Webroot (ADR 0023) las die Anwendung ihre Konfiguration nicht mehr und fiel mit `AUTH_MODE=auto` still auf den **Dev-Login mit Administratorrechten** zurück (Anmeldeseite „Lokal anmelden (Dev-Modus)“ unter der öffentlichen Adresse, SQLite statt PostgreSQL).
- **Bezug:** [0005](0005-authentifizierung.md), [0009](0009-deployment-wege.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.md), Prüfbericht F1

## Entscheidung

1. **Dev-Login nie stillschweigend:** Außerhalb von `ASPNETCORE_ENVIRONMENT=Development` verwendet `AppConfig` den Dev-Login nur bei **ausdrücklich** gesetztem `AUTH_MODE=dev`. `auto`, leere und unbekannte Werte (Tippfehler wie `odic`) ergeben **OIDC** oder einen **Startabbruch** mit klarer Meldung (`InvalidOperationException`, keine Werte). `AUTH_MODE=oidc` ohne `OIDC_ISSUER`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET` bricht ebenfalls ab. In `Development` (lokales `dotnet run`, VS Code) bleibt `auto` → Dev-Login, wenn OIDC nicht konfiguriert ist.
2. **Ausdrücklich `AUTH_MODE=dev` in Nicht-Development** bleibt möglich (lokales Ausprobieren im Docker-Image, Integrationstests), schreibt aber beim Start eine **Warnung** ins Log. (Strengere Variante aus 0014 — Dev-Login nur in `Development` — bleibt Vorschlag.)
3. **`ENV_FILE` ist verbindlich:** Ist `ENV_FILE` gesetzt und die Datei nicht vorhanden oder nicht lesbar (z. B. fehlende Berechtigung des App-Pools), bricht der Start ab, statt ohne Konfiguration weiterzulaufen.
4. **Deployment (IIS):** Der Pfad der `.env` wird zusätzlich zur App-Pool-Variable in der `web.config` (`aspNetCore/environmentVariables`, nach dem Spiegeln) gesetzt. Nach dem Start prüft der Workflow: Die Anmeldeseite darf bei `AUTH_MODE≠dev` nicht den Dev-Login zeigen, und `/badges/issuer` muss zu `PUBLIC_BASE_URL` passen (beweist, dass die `.env` gelesen wurde). Sonst schlägt das Deployment fehl.

## Konsequenzen

- Eine fehlerhaft platzierte oder nicht lesbare Konfiguration führt zu einem sichtbaren Startfehler (HTTP 500.30, Grund im `stdout`-Log) statt zu einem offenen Administratorzugang.
- Bestehende Installationen mit `AUTH_MODE=auto` und vollständigem OIDC laufen unverändert; solche ohne OIDC müssen `AUTH_MODE=dev` ausdrücklich setzen (nur für lokale Tests) oder OIDC konfigurieren.
- Tests und Docker-Rauchtest setzen `AUTH_MODE=dev` bereits ausdrücklich.

## Leitplanken für Änderungen

- Kein Pfad darf aus fehlender Konfiguration einen Zugang ohne Anmeldung machen; neue Betriebsmodi brauchen eine ausdrückliche Einstellung.
- Tests: `AuthModeTests` (Production ohne OIDC → Abbruch, Tippfehler → Abbruch, Development → Dev, `ENV_FILE` fehlt → Abbruch).
