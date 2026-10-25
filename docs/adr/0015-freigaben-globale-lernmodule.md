# 0015 — Benutzerfreigaben, globale Lernmodule und Ergebniszuordnung

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Baut auf:** [0011](0011-mehrbenutzerarchitektur.md) (Eigentum), [0012](0012-rollen-aus-entra-app-roles.md) (Rollen), [0013](0013-mcp-benutzergebundene-anmeldung.md) (MCP)
- **Differenziert:** die Aussage „es gibt keinen gemeinsamen Kartenbestand“ — private Bestände bleiben isoliert, zusätzlich existieren **explizit verwaltete** freigegebene und globale Bereiche.
- **Ergänzt durch:** [0016](0016-bilder-in-lernkarten.md) (Bilder werden in freigegebene und globale Kopien übernommen)
- **Nicht umgesetzt (offen):** Sitzungs-/Betriebsmodus-Härtung ([0014](0014-sitzungs-und-betriebsmodus-haertung.md), Status „Vorgeschlagen“); die bekannten Befunde F1–F4 des [Prüfberichts](../reviews/2026-10-24-mehrbenutzer-pruefbericht.md) bestehen weiter.

## Kontext

Module und Karten waren nach ADR 0011 ausschließlich privat. Benutzer sollen Module gezielt anderen Benutzern zum Lernen überlassen und
Administratoren sollen geprüfte Module für alle veröffentlichen können — ohne dass dabei die Eigentümerfilter aus ADR 0011 aufgeweicht,
private Ergebnisse offengelegt oder vorhandene Ergebnisse gelöscht werden.

## Entscheidung

### Drei Bereiche
| Bereich | Wem gehört der Inhalt? | Wer darf lernen? | Wer darf ändern? |
|---|---|---|---|
| **PRIVAT** | einem Benutzer (`owner_user_id`) | nur der Eigentümer | nur der Eigentümer |
| **FREIGEGEBEN** | weiterhin dem Eigentümer (dasselbe Modul, keine Kopie) | der Eigentümer und aktive Empfänger | nur der Eigentümer |
| **GLOBAL** | niemandem (`owner_user_id IS NULL`, Herkunft als Snapshot) | alle Benutzer, solange die Version aktiv ist | **niemand** über Benutzer-/MCP-Wege; Verwaltung nur durch Administratoren (aktivieren/deaktivieren, ersetzen) |

Die Änderungs-Methoden des Repositories (`UpdateCardAsync`, `SetArchivedAsync`, `DeleteCardAsync`, `DeleteModuleAsync`, `CreateCardAsync` …) behalten ihren **Eigentümerfilter** unverändert. Lesezugriffe auf fremde
Inhalte laufen ausschließlich über **einen** Zugriffsentscheid: `CardRepository.ResolveModuleAccessAsync(userId, moduleId)` → (`private` | `shared` | `global`, `CanEdit`) oder „nicht vorhanden“.
`ListReadableCardsAsync`, `GetReadableCardAsync`, Quiz-, Chat- und Statuspfade verwenden ihn.

### Persönliche Freigaben (Ebene: ganzes Modul)
- Tabelle `module_shares` (Modul, Empfänger, wer freigab, Zeitpunkt, `revoked_at`; eindeutig je Modul+Empfänger). Freigegeben wird immer das **ganze Modul** samt aktiver Karten und statischer Quizze (Kartenebene bewusst nicht vorgesehen).
- Nur der **Eigentümer** darf freigeben/widerrufen (serverseitig geprüft, nie anhand übergebener Eigentümer-IDs). Empfänger müssen aktive Benutzer sein, Selbstfreigabe ist verboten; Empfänger werden über eine Suche (mindestens 3 Zeichen, nur aktive Benutzer, nur Name und E-Mail) gewählt.
- Empfänger dürfen lesen, lernen, Quiz machen, Lernstatus setzen, chatten — **nicht** ändern, archivieren, löschen, überschreiben, weiter freigeben oder einreichen. Archivierte Karten sieht nur der Eigentümer.
- Freigaben übertragen **nichts Persönliches**: Lernstatus, Chat-Verläufe und Ergebnisse bleiben beim jeweiligen Benutzer (`user_sub`); der Eigentümer sieht sie nicht.
- **Widerruf:** setzt `revoked_at`; der Zugriff endet sofort. Bereits erzielte Ergebnisse des Empfängers **bleiben erhalten** und in seinem Verlauf sichtbar. Erneutes Freigeben reaktiviert den Eintrag.

