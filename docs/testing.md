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
- **Bilder in Karten (ADR 0016):** gültiges PNG importieren und unverändert auslesen; mehrere Bilder; fehlendes/leeres `images`; Ablehnung von zu vielen Bildern, ungültigem Base64, `data:`-Präfix, falschem/nicht erlaubtem MIME-Typ, Nicht-Bild-Daten, zu großen Bildern (2 MiB + 1 Byte; genau 2 MiB erlaubt), zu langem Alternativtext;
  Update-Semantik (fehlend = behalten, `[]` = entfernen, auch bei `PUT`); Stapelimport meldet Fehlergründe; Live-Schema und sein Beispiel; `demoodule/demo.json`; Freigabe/globale Kopie enthalten Bilder; MCP `add_card`, `import_cards`, `get_import_schema`
- **Bildspeicher, Proxy und SAS (ADR 0017/0018):** Komponententests ohne Server (Local-Provider: Pfadsicherheit/Atomarität/Berechtigungen; Database-Provider; Konfigurationsprüfung; Cache: LRU, Datei, TTL, Integrität; **SigV4-Testvektor** von AWS; SSRF-Adressprüfung; Redaktion von Secrets; `StorageUrlGuard`-Ablehnungen; SAS-Struktur `sp=r`/`sr=b`/TTL/Blob-Pfad; Presign-URL; Ablehnung manipulierter Speicherschlüssel und fremder Broker-URLs; keine Weiterleitungen; Größenlimit; Speicher nicht erreichbar; SharePoint gegen einen Graph-Mock inkl. ETag-Revalidierung, fremder Download-URL, Token-Wiederverwendung, 429). Ende-zu-Ende je Provider mit demselben Szenario (`ImageScenario`: Speichern, byte-identischer Abruf, ETag/304, kein Redirect, fremder Benutzer 404, ohne Anmeldung 401, Ersetzen/Entfernen/Löschen mit Aufräumen im Speicher, keine SAS-/Cloud-URL in Antworten): database, local, **Azure Blob gegen Azurite** (wird übersprungen, wenn `127.0.0.1:10000` nicht erreichbar ist; Log ohne `sig=`/Kontoschlüssel), SharePoint (Graph-Mock; 15 parallele Abrufe aus dem Cache lösen keinen Graph-/Token-Aufruf aus). Außerdem: Migration database → local (Trockenlauf, Abschnitte mit Cursor, Idempotenz, Audit, nur Admin, nicht konfigurierter Zielprovider → 400) und `IMAGE_PROXY_ENABLED=false` → 503. Nicht automatisiert prüfbar: User-Delegation-SAS gegen echtes Azure, echtes S3, echtes Graph.
- **Quiz-Engine (ADR 0019):** Standardkonfiguration und Validierung (ungültige Kombinationen → 400); Pool als Projektion von `quiz[]` (Quelle der Wahrheit, Aktualisierung, Duplikate); Kartenquiz und Modulquiz über dieselbe Bewertung; Single Choice, Multiple Response (exakte Menge), Freitext;
  Sitzung ohne Lösungen im Start, einmalige Abgabe (409), fremde Sitzung (404); Multiple Choice aus, nur Auswahl (Fehlermeldung statt stillem Wechsel), Umfang, Bereich, Bestehensgrenze, Lösungen verbergen, Wiederholung sperren, Quiz deaktivieren; Deaktivieren einzelner Fragen (Ergebnisse bleiben);
  Rechte (fremd 404, Freigabeempfänger 403 auf Konfiguration/Pool, darf Quiz durchführen), globale Version friert Konfiguration/Fragen ein (auch Admin 403); alter Ablauf ohne Sitzung; Ergebnisse speichern Sitzung/bestanden/Bestehensgrenze/Modulversion; Startmigration von `quiz[]` in den Pool (Neustart, idempotent).
  Mit **OpenAI-Mock** (`MockOpenAi`, `OPENAI_BASE_URL`): Generierung inkl. Verwerfen ungültiger Fragen, Speichern und Wiederverwenden, Strategien `none`/`on_demand`/`prefer_stored`/`mix`, Herkunft `fixed`/`ai`, flüchtige Fragen für Lernende (Pool unverändert), Freitext-Bewertung per KI, KI-Fehler → Fallback. Nicht automatisiert prüfbar: echte OpenAI-Antworten.
