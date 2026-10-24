# Prüfbericht: Mehrbenutzerfähigkeit und Benutzertrennung

- **Datum:** 2026-10-24
- **Geprüfter Stand:** Arbeitsverzeichnis auf Basis von Commit `342c9b2` mit den (noch nicht committeten) Änderungen aus ADR 0011, 0012 und 0013
- **Art:** Prüfung, **keine Änderung am Produktivcode**. Neu sind nur Tests (`tests/LearnCards.IntegrationTests`) und diese Dokumentation.
- **Ergebnis in einem Satz:** Die Benutzertrennung ist in der aktuellen Implementierung wirksam (keine Datenvermischung zwischen Benutzern gefunden); es bestehen aber ein
  **hohes** Konfigurationsrisiko (Dev-Login als stiller Rückfall) und mehrere mittlere/niedrige Punkte bei Sitzungsverwaltung und Gleichzeitigkeit.

## 0. Wichtiger Hinweis zu den Annahmen des Auftrags

Der Auftrag beschreibt Module/Karten als **global** und den `X-MCP-Key` als **vollen API-Zugriff ohne Benutzer** („Annahme: gemeinsame Nutzung ist beabsichtigt“).
Das entspricht dem Stand **vor** ADR 0011–0013 und wurde inzwischen durch ausdrückliche Entscheidung (Aufträge 1 und 3) ersetzt:

| Annahme im Auftrag | Historischer Stand (`342c9b2`) | Aktueller Stand |
|---|---|---|
| `modules`/`cards` ohne Benutzerkennung, global | **bestätigt** (Code: `ListModulesAsync()`, `ListCardsAsync()` ohne Filter; `modules.name` global `UNIQUE`) | `owner_user_id` an beiden Tabellen, Zugriff nur für den Eigentümer ([ADR 0011](../adr/0011-mehrbenutzerarchitektur.md)) |
| `X-MCP-Key` = voller API-Zugriff | **bestätigt** (`api.AddEndpointFilter`: `keyOk` genügt; Benutzer-ID fiel auf Pseudo-Benutzer `"mcp-client"` zurück) | Dienstschlüssel ohne Datenzugriff; Benutzer arbeiten mit benutzergebundenen Tokens ([ADR 0013](../adr/0013-mcp-benutzergebundene-anmeldung.md)) |
| Identität = OIDC-`sub` / Dev-Kennung | bestätigt | interne Benutzer-ID (`uid`), externe Identitäten in `user_identities` |
| Dokumentation kennt keine privaten Module | bestätigt | Dokumentation aktualisiert, siehe Abschnitt 7 |

Die historischen Aussagen stützen sich auf Code-Lektüre; ein Zwei-Benutzer-Test am alten Stand war **nicht möglich** (der damalige Dev-Login kennt nur einen Benutzer).
Alle folgenden Feststellungen betreffen den **aktuellen Stand**.

## 1. Antworten auf die zehn Fragen

