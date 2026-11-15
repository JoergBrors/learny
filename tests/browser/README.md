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
| `avatar-image.mjs` | KI-Bildavatar (ADR 0056) gegen den lokalen **Bild-Stub** `stub-image-api.mjs` (`node tests/browser/stub-image-api.mjs`; Instanz mit `OPENAI_API_KEY=stub-key OPENAI_BASE_URL=http://127.0.0.1:5298/v1`): Stil/Quelle, Vorschläge, Fehlerfall, Foto + Einwilligung, Übernehmen, Beständigkeit nach erneuter Anmeldung, Fremdzugriff, Mobil/Dark Mode (Screenshots mit `SHOT_DIR`). Prüft **nicht** die Qualität eines echten Bildmodells; ein dritter Hostname-Benutzer nutzt `LEARNY_URL_B`. |
| `avatar-gallery.mjs` | Persönliche Avatar-Galerie (ADR 0057) gegen den Bild-Stub (siehe `avatar-image.mjs`): Varianten speichern, Gruppen, Verwenden/Favorit/Umbenennen/Löschen mit Bestätigung, ZIP-Export (mit/ohne Passwort, 7-Zip-Test falls vorhanden), Import in ein zweites Konto (falsches Passwort, Vorschau, Duplikate), Isolation, Mobil/Dark Mode. |
| `avatar-moods.mjs` | Fünf Stimmungsbilder je KI-Figur (ADR 0058) gegen den Bild-Stub: Bestätigungsdialog (Kosten/Anzahl), Erzeugen nur nach Bestätigung, Figur mit fünf Kacheln, Stimmungswechsel im Profil ohne KI-Aufruf, Hinweis bei fehlender Stimmung, gezieltes Ergänzen, Export (AES-256, 7-Zip) und Reimport, Mobil/Dark Mode. Prüft nicht die Qualität eines echten Modells. |
| `avatar-variants.mjs` | Vorschlags-Galerie des Generators (Auswahl, Übernehmen, Verwerfen) im KI-Bild- und im SVG-Modus. |
| `co-author-versions.mjs` | Modulversionen durch Co-Autoren (ADR 0059): Veröffentlichen als direkter/Themen-Co-Autor mit Bestätigung, Sperre bei ungespeicherter Karte, schreibgeschützte Versionsseite, Konflikt, Entzug, Themen bleiben auf ihrer Version. |
| `perf-baseline.mjs` | Performance-Baseline (Navigationsdauer, lange Tasks, CLS, DOM, Transport); siehe docs/reviews/2026-11-15-performance-browser-pwa.md. |
| `perf-server.mjs` | Server-Phasen (nur Development): `Server-Timing` je SSR-Seite (total, Datenbankdauer, Anzahl Abfragen), mindestens 5 Läufe, Median/Min–Max. Zeigt N+1-Muster; siehe docs/reviews/2026-11-15-performance-browser-pwa.md. |
| `lighthouse.mjs` | Lighthouse (mobil + Desktop, mehrere Läufe, Median) gegen die angemeldete Dev-Instanz; Lighthouse-CLI separat installieren (`LIGHTHOUSE_CLI`, `LH_OUT`, `LH_PATH`). Anmeldung im selben Chrome (Cookie-Header erreichen den WebSocket nicht). |
| `perf-cache.mjs` | Ressourcen/Cache öffentlicher Seiten (ohne Anmeldung, Standard Produktion): Erstbesuch gegen Wiederholung/Reload, Requests, übertragene Bytes, Protokoll, mehrere Zyklen (Median). Nicht gegen Development (kein langes Caching). |
| `lighthouse-attach.mjs` | Lighthouse gegen einen bereits **von Hand angemeldeten** eigenen Chrome (z. B. Produktion mit OIDC), 5 Läufe mobil/Desktop, Median/Streuung; Anleitung im Abschnitt „Authentifizierte Seiten messen“. |
| `a11y-seo.mjs` | Meta-Beschreibung, sichtbarer Text im zugänglichen Namen (Sidebar, Darstellung, Kartenbuttons) und Kontrast (>= 4,5:1) in Hell/Dunkel. |
| `pwa-nav.mjs` | PWA-Navigation (`pwa.js`): Ladebalken beim Menüklick, Notausgang bei blockierter Blazor-Navigation. Herunterziehen (nur Touch/Standalone) ist hier nicht prüfbar. |
| `module-list-updates.mjs` | Modulliste: Gruppen ein-/ausklappen, Markierung „neuere Version“, Overlay mit Kartenvergleich und bestätigte Übernahme in den Themenentwurf; „Entwurf mit Änderungen“ vergleichen und als Version veröffentlichen. |
| `module-collaboration.mjs` | Direkte Modulfreigabe, „Zugriff über Themen“, Reiter „Gemeinsam bearbeitet“ (Module), nachträglich zugeordnetes Modul, Mehrfachzuordnung, Themenentzug, Mitarbeit beenden |

