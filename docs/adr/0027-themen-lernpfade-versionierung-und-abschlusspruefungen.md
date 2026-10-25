# 0027 — Themen, Lernpfade, Versionierung und Abschlussprüfungen

- **Status:** Teilweise ersetzt durch [0028](0028-themenmodule-json-und-mcp-verwaltung.md) (Punkt 2: nur eigene private Module; Punkt 10: schreibender MCP-Zugriff auf eigene Themenentwürfe). Punkt 5 (Zuweisungen durch den Eigentümer) ist durch [0029](0029-themenzugriff-pflichtzuweisungen-und-themenadministration.md) ersetzt: Zuweisungen vergeben nur Admin und Themen-Admin, an eine konkrete Version. Die übrigen Entscheidungen gelten weiter.
- **Datum:** 2026-10-25
- **Anlass:** Die Hierarchie Thema → Lernmodule → Lernkarten/Modulprüfungen → Abschlussprüfung soll Lernpfade abbilden (z. B. „Microsoft 365 Administration“ aus Entra, Exchange, Teams), ohne Module zu duplizieren.
- **Bezug:** [0004](0004-datenbank-abstraktion.md), [0011](0011-mehrbenutzerarchitektur.md), [0015](0015-freigaben-globale-lernmodule.md), [0019](0019-einheitliches-quizsystem.md), [0021](0021-zertifikate-und-open-badges.md), [0022](0022-polygon-verankerung-und-kostenschranke.md)

## Entscheidung

1. **Datenmodell (nur `CREATE TABLE IF NOT EXISTS`, SQLite und PostgreSQL):**
   `topics` (Entwurf, Eigentümer, Status `active|archived`, `draft_revision`/`published_revision`), `topic_modules` (Zuordnung Thema ↔ Modul mit `sort_order`; ein Modul kann in beliebig vielen Themen liegen, ohne Fremdschlüssel auf `modules`), `topic_exam_configs` (JSON: Modulprüfungen + Abschlussprüfung), `topic_questions` (eigene Themenfragen), `topic_versions` (unveränderliche Versionen mit `snapshot_json`, `version_number`, `global_status`), `topic_version_modules` (Zuordnung Version → eingefrorene Modulkopie mit `content_hash`), `topic_shares`, `topic_assignments`, `topic_user_state` (zuletzt gesehene Version, letzte Aktivität), `topic_exam_attempts` (Versuch ↔ Quizsitzung ↔ Themenversion).