| # | Frage | Status | Ergebnis und Beleg |
|---|---|---|---|
| 1 | Zwei Benutzer unabhängig anmelden? | bestätigt | Test „jeder Benutzer hat eine eigene interne ID“; Dev-Login `?devUser=<name>` erzeugt getrennte Benutzer/Sitzungen. OIDC real **nicht verifizierbar** (kein Entra-Zugang in der Testumgebung). |
| 2 | Theme/Präferenzen getrennt? | bestätigt | Test „Theme ist pro Benutzer getrennt“ (`user_preferences.user_sub = @u`, `CardRepository.SaveThemeAsync`). |
| 3 | Lernstatus ohne Beeinflussung anderer? | bestätigt | Test „Doras Lernstatus verändert Emils Lernstatus nicht“. Hinweis: Da Karten jetzt privat sind, gibt es keine *gemeinsame* Karte mehr, an der sich Status zweier Benutzer überlagern könnte. |
| 4 | Chat-Verlauf/Quiz-Ergebnisse nur für Berechtigte? | bestätigt (Quiz) / teilweise (Chat) | Quiz: Tests „Bob sieht Alices Ergebnis nicht“, „kann es nicht löschen“. Chat: Lesepfad geprüft (leer bei fremder Karte). Geschrieben wird der Chat-Verlauf **nur von der Blazor-Oberfläche** (`ChatPanel` → `SaveChatHistoryAsync` mit der `uid` des Circuits); der API-Endpunkt `POST /api/chat/` speichert nichts. Schreibpfad daher nur per Code-Review verifiziert. |
| 5 | Manipulierte Anfragen auf fremde Datensätze? | bestätigt (nicht möglich) | Fremde IDs → 404 (Karten, Module, Status, Quiz, Chat); Header `X-User-Id`, `sub`, `uid`, Query `user_sub`/`owner_user_id` und Body-Felder werden ignoriert; ungültige IDs → 404 statt 500; SQL-Injektionsversuch wirkungslos. |
| 6 | Sitzungsdaten nach Wechsel/Logout getrennt? | teilweise (siehe F2, F3) | Benutzerwechsel im selben Browser: neue Sitzung zeigt nur Daten des neuen Benutzers (Test). Logout meldet die Browser-Sitzung ab (Test). **Abweichung:** ein vor dem Logout kopiertes Cookie bleibt gültig (F2); offene Blazor-Circuits übernehmen Sperren nicht sofort (F3). |
| 7 | Gleichzeitige Aktionen → Datenvermischung? | bestätigt (keine Vermischung) / Abweichung F4 | 40 parallele Theme-Schreibzugriffe und 30 parallele Statusänderungen zweier Benutzer: keine Vermischung. Singleton-Dienste halten keinen Benutzerzustand (siehe Abschnitt 3). **Aber:** paralleler Erstzugriff desselben Schlüssels liefert auf PostgreSQL HTTP 500 (F4). |
| 8 | Welche persönlichen Endpunkte per `X-MCP-Key`? | bestätigt (keiner) | Test über 26 benutzerbezogene Endpunkte: alle → 403. Erreichbar mit dem Schlüssel nur `GET /api/schema/import/cards` und `POST /api/mcp/link/exchange` (Code-Tausch). Historisch waren alle erreichbar (unter `"mcp-client"`). |
| 9 | Welche Änderungen an Modulen/Karten wirken auf alle? | bestätigt (keine) | Es gibt keine gemeinsamen Module/Karten mehr; Import, Bearbeiten, Archivieren, Löschen wirken nur im Bestand des Eigentümers. Historisch wirkte **alles** global (inkl. Löschen eines Moduls samt Karten und aller Ergebnisse). |
| 10 | Entspricht das den dokumentierten Entscheidungen? | bestätigt mit Abweichungen | Code entspricht ADR 0011–0013. Abweichungen zu ADR 0005/0003: F1, F2, F3. Dokumentationslücken: Abschnitt 7 (inzwischen geschlossen). |

## 2. Feststellungen

Status: **bestätigt** = in Doku und Code übereinstimmend; **Abweichung** = Verhalten weicht von der Erwartung/Dokumentation ab; **nicht verifizierbar** = nur per Code-Lektüre oder nicht prüfbar.

