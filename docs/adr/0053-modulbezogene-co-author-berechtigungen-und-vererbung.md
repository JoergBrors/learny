# 0053 — Modulbezogene Co-Author-Berechtigungen und Vererbung aus Themen

- **Status:** Angenommen und umgesetzt (2026-11-14)
- **Teilweise geändert durch:** [0059](0059-co-autoren-duerfen-modulversionen-veroeffentlichen.md) (Co-Autoren dürfen Modulversionen veröffentlichen; Themenveröffentlichung, Freigabe, Zuweisung bleiben gesperrt).
- **Ergänzt durch:** [0054](0054-lern-und-pruefungsvorschau-fuer-content-co-autoren.md) (Modulvorschau folgt `GetModuleEditAccessAsync`, inkl. Mehrfachzuordnungs-Schutz und Entzug; direkte Modulrechte gewähren keine Themenvorschau).
- **Datum:** 2026-11-14
- **Ergänzt:** [0052](0052-themenbezogene-co-author-berechtigungen.md) (Themen-Co-Autoren); verallgemeinert dessen Regel „Bearbeitungskontext für Module“.

## Kontext

ADR 0052 erlaubt Co-Autoren eines Themas, die Module im Entwurf dieses Themas zu bearbeiten (`ResolveModuleEditOwnerAsync`). Eigenständige Module ohne Thema lassen sich nicht gemeinsam bearbeiten,
und der Zusammenhang „Themenfreigabe → Module“ war nur im Code, nicht als Berechtigungsmodell beschrieben. Module und Karten bleiben privat (ADR 0011, 0015); Lernfreigaben, Zuweisungen und
Veröffentlichung sind eine andere Berechtigungsklasse (ADR 0015, 0029, 0030).

## Entscheidung

1. **Zwei voneinander unabhängige Berechtigungsquellen** für das Bearbeiten eines fremden privaten Moduls:
   - **a) Direkte Modulfreigabe:** Tabelle `module_coauthors` (`module_id`, `user_id`, `granted_by`, `created_at`; Primärschlüssel (`module_id`, `user_id`); Fremdschlüssel `ON DELETE CASCADE` auf
     `modules` und `users`; Index auf `user_id`). Gilt nur für dieses Modul, unabhängig von Themen.
   - **b) Abgeleitet aus einem Themen-Co-Author (ADR 0052):** wird **nie gespeichert**. Bei jedem Zugriff aus den *aktuellen* Daten berechnet: Benutzer ist Co-Author eines **aktiven** Themas
     desselben Eigentümers, und das Modul liegt im **Entwurf** (`topic_modules`) dieses Themas. Damit gelten automatisch auch später hinzugefügte Module; Entfernen des Moduls aus dem Thema,
     Entzug/Austritt des Themen-Co-Authors oder Archivieren des Themas beendet das abgeleitete Recht sofort. Eine bloße Themenfreigabe erzeugt keine Zeile in `module_coauthors`.
2. **Eine zentrale Entscheidung:** `TopicService.GetModuleEditAccessAsync(actor, moduleId)` liefert `Owner`, `Direct`, `ViaTopic` oder `None` (mit den Themen der abgeleiteten Herkunft) — immer gegen die
   Datenbank, nie aus Request-Daten, Token-Claims oder gemerkten Rollen. `ResolveModuleEditOwnerAsync` ist nur noch eine Projektion davon (liefert den Eigentümer bei `Direct`/`ViaTopic`, sonst den
   Handelnden). Voraussetzungen: Modul ist **privat** (globale Module und eingefrorene `scope='topic'`-Kopien sind nie bearbeitbar), der Handelnde ist kein eingeschränktes Lernprofil (ADR 0049; selbst
   ein vorhandener Eintrag ergibt `None`) und ein aktiver Benutzer.