- **Editor-Schnittstellen, Beispiele, Templates, Schema, MCP, Hilfe (ADR 0019):** Fragen anlegen/ändern/zuordnen/(de)aktivieren (Rechte, Validierung, Duplikate, `card_json`-Fragen nur über die Karte, Editor-Fragen überleben das Speichern der Karte);
  Sitzungsablauf (410) und Bereinigung (24 h, Ergebnisse bleiben, aktive Sitzung läuft nach Neustart weiter); **alle** Dateien unter `wwwroot/templates/` und `demoodule/` sowie das Beispiel des Live-Schemas: nur Schemafelder, gültige Quiz-/Bildstruktur (Indizes, Optionenzahl, Limits), echter Import und unveränderte Quizfragen;
  Altformat ohne `quiz` bzw. ohne `type`; Live-Schema beschreibt alle drei Fragetypen; MCP-Eingabeschema und Tool-Texte stimmen mit dem Schema überein (`add_card` mit `multiple_response`/`free_text`); Hilfeseite verlinkt genau die ausgelieferten Templates, die Downloads liefern den Dateiinhalt, Pflichtbegriffe sind vorhanden, keine späteren Funktionen.
- **Zertifikate, Merkle, Kostenschranke, Verankerung (ADR 0021/0022, `BlockchainUnitTests`, ohne Netzwerk, mit steuerbarer Fake-Kette):** Merkle (1–100 Blätter, Determinismus, Manipulation, Domänentrennung); Konfiguration (Standardwerte, Limit > 0,001 EUR ungültig, Mainnet nie implizit); Kostenschranke (günstig erlaubt, > 0,001 EUR gesperrt, Batch-Verteilung, veralteter/fehlender Kurs, unbekanntes Gas, Sicherheitsaufschlag, Obergrenze technisch in der Transaktion);
  Badges (Struktur, Signatur, Manipulation, `alg: none`, fremder Aussteller, Idempotenz, Widerruf, Datenschutz); Pipeline (Warten, Bündelung in einer Transaktion, Finalität, keine Doppelverankerung auch parallel, Neustart, Widerruf vor/nach Verankerung, Netzwerkwechsel, Blockchain aus); **Mainnet: bei Kosten über 0,001 EUR je Nachweis wird auch nach 6 Wiederholungen nichts gesendet; unbekannte Kosten, fehlender/veralteter Kurs, falsche Chain-ID, kein Guthaben, keine Freigabe und fehlender Signierer senden nie**; Fehlerfälle (RPC, verworfene Transaktion, Status 0, Faucet leer); Auditor (nur globale Module), Administrator (keine privaten Inhalte).
  Nicht automatisiert: echtes Senden an Amoy/Mainnet (Signierlösung offen), die Oberfläche der neuen Seiten.
- **Karteneditor und Rich-Text (ADR 0020):** Komponententests `RichText` (Format, Maskierung, Klartext-Kompatibilität, XSS-Angriffe: Skript, img/onerror, SVG, iframe, style, Event-Handler, `javascript:`/`data:`/`//`/`ftp:`/`mailto:`/relative/`file:`-Links, Zugangsdaten und Anführungszeichen in URLs, Markup in URLs, Platzhalterzeichen, pathologische Eingaben, Grenzen);
  Server: Textgrenzen bei POST/PUT, Markup unverändert gespeichert, Lernansicht gibt Angriffe nur als Text aus, Bilder behalten/Metadaten/Reihenfolge/Limit/fremde `image_id`, Duplizieren (Bilder, quiz[], Editor-Fragen ohne Duplikate), Sortieren, Archivieren/Wiederherstellen, Konflikterkennung (`X-Card-Updated-At`, 409),
  Rechte (Empfänger, Fremde, Administratoren, globale Versionen), Editor-Seite nur für Eigentümer, Vorlage mit Formatierung, `rich_text_rules` im Live-Schema, MCP-Beschreibungen.
