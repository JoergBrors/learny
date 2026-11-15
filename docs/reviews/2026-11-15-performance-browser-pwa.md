# Performancebericht Browser/PWA (15.11.2026)

Stand: Zwischenbericht. Untersucht wurde der Auftrag „Browser- und PWA-Performance“. Die Ursache der langsamen Navigation ist belegt (Transport), die Maßnahme dafür ist Infrastruktur und noch nicht umgesetzt. Die Optimierung von Rendering/Layout ist **nicht** begonnen, weil die Messung dort keinen Engpass zeigt (siehe unten).

## Ausgangswerte (Trace vom 15.11.2026, Angabe des Auftrags)

120 Requests, davon 112 an `/_blazor` (47 GET, 65 POST); längster `/_blazor`-GET 4.335 ms; `GET /dashboard` 2.057 ms; längster Main-Thread-Task 133,6 ms; 7 Tasks > 50 ms; Layout bis 47,3 ms; langsamste Interaktion 333 ms; keine Service-Worker-Requests.

## Bestätigte Ursache: Produktion bietet kein WebSocket an

Nachweis am 15.11.2026 gegen `https://learny.brors.date`:

```
POST /_blazor/negotiate?negotiateVersion=1
→ availableTransports: ServerSentEvents, LongPolling      (kein WebSockets)
```

Die lokale Entwicklungsinstanz (Kestrel) bietet `WebSockets, ServerSentEvents, LongPolling` an. Die Antwort kommt von der Anwendung selbst; der Server lässt WebSockets also nicht zu. Das erklärt das Muster des Traces (viele `/_blazor`-GET/POST statt einer dauerhaften Verbindung): Jede Interaktion und jede Navigation kostet mehrere HTTP-Roundtrips über Cloudflare, dazu Wartezeiten der Long-Poll-Anfragen (bis 4,3 s).

Wahrscheinliche Ursache (im Repository belegt, am Server **nicht** geprüft): `.github/workflows/deploy-iis-on-tag.yml` installiert nur `Web-Server, Web-Mgmt-Tools, Web-Scripting-Tools` (und `Web-Filtering`), nicht das IIS-Feature **`Web-WebSockets`** („WebSocket-Protokoll“); `web.config` enthält keine WebSocket-Angabe. Ob das Feature auf dem Server tatsächlich fehlt und ob Cloudflare WebSockets für die Domain zulässt (Standard: ja), ist offen.

Ein erzwungener WebSocket-Transport im Code ist **keine** Lösung (ohne Serverfeature bricht die Verbindung ab) und wurde nicht eingebaut.

### Maßnahme (Workflow vorbereitet, nicht committet/deployt)

1. Auf dem IIS-Server `Install-WindowsFeature Web-WebSockets` (kein Neustart nötig, Anwendungspool recyceln).
2. Im Deploy-Workflow `Web-WebSockets` zu den installierten Features ergänzen und im Schritt „Verify“ den Transport prüfen (`negotiate` muss `WebSockets` enthalten).
3. In Cloudflare prüfen: Netzwerk → WebSockets = an.
4. Danach erneut messen (Reproduktion unten) und `availableTransports` kontrollieren.

## Lokale Messung (Entwicklungsinstanz, Desktop 1300×1000, Median aus 3 Läufen)

Skript: `tests/browser/perf-baseline.mjs` (25 Module à 12 Karten, 4 Themen; kalt = harte Navigation, warm = Menüklick in laufender Sitzung).

| Seite | kalt ms | warm ms | Tasks > 50 ms | max. Task ms | CLS | DOM-Knoten |
|---|---|---|---|---|---|---|
| Dashboard | 1331 | 66 | 1 | 88 | 0 | 202 |
| Home (Module) | 1338 | 62 | 0 | 0 | 0 | 550 |
| Themen | 1327 | 61 | 0 | 0 | 0,001 | 156 |
| Abschlüsse | 1329 | 62 | 0 | 0 | 0,001 | 141 |