`lib.mjs` enthält die gemeinsamen Hilfen (Chrome-Start, DevTools-Verbindung, Klicken/Tippen, API-Aufrufe mit der Cookie-Sitzung). Chrome-Profil und Prozess werden am Ende entfernt.

## Authentifizierte Seiten messen (Lighthouse am eigenen, angemeldeten Chrome)

`lighthouse-attach.mjs` misst Seiten hinter einer Anmeldung (z. B. Produktion mit OIDC, wo es keinen Dev-Login gibt). Das Skript meldet **nichts selbst an** und kennt weder Zugangsdaten noch Cookies: Sie melden sich **von Hand** in einem separaten Chrome-Profil an, Lighthouse hängt sich per lokalem DevTools-Port an diesen Chrome und nutzt dessen Sitzung (damit ist auch der Blazor-WebSocket authentifiziert; ein Cookie per `--extra-headers` erreicht den WebSocket nicht und erzeugt einen künstlichen Circuit-Fehler).

**Voraussetzungen:** Node.js >= 22, Chrome, Lighthouse-CLI **außerhalb des Repositories** (keine Projektabhängigkeit, ADR 0002).

```powershell
# 1. Lighthouse einmalig in einem Ordner AUSSERHALB des Repositories installieren
mkdir $env:USERPROFILE\lh; cd $env:USERPROFILE\lh; npm init -y; npm install lighthouse

# 2. Chrome mit separatem, frischem Profil ohne Erweiterungen starten; der Debug-Port ist nur über 127.0.0.1 erreichbar
& "C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-address=127.0.0.1 --remote-debugging-port=9333 --user-data-dir="$env:TEMP\lh-profil" --disable-extensions --no-first-run https://learny.brors.date
#    In DIESEM Fenster von Hand anmelden und es offen lassen.

# 3. Im Repository-Ordner messen
$env:LIGHTHOUSE_CLI = "$env:USERPROFILE\lh\node_modules\lighthouse\cli\index.js"
$env:LH_OUT   = "$env:TEMP\lh-ergebnis"
$env:LH_PATHS = "/topics,/dashboard,/,/topics/<Themen-ID>"
node tests/browser/lighthouse-attach.mjs
```

| Variable | Bedeutung |
|---|---|
| `LIGHTHOUSE_CLI` (Pflicht) | Pfad zu `lighthouse\cli\index.js` der Installation |
| `LH_OUT` (Pflicht) | Ordner für die Roh-JSON; **muss außerhalb des Repositories liegen** (sonst Abbruch) |
| `LH_PATHS` (Pflicht) | kommagetrennte Pfade, z. B. `/topics,/dashboard,/` |
| `LH_BASE` | Basisadresse (Standard `https://learny.brors.date`) |
| `LH_PORT` | DevTools-Port (Standard 9333, nur lokal) |
| `LH_RUNS` | Läufe je Seite und Profil (Standard 5) |

**Auswertung:** je Seite und Profil (mobil, Desktop) werden alle Läufe ausgeführt und **Median mit Minimum–Maximum** ausgegeben (Performance, Accessibility, Best Practices, SEO, FCP, LCP, TBT, CLS, Lighthouse-Version), dazu die auffälligen Audits mit gekürzten Elementbeschreibungen. Meldet Lighthouse Browser-Erweiterungen („Chrome extensions negatively affected …“), steht `>>> ACHTUNG` und der Exit-Code ist 1: die Performance-Werte sind dann nicht verwertbar (Chrome zeigt auch mit `--disable-extensions` einige eigene Komponenten-Ziele; maßgeblich ist die Warnung von Lighthouse). Exit-Codes: 0 = alle Läufe gültig, 1 = fehlgeschlagene Läufe/keine Ergebnisse/Erweiterungen erkannt, 2 = falscher Aufruf oder Chrome nicht erreichbar.