2. **Module werden nie dupliziert, um sie einem Thema zuzuordnen.** Im Entwurf verweist `topic_modules` auf das Originalmodul. Hinzufügen dürfen Eigentümer **eigene** Module und **aktiv global veröffentlichte** Module — nie Module, die nur mit ihnen geteilt wurden (kein Weiterreichen fremder privater Inhalte).
3. **Versionierung:** Änderungen am Entwurf wirken nie auf veröffentlichte Versionen. „Veröffentlichen“ erzeugt eine neue Version mit streng steigender Nummer (`MAX+1`, `UNIQUE(topic_id, version_number)`, eine Zeile mit Status `building` verhindert parallele Veröffentlichung). Pro Modul wird ein Inhalts-Hash der aktiven Karten/Fragen gebildet; **unveränderte Module verwenden dieselbe eingefrorene Kopie** (Lernstand und bestandene Prüfungen bleiben erhalten), geänderte erhalten eine neue Kopie (`scope='topic'`, ohne Eigentümer). Ohne Änderung (`draft_revision == published_revision` und gleiche Hashes) entsteht keine Version. Frühere Versionen bleiben les- und startbar.
4. **Zugriff:** Eingefrorene Themenkopien sind nur über `ResolveModuleAccessAsync` (Kontext `topic`) und `CanLearnTopicModuleAsync` erreichbar: Eigentümer, Freigabeempfänger, Zugewiesene oder bei aktiver globaler Version alle angemeldeten Benutzer. Lernen nur über das Thema; der modulweite `/quiz/start` ist für Themenkopien gesperrt (403), Abgaben ohne Sitzung werden abgelehnt. Es gibt kein „Admin liest alles“. Rollen werden in der Datenbank geprüft, die Benutzer-ID stammt nur aus dem Claim.
5. **Freigaben und Zuweisungen:** Freigabe (`topic_shares`) erlaubt Lernen einer **veröffentlichten** Version; Zuweisung (`topic_assignments`) markiert eine Lernaufgabe und gibt dem Eigentümer Einsicht in den Fortschritt der Zugewiesenen. Beides lässt sich widerrufen; Ergebnisse bleiben erhalten. Freigeben, Zuweisen, Einreichen sowie Freigeben/Ablehnen/Zurücknehmen globaler Versionen werden über `UserService.WriteAuditAsync` protokolliert.
6. **Globale Themen:** Der Eigentümer reicht eine Version ein (`pending_approval`, höchstens eine offene Einreichung je Thema). Nur ein Administrator gibt frei oder lehnt ab (atomare `UPDATE`-Übernahme gegen Doppelentscheidungen). Freigabe setzt die Vorgängerversion auf `superseded`; **Ablehnung lässt die bisherige aktive Version unverändert**. Zurücknahme: `deactivated`. Der Administrator prüft nur die eingefrorene Version, nie private Originale.
7. **Prüfungen auf der bestehenden Quiz-Engine ([0019](0019-einheitliches-quizsystem.md)):** Keine zweite Engine. `QuizService.StartTopicExamSessionAsync` baut eine normale serverseitige Sitzung (nur gespeicherte Fragen, `Origin=fixed`, `Strategy=none`, damit auch ohne OpenAI); Bewertung ausschließlich über `SubmitQuizAsync`/`GradeSessionAsync`, Lösungen verlassen den Server nicht vor der Abgabe. `topic_exam_attempts` bindet jeden Versuch an Thema, **Version** und Modul. Modulprüfung: Fragenzahl, Bestehensgrenze, Wiederholung, Mindestprozent für das nächste Modul. Abschlussprüfung: Fragenzahl und Verteilung `even|proportional|manual`, optional Themenfragen, Freischaltregeln (alle Module abgeschlossen / Modulprüfungen bestanden). Reicht der Pool nicht, wird mit „… nur N passende Fragen verfügbar“ abgelehnt statt zu raten.
8. **Fortschritt** je Benutzer und Version (Module begonnen/abgeschlossen, Modulprüfungen, Abschlussprüfung, Versuche, bestes Ergebnis, letzte Aktivität, „neue Version verfügbar“). Eine neue Modulkopie erscheint wieder als offen, unveränderte bleiben bestanden.
9. **Zertifikate:** Es entstehen **keine Themenzertifikate** und keine Blockchain-Transaktionen; für Kontext `topic` überspringt der `QuizService` die Ausstellung (Modulzertifikate nach [0021](0021-zertifikate-und-open-badges.md) bleiben unberührt). Erweiterungspunkt: `topic_exam_attempts` (bestandener Abschlussversuch je Benutzer/Version) ist die verifizierte Grundlage, auf der ein späteres Themenzertifikat per neuer ADR aufsetzen kann.
10. **API/MCP:** REST unter `/api/topics*` und `/api/admin/topics*` (Eigentümer-/Admin-Prüfung im Service). MCP-Tokens dürfen nur lesen (`list_topics`, `get_topic`, `get_topic_exam_structure`); Freigaben, Zuweisungen, Fortschritt, Versuche, Benutzersuche sowie alle Administrations- und globalen Aktionen sind für Tokens gesperrt (`TokenMayReadTopics`).

## Konsequenzen

- Neue Tabellen ohne Änderung bestehender; Karten-JSON und Import bleiben kompatibel (Themen sind kein Importbestandteil).
- Speicherbedarf wächst nur um geänderte Modulkopien (Hash-Wiederverwendung).
- Der Eigentümer kann Module eines Themas weiter ändern; die Wirkung auf Lernende entsteht erst mit „Veröffentlichen“.
- Löschen eines Moduls entfernt es aus Entwürfen (`topic_modules`); veröffentlichte Versionen behalten ihre Kopie. Themen werden archiviert statt gelöscht.

## Grenzen

- Freitextbewertung modulübergreifender Fragen stützt sich auf die hinterlegte Erklärung der Frage.
- Themenzertifikate und Verankerung sind bewusst nicht umgesetzt (Punkt 9).

## Leitplanken für Änderungen

- Veröffentlichte Versionen und ihre Modulkopien nie nachträglich ändern; neue Nummern nur über die Veröffentlichung.
- Prüfungen ausschließlich über `StartTopicExamSessionAsync` und die bestehende Bewertung; keine Lösungen an Clients vor der Abgabe.
- Hinzufügen fremder Module nur, wenn global aktiv; Zugriff auf Themenkopien nie am `ResolveModuleAccessAsync`/`CanLearnTopicModuleAsync` vorbei.
- Globale Aktionen und Benutzerzuordnung nie über MCP.
- Tests: `TopicE2ETests` (T1–T80), `tests/browser/topics-browser-check.js`.