(„kalt“ enthält ca. 1,2 s feste Wartezeit des Skripts.) Lokal ist die Oberfläche schnell: Warmnavigation ≈ 60 ms, praktisch keine langen Tasks, kein relevanter Layout Shift, kleine DOM-Bäume. **Rendering, Layout und Komponentenbäume sind nach dieser Messung nicht der Engpass**; die im Trace gesehenen 133 ms/47 ms/4.825 dirty objects wurden lokal nicht reproduziert. Daher wurden dort bewusst keine Änderungen (ShouldRender, Virtualisierung, `@key`) vorgenommen — Messung vor Optimierung.

## Nicht gemessen / offen

- **Browser vs. installierte PWA, Suspend/Resume:** nicht durchführbar in dieser Umgebung (kein iOS/Android-Gerät, headless Chrome). Vorhandene Wiederverbindungslogik (`ReconnectModal.razor.js`) unverändert.
- **Produktionsmessung:** ohne Anmeldung (OIDC) nicht möglich; Trace-Werte stammen aus dem Auftrag. Ob Server, Datenbank oder Netzwerk die 2 s von `GET /dashboard` verursachen, ist offen. Verdacht (ungeprüft): Vorrendern führt `OnInitializedAsync` je Seite doppelt aus (Prerender + Sitzung); Änderung betrifft alle Seiten und braucht eine eigene Entscheidung.
- **Service Worker/Cache:** Es existiert keiner (nur `manifest.webmanifest`, siehe `docs/architecture.md`). Statische Dateien sind über `MapStaticAssets`/`@Assets` fingerprinted; eine neue Cache-Schicht wurde nicht eingeführt (Auftrag verbietet unkontrolliertes Cachen von HTML/API; Nutzen ohne Messung unbelegt).
- **Bilder/Avatare:** nicht untersucht.
- **Development-Diagnostik (Punkt 8):** nicht umgesetzt.

## Bereits umgesetzt (vor diesem Auftrag, v0.1.32)

`wwwroot/pwa.js`: Ladebalken beim Menüklick, Notausgang (harte Navigation nach 4 s ohne Adresswechsel), Herunterziehen zum Aktualisieren in der installierten App. Gegen langsame Long-Polling-Navigation ist der Notausgang nur ein Pflaster.

## Reproduktion

```bash
AUTH_MODE=dev SQLITE_PATH=/tmp/perf.db ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 dotnet run --project src/LearnCards.Web --no-launch-profile
LEARNY_BROWSER_TEST=1 [PERF_RUNS=3] [PERF_MOBILE=1] node tests/browser/perf-baseline.mjs
# Produktion: Transport prüfen (ohne Anmeldung möglich)
curl -s -X POST "https://<host>/_blazor/negotiate?negotiateVersion=1" -H "Content-Length: 0"   # availableTransports muss WebSockets enthalten
```

## Nachtrag: Workflow für Web-WebSockets (vorbereitet)