| ID | Schwere | Status | Titel |
|---|---|---|---|
| F1 | **Hoch** | Abweichung | `AUTH_MODE=auto` fällt ohne OIDC-Konfiguration lautlos auf den Dev-Login zurück (auch in `Production`) |
| F2 | Mittel | Abweichung | Session-Cookie bleibt nach Logout gültig (kein serverseitiger Widerruf) |
| F3 | Mittel | nicht verifizierbar | Offene Blazor-Circuits übernehmen Sperre/Rollenentzug erst nach Neuaufbau |
| F4 | Mittel | Abweichung | Upsert-Wettlauf (SELECT → INSERT) führt auf PostgreSQL zu HTTP 500 |
| F5 | Niedrig | bestätigt | Altdaten-Migration und Bootstrap-Admin sind nicht an den Identity Provider gebunden |
| F6 | Niedrig | bestätigt | `ForwardedHeaders` vertrauen jedem Proxy (`KnownProxies`/`KnownNetworks` geleert) |
| F7 | Niedrig | bestätigt | `/auth/logout` und Dev-Login per GET (Logout-CSRF); Dev-Login erlaubt Benutzer- und Rollenwahl |
| F8 | Niedrig | bestätigt | Kein Löschpfad für personenbezogene Daten; `user_sub`-Tabellen ohne Fremdschlüssel |
| F9 | Niedrig | bestätigt | Keine Rate-Limits/Größenlimits (Import, Token-Endpunkte), keine Sicherheits-Header (CSP) |
| F10 | Info | bestätigt | Chat-Verlauf wird nur von der Blazor-UI gespeichert, nicht über die API |
| F11 | Info | bestätigt | PostgreSQL-Zugriff über **eine** serialisierte Verbindung (Durchsatz-Obergrenze) |

### F1 — Dev-Login als stiller Rückfall (Hoch)
- **Dateien:** `Infrastructure/AppConfig.cs` (Auth-Modus-Bestimmung), `Auth/AuthEndpoints.cs` (`/auth/login` im Dev-Modus), `Services/UserService.cs` (`IsBootstrapAdmin`)
- **Ursache:** `AUTH_MODE=auto` (Standard) wählt `dev`, sobald `OIDC_ISSUER`/`OIDC_CLIENT_ID` fehlen oder mit `YOUR_` beginnen. Eine unvollständige Produktivkonfiguration öffnet damit
  die App für **jeden**; der Standardbenutzer `local-dev-user` ist im Dev-Modus **Administrator**, `?devUser=`/`?devRoles=Admin.All` erlauben beliebige Benutzer und Rollen.
- **Auswirkung:** vollständige Übernahme (Administration, Benutzerverwaltung, Daten des Dev-Benutzers) ohne Anmeldung.
- **Prüfschritt (ausgeführt):** `ASPNETCORE_ENVIRONMENT=Production`, `AUTH_MODE` nicht gesetzt, `OIDC_*` leer → Log „Auth-Modus: dev“; `GET /auth/login` → 302; danach `GET /api/me` mit dem Cookie →
  `roles: ["admin","user"]`.
- **Korrektur:** Dev-Login nur bei **explizitem** `AUTH_MODE=dev` **und** `ASPNETCORE_ENVIRONMENT=Development`; sonst Start abbrechen (fail closed). `devUser`/`devRoles` nur in `Development`.

### F2 — Cookie nach Logout weiter gültig (Mittel)
- **Dateien:** `Program.cs` (Cookie-Konfiguration), `Auth/AuthEndpoints.cs` (`/auth/logout`)
- **Ursache:** Das Cookie ist selbstenthaltend (Data Protection); `SignOutAsync` löscht es nur im Browser. Es gibt keine serverseitige Sitzungs-ID/Sperrliste. Das Cookie gilt bis zum Ablauf (10 h, gleitend).
- **Auswirkung:** Ein abgegriffenes Cookie (z. B. durch Proxy-Log, Malware) bleibt nach dem Logout des Opfers nutzbar.
- **Prüfschritt (ausgeführt, Test „Replay des Cookies nach Logout“):** Login, Cookie kopieren, `GET /auth/logout`, Anfrage mit dem kopierten Cookie → **HTTP 200**.
- **Korrektur:** serverseitige Sitzungsversion pro Benutzer (`session_version` im Claim, in `OnValidatePrincipal` geprüft; Logout/„überall abmelden“/Sperre erhöht sie) oder Sitzungstabelle; absolute Maximaldauer.