3. **Mehrfachzuordnung (Schutzgrenze).** Die Regel aus ADR 0052 bleibt für das *abgeleitete* Recht unverändert: Liegt das Modul (auch) im Entwurf eines Themas, an dem der Handelnde **kein**
   Bearbeitungsrecht hat, entsteht **kein** abgeleitetes Recht — die Änderung würde ein Thema berühren, das er nicht bearbeiten darf, und es gibt keine stille Kopie. Der Zugriff wird verweigert
   („nicht gemeinsam bearbeitbar“ im Themeneditor); der Eigentümer kann das Modul in diesem Fall direkt freigeben (Quelle a) oder es klonen. Besteht Co-Author-Recht an *allen* Themen mit dem Modul
   (mindestens eines davon über Co-Author), gilt das abgeleitete Recht. Eine **direkte** Freigabe ist die ausdrückliche Entscheidung des Eigentümers und wird durch die Themenzuordnung nicht
   eingeschränkt. Veröffentlichte Themenversionen verwenden eingefrorene Kopien und sind nie betroffen (ADR 0030).
4. **Rechte.** Eigentümer, direkter Modul-Co-Author und abgeleiteter Co-Author dürfen Karten erstellen/ändern/verschieben/duplizieren/archivieren sowie Quizkonfiguration, Fragenpool und Kompetenzen
   bearbeiten — ausschließlich über die Weboberfläche (Blazor, serverseitig geprüft) und den vom Dienst gelieferten Bearbeitungskontext. **Nicht:** Modulversion veröffentlichen, Lernfreigabe
   (`module_shares`), globale Einreichung, Löschen/Umbenennen des Moduls, Zuweisung. Moduleigentum ändert sich nie; es gibt keine Eigentumsübertragung.
5. **Verwaltung direkter Co-Autoren:** Eigentümer und **direkte** Modul-Co-Autoren fügen hinzu/entfernen und beenden die eigene Mitarbeit (`AddModuleCoAuthorAsync`, `RemoveModuleCoAuthorAsync`,
   `LeaveModuleCoAuthorAsync`); der Eigentümer ist nicht entfernbar, Selbsterteilung nicht möglich, höchstens 50 je Modul. Ein **nur abgeleitetes** Recht verwaltet keine direkten Co-Autoren und
   kann sie nicht „verlassen“. Themen-Co-Autoren werden weiterhin ausschließlich im Themenkontext verwaltet (ADR 0052). Eingeschränkte Lernprofile können nicht eingetragen werden.
6. **Entzug wirkt sofort, auch in offenen Sitzungen.** Jeder Aufruf der Entscheidung prüft die Datenbank. Der Modul-Editor löst den Kontext vor **jedem** Schreibzugriff neu auf
   (`ModuleEditor.StillAllowedAsync`); ein veralteter, beim Öffnen ermittelter Kontext schreibt nichts. Die Dienste (`CardRepository`, `CompetencyService`, `QuizService`) arbeiten unter dem
   übergebenen Eigentümer; ein Aufruf mit der Identität eines entzogenen Benutzers wird dort abgewiesen („nicht gefunden“). **Restfenster:** zwischen der Auflösung und dem Schreiben liegen
   Millisekunden; ein Entzug in genau diesem Fenster kann eine bereits laufende einzelne Operation nicht zurückholen (keine verteilte Transaktion über Berechtigung und Daten). Der Test
   „parallele Bearbeitung während des Entzugs“ belegt, dass nach abgeschlossenem Entzug kein weiterer Schreibzugriff beginnt.
   **Fragenpool und Quizkonfiguration (Klarstellung nach Analyse 2026-11-14):** Die Schreibpfade `QuizService.CreateQuestionAsync`, `UpdateQuestionAsync`, `SetQuestionActiveAsync` und `SaveConfigAsync`
   (sowie das Lesen der Lösungen über `ListQuestionsAsync`) prüfen über `RequireOwnedModuleAsync` → `ResolveModuleAccessAsync` ausschließlich das **Eigentum** des übergebenen Benutzers. Sie sind
   damit wie die Kartenpfade nur unter dem vom Aufrufer gelieferten Eigentümerkontext nutzbar; mit der Identität eines Co-Autors oder entzogenen Benutzers werden sie abgewiesen. Die Entscheidung,
   *welcher* Kontext gilt, trifft allein `ResolveModuleEditOwnerAsync`; im Modul-Editor ruft jeder Pfad vorher `StillAllowedAsync` — **Schreib- und Lesepfade** (Karte laden, Fragenpool laden,
   Archivansicht). Das Panel „Quiz-Einstellungen“ erhält den Kontext über den Parameter `ResolveUser` und löst vor jedem Laden und Speichern neu auf (zuvor verwendete es die Identität des Handelnden;
   Co-Autoren konnten die Quizkonfiguration deshalb weder lesen noch speichern — Fehler behoben, kein neues Recht). REST (`/api/modules/{id}/quiz-*`) und MCP bleiben eigentümergebunden.
