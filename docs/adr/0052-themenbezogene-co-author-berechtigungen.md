# 0052 — Themenbezogene Co-Author-Berechtigungen

- **Status:** Angenommen und umgesetzt (Abnahme 2026-11-11)
- **Datum:** 2026-11-11

## Kontext

Themen (ADR 0027) haben genau einen Eigentümer; jede Schreib-Operation prüfte bisher „Benutzer = Eigentümer“ (`RequireOwnTopicAsync`). Module und Karten sind privat
(ADR 0011, 0015), Themenentwürfe enthalten nur eigene private Module (ADR 0028), veröffentlichte Versionen und ihre eingefrorenen Kopien sind unveränderlich (ADR 0030).
Mehrere Personen sollen gemeinsam an einem Thema arbeiten können, ohne dass dafür globale Rollen (ADR 0012), Lernfreigaben (ADR 0015), Themenzuweisungen (ADR 0029)
oder das Eigentum an Modulen und Karten gelockert werden.

## Entscheidung

1. **Co-Author ist eine objektbezogene Berechtigung, keine Rolle.** Tabelle `topic_coauthors` (`topic_id`, `user_id`, `granted_by`, `created_at`; Primärschlüssel
   (`topic_id`, `user_id`), Fremdschlüssel mit `ON DELETE CASCADE`). Ein Benutzer kann Co-Author vieler Themen sein; die Berechtigung gilt ausschließlich für das Thema der Zeile.
   Bestehende Themen haben keine Zeilen und verhalten sich unverändert. Es gibt keine Migration von Inhalten.
2. **Eine zentrale Entscheidung.** `TopicService.GetTopicRoleAsync`/`RoleForAsync` liefert `Owner`, `CoAuthor` oder `None` — gegen die Datenbank, nie aus Request-Daten oder Token-Claims.
   `RequireEditableTopicAsync` (Eigentümer **oder** Co-Author) ersetzt `RequireOwnTopicAsync` nur dort, wo der **Entwurf** bearbeitet wird. Alle übrigen Pfade bleiben
   beim Eigentümer (`RequireOwnTopicAsync` unverändert). Die Eigentümerprüfung wird nirgends pauschal gelockert.
3. **Rechte.**
   - Eigentümer: alles wie bisher, zusätzlich Co-Autoren verwalten.
   - Co-Author: Thema und Entwurf lesen und bearbeiten (Name, Beschreibung, Modulreihenfolge, Moduleinträge entfernen, neue Modulstände festlegen, Prüfungsstruktur und Themenfragen),
     Inhalte der im Entwurf zugeordneten Module bearbeiten (Karten, Quizkonfiguration, Fragenpool, Kompetenzen — nur über die Weboberfläche, siehe 5.), weitere Co-Autoren hinzufügen und entfernen,
     die eigene Mitarbeit beenden.
   - **Nicht** Co-Author-Rechte: Veröffentlichen, globale Einreichung/Freigabe, Lernfreigaben, Zuweisungen, Archivieren, Duplizieren, Module aus der Bibliothek des Eigentümers hinzufügen,
     Thema per JSON ersetzen, Modulversionen veröffentlichen, den Eigentümer entfernen oder ändern. Es gibt keine Eigentumsübertragung.
4. **Eingeschränkte Lernprofile** (ADR 0049) sind ausgeschlossen: sie können nicht eingetragen werden, und selbst ein vorhandener Eintrag ergibt `TopicRole.None`.
   Eintragen kann man nur aktive, existierende Benutzer; man kann sich nicht selbst eintragen; Höchstzahl 50 je Thema.
5. **Bearbeitungskontext für Module.** Module bleiben Eigentum des Themeneigentümers. `ResolveModuleEditOwnerAsync(actor, moduleId)` liefert für den Dienstaufruf den Moduleigentümer **nur**, wenn
   das Modul privat ist, im Entwurf (`topic_modules`) eines Themas desselben Eigentümers liegt, an dem der Handelnde Co-Author ist, und das Modul **in keinem anderen Thema** ohne Bearbeitungsrecht
   des Handelnden liegt. Sonst gilt unverändert die Eigentümerprüfung („nicht gefunden“). Eingefrorene Themenmodule (`scope = 'topic'`) liegen nicht in `topic_modules` und sind nie bearbeitbar.
   Es gibt keine zweite Karten- oder Quizpersistenz; die REST-Routen für Karten und Module (`/api/cards`, `/api/modules/{id}/…`) bleiben eigentümergebunden, weil MCP-Tokens dieselben Routen nutzen (ADR 0007) —
   die gemeinsame Kartenbearbeitung ist eine Funktion der Weboberfläche (Blazor, serverseitig geprüft). Handelnde Person bleibt im Audit der Co-Author (`topic.coauthor.*`).
6. **Gleichzeitigkeit.** Der Themenentwurf nutzt `topics.draft_revision`: Speichern von Name/Beschreibung, Prüfungskonfiguration, Modulreihenfolge und -entfernung prüft die geladene Revision (UI und
   optionaler Header `X-Topic-Draft-Revision`); bei Abweichung Konflikt (HTTP 409), nichts wird überschrieben. Karten behalten den bestehenden `updated_at`-Konflikt (ADR 0006). Keine Echtzeit-Synchronisierung.