### F3 — Offene Blazor-Circuits (Mittel, nicht verifizierbar)
- **Dateien:** `Components/**`, `Program.cs`
- **Ursache:** Ein Circuit bindet den Principal beim Verbindungsaufbau. `OnValidatePrincipal` läuft nur bei HTTP-Anfragen (nicht bei SignalR-Nachrichten). Es gibt keinen
  `RevalidatingServerAuthenticationStateProvider`. Benutzerbezogene Repository-Aufrufe nutzen die `uid` des Circuits; ein **inzwischen gesperrter** Benutzer kann mit offenem Tab weiterarbeiten,
  bis der Circuit endet. Die Datenisolation bleibt bestehen (die `uid` ist unverändert), administrative Aktionen prüfen die Rolle zusätzlich gegen die DB.
- **Prüfschritt (manuell, nicht automatisiert):** Tab als Benutzer öffnen, Benutzer in `/admin` sperren, im offenen Tab Karten ändern → Aktion gelingt weiterhin.
- **Korrektur:** `RevalidatingServerAuthenticationStateProvider` (Teil des Frameworks, kein NuGet) mit kurzem Intervall, der Benutzerstatus und Sitzungsversion prüft.

### F4 — Upsert-Wettlauf (Mittel, funktional)
- **Datei:** `Services/CardRepository.cs` (`UpsertInternalAsync`: `SELECT 1 …` dann `INSERT` bzw. `UPDATE`, nicht atomar)
- **Auswirkung:** Zwei gleichzeitige Erstspeicherungen desselben Schlüssels (z. B. Doppelklick auf „gelernt“, zwei Tabs) → eine liefert **HTTP 500**
  (`duplicate key … user_card_states_pkey`). Keine Datenvermischung, da der Primärschlüssel `(user_sub, card_id)` greift; betrifft nur eigene Daten. SQLite zeigte den Fehler im Test nicht, PostgreSQL schon.
- **Prüfschritt (ausgeführt, PostgreSQL):** 30 parallele `PUT /api/cards/{id}/state` auf einer Karte ohne bestehenden Status → 4 × HTTP 500.
- **Korrektur:** atomares `INSERT … ON CONFLICT (…) DO UPDATE` (PostgreSQL und SQLite ≥ 3.24 unterstützen es); betrifft `user_preferences` und `user_card_states`.

### F5 — Provider-Bindung (Niedrig)
- **Dateien:** `Services/UserService.cs` (`MigrateLegacyDataAsync`, `IsBootstrapAdmin`)
- **Ursache:** Die Umschreibung alter `user_sub`-Werte und die `ADMIN_SUBJECTS`-Prüfung vergleichen nur das **Subject**, nicht den Provider. Bei einem späteren Wechsel/Zusatz eines Identity Providers könnte ein dort identisches Subject fremde Altdaten
  übernehmen bzw. Admin werden. In der heutigen Konfiguration (ein Issuer; Dev-Subjects sind `local-dev-user` bzw. `dev-<name>`) nicht ausnutzbar.
- **Korrektur:** Bootstrap und Legacy-Migration an `provider == OIDC_ISSUER` binden (nie an `dev`).

### F6 — Forwarded Headers (Niedrig)
- **Datei:** `Program.cs` (`fwd.KnownNetworks.Clear(); fwd.KnownProxies.Clear();`)
- **Auswirkung:** `X-Forwarded-Host/-Proto` sind von beliebigen Clients setzbar; sie beeinflussen die OIDC-Redirect-URI (der IdP akzeptiert nur registrierte URIs) und den `Secure`-Cookie-Modus (`SameAsRequest`). Kein IP-basiertes Verhalten betroffen.
- **Korrektur:** erlaubte Proxys konfigurieren (`KNOWN_PROXIES`) bzw. IIS/Azure-Modul nutzen; Cookies in Produktion `Secure = Always`.

### F7 — GET-Logout, Dev-Login (Niedrig)
- `GET /auth/logout` lässt sich cross-site auslösen (Logout-CSRF, lästig, kein Datenzugriff). `GET /auth/login?devUser=` erlaubt im Dev-Modus Login-CSRF (nur Dev). Korrektur: Logout per POST mit Antiforgery.