- `.github/workflows/deploy-iis-on-tag.yml`: Diagnose listet `Web-WebSockets`; „Ensure IIS…“ installiert es idempotent ohne Neustart (Neustartbedarf wird als Warnung gemeldet); neuer Schritt „Verify WebSocket Transport“ (Feature, Negotiate, echter WebSocket-Handshake; optional öffentlicher Pfad über `IIS_PUBLIC_URL`; strikt mit `IIS_REQUIRE_WEBSOCKETS=true`). Details: [iis-deployment.md](../iis-deployment.md).
- **Lokal geprüft** (Prüfskript des Schritts mit `pwsh` gegen die Dev-Instanz): Origin: Transporte `WebSockets, ServerSentEvents, LongPolling`, Handshake erfolgreich (State=Open), Ergebnis „bestanden“. Gegen `https://learny.brors.date` als öffentlicher Pfad: Negotiate bietet WebSockets nicht an → Warnung (mit `IIS_REQUIRE_WEBSOCKETS=true` Fehler) — der Schritt erkennt den Produktionsbefund also.
- **Nicht lokal prüfbar:** der Installationsteil (`Install-WindowsFeature`, Windows-Server-Runner), der Origin-Handshake gegen IIS, Cloudflare-Pfad, Browser-Nachmessung (Transport, `/_blazor`-Anzahl, Navigation) und PWA/Suspend-Resume. Diese Punkte bleiben nach freigegebenem Deployment zu prüfen: `perf-baseline.mjs` gegen die Produktion ist ohne Anmeldung nicht möglich; stattdessen Chrome-Trace wie im Ausgangsbefund wiederholen und `availableTransports` sowie die Anzahl der `/_blazor`-Requests vergleichen.
- **Risiken:** Ein WebSocket-Handshake am Origin beweist nicht, dass der öffentliche Pfad (Cloudflare) ihn durchlässt — daher `IIS_PUBLIC_URL`. `Install-WindowsFeature` braucht Administratorrechte des Runners; auf einem Client-Betriebssystem ist es nicht verfügbar (Warnung statt Abbruch).

## Nachtrag 2: Langsame Menüwechsel Dashboard/Themen (Server-Phasen, N+1-Abfragen)

**Trace 15.11.2026 17:54 (Auftrag):** Dashboard 2.014 ms (TTFB ≈ 1.996 ms), Themenübersicht ≈ 1.023 ms, Modul-/Themendetail 105–214 ms. WebSockets funktionieren inzwischen.

**Bestätigte Ursache: viele einzelne Datenbankabfragen (N+1) bei der serverseitigen Datenbeschaffung**, nicht Rendering oder Transport. Messung mit dem neuen Development-Server-Timing (`Data/TimingDatabase.cs`, Header `Server-Timing: total, db;desc="N queries"`, nur in Development, ohne SQL-Text/Parameter) und `tests/browser/perf-server.mjs` (Benutzer mit 12 Themen à 4 Modulen, 5 Läufe, Median; SQLite im Prozess, daher kleine Millisekundenwerte — maßgeblich ist die **Abfrageanzahl**):

| Seite | Abfragen vorher | Abfragen nachher | total ms vorher → nachher (lokal, SQLite) |
|---|---|---|---|
| `/dashboard` | 1.392 | **39** | 121 → 6 |
| `/topics` | 684 | **384** | 48 → 28 |
| `/` (Module) | 698 | 398 | 40 → 34 |
| `/achievements` | 6 | 6 | 2 → 3 |

Plausibilitätsprüfung (Annahme, nicht gemessen): Bei einem Datenbankserver mit rund 1,4–1,5 ms je Abfrage ergeben 1.392 bzw. 684 Abfragen ziemlich genau die gemessenen ≈ 2,0 s bzw. ≈ 1,0 s. Die Abfragezahlen der Produktionsdaten sind nicht bekannt (Datenmenge und Datenbank-Latenz des Traces unbekannt); die Größenordnung passt zum Muster „Dashboard ≈ doppelt so teuer wie Themenübersicht“.

**Gefundene Einzelursachen**
1. **Dashboard lud die Themenübersicht zweimal:** `DashboardService.LoadAsync` und danach `LandingService.ResolveAsync` riefen beide `TopicService.ListOverviewAsync`. → Die Übersicht wird einmal geladen und an `ResolveAsync` übergeben (optionaler Parameter).
2. **Dashboard berechnete die Statistik aller eigenen Themen**, obwohl es nur Zugewiesenes/Pflicht anzeigt. → `ListOverviewAsync(…, assignedOnly: true)` lässt „Eigene“ und „Gemeinsam bearbeitet“ aus; Zugewiesen, geteilt, global und Pflicht (inkl. eigener/globaler Pflichtthemen) bleiben unverändert, Berechtigungsprüfungen (`ResolveVersionForLearnerAsync`, `ComputeProgressForUserAsync`) sind nicht berührt.
3. **Je Modul eines Themenentwurfs wurde der teure Inhaltsfingerabdruck (`GetStateAsync`) berechnet**, nur um „neuere Version verfügbar“ anzuzeigen — für die Zählwerte (Module/Karten/Fragen) unnötig. → `DraftModuleViewsAsync(…, includeState: false)` in `DraftStatsAsync`; die Ansichten, die den Status brauchen, sind unverändert.
4. `DraftStatsAsync` lud die Themenfragen zweimal. → einmal.