7. **Entzug** löscht nur die Zeile und wirkt sofort (jeder Aufruf prüft die Datenbank); Themen-, Modul-, Karten- und Lerninhalte bleiben unverändert. Auch eine bereits geöffnete Sitzung des Modul-Editors prüft die Berechtigung vor **jedem** Schreibzugriff erneut (`StillAllowedAsync`, Klarstellung nach der Abnahme: zuvor wurde der Bearbeitungskontext nur beim Laden aufgelöst).
8. **REST** unter `/api/topics/{id}/coauthors` (Liste, Kandidatensuche, hinzufügen, entfernen, `leave`); Entscheidungen nur im Service. Nicht für MCP-Tokens (`TokenMayReadTopics` schließt `/coauthors` aus).
9. **Oberfläche:** Bereich „Zusammenarbeit“ im Themeneditor (Eigentümer markiert, Hinzufügen mit Suche und Bestätigung, Entfernen mit Sicherheitsabfrage, eigene Mitarbeit beenden); Themenübersicht mit
   den Reitern „Meine Themen“, „Gemeinsam bearbeitet“ und „Alle zugänglichen Themen“ sowie der Kennzeichnung „Co-Author“.

## Konsequenzen

- Gemeinsames Arbeiten an Entwürfen ohne neue Rolle, ohne Lockerung des Modulbesitzes und ohne Auswirkung auf veröffentlichte Versionen.
- Co-Autoren sehen nur Module, die dem Thema zugeordnet sind; die Bibliothek des Eigentümers bleibt privat. Neue Module fügt der Eigentümer hinzu.
- Ein Modul, das in mehreren Themen liegt, ist nur gemeinsam bearbeitbar, wenn der Co-Author an allen beteiligten Themen berechtigt ist (sonst Auswirkungen auf andere Themen).
- Kartenbearbeitung durch Co-Autoren per REST/MCP ist bewusst nicht enthalten (spätere Erweiterung braucht eigene Entscheidung zu MCP, ADR 0007/0013).
- PostgreSQL und SQLite nutzen dieselben parametrisierten Anweisungen; die Co-Author-Anweisungen (Schema, Primär-/Fremdschlüssel, `ON DELETE CASCADE`, Hinzufügen/Entfernen, Übersicht, Revisionskonflikt) sind am 2026-11-11 gegen eine Wegwerf-Instanz PostgreSQL 16 geprüft (`PostgresReadinessTests`, siehe `docs/testing.md`).

## Leitplanken für Änderungen

- Schreibrechte an Themen nur über `GetTopicRoleAsync`/`RequireEditableTopicAsync` bzw. `RequireOwnTopicAsync`; neue Schreibpfade festlegen, ob Co-Autoren dürfen (Standard: nein).
- Nie eine Rolle, ein Token-Claim oder eine Request-Angabe als Berechtigung verwenden; Berechtigungen anderer Themen fließen nie ein.
- Co-Author gewährt nie Veröffentlichung, Freigabe, Zuweisung, Administration oder Zugriff auf private Module außerhalb des Themenkontexts; eingeschränkte Lernprofile nie.
- Veröffentlichte Versionen und eingefrorene Kopien bleiben unveränderlich (ADR 0030); Lernfreigabe (ADR 0015) bleibt unabhängig.
- MCP-Tokens verwalten keine Co-Autoren.
- Mitziehen: `Schema.cs`, `docs/database-schema.md`, `docs/architecture.md`, `AGENTS.md`, Hilfeseite, Tests (`CoAuthorTests.cs`).

## Betroffener Code

- `Services/TopicService.CoAuthors.cs`, `Services/TopicService.cs`, `TopicService.Versions.cs`, `TopicService.Transfer.cs`
- `Data/Schema.cs` (`topic_coauthors`), `Domain/Topics.cs`
- `Api/TopicEndpoints.cs`
- `Components/Pages/TopicEditor.razor`, `TopicPage.razor`, `Topics.razor`, `ModuleEditor.razor`, `wwwroot/app.css`
- `tests/LearnCards.IntegrationTests/CoAuthorTests.cs`

## Abnahme (2026-11-11)

- **Automatisch:** 1934/1934 Prüfungen auf SQLite und PostgreSQL 16 (Wegwerf-Container, `TEST_DB_PROVIDER=postgres`), danach 1919/1919 auf SQLite mit dem endgültigen Code; MCP-Token auf allen fünf Co-Author-Routen 403; Test-Runner erkennt veraltete DLLs.
- **Browser (zwei getrennte Sitzungen, headless Chrome):** Themeneditor 26/26; Modul-/Kartenbearbeitung: Karte ändern, neue Karte anlegen, Eigentümer sieht beides, Modulbibliothek bleibt beim Eigentümer, Modul mit zusätzlichem nicht zugänglichem Thema nicht bearbeitbar, Konflikt (`updated_at`) ohne Überschreiben, Entzug wirkt in der offenen Sitzung sofort (Abweichung behoben, s. o.).
- **Offen/bewusst nicht Teil:** Mobilgeräte, produktionsnahe PostgreSQL-Instanz (TLS), keine automatisierte Browsersuite (Schritte in `docs/testing.md`).