### Einreichung und Veröffentlichung (global)
- Der Eigentümer **reicht ein Modul ein** (`module_submissions`: offen / veröffentlicht / abgelehnt). Dabei wird sofort eine **eingefrorene Kopie** („staged“) mit eigenen IDs erzeugt
  (`modules.scope = 'global'`, `global_status = 'staged'`, Karten inkl. statischer Quizze, nur aktive Karten). Spätere Änderungen am Original verändern die Einreichung nicht.
- Der Administrator sieht **nur diese Kopie** (Vorschau), nie das private Original — die Administratorrolle gibt **keine** Leserechte auf fremde private Inhalte.
- Nur ein **Administrator** (serverseitig `RequireRoleAsync`) kann **veröffentlichen oder ablehnen**; die Entscheidung wird atomar beansprucht (zwei Administratoren können dieselbe Einreichung nicht doppelt entscheiden).
  Ablehnung verwirft die Kopie, das Original bleibt privat/freigegeben unverändert. Es gibt keine Veröffentlichung durch ein Request-Feld.
- Herkunft und Urheber werden als **Snapshot** festgehalten (`source_module_id`, `source_owner_user_id`, `source_owner_name`, `published_at`, `published_by`).

### Globale Veröffentlichungsversion
- Eine **Veröffentlichung** hat eine stabile `publication_id`; jede veröffentlichte Version ist eine **eigene Zeile in `modules`** mit eigener Modul-ID, `global_version` und `global_status`:
  `staged` (eingereicht) → `active` → `superseded` (durch neuere Version ersetzt) oder `deactivated` (zurückgenommen). Die ID des privaten Originals wird **nie** als globale Identität wiederverwendet.
- Nur die **aktive** Version ist lernbar. Eine **Aktualisierung** ist eine erneute Einreichung durch den Eigentümer (neue Version derselben `publication_id`); bei Freigabe wird die bisherige Version `superseded`.
  Bis dahin bleibt die globale Version stabil, auch wenn das Original geändert wird.
- **Rücknahme:** Der Administrator deaktiviert (bzw. reaktiviert die neueste deaktivierte Version). Es wird nichts gelöscht; Ergebnisse und alte Versionen bleiben erhalten.
- Globale Module können von Benutzern weder bearbeitet noch gelöscht, freigegeben oder erneut eingereicht werden.

### Lernergebnisse
- `quiz_results` erhält `context` (`private` | `shared` | `global`) und verweist weiterhin auf die **konkrete Modul-ID** — bei globalen Modulen also auf die konkrete **Version**. Fremdschlüssel, Quiz- und Folienlogik bleiben unverändert.
- Ergebnisse gehören **immer dem lernenden Benutzer** (`user_sub`). Die Ergebnisse eines Empfängers auf einem freigegebenen Modul sieht der Eigentümer **nicht**.
- Eigene Ergebnisse (alle Kontexte) sieht jeder Benutzer über seinen Verlauf; fremde nie.
- **Globale Ergebnisse** (nur Kontext `global`): Administrator und Auditor sehen (a) aggregierte Werte je Version (Versuche, Lernende, Durchschnitt) und (b) eine Detailliste mit Lernendenname, Kategorie, Punkten, Note, Zeitpunkt —
  **ohne Antworten und Feedbacktexte** (Datenminimierung). Jeder Detailzugriff wird protokolliert (`publication.results.read`). Chat-Verläufe und Lernstatus bleiben privat; sie sind keine „Lernergebnisse“ im Sinne dieser ADR.
- **Schutz vorhandener Ergebnisse:** Da Ergebnisse per Fremdschlüssel an Modul/Karte hängen, wird das **Löschen** eines Moduls oder einer Karte abgelehnt (HTTP 409), sobald **andere Benutzer** Ergebnisse darauf haben; archivieren bleibt möglich.
  Veröffentlichte Versionen werden nie gelöscht (nur `superseded`/`deactivated`).