### F8 — Personenbezogene Daten ohne Löschpfad (Niedrig)
- `chat_history`, `quiz_results`, `user_card_states`, `user_preferences` verweisen per `user_sub` **ohne Fremdschlüssel** auf `users`; es gibt keine Funktion „Benutzer löschen/anonymisieren“. Beim Löschen von `users`
  (derzeit nur manuell möglich) blieben Verläufe verwaist (`owner_user_id` wird `NULL`, Inhalte würden zu unzugeordnetem Altbestand). Für Datenschutzanfragen fehlen Export und Löschung.

### F9 — Limits und Header (Niedrig)
- Kein Rate-Limiting (Login-Callback, Token-Austausch, Import), keine Größenbegrenzung des Importkörpers, keine `Content-Security-Policy`/`X-Content-Type-Options`/`Referrer-Policy`.
  Blazor kodiert Ausgaben (kein `MarkupString`/`innerHTML` im Code gefunden), daher kein konkretes XSS festgestellt.

### F10/F11 — Hinweise
- F10: Wer den Chat-Verlauf per API/MCP nutzt, bekommt keinen persistierten Verlauf; dokumentiert in [architecture.md](../architecture.md).
- F11: `PostgresDatabase` nutzt eine Verbindung mit `SemaphoreSlim`; das verhindert Interleaving (Pluspunkt für Korrektheit), begrenzt aber den Durchsatz bei vielen gleichzeitigen Benutzern.

## 3. Weitere im Code verifizierte Punkte

- **Identität:** Die Benutzer-ID kommt ausschließlich aus dem serverseitig gesetzten Claim `uid` (Blazor) bzw. `HttpContext.Items` (API, vom Filter gesetzt). Kein Endpunkt liest eine Benutzerkennung aus Query, Header oder Body.
- **Alle Zugriffe auf `user_preferences`, `user_card_states`, `chat_history`, `quiz_results`** (`CardRepository`) filtern mit `user_sub = @u` bzw. verknüpfen über besitzgefilterte Karten; Löschen von Ergebnissen: `WHERE id = @id AND user_sub = @user_sub`.
  Schreibende Endpunkte prüfen vorab den Kartenbesitz (`GetCardAsync(id, owner)`).
- **Singleton-Dienste:** `CardRepository`, `QuizService`, `ChatService`, `OpenAiClient`, `UserService`, `McpLinkService`, `OidcService` halten keine benutzerbezogenen Felder, Caches oder statischen Variablen
  (nur `static readonly` Konstanten/Regex). Blazor-Komponenten halten die `uid` pro Komponenteninstanz (Circuit-bezogen). Im MCP-Server ist der Benutzerkontext per `AsyncLocal` an die einzelne Anfrage gebunden.
- **SQLite und PostgreSQL:** gleiche SQL-Filter, gleiche Testergebnisse (SQLite 131/131; PostgreSQL 130/131, die Abweichung ist F4).
- **Ohne/ungültige Anmeldung:** API → 401 (kein Redirect), Blazor-Seiten → Weiterleitung zum Login, gesperrte/unbekannte Benutzer → 401, bei alten Cookies ohne `uid` Neuanmeldung.

## 4. Durchgeführte und nicht durchführbare Tests

**Ausgeführt** (`dotnet build LearnCards.slnx` ohne Fehler; Integrationstests, siehe [testing.md](../testing.md)):
- SQLite: **131/131** bestanden. PostgreSQL 16 (Docker): **130/131**, fehlgeschlagen ist der parallele Erstzugriff (F4).
- Testbenutzer: `alice`, `bob`, `dora`, `emil` (getrennte Cookie-Sitzungen), Administrator, Auditor-Rolle; MCP-Server als echter Prozess; keine produktiven Daten verwendet (temporäre Datenbanken).
- Zusätzlich manuell reproduziert: F1 (Production ohne OIDC).

