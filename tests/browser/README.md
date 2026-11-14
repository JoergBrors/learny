# Optionale Browserprüfungen (Co-Author-Modus, ADR 0052/0053)

Skriptgesteuerte Ende-zu-Ende-Prüfungen der Blazor-Oberfläche mit **zwei getrennten Sitzungen** (Eigentümer und Co-Autor). Sie sind **kein Teil** der regulären Integrationstestsuite
(`dotnet run --project tests/LearnCards.IntegrationTests`) und werden dort nie ausgeführt; die Suite benötigt keine Browserinstallation.

## Voraussetzungen

- **Node.js ≥ 22** (globales `WebSocket`; keine npm-Pakete nötig) und ein installierter **Chrome/Chromium** (`CHROME_PATH` setzen, falls er nicht an einem Standardort liegt). Chrome wird headless über das DevTools-Protokoll gesteuert.
- Eine **lokale Entwicklungsinstanz** mit **Wegwerf-Datenbank**, gestartet mit `AUTH_MODE=dev` (Dev-Login `/auth/login?devUser=<name>`, siehe [docs/local-development.md](../../docs/local-development.md)). Der Dev-Login ist nach ADR 0026 außerhalb von Entwicklung nie aktiv — gegen eine produktive Instanz (OIDC) bricht jedes Skript beim Login ab. **Nie gegen produktive Daten ausführen**: die Skripte legen Benutzer (`ca-*`, `mc-*`), Module, Karten und Themen an.
- Zwei Sitzungen entstehen über zwei Hostnamen derselben Instanz (Cookies sind je Host getrennt): Eigentümer `http://localhost:<port>`, Co-Autor `http://127.0.0.1:<port>`.
- Keine Secrets, keine absoluten Pfade im Repository; alles über Umgebungsvariablen.

## Start

```bash
# 1. Instanz starten (Beispiel, eigene Wegwerf-Datenbank; Ausgabeordner anpassen, wenn bin/Debug gesperrt ist)
AUTH_MODE=dev SQLITE_PATH=/tmp/learny-browser.db ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 \
  dotnet run --project src/LearnCards.Web --no-launch-profile

# 2. Prüfungen (in einem zweiten Terminal)
LEARNY_BROWSER_TEST=1 node tests/browser/co-author-topic.mjs
LEARNY_BROWSER_TEST=1 node tests/browser/co-author-module-cards.mjs
LEARNY_BROWSER_TEST=1 node tests/browser/module-collaboration.mjs
```

PowerShell: `$env:LEARNY_BROWSER_TEST = '1'; node tests/browser/co-author-topic.mjs`. Weitere Variablen: `LEARNY_URL_A` / `LEARNY_URL_B` (Standard `http://localhost:5199` / `http://127.0.0.1:5199`),
`LEARNY_CDP_PORT` (Standard 9333), `CHROME_PATH`. Exitcode 0 = alle Schritte bestanden, 1 = mindestens ein Schritt fehlgeschlagen oder Skriptfehler, 2 = Voraussetzung fehlt.

## Skripte

| Skript | Prüft |
|---|---|
| `co-author-topic.mjs` | Themeneditor: Co-Author hinzufügen/entfernen/beenden mit Sicherheitsabfragen, Reiter „Gemeinsam bearbeitet“, Kennzeichnung, verbotene Aktionen, Revisionskonflikt |
| `co-author-module-cards.mjs` | Modul-/Kartenbearbeitung als Co-Autor, Eigentümergrenzen, Mehrfachzuordnung, `updated_at`-Konflikt, Quiz-Einstellungen, Entzug in offener Sitzung (Meldung **und** Datenzustand) |
| `module-collaboration.mjs` | Direkte Modulfreigabe, „Zugriff über Themen“, Reiter „Gemeinsam bearbeitet“ (Module), nachträglich zugeordnetes Modul, Mehrfachzuordnung, Themenentzug, Mitarbeit beenden |

`lib.mjs` enthält die gemeinsamen Hilfen (Chrome-Start, DevTools-Verbindung, Klicken/Tippen, API-Aufrufe mit der Cookie-Sitzung). Chrome-Profil und Prozess werden am Ende entfernt.