### Rollen und Rechte
| Aktion | `user` | `admin` | `auditor` |
|---|:-:|:-:|:-:|
| Eigene Module bearbeiten, freigeben, Freigaben widerrufen, einreichen | ✔ | ✔ | ✔ |
| Freigegebene und globale Module lernen (inkl. Quiz) | ✔ | ✔ | ✔ |
| Eigene Ergebnisse sehen | ✔ | ✔ | ✔ |
| Fremde private/persönliche Inhalte und Ergebnisse lesen | ✘ | ✘ | ✘ |
| Einreichungen sehen, Vorschau der Kopie | ✘ | ✔ | ✘ |
| Veröffentlichen / ablehnen / deaktivieren / reaktivieren | ✘ | ✔ | ✘ |
| Globale Module aller Versionen und Status einsehen | ✘ | ✔ | ✔ (lesend) |
| Globale Ergebnisse (aggregiert + Detail) einsehen | ✘ | ✔ | ✔ (lesend) |
| Veröffentlichungsprotokoll (`publication.*`, `submission.*`) lesen | ✘ | ✔ (gesamtes Protokoll) | ✔ (nur diese Aktionen) |
| Benutzer-/Rollenverwaltung | ✘ | ✔ | ✘ |

Der **Auditor** ist die vorhandene Rolle `auditor` (keine neue Rolle). Alle administrativen Aktionen prüfen die Rolle serverseitig gegen die DB (`RequireRoleAsync` / `RequireAnyRoleAsync`).

### Protokoll (`audit_log`)
`share.grant`, `share.revoke`, `submission.create`, `publication.reject`, `publication.approve`, `publication.update` (neue Version), `publication.deactivate`, `publication.activate`, `publication.results.read`. Mit Akteur, Ziel-ID, Zeitpunkt;
keine Tokens, keine Antworten, keine Ergebnisinhalte.

### API und MCP
- Neue Pfade: `/api/sharing/*` (Freigaben, Einreichung; nur Browser-Sitzung), `/api/admin/submissions|publications/*` (nur Administrator), `/api/global/*` (Administrator/Auditor, lesend).
  `GET /api/modules/?scope=private|shared|global|all` (Standard `private`, abwärtskompatibel); `GET /api/cards/?module_id=` und `GET /api/cards/{id}` lesen auch freigegebene/globale Inhalte.
- **MCP** (ADR 0013 bleibt): Tokens dürfen weiterhin nur `/api/modules`, `/api/cards`, `/api/import`, `/api/schema`, `/api/mcp/whoami` — also **lesend** auch freigegebene und globale Module (`list_modules` mit `scope`, `list_cards`).
  Freigeben, Einreichen, Veröffentlichen und Ergebnisse sind über MCP **nicht** möglich, auch nicht für das Token eines Administrators. Schreiboperationen (`add_card`, `import_cards`, `archive_card`) wirken nur im eigenen Bestand.
  `add_card`/`import_cards` kennen weiterhin nur den eigenen Bestand. Kein Datenbankzugriff im MCP-Server.

### Migration
Idempotent, ohne Datenverlust: neue Tabellen `module_shares`, `module_submissions`; neue Spalten an `modules` (`scope` mit `DEFAULT 'private'`, `publication_id`, `global_version`, `global_status`, `source_module_id`, `source_owner_user_id`, `source_owner_name`, `published_at`, `published_by`)
und `quiz_results.context` (`DEFAULT 'private'`) in `PostgresDatabase.MigrateSharingAsync` bzw. `SqliteDatabase.MigrateSharing`; Indizes `ix_modules_scope`, `ix_modules_publication` danach (`Schema.SharingIndexStatements`).
Bestehende Module, Karten und Ergebnisse bleiben unverändert (`private`). Unzugeordneter Altbestand (`owner_user_id IS NULL`) ist nur noch mit `scope = 'private'` gemeint; globale Module erscheinen dort nicht.

## Nachtrag 2026-10-25: Ersetzen wählen und Löschen