**Doppelte Initialisierung durch Prerendering:** Seiten mit `@rendermode InteractiveServer` führen `OnInitializedAsync` beim Vorrendern und erneut in der Sitzung aus. Der Server-Timing-Header misst nur die Vorrender-Ausführung (HTTP). Die zweite Ausführung ist nicht gemessen und **nicht verändert** (Prerendering bleibt an; `PersistentComponentState` wäre ein eigener Auftrag).

**Verbleibende Engpässe**
- `/topics`: weiterhin ≈ 32 Abfragen je eigenem Thema (je Modul Zugriff auflösen, festgelegte Version/Original laden, Frage- und Kartenzählungen). Weitere Einsparung braucht eine gebündelte Abfrage über alle Module; wegen der Berechtigungsprüfung je Modul bewusst nicht in diesem Schritt.
- `/` (Modulliste): 398 Abfragen bei 100 Modulen/12 Themen — nicht Gegenstand dieses Auftrags, gleiche Mustergruppe (Modulaktualisierungen/Entwurfserkennung).
- Keine PostgreSQL-Messung der Laufzeiten und keine Messung in der Produktion; Zielwert „unter 300 ms“ ist nur über die Abfrageanzahl plausibel, nicht gemessen belegt.

**Wahrgenommene Navigation:** Ladebalken und Notausgang (`pwa.js`, v0.1.32) sind vorhanden; der aktive Menüpunkt wird über `aria-current` markiert; die Sidebar wird nie blockiert. Eine „Sofortnavigation“ mit alten Inhalten wurde bewusst nicht eingeführt.

**Reproduktion:** `LEARNY_BROWSER_TEST=1 PERF_RUNS=5 node tests/browser/perf-server.mjs` gegen eine Dev-Instanz (Development liefert `Server-Timing`).

**Release:** v0.1.34 (nur IIS, per Tag; kein GitHub-Release, daher kein Docker-Build). Tests vor dem Release: Build ohne Fehler, Integrationstests 2474/2474 (SQLite im Prozess + PostgreSQL 16). Ausstehend: PostgreSQL-Laufzeitmessung, Produktionsmessung und PWA-Messung (mobil); `/topics` braucht weiter 384 Abfragen (12 Themen).

## Nachtrag 3: Lighthouse (Browser/PWA), Stand nach v0.1.34

**Ausgangsbericht (Auftrag, 15.11.2026, mobile Simulation, Produktion, `/topics/{id}`):** Performance 89, Accessibility 96, Best Practices 100, SEO 92; FCP 1,3 s, LCP 2,0 s, TBT 410 ms, CLS 0, Serverantwort 150 ms; Hinweise: Kontrast, 3 Namensabweichungen, fehlende Meta-Beschreibung, CSS/JS-Minifizierung, Cache-Lebensdauern, renderblockierendes CSS, bfcache. Lighthouse warnt selbst, dass Browser-Erweiterungen die Messung verfälscht haben; die 561 KiB „ungenutztes JavaScript“ und die TBT von 410 ms sind deshalb nicht Learny zuzurechnen (ohne den Originalbericht nicht aufschlüsselbar).