7. **REST** unter `/api/modules/{id}/coauthors` (GET, `candidates`, POST, DELETE `{userId}`, `leave`). Die Antwort unterscheidet `access` = `owner` | `direct` | `via_topic`; `direct_co_authors` und
   `derived_access` sind getrennte Listen. Die Karten-/Modul-REST-Routen (`/api/cards`, `/api/modules/{id}/…`) und MCP bleiben unverändert eigentümergebunden (ADR 0007/0052);
   **MCP-Tokens sind von jedem Pfad mit `/coauthors` ausgeschlossen** (Filter in `ApiEndpoints`, unabhängig von der Rolle).
8. **Oberfläche:** Modul-Editor-Bereich „Zusammenarbeit“ (direkte Co-Autoren verwalten, „Zugriff über Themen“ getrennt und mit Themennamen, Sicherheitsabfragen); Themeneditor: Link
   „✏ Karten bearbeiten“ je Modul nur bei gültigem Zugriff, sonst Hinweis „nicht gemeinsam bearbeitbar“; Modulübersicht: Reiter „Gemeinsam bearbeitet“ mit der Zugriffsart („Direkt als Co-Author“ /
   „Über Thema …“), ohne die übrige Bibliothek des Eigentümers offenzulegen. Im Co-Author-Kontext fehlen die Links zur Lernansicht des Moduls (Lernrecht ≠ Bearbeitungsrecht) und die Themenverwendung.

## Konsequenzen

- Eigenständige Module sind gemeinsam bearbeitbar; Themen-Co-Autoren benötigen keine zusätzliche Modulfreigabe.
- Abgeleitete Rechte können nie „veralten“, weil sie nicht gespeichert werden; dafür kostet jede Entscheidung einige Datenbankabfragen (bewusst, für kleine Themen/Module unkritisch).
- Ein Modul mit Mehrfachzuordnung ist für Themen-Co-Autoren nur bearbeitbar, wenn sie an allen beteiligten Themen berechtigt sind — sonst direkte Freigabe oder Klon durch den Eigentümer.
- Direkte Co-Autoren sehen in „Zugriff über Themen“ nur Themen, an denen sie selbst beteiligt sind; nur der Eigentümer sieht alle.
- PostgreSQL und SQLite nutzen dieselben parametrisierten Anweisungen; getestet auf beiden (siehe `docs/testing.md`).

## Leitplanken für Änderungen

- Schreibrechte an fremden Modulen **nur** über `GetModuleEditAccessAsync` (bzw. `ResolveModuleEditOwnerAsync`); nie aus UI-Zustand, Claims oder gemerkten Rollen. Neue Schreibpfade im Modul-Editor
  rufen vorher `StillAllowedAsync`.
- Abgeleitete Rechte nie in `module_coauthors` speichern; Themenfreigabe erzeugt keine Modulfreigabe.
- Co-Author gewährt nie Lernfreigabe, Zuweisung, Veröffentlichung von Themen oder globalen Modulen (Modulversionen seit ADR 0059 erlaubt), Löschen/Umbenennen, Eigentumsübertragung oder Zugriff auf andere Module/Themen des Eigentümers.
- MCP-Tokens verwalten keine Co-Autoren; Karten-REST/MCP bleiben eigentümergebunden.
- Mitziehen: `Schema.cs`, `docs/database-schema.md`, `docs/architecture.md`, `AGENTS.md`, Hilfeseite, README, `ModuleCoAuthorTests.cs`.

## Betroffener Code

- `Services/TopicService.ModuleCoAuthors.cs`, `Services/TopicService.CoAuthors.cs` (`ResolveModuleEditOwnerAsync`)
- `Data/Schema.cs` (`module_coauthors`), `Domain/ModuleCollaboration.cs`
- `Api/TopicEndpoints.cs` (`/modules/{id}/coauthors`), `Api/ApiEndpoints.cs` (MCP-Sperre)
- `Components/Pages/ModuleEditor.razor`, `TopicEditor.razor`, `Home.razor`
- `tests/LearnCards.IntegrationTests/ModuleCoAuthorTests.cs`, `PostgresReadinessTests.cs`