- Beim Veröffentlichen wählt der Administrator, **welche Veröffentlichung ersetzt wird**: Standard (Aktualisierung ersetzt die eigenen früheren Versionen, Erstveröffentlichung wird eigenständig), „als eigenständig neu“ (`replace_publication_id=new`) oder eine vorhandene Veröffentlichung
  (`POST /api/admin/submissions/{id}/approve` mit `{"replace_publication_id": "<ID>"}`). Die Einreichung wird dann als nächste Version dieser Veröffentlichung geführt; frühere Versionen werden `superseded`. Ein unbekanntes Ziel → 404, die Einreichung bleibt offen.
- Administratoren können eine globale Version **endgültig löschen** (`DELETE /api/admin/global-modules/{moduleId}`): Karten, nicht mehr referenzierte Bilder und die Lernergebnisse dieser Version werden entfernt (Bestätigung in der UI), das private Original des Autors bleibt unberührt; Audit `publication.delete`. Deaktivieren bleibt die schonende Alternative.

## Konsequenzen

- Das globale Modell ist **kopierbasiert**: Eine Veröffentlichung dupliziert Karten (Speicher), dafür sind Version, Ergebnisse und Rücknahme sauber getrennt und die Lern-/Quiz-/Folienlogik funktioniert ohne Sonderfälle.
- Löschen von Modulen/Karten mit Ergebnissen anderer Benutzer ist blockiert (bewusste Einschränkung zum Schutz fremder Ergebnisse).
- Die Benutzersuche zeigt jedem angemeldeten Benutzer Anzeigename und E-Mail aktiver Benutzer (nur bei ≥ 3 Zeichen, höchstens 10 Treffer) — Datenschutzabwägung: nötig für gezielte Freigaben.
- Globale Ergebnisdetails enthalten Namen; Zugriff nur für Administrator/Auditor und protokolliert.
- Bekannte offene Sicherheitsrisiken (Prüfbericht F1–F4, ADR 0014) gelten unverändert. **Vor produktiver Mehrbenutzernutzung ist ADR 0014 umzusetzen**, insbesondere F1 (stiller Dev-Login-Rückfall mit Administratorrechten würde auch den Veröffentlichungsworkflow öffnen).

## Leitplanken für Änderungen

- Lesezugriffe auf fremde Module **nur** über `ResolveModuleAccessAsync` bzw. die davon abgeleiteten `*Readable*`-Methoden; Änderungszugriffe immer mit `ownerId`. Keine Abkürzung über „Admin darf alles“.
- Neue globale Funktionen: `status`/`scope` in jeder Abfrage beachten (`staged`, `superseded`, `deactivated` sind **nicht** lernbar); Tests in `tests/LearnCards.IntegrationTests` (Abschnitt „Freigaben, Veröffentlichung und globale Ergebnisse“) ergänzen.
- Freigaben/Veröffentlichung nie über MCP-Token oder `X-MCP-Key` freischalten (eigene ADR).
- Persönliche Daten (Status, Chat, Ergebnisse) nie über Freigaben mitgeben; Detailansichten für Ergebnisse nur mit Datenminimierung und Protokollierung.
- Veröffentlichte Versionen nie löschen oder IDs wiederverwenden.

## Betroffener Code

- Daten: `Data/Schema.cs`, `PostgresDatabase.cs` (`MigrateSharingAsync`), `SqliteDatabase.cs` (`MigrateSharing`)
- Dienste: `Services/SharingService.cs`, `CardRepository.Sharing.cs`, `CardRepository.cs`, `QuizService.cs`, `ChatService.cs`, `UserService.cs` (`RequireAnyRoleAsync`, `SearchUsersAsync`, `ListPublicationAuditAsync`)
- API: `Api/ApiEndpoints.cs` (`MapSharingEndpoints`, `MapGlobalReviewEndpoints`, Admin-Veröffentlichung)
- UI: `Components/Pages/Home.razor`, `ModulePage.razor`, `Admin.razor`, `Profile.razor`, `Help.razor`, `Components/Shared/SharePanel.razor`
- MCP: `LearnCards.McpServer/Program.cs` (`list_modules` mit `scope`)