### Messmethode (reproduzierbar)
Lighthouse 13.6.0 (CLI, außerhalb des Repositories installiert), Chrome ohne Erweiterungen, Dev-Instanz (Wegwerf-DB), Anmeldung per Dev-Login **im selben Chrome** (`tests/browser/lighthouse.mjs`, 5 Läufe je Profil, Median). **Wichtig:** Ein Cookie per `--extra-headers` erreicht den WebSocket nicht; der Circuit endet dann mit `UnauthorizedAccessException` (Konsolenfehler, Best Practices 96) — ein reines Messartefakt, kein Produktfehler. Einschränkung: Development liefert die Dateien **unkomprimiert** (CSS 83 KB, `blazor.web.js` 200 KB; Produktion: 13,8 KB bzw. 47 KB Brotli) — absolute LCP-Werte sind lokal höher als in Produktion; verglichen werden Vorher/Nachher unter gleichen Bedingungen.

### Bereinigte Baseline (vor den Änderungen, `/topics/{id}`, 5 Läufe, Median)
| | Mobile | Desktop |
|---|---|---|
| Performance | 92 (88–92) | 100 |
| Accessibility | 100 (diese Seite; Seitenscan siehe unten) | 100 |
| Best Practices | 100 | 100 |
| SEO | 91 | 91 |
| FCP / LCP | 1,80 s / 3,16 s | 0,32 s / 0,65 s |
| TBT / CLS | 0 ms / 0 | 0 / 0 |
| lange Tasks | 2–3 (88–174 ms, Dokumentparsen/„unattributable“) | 0 |

**Seitenscan Barrierefreiheit** (8 Seiten, Hell und Dunkel, vor den Änderungen): Dunkelmodus 92–97 (weiße Schrift auf `#8B5CF6` = 4,23:1 bei Primärbuttons, aktiven Chips, Pills); Hellmodus: `#2E9E6B`/`#C97B2D` auf Weiß 3,3:1 (Check/Wiederholen, Statistik), Badges 2,9–3:1; `aria-prohibited-attr` (Fortschrittsbalken ohne Rolle), `aria-required-children` (Archiv-Checkbox in der Tablist), `select-name` (Profil), `target-size` (13-px-Checkbox, kleine Link-Badges), Namensabweichungen.