**Datenschutz und Sicherheit**
- Die Roh-JSON-Dateien enthalten Ausschnitte der gemessenen (angemeldeten) Seiten und gehören **nie** ins Repository und nicht in Tickets oder Chats; Screenshots werden nicht erzeugt (`--skip-audits`). Weitergeben: nur die Konsolenzusammenfassung — und auch sie vorher auf Themen-/Modulnamen in den gekürzten Tags prüfen.
- Der Chrome-Debug-Port gibt volle Kontrolle über den Browser samt Anmeldung: nur an `127.0.0.1` binden (`--remote-debugging-address=127.0.0.1`), nie auf `0.0.0.0` oder im Netzwerk freigeben, nur für die Dauer der Messung offen lassen und das Messprofil danach schließen und löschen.
- Das Skript beendet keine Chrome-Prozesse (auch nicht den gestarteten); das Chrome-Fenster schließen Sie selbst. Es verwendet ausschließlich den angegebenen Port.
- Einschränkung: Gemessen wird von Ihrem Rechner aus über Ihr Netzwerk; die Anmeldung bei OIDC/Produktion ist nur so lange gültig, wie die Sitzung des Profils lebt. Installierte PWA und Suspend/Resume sind damit **nicht** messbar (dafür ist ein echtes Gerät nötig).

## Ressourcen und Cache öffentlicher Seiten messen (`perf-cache.mjs`)

Misst, wie viel ein **Erstbesuch** (leerer Browser-Cache) und ein **Wiederholungsbesuch** übertragen. Standardziel ist die Produktion, aber nur **öffentliche Seiten** (`/login`, `/verify`): Es wird nichts angemeldet, nichts geschrieben, es sind keine Zugangsdaten nötig.

**Voraussetzungen:** Node.js >= 22 und ein installierter Chrome (`CHROME_PATH`, falls er nicht an einem Standardort liegt). Es wird ein eigener, frischer Chrome ohne Erweiterungen gestartet und am Ende wieder beendet (nur dieser eigene Prozess); die Variable `LEARNY_BROWSER_TEST=1` ist wie bei allen Skripten hier Pflicht.

```powershell
$env:LEARNY_BROWSER_TEST = '1'
node tests/browser/perf-cache.mjs
# optional:
$env:PERF_BASE  = 'https://learny.brors.date'   # Basisadresse (Standard: Produktion)
$env:PERF_PATHS = '/login,/verify'              # kommagetrennte öffentliche Pfade
$env:PERF_RUNS  = '5'                           # Zyklen je Seite
```

**Ablauf je Zyklus:** Browser-Cache leeren → (1) **Erstbesuch** → (2) **Wiederholung** durch erneute Navigation → (3) **Reload**. Je Seite werden die Zyklen wiederholt; ausgegeben wird der Median mit Minimum–Maximum.

**Ausgabeformat:** eine Zeile je Seite und Besuchsart: `Requests`, `übertragen` (KB, inkl. HTML), `aus Cache x/y`, `TTFB`, Protokoll (z. B. `h3`); darunter die größten Ressourcen des Erstbesuchs.

**Interpretation**
- `Requests` = das Dokument **plus alle Resource-Timing-Einträge, einschließlich der aus dem Cache bedienten**. Ein Cache-Treffer zählt also als Request, überträgt aber 0 Bytes. Wie viele davon aus dem Cache kamen, steht in `aus Cache x/y` (y = Ressourceneinträge ohne das Dokument; x = Einträge mit 0 übertragenen Bytes und vorhandenem Inhalt).
- `übertragen` ist die Summe der `transferSize` (Header + Body über das Netz) des Dokuments und aller Ressourcen. Fingerprintete Dateien (`immutable`) erscheinen bei der Wiederholung mit 0 Bytes; was weiterhin übertragen wird, ist im Wesentlichen das HTML.
- Ein Reload kann Ressourcen revalidieren (bedingte Anfragen, `304`) und kostet daher wenige Bytes mehr als eine Navigation.

**Datenschutz:** Das Skript liest nur Resource-Timing-Kennzahlen (Adresse, Bytes, Protokoll) öffentlicher Seiten; es setzt und protokolliert keine Cookies oder Tokens, schreibt keine Dateien (außer dem temporären Chrome-Profil, das am Ende gelöscht wird) und führt keine Aktionen auf der Seite aus.

**Grenzen:** Nur öffentliche Seiten (angemeldete Seiten: siehe `lighthouse-attach.mjs`); Messung von einem Entwicklungsrechner aus (Netzwerk nicht kontrolliert, Cloudflare/h3 sitzt dazwischen); gegen eine Development-Instanz **nicht aussagekräftig** (kein langes Caching, unkomprimiert). Misst weder Renderzeiten (dafür Lighthouse) noch die WebSocket-Verbindung. Exit-Codes: 0 = Messung vollständig, 1 = Seite nicht geladen oder keine Messdaten.
