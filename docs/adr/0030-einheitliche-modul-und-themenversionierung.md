# 0030 — Einheitliche Modul- und Themenversionierung mit festgelegten Modulversionen

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ergänzt:** [0027](0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md) (Themenversionen), [0015](0015-freigaben-globale-lernmodule.md) (Freigaben), [0028](0028-themenmodule-json-und-mcp-verwaltung.md), [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md)

## Kontext

Themenversionen (ADR 0027) frieren Modulkopien ein, doch Module selbst hatten keine sichtbaren Versionen. Der Themenentwurf folgte dem *aktuellen* Stand eines Moduls: Wer ein Modul nach der Zuordnung änderte, nahm die Änderung bei der nächsten Themenveröffentlichung unbemerkt mit. Die Themenseite zeigte außerdem für den Eigentümer immer den Entwurf, auch ohne unveröffentlichte Änderungen.

## Entscheidung

1. **Modulversionen.** Ein eigenes privates Modul hat einen bearbeitbaren Entwurf (das Modul selbst) und fortlaufend nummerierte, unveränderliche Versionen (`module_versions`: `module_id`, `version_number`, `snapshot_module_id`, `content_hash`, `note`, `published_at`). Der Inhalt einer Version steht in einer eingefrorenen Modulkopie (`scope = 'topic'`, kein Eigentümer) — derselbe Mechanismus wie bei Themenversionen (`CardRepository.CreateTopicSnapshotModuleAsync`). Versioniert sind Metadaten, Karten, Bilder, Reihenfolge, feste Quizfragen, Quizkonfiguration und Fragenpool. Die stabile `reference_id` (ADR 0028) bleibt die Identität über alle Versionen. Eine neue Version entsteht nur bei inhaltlicher Änderung (`ComputeModuleContentHashAsync`), nie doppelt (409). Den Dienst stellt `ModuleVersionService` bereit.
2. **Themen binden konkrete Modulversionen.** `topic_modules.module_version` hält je Zuordnung die festgelegte Modulversion. Beim Zuordnen, beim JSON-Import neuer Module und beim Ersetzen durch eine eigene Kopie wird der *dann aktuelle* Stand festgelegt (bei Bedarf entsteht dafür automatisch eine Modulversion). Bestehende Entwürfe (Spalte NULL) werden beim ersten Zugriff auf den aktuellen Stand festgeschrieben. Spätere Modulbearbeitung oder neue Modulversionen ändern weder den Themenentwurf noch veröffentlichte Themenversionen; **neuere Stände werden nur ausdrücklich übernommen** (`POST /api/topics/{id}/modules/{moduleId}/adopt`, ohne Versionsangabe = aktueller Stand, sonst eine bestehende Version). Beim Import einer Themen-JSON bleiben vorhandene Festlegungen erhalten; Modulversionen entstehen erst nach allen anderen Schritten (alles oder nichts).
3. **Themenversion = Festlegung.** `TopicService.PublishAsync` veröffentlicht die festgelegten Modulversionen: die eingefrorene Kopie der Modulversion wird direkt in `topic_version_modules` eingetragen (keine neue Kopie des aktuellen Moduls). Themenversionen mit gleicher Modulversion teilen die Kopie (Karten-IDs, Lernstand und bestandene Modulprüfungen bleiben erhalten). Die Verbindung Themenversion → Modulversion ist dauerhaft (`TopicModuleView.Version` im Snapshot).
4. **Änderungserkennung nach Inhalt.** Ob der Entwurf von der Veröffentlichung abweicht, entscheidet ein Inhaltsfingerabdruck (Name, Beschreibung, Module in Reihenfolge mit Modulversion, Prüfungskonfiguration, aktive Themenfragen), der bei jeder Veröffentlichung im Snapshot gespeichert wird (`DraftFingerprint`). Zurückgenommene Änderungen gelten nicht als Änderung. Bei Altversionen ohne Fingerabdruck gilt weiter der Revisionszähler. Die Themenseite (`GetViewAsync`) zeigt dem Eigentümer ohne Versionsangabe die aktuelle Veröffentlichung; der Entwurf erscheint nur bei tatsächlicher Abweichung (oder bei nie veröffentlichten Themen).
5. **Freigaben.** Persönliche Freigabe und globale Veröffentlichung eines Themas verwenden unverändert ausschließlich die eingefrorenen Kopien der festgelegten Modulversionen (Themenkontext, `ResolveModuleAccessAsync`/`CanLearnTopicModuleAsync`); es entstehen keine neuen veränderlichen Modulverweise. Private Ursprungsmodule bleiben geschützt. Widerruf und Rücknahme wirken wie bisher; bereits erzielte Ergebnisse und Nachweise bleiben erhalten.
6. **Lesen eigener Versionen.** Der Eigentümer darf die eingefrorenen Kopien seiner eigenen Modulversionen lesen (`OwnsModuleVersionSnapshotAsync`, nie ändern). Beim Löschen eines Moduls verschwinden nur Versionskopien, die keine Themenversion verwendet.

## Konsequenzen

- Verhalten ändert sich bewusst: Änderungen an einem Modul gelangen nicht mehr automatisch in neue Themenversionen; im Themeneditor erscheinen „Neuere Version verfügbar“/„Unveröffentlichte Änderungen am Modul“ mit der Aktion „Neuen Stand übernehmen“.
- Modulversionen kosten Speicher (eine eingefrorene Kopie je Version, Bilder teilen die Binärdaten).
- Lernziele und Kompetenzzuordnungen (ADR 0031) werden Teil des Inhaltshashes und damit der Modulversion.

## Leitplanken für Änderungen

- Veröffentlichte Modul- und Themenversionen sind unveränderlich; neue Inhalte → neue Version.
- Themen verweisen immer auf eine Modulversion, nie auf den veränderlichen aktuellen Stand; Übernahmen nur ausdrücklich.
- Zugriff nur über `ResolveModuleAccessAsync`/`CanLearnTopicModuleAsync`; keine zweite Snapshot-Mechanik.
- Neue Spalten NULL-fähig, Migrationen idempotent in `PostgresDatabase`/`SqliteDatabase` (ADR 0004); `docs/database-schema.md` pflegen.
- Tests: `ModuleVersionTests.cs`, `TopicViewTests.cs`.

## Betroffener Code

- `Services/ModuleVersionService.cs`, `Services/TopicService*.cs` (`AdoptModuleVersionAsync`, `PublishAsync`, `GetViewAsync`, Fingerabdruck), `Services/CardRepository*.cs`
- `Api/TopicEndpoints.cs` (`/modules/{id}/versions`, `/topics/{id}/modules/{moduleId}/adopt`, `/topics/{id}/view`)
- `Components/Pages/ModuleEditor.razor`, `TopicEditor.razor`, `TopicPage.razor`, `Home.razor`