### Bestätigte Ursachen und Maßnahmen
| Befund | Ursache | Änderung |
|---|---|---|
| SEO 91 | keine Meta-Beschreibung | statische, allgemeine Beschreibung in `App.razor` (keine Seiteninhalte, nichts Privates) |
| 3 Namensabweichungen (Sidebar) | `aria-label` ohne sichtbaren Text („Start“, „Lernen“, „Mehr“) | `MainLayout.razor`: Label beginnt mit dem sichtbaren Text („Start – Mein Dashboard“ …) |
| Darstellungs-Button (Desktop) | `aria-label` ohne „Auto/Hell/Dunkel“ | `app.js`: sichtbarer Kurztext im Namen |
| Kartenbuttons (Zurück, Weiter, Quiz, Chat, Check, Wiederholen) | Namen ohne sichtbaren Text | `FlashCardView.razor`: Namen enthalten den sichtbaren Text |
| Kontrast dunkel | `--brand-500` (#8B5CF6) als Fläche mit weißem Text | Primärbutton/Chip/Pill im Dunkelmodus `#7C3AED` (5,7:1), Hover `#6D28D9` |
| Kontrast hell | `--ok`/`--warn` als Textfarbe | neue Variablen `--ok-strong` `#267D56`, `--warn-strong` `#94550E` (>= 4,5:1 auch auf getöntem Grund) für Check/Wiederholen, Statistik, Badges |
| `aria-prohibited-attr` | `aria-label` an `<span>` ohne Rolle | `role="img"` am Fortschrittsbalken (Dashboard) |
| `aria-required-children` | Checkbox „Archivierte anzeigen“ in der Tablist | außerhalb der Tablist (`Topics.razor`) |
| `select-name` | Select ohne Name | `aria-label` (Profil, Token-Gültigkeit) |
| `target-size` | 13-px-Checkbox, kleine Link-Badges | Checkbox 24 px, `a.badge` Mindesthöhe 24 px |

### Ergebnis nach den Änderungen (`/topics/{id}`, 5 Läufe, Median)
| | Mobile | Desktop |
|---|---|---|
| Performance | 92 | 100 |
| Accessibility | 100 | 100 |
| Best Practices | 100 | 100 |
| SEO | **100** (vorher 91) | **100** (vorher 91) |
| FCP / LCP | 1,80 s / 3,16 s | 0,32 s / 0,65 s |
| TBT / CLS | 0 / 0 | 0 / 0 |
| lange Tasks | 3 | 0 |

**Vollständiger Seitenscan nach allen Änderungen** (Dashboard, Themenliste, Modulliste, Abschlüsse, Hilfe, Profil, Themendetail, Modul; je Hell und Dunkel; Desktop-Preset, 1 Lauf je Seite): Accessibility **100** und SEO **100** auf allen 16 Messungen. Übrig bleibt je 1 Treffer „label-content-name-mismatch“ auf Themenliste, Modulliste und Modulseite (die unten genannten Sammelelemente).
Bewusste Abweichung: Listenkarten (`role="link"`, Themenkarte) und `flip-scene` behalten ihr beschreibendes `aria-label`; der sichtbare Text ist der gesamte Karteninhalt, ein Name aus dem Inhalt wäre für Screenreader schlechter (WCAG 2.5.3 bei diesen Sammelelementen nicht erfüllt, dokumentiert).
Tests: `tests/browser/a11y-seo.mjs` (9 Prüfungen: Meta-Beschreibung, Sidebar-/Darstellungs-/Kartenbutton-Namen, Kontrast Hell/Dunkel); bestehende Browsertests `pwa-nav`, `co-author-topic`, `module-list-updates`, `module-collaboration` bestehen; Integrationstests 2248/2248 (SQLite).

### Nicht verändert / Begründung
- **Lange Tasks / TBT:** lokal TBT 0 ms, 2–3 Tasks (Dokumentparsen, „unattributable“); die 410 ms des Ausgangsberichts sind lokal nicht reproduzierbar und plausibel von Erweiterungen/Gerätedrosselung beeinflusst (nicht belegbar). Framework-Code (`blazor.web.js`) nicht angefasst.
- **JavaScript „ungenutzt“:** lokal nur `blazor.web.js` (Framework, ≈ 112 KiB Schätzung); eigene Skripte < 7 KB je Datei. Keine Änderung.
- **CSS-Minifizierung / ungenutztes CSS:** Produktion liefert `app.*.css` als Brotli mit 13,8 KB (`max-age=31536000, immutable`); die Lighthouse-Schätzung (14 KiB) gilt für die unkomprimierte Entwicklungsauslieferung. Eine Minifizierung ohne zusätzliche Pakete bräuchte einen eigenen Build-Schritt — Nutzen minimal, nicht umgesetzt. Kein CSS entfernt (Dark Mode, Breakpoints, dynamische Komponenten nicht automatisch belegbar).
- **Cache-Lebensdauern (Produktion geprüft):** fingerprintete Dateien `max-age=31536000, immutable` + Brotli (optimal); `_framework/blazor.web.js` `max-age=14400`, Markenbilder `max-age=14400, must-revalidate`, Manifest `no-cache` — nicht fingerprinted, längere Lebensdauer würde Updates verzögern, daher unverändert; HTML `no-cache, no-store` (authentifiziert, richtig).
- **Bilder/Avatare:** nicht verändert; größte Markenbilder 17–52 KB, Avatare laufen über den Image-Proxy (ADR 0018); keine Cache-Änderung an privaten Bildern.
- **PWA:** weiterhin nur Manifest + Wiederverbindungslogik, **kein Service Worker, keine Cache-Strategie** (HTTP-Cache + fingerprintete Assets sorgen dafür, dass nach Deployments neue CSS/JS-Dateien geladen werden); echte installierte PWA/Suspend-Resume nicht geprüft (kein Gerät).
- **Back/Forward-Cache:** zwei Ursachen, beide nicht beseitigbar: `Cache-Control: no-store` am (authentifizierten) HTML-Dokument und die aktive WebSocket-Verbindung (Blazor Server). Nicht erzwungen.
- **Navigation:** Menüwechsel unverändert schnell (warm ≈ 61 ms, `perf-baseline.mjs`, 5 Läufe); v0.1.34-Optimierungen erhalten; weitere N+1-Abfragen (`/topics` 384, `/` 398) nur dokumentiert.
- **LCP mobil 3,2 s:** LCP-Element ist ein Text, der vom renderblockierenden CSS (unkomprimiert 83 KB, simuliertes langsames 4G) abhängt; Server-TTFB 10 ms. Produktion liefert das CSS mit 13,8 KB; Ziel < 2,5 s lokal nicht erreichbar, in Produktion laut Ausgangsbericht (2,0 s) bereits erfüllt. Performance mobil 92 (Ziel >= 90 erreicht, aber nicht durch diese Änderungen verbessert); TBT-Ziel (< 200 ms) lokal erfüllt (0).
- **Folgeaufträge (Empfehlung):** Produktions-Lighthouse ohne Erweiterungen (Navigationsmodus mit Anmeldung), kritisches CSS/Preload prüfen, `/topics` und Modulliste (N+1), Echtgerät-PWA-Test.

### Abschlussprüfung (Releasebereitschaft, ohne Version)
- **Zusätzlich gefunden und behoben:** Die gedrückten Zustände „Check“/„Wiederholen“ (`btn-known-active`, `btn-review-active`) hatten im Dunkelmodus weiße Schrift auf hellem Grün/Orange (2,3:1) — Lighthouse prüft diese Zustände nicht. Dunkelmodus jetzt dunkle Schrift (7,3:1), Hellmodus 5,1/5,9:1; abgedeckt durch `a11y-seo.mjs` (11 Prüfungen).
- **Lighthouse final** (Dev-Instanz, Chrome ohne Erweiterungen, angemeldet, `/topics/{id}`, 5 Läufe, Median): Mobile Performance 92 (74–99), Accessibility 100, Best Practices 100, SEO 100, FCP 1,81 s, LCP 3,16 s, TBT 0 ms, CLS 0, lange Tasks 3; Desktop 100/100/100/100, FCP 0,32 s, LCP 0,65 s, TBT 0, CLS 0. **Ausreißer:** In 1 von 5 mobilen Läufen TBT 734 ms (Performance 74), Ursache nicht geklärt (Lauf 4, kein Kaltstart; vermutlich Last auf dem Messrechner, nicht belegt); die übrigen Läufe 90–99. Das bereinigte Ausgangsniveau (Median 92) ist erreicht, keine Regression im Median.
- **Meta-Beschreibung** korrekt im HTML ausgegeben (`<meta name="description">`, allgemeiner Text, keine Seiteninhalte).
- **Tests:** Build ohne Fehler; Integrationstests 2248/2248 (SQLite); Browsertests `a11y-seo` 11/11, `pwa-nav` 5/5, `co-author-topic` 26/26, `module-list-updates` 28/28, `module-collaboration` 20/20.
- **Semantik:** Beschreibende Labels der Listenkarten und der Flip-Karte bleiben bewusst erhalten (siehe oben).
- **Offen / nicht geprüft:** installierte Produktions-PWA und Suspend/Resume auf einem Gerät; kein Service Worker (HTTP-Cache und fingerprintete Assets, keine eigene Cache-Strategie); Produktions-Lighthouse ohne Erweiterungen; N+1-Abfragen auf `/topics` und Modulliste; mobiler LCP lokal über 2,5 s (Dev unkomprimiert).

## Produktions-Lighthouse v0.1.35

Stand: 15.11.2026, nach dem IIS-Deployment von v0.1.35 (Tag `v0.1.35`, Commit `45f5740`; kein Docker-Build, kein GitHub-Release).

**Zustand der Produktion:** Deployment-Lauf „Deploy To IIS On Tag“ erfolgreich, `/health` HTTP 200, `/_blazor/negotiate` bietet `WebSockets, ServerSentEvents, LongPolling` an, die Meta-Beschreibung steht in der ausgelieferten HTML-Seite.

### Messbedingungen
- Lighthouse 13.6.0 (CLI, außerhalb des Repositories installiert), Kategorien Performance, Accessibility, Best Practices, SEO.
- Chrome headless (`--headless=new --disable-extensions --no-sandbox`) mit frischem Profil, **ohne Browser-Erweiterungen**, ohne Anmeldung.
- Mobil: Standard-Simulation von Lighthouse (mobile Geräteemulation, simulierte Drosselung); Desktop: `--preset=desktop`.
- Adresse `https://learny.brors.date`, also über Cloudflare und IIS. Gemessen von einem Entwicklungsrechner; Netzwerkstandort und -qualität sind nicht kontrolliert.
- 5 Durchläufe je Seite und Geräteprofil; angegeben ist der Median, in Klammern Minimum–Maximum.

### Ergebnisse (öffentliche Seiten)
| Seite | Profil | Performance | Accessibility | Best Practices | SEO | FCP = LCP | TBT | CLS |
|---|---|---|---|---|---|---|---|---|
| `/login` | mobil | 100 (100–100) | 100 (100–100) | 100 (100–100) | 100 (100–100) | 1,22 s | 0 ms | 0 |
| `/login` | Desktop | 100 (100–100) | 100 (100–100) | 100 (100–100) | 100 (100–100) | 0,30 s | 0 ms | 0 |
| `/verify` | mobil | 100 (100–100) | 100 (100–100) | 100 (100–100) | 100 (100–100) | 1,21 s | 0 ms | 0 |
| `/verify` | Desktop | 100 (100–100) | 100 (100–100) | 100 (100–100) | 100 (100–100) | 0,29 s | 0 ms | 0 |

Alle 20 Durchläufe erreichten in allen vier Kategorien 100 Punkte; es gab keine Ausreißer.

### Einordnung
- Die Messungen bestätigen die sehr gute Performance der **öffentlichen** Seiten in Produktion (u. a. Meta-Beschreibung ausgeliefert, SEO 100).
- Sie belegen **nicht** die Performance **authentifizierter Lernseiten**: `/login` und `/verify` sind kleine, statische Seiten ohne Anmeldung, ohne interaktive Blazor-Sitzung mit Datenbeschaffung und ohne Dashboard-/Themen-/Modullisten.
- Der **ursprüngliche Lighthouse-Befund auf `/topics/{id}`** (Performance 89, Accessibility 96, Best Practices 100, SEO 92, TBT 410 ms, LCP 2,0 s) ist damit **nicht als behoben bestätigt**. Eine vergleichbare Produktionsmessung dieser Seite liegt nicht vor; die Korrekturen sind dort bisher nur in der Entwicklungsinstanz gemessen (siehe Nachträge oben).

### Offener Lighthouse-Hinweis
- **CSS-Minifizierung** (Audit „unminified-css“, Score 0,5): beobachtet in der Auswertung von `/login` mobil, Lauf 1; für die übrigen Profile und Seiten nicht gesondert ausgewertet. Gering priorisiert: Produktion liefert `app.*.css` bereits als Brotli (≈ 13,8 KB) mit `max-age=31536000, immutable`; die Lighthouse-Schätzung bezieht sich auf die unkomprimierte Größe.

### Nicht gemessen / offene Prüfungen
- `/topics/{id}`, `/dashboard`, `/topics` und `/` (angemeldete Sitzung) in Produktion — Anmeldung per OIDC nötig, kein Zugang für das Messskript.
- Installierte iPhone-PWA.
- Suspend/Resume der PWA.
- WebSocket-Reconnect nach längerer Inaktivität.