**Nicht durchführbar / nicht automatisiert:**
- Zwei **reale Browser-Sitzungen** mit der interaktiven Blazor-Oberfläche (keine Browser-Automatisierung verfügbar). Die Oberflächen wurden nur per HTTP-Rendering geprüft; die zugrunde liegenden Repository-Pfade sind über die API abgedeckt.
- Echter **Entra-ID-Login** (nur Dev-Login), Sperre in offenen Circuits (F3), Lasttests, Schreibpfad des Chat-Verlaufs (nur Blazor).

Manuelle Prüfanleitung (zwei Browser): Fenster A normal, Fenster B privat; mit `…/auth/login?devUser=anna` bzw. `?devUser=ben` anmelden; in A Modul und Karten importieren, Theme umschalten, Karte als „gelernt“ markieren;
in B prüfen, dass nichts sichtbar ist und Theme/Status unabhängig sind; in B die ID einer Karte aus A in die URL `/module/<id>` eintragen → „nicht gefunden“.

## 5. Empfohlene Korrekturen (Priorität)

1. **F1 (Hoch):** Dev-Login nur bei explizitem `AUTH_MODE=dev` **und** `Development`; sonst Start verweigern. `devUser`/`devRoles` nur in `Development`.
2. **F4 (Mittel):** `INSERT … ON CONFLICT DO UPDATE` für alle Upserts (beide Provider), Test beibehalten.
3. **F2/F3 (Mittel):** serverseitige Sitzungsversion + `RevalidatingServerAuthenticationStateProvider`; Logout per POST.
4. **F5/F6 (Niedrig):** Provider-Bindung für Bootstrap/Legacy-Migration; bekannte Proxys konfigurieren; `Secure`-Cookie in Produktion erzwingen.
5. **F8/F9 (Niedrig):** Benutzer-Export/-Löschung (inkl. Fremdschlüssel bzw. Bereinigung), Rate-Limits, Größenlimits, Sicherheits-Header.

Vorschlag für eine gesonderte Entscheidung: [ADR 0014 — Sitzungs- und Betriebsmodus-Härtung](../adr/0014-sitzungs-und-betriebsmodus-haertung.md) (Status „Vorgeschlagen“, **nicht umgesetzt**, benötigt Freigabe).

## 6. Offene Architekturfragen

- Soll ein Administrator fremde private Inhalte einsehen dürfen (Support/Audit), und unter welchen Bedingungen (Protokollierung, Vier-Augen)? Aktuell: nein.
- Sitzungsdauer und „überall abmelden“: gewünschte Maximaldauer (derzeit 10 h gleitend), Pflicht zur Neuanmeldung bei Rollenänderung?
- Aufbewahrungs- und Löschfristen für Chat-Verläufe und Quiz-Ergebnisse; wer darf Export/Löschung auslösen?
- Soll der Chat-Verlauf auch über API/MCP-Aufrufe persistiert werden (aktuell nur Oberfläche)?
- Schwellenwert für den Wechsel von der Einzelverbindung auf einen Verbindungspool bei PostgreSQL.

## 7. Dokumentationslücken

Zum Zeitpunkt des Auftrags **fehlten oder widersprachen**: (a) `docs/architecture.md` und `docs/database-schema.md` beschrieben Module/Karten als global und kannten weder `users`, `user_identities`, `user_roles`, `audit_log` noch
`owner_user_id`; (b) ADR 0005 nannte den MCP-Schlüssel „vollen Zugriff“; (c) die Deployment-Dokumente kannten `ADMIN_SUBJECTS`, `OIDC_ROLE_MAP`, `MCP_PUBLIC_URL`, `MCP_TOKEN_DAYS`, `LEARNCARDS_WEB_PUBLIC_URL` nicht;
(d) die Aussage „nur persönliche Lerndaten sind benutzerbezogen, Module/Karten global“ stand an keiner Stelle ausdrücklich.
Diese Lücken sind mit ADR 0011–0013 und der gleichzeitigen Aktualisierung aller Markdown-Dateien **geschlossen**; maßgeblich ist jetzt: *Module und Karten sind privat (Eigentümer), persönliche Lerndaten sind benutzerbezogen, es gibt keinen gemeinsamen Kartenbestand.*