- **Editor-Browserprüfung:** `node tests/browser/editor-browser-check.js <ausgabeordner> <LearnCards.Web.dll> <Web-Projektordner>` (Node 22+, Chrome/Edge): neue Karte, Formatierung (fett, Liste, Link inkl. Ablehnung von `javascript:`), Vorschau-Overlay (live, verschiebbar, Position nach Schließen/Öffnen erhalten, Zurücksetzen, mobil unten), Bild-Upload mit Alternativtext/Unterschrift, feste Frage (Multiple Response), Speichern, Pool-Frage (Editor), Aktivieren/Deaktivieren,
  Duplizieren, Archivieren, Sortieren, ungespeicherte Änderungen (Kartenwechsel, Verlassen der Seite, beforeunload), Konflikt, Sicherheit (bösartige Inhalte), Mobil 390 px, dunkler Modus, JavaScript-Konsole.
- **Browserprüfung (manuell/automatisierbar):** `node tests/browser/quiz-browser-check.js <ausgabeordner> <LearnCards.Web.dll> <Web-Projektordner>` (Node 22+, Chrome oder Edge, nur Bordmittel) startet die Anwendung im Dev-Modus mit SQLite, legt Testdaten an und prüft per Chrome-DevTools-Protokoll: Konfiguration (Desktop, Mobil 390 px, hell/dunkel, Auto-Deaktivierung, Fehlermeldung, Speichern),
  Single Choice, Multiple Response und Freitext im Modul- und Kartenquiz, Wiederholung, deaktiviertes Quiz, JavaScript-Fehler; legt Screenshots ab.
- **Freigaben und globale Module (ADR 0015, Abläufe 1–11):** privat bleibt privat; Freigabe (nur Eigentümer, nur aktive Empfänger, kein Selbstfreigeben); Empfänger lesen/lernen/quizzen, ändern aber nichts; Ergebnisse gehören dem Lernenden; Einreichung/Vorschau/Veröffentlichung/Ablehnung nur durch Administratoren;
  globale Version ist stabil gegenüber Änderungen am Original, Aktualisierung = neue Version, Rücknahme/Reaktivierung; globale Ergebnisse für Administrator und Auditor (aggregiert und minimiert, protokolliert), nicht für andere Benutzer; Widerruf erhält Ergebnisse; Löschschutz (409);
  MCP sieht nur Erlaubtes und kann nichts verwalten, auch nicht mit Administrator-Token; manipulierte IDs → 404
- MCP-Zugänge: persönliche Tokens (nur eigene Daten, nur Daten-Routen, Widerruf, ungültige/fremde Tokens), Zustimmung (nur erlaubte Rückleitung, einmaliger Code, nur der Dienstschlüssel tauscht)
- **OAuth Ende-zu-Ende mit echtem MCP-Server:** Registrierung (unsichere Redirects abgelehnt), Authorize → Learny-Zustimmung → Callback (Login-CSRF-Schutz) → Token (PKCE, Einmal-Code),
  Tool-Aufrufe `whoami`/`list_modules`/`list_cards`/`add_card`/`archive_card` pro Benutzer getrennt, 401 nach Widerruf bzw. Sperre
- IdP-Rollen: `Admin.All` im simulierten `roles`-Claim vergibt `admin`, fehlt er später wird die Rolle entzogen, nicht abgebildete Werte werden ignoriert, manuelle Rollen bleiben

Exitcode 0 = alle Prüfungen bestanden (aktuell 273 Prüfungen; auf PostgreSQL schlägt bis zur Korrektur von Prüfbericht F4 genau eine fehl). Bei Fehlern wird das Server-Log ausgegeben.

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
