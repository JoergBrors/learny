# Tests

Die Tests kommen ohne externe NuGet-Pakete aus ([ADR 0002](adr/0002-keine-nuget-pakete.md)). Es gibt eine Konsolenanwendung, die
`LearnCards.Web` als Prozess mit temporärer Datenbank startet und über HTTP prüft.

## Integrationstests (Mehrbenutzer, Rollen, Datenisolation)

```bash
dotnet build LearnCards.slnx
dotnet run --project tests/LearnCards.IntegrationTests --no-build
```

Ablauf: Der Test startet zusätzlich den MCP-Server (HTTP, OAuth aktiv). Pfade der DLLs lassen sich mit `TEST_WEB_DLL`/`TEST_MCP_DLL` überschreiben (z. B. wenn die Standard-Ausgabe von einem laufenden Debug-Prozess gesperrt ist).
Die Web-App startet auf einem freien Port (Dev-Auth, temporäre SQLite-Datei, temporäre Data-Protection-Schlüssel),
meldet vier Konstellationen an (Administrator `local-dev-user`, Benutzer `alice`, Benutzer `bob`, Rollenvergabe an Alice als Auditor)
und prüft u. a.:

- ohne Login 401; jede Identität hat eine eigene interne ID
- Bob sieht, ändert, archiviert, löscht, quizzt oder chattet nicht mit Alices Inhalten (jeweils 404/leer)
- manipulierte IDs und SQL-Fragmente in Parametern bleiben wirkungslos
- gleichnamige Module pro Benutzer; Import mit fremder Karten-ID überschreibt nichts
- Lernergebnisse sind pro Benutzer getrennt und nicht fremd löschbar
- Rollen: Selbst-Erhöhung unmöglich, Auditor ohne Adminrechte, letzter Admin geschützt, Audit-Log
- `X-MCP-Key` ohne verknüpften Benutzer sieht keine Daten; mitgesendete Benutzer-IDs ändern nichts; Admin-API über den Schlüssel gesperrt
- gesperrte Benutzer verlieren sofort den Zugang
- MCP-Zugänge: persönliche Tokens (nur eigene Daten, nur Daten-Routen, Widerruf, ungültige/fremde Tokens), Zustimmung (nur erlaubte Rückleitung, einmaliger Code, nur der Dienstschlüssel tauscht)
- **OAuth Ende-zu-Ende mit echtem MCP-Server:** Registrierung (unsichere Redirects abgelehnt), Authorize → Learny-Zustimmung → Callback (Login-CSRF-Schutz) → Token (PKCE, Einmal-Code),
  Tool-Aufrufe `whoami`/`list_modules`/`list_cards`/`add_card`/`archive_card` pro Benutzer getrennt, 401 nach Widerruf bzw. Sperre
- IdP-Rollen: `Admin.All` im simulierten `roles`-Claim vergibt `admin`, fehlt er später wird die Rolle entzogen, nicht abgebildete Werte werden ignoriert, manuelle Rollen bleiben

Exitcode 0 = alle Prüfungen bestanden. Bei Fehlern wird das Server-Log ausgegeben.

### Gegen PostgreSQL

```bash
docker run -d --rm --name lc-it-pg -p 55432:5432 -e POSTGRES_PASSWORD=itpass -e POSTGRES_USER=learncards -e POSTGRES_DB=learncards_it postgres:16-alpine

TEST_DB_PROVIDER=postgres POSTGRES_HOST=127.0.0.1 POSTGRES_PORT=55432 POSTGRES_USER=learncards \
POSTGRES_PASSWORD=itpass POSTGRES_DB=learncards_it POSTGRES_SSLMODE=disable \
dotnet run --project tests/LearnCards.IntegrationTests --no-build

docker stop lc-it-pg
```

Der Test legt Benutzer und Module an; **nie** gegen eine produktive Datenbank ausführen. In PowerShell Variablen mit `$env:NAME = "..."` setzen.

## Manuelle Migrationsprüfung (Altdatenbank)

1. Vorversion (vor ADR 0011) auschecken (`git worktree add <pfad> <commit>`), bauen, mit SQLite bzw. PostgreSQL starten, per Dev-Login Karten
   importieren, Kartenstatus/Theme setzen, ein Quiz abgeben.
2. Neue Version auf **derselben** Datenbank starten (erste Anmeldung als Dev-Admin).
3. Erwartung: `/api/me` zeigt 0 Module; `/api/admin/unowned-modules` listet das Altmodul; nach `POST /api/admin/modules/{id}/owner` sind Karten,
   Kartenstatus, Theme und Quiz-Verlauf wieder sichtbar; zweiter Start der Anwendung verändert nichts.
4. Bei SQLite: Windows-Hinweis unten.

## Hinweis Windows / SQLite

Die Anwendung lädt unter Windows `winsqlite3.dll` (Systembibliothek) vor `sqlite3.dll`. Liegt ein fremdes, inkompatibles `sqlite3.dll` im `PATH`
(z. B. von der AWS CLI), hätte die frühere Reihenfolge einen Absturz (0xC0000005) beim Datenbankstart verursacht.

## Nicht automatisiert

- Blazor-Oberflächen: Die Seiten `/`, `/profile`, `/admin`, `/module/{id}` wurden nur per HTTP-Rendering geprüft (Seite lädt, Inhalte vorhanden, Nicht-Admin wird von `/admin` umgeleitet).
  Interaktive Aktionen (Anzeigename speichern, Rollen-Buttons, Zuweisen) sind nicht automatisiert; manuell prüfen: `dotnet run` in `src/LearnCards.Web`, Login, `/profile`, `/admin`.
- OIDC-Login gegen Entra ID (nur Dev-Login wird getestet).
- Last-Tests und Zwei-Browser-Sitzungen mit der interaktiven Oberfläche (siehe Anleitung im [Prüfbericht](reviews/2026-10-24-mehrbenutzer-pruefbericht.md)); die Prüfbericht-Checks (persönliche Daten, Gleichzeitigkeit, Logout/Benutzerwechsel, Schlüssel-Matrix) laufen im Abschnitt „Prüfbericht“ der Integrationstests.
- Bekannt fehlschlagend auf PostgreSQL: paralleler Erstzugriff auf den Lernstatus (HTTP 500, Prüfbericht F4) — bis zur Korrektur ist das ein erwarteter Fehler.
