# 0019 — Einheitliche Quiz-Engine, konfigurierbare Fragetypen und persistenter Fragenpool

- **Status:** Akzeptiert
- **Datum:** 2026-10-25
- **Ersetzt teilweise:** [0010](0010-folienmodus-und-karten-quiz.md) — die getrennte Quizlogik (statisches Karten-Quiz mit clientseitiger Auswertung vs. KI-Modulquiz). Folienmodus (`slide_number`, `target_time_sec`) und die Felder von `quiz[]` aus 0010 bleiben unverändert.
- **Ergänzt:** [0006](0006-kanonisches-karten-json.md) (quiz[] bleibt Import-/Export-Vertrag), [0008](0008-ki-integration-und-fallback.md) (Offline-Fallback bleibt Pflicht), [0011](0011-mehrbenutzerarchitektur.md), [0015](0015-freigaben-globale-lernmodule.md) (Rechte, eingefrorene Versionen)
- **Vorbereitung für:** Auftrag 2 (WYSIWYG-Editor), Auftrag 3 (Zertifikate/Auditor)

## Kontext (Ist-Zustand, analysiert)

| Aspekt | Kartenquiz (alt) | Modulquiz (alt) |
|---|---|---|
| Fragen erzeugt | statisch aus `cards.quiz_json` oder pro Aufruf per KI (`GetCardQuizAsync`, nicht gespeichert) | KI (`StartQuizAsync`, Freitext, nicht gespeichert); offline direkt `card.question` |
| Fragenspeicher | nur `quiz_json` der Karte; KI-Fragen flüchtig | keiner |
| Bewertung | clientseitig in `QuizRunner` (Treffer zählen) | serverseitig `SubmitQuizAsync` (KI-Prompt oder Stichwort-Heuristik) |
| Ergebnis gespeichert | nein | ja (`quiz_results`) |
| Konfiguration | keine | nur Fragenzahl im Dialog |

Folge: zwei Bewertungswege, Lösungen im Browser sichtbar (kein verifizierbares Ergebnis), keine Wiederverwendung generierter Fragen, kein Fragetyp außer Single Choice/Freitext.

## Entscheidung

1. **Eine Engine:** `QuizService` ist die einzige fachliche Implementierung für Frageauswahl, Sitzung, Antwortvalidierung, Bewertung und Ergebnisspeicherung. `CardQuizPanel` (Kartenquiz) und `QuizPanel` (Modulquiz) sind nur noch Einstiegskomponenten über die gemeinsame Oberfläche `QuizRunner`.
2. **Serverseitige Sitzungen:** `POST /api/quiz/start` (und `POST /api/cards/{id}/quiz`) legt eine Sitzung (`quiz_sessions`) mit den Lösungen **nur serverseitig** an und liefert Fragen ohne Lösung. `POST /api/quiz/submit` bewertet mit der Sitzung (genau einmal abgebbar, 409 bei Wiederholung; fremde Sitzung → 404).
   Ohne `session_id` (ältere Clients) wird die Abgabe als reine Freitext-Sitzung behandelt — dieselbe Bewertungslogik.
3. **Fragetypen:** `single_choice`, `multiple_response` (alles-oder-nichts: exakt die richtige Menge), `free_text`. Auswahlfragen werden lokal exakt bewertet (10 Punkte), Freitext per KI, ohne KI oder bei KI-Fehlern per lokaler Heuristik. Ergebnis: Punkte, Prozent, Note (A–F wie bisher), **bestanden** (Prozent ≥ Bestehensgrenze).
4. **Quizkonfiguration je Modul** (`quiz_configs`, JSON; fehlt sie, gelten kompatible Standardwerte): `enabled`, `allow_multiple_choice`, `origin` (`fixed|ai|mixed`), `strategy` (`none|on_demand|prefer_stored|mix`), `single_choice`/`multiple_response`/`free_text`, `question_count` (1–20), `shuffle_questions`, `shuffle_answers`,
   `scope` (`card|module|both`), `pass_percent` (1–100), `allow_retry`, `show_correct_answers`. **Ungültige Kombinationen werden serverseitig mit Begründung abgelehnt** (z. B. Multiple Choice aus bei aktivem Auswahlformat; aktives Quiz ohne Format; „nur feste Fragen“ mit Generierung); die UI verhindert sie bereits (Auto-Deaktivierung).
5. **Persistenter Fragenpool** `quiz_questions` je Modul (bzw. je eingefrorener Modulversion), optional an eine Karte gebunden: Typ, Frage, Optionen, richtige Indizes, Erklärung/Musterlösung, `origin` (`manual|ai`), `source`, `active`, `module_version`, Fingerprint, Zeitstempel.
   Doppelte Fragen werden über den Fingerprint (Karte, Typ, normalisierter Text, Optionen, richtige Optionen) verhindert (Unique-Index je Modul).
6. **Quelle der Wahrheit (genau ein Ort je Frage):**
   - `source = card_json`: die Frage wird im `quiz[]` der Karte gepflegt (kanonischer Import-/Export-Vertrag, ADR 0006). Der Pool enthält nur eine **Projektion**, die bei jedem Speichern/Import der Karte exakt nachgeführt wird (neu → einfügen, entfallen → entfernen, unverändert → Zeile und Aktivstatus bleiben). Änderungen am Pool sind für diese Fragen gesperrt („über die Karte ändern“).
   - `source = ai`: KI-generiert, nur im Pool. `source = editor`: nur im Pool (Auftrag 2).
   - `cards.quiz_json` wird nie gelöscht. Beim Start überführt eine idempotente Migration (`cards.quiz_synced`) vorhandenes `quiz[]` in den Pool.
7. **Auswahl und Generierung:** gespeicherte, aktive Fragen der erlaubten Typen/Herkunft → je Strategie: `none` nur Gespeichertes · `on_demand` Lücke durch neue Fragen füllen · `prefer_stored` nur generieren, wenn nichts Passendes gespeichert ist · `mix` ≈ halb gespeichert, halb neu.
   Generierung über `OpenAiClient.CompleteJsonAsync` (Tokenbudget nach Fragenzahl); nur erlaubte Typen; jede KI-Frage wird validiert (Typ, Optionenzahl, richtige Indizes, Quellkarte aus der Auswahl) und dedupliziert, ungültige werden verworfen; gültige werden im Pool gespeichert (nur im eigenen Modul).
   **Offline-Fallback:** ohne KI bzw. bei KI-Fehlern werden fehlende Fragen als Freitext aus den Karten abgeleitet — nur wenn Freitext erlaubt ist; die Einstellungen führen nie zu einem stillen Wechsel auf nicht erlaubte Typen (stattdessen Fehlermeldung bzw. Hinweis in `notices`).
8. **Umfang:** Modulquiz: `min(question_count, angefragte Zahl)` aus den gecheckten Karten; Kartenquiz: `min(question_count, max(Anzahl gespeicherter Karten-Fragen, 3))`. `scope` sperrt Karten- bzw. Modulquiz (403). `allow_retry=false`: nach einem Versuch weitere Starts → 409. `show_correct_answers=false`: Ergebnis (und Verlauf) enthalten weder richtige Optionen noch Erklärungen.
9. **Rechte und Versionen:** Konfiguration und Pool ändern nur Eigentümer privater Module (`ResolveModuleAccessAsync.CanEdit`); Freigabeempfänger und alle Lernenden dürfen die Konfiguration lesen und Quizze durchführen, den Pool weder lesen noch ändern (403; er enthält Lösungen). Fremde Module verhalten sich wie „nicht vorhanden“ (404).
   Beim Einreichen werden Konfiguration und **aktive** Pool-Fragen mit der eingefrorenen Version kopiert (neue IDs, Karten-Zuordnung angepasst); danach ändern weder Lernende noch Administratoren sie (403). **Von Lernenden in freigegebenen/globalen Modulen generierte Fragen sind flüchtig** (nur in der Sitzung) — der Pool bleibt unverändert.
   Bereits gespeicherte Ergebnisse bleiben unverändert; neue Ergebnisse speichern zusätzlich `session_id`, `passed`, `pass_percent`, `module_version`.
10. **Karten-JSON bleibt kompatibel:** `single_choice` mit `correct_index` (genau 4 Optionen) unverändert; optional neu: `multiple_response` (3–6 Optionen, `correct_indexes`) und `free_text` (nur `explanation` = Musterlösung). Das Importschema (`/api/schema/import/cards`) bleibt kompatibel (Pflichtfelder gelockert auf `type`, `question`, `explanation`).

## Sicherheitsfolgen / bewusste Änderungen

- `POST /api/cards/{id}/quiz` liefert nicht mehr `correct_index`/`explanation` vor der Abgabe (bisher clientseitig sichtbar). Das ist nötig für ein verifizierbares Ergebnis (Auftrag 3).
- `GET /api/cards/{id}` und die Kartenlisten enthalten weiterhin das vollständige `quiz[]` inkl. Lösungen (Export-Vertrag, auch für Lernende in freigegebenen/globalen Modulen). Für Zertifikate muss das in Auftrag 3 neu bewertet werden (siehe Offene Punkte).

## Schnittstellen für Auftrag 2 und 3 (Service-Ebene, `QuizService`)

Auftrag 2 (Editor): `GetConfigViewAsync`, `SaveConfigAsync`, `ListQuestionsAsync(moduleId, user, cardId?, origin?, active?)`, `CreateQuestionAsync`, `UpdateQuestionAsync` (nicht für `card_json`-Fragen), `SetQuestionActiveAsync`; Validierung `QuizPoolService.Validate`. REST: `GET|PUT /api/modules/{id}/quiz-config`, `GET|POST /api/modules/{id}/quiz-questions`, `PUT …/quiz-questions/{qid}`, `PUT …/quiz-questions/{qid}/active`.
Auftrag 3 (Nachweise): `QuizService.GetVerifiedResultAsync(resultId, user)` → `VerifiedQuizResult` (Ergebnis-ID, Sitzung, Benutzer, Modul, **Modulversion**, Punkte, Prozent, Bestehensgrenze, bestanden, Zeitpunkt) — nur für Ergebnisse aus Sitzungen, nie aus Altdaten. Es werden **keine** Zertifikate erzeugt.

## Verworfene Alternativen

- *Pool als alleinige Wahrheit auch für `quiz[]`*: würde den Karten-JSON-Vertrag und alle Importpfade (REST, MCP, Datei) umbauen; die Projektion hält den Vertrag stabil und vermeidet Doppelpflege durch klare Quellenzuordnung je Frage.
- *Zustandslose Bewertung (Lösungen vom Client)*: nicht manipulationssicher; Zertifikate wären wertlos.
- *Teilpunkte bei Multiple Response*: bewusst nicht in Auftrag 1 (alles-oder-nichts ist eindeutig und nachvollziehbar); später als Konfiguration erweiterbar.

## Leitplanken für Änderungen

- Bewertung nur in `QuizService.GradeSessionAsync`; keine Bewertungslogik in Komponenten.
- Lösungen verlassen den Server nur im Ergebnis und nur, wenn `show_correct_answers` es erlaubt (und im Karten-JSON des Eigentümers/Exports).
- Neue Fragetypen: `QuizTypes`, `CardQuizQuestion.Validate`, `QuizPoolService.Validate`, `QuizService` (Auswahl/Bewertung), `QuizRunner`, `CardImportSchema`, Vorlagen, Help-Seite gemeinsam ändern.
- Änderungen am Pool von `card_json`-Fragen nur über `SyncCardQuizAsync`.

## Nachtrag 2026-10-25: Stabilisierung vor dem Editor (Auftrag 1b)

**Schnittstellen für den Editor (Auftrag 2)** — vollständig vorhanden, keine zweite Fragenverwaltung:
`GET|PUT /api/modules/{id}/quiz-config`; `GET /api/modules/{id}/quiz-questions` (Filter `card_id`, `origin`, `active`); `POST /api/modules/{id}/quiz-questions` (neue Frage: `source=editor`, `origin=manual`, optional `card_id` desselben Moduls);
`PUT /api/modules/{id}/quiz-questions/{qid}` (Typ, Text, Optionen, richtige Indizes, Erklärung, Karten-Zuordnung; für `card_json`-Fragen abgelehnt — „über die Karte ändern“); `PUT …/{qid}/active`. Nur Eigentümer privater Module (Empfänger 403, fremde 404, globale Versionen 403); ungültige Eingaben und doppelte Fragen → 400.

**Quelle der Wahrheit abgesichert:** Das Speichern einer Karte (`SyncCardQuizAsync`) ändert ausschließlich `source=card_json`-Fragen dieser Karte; Editor- und KI-Fragen (auch wenn sie derselben Karte zugeordnet sind) bleiben unberührt (Test).

**KI-Fragen des Eigentümers** werden dauerhaft gespeichert und wiederverwendet (Strategien `on_demand`, `prefer_stored`, `mix`). Für Freigabeempfänger und Lernende in globalen Modulen bleibt die Generierung bewusst flüchtig; eine persönliche Fragenpersistenz für sie ist **nicht** Teil dieser Entscheidung und müsste als getrennte Erweiterung entschieden werden
(z. B. benutzerbezogene Pool-Ebene, ohne die eingefrorene Version zu verändern).

**Sitzungsbereinigung:** Eine Sitzung ist 4 Stunden gültig (danach 410 bei der Abgabe). Sitzungen, die älter als 24 Stunden sind (abgegeben oder abgelaufen), werden beim Start und höchstens alle 10 Minuten beim Quizstart in begrenzten Stapeln (≤ 4 × 500) gelöscht. Gültige Sitzungen und alle gespeicherten Ergebnisse (`quiz_results`, ohne Fremdschlüssel auf Sitzungen) bleiben erhalten.

**Sicherheitsgrenze (wichtig für Auftrag 3):** Der aktuelle Quizmodus gilt **nicht** automatisch als manipulationssichere Zertifikatsprüfung. Lösungen sind vor einer Prüfung auf diesen Wegen sichtbar:
1. `GET /api/cards/{id}` und `GET /api/cards/?module_id=…` liefern `quiz[]` inkl. `correct_index`/`correct_indexes`/`explanation` an jeden, der die Karte lesen darf — Eigentümer, Freigabeempfänger **und Lernende in globalen Modulen** (Import-/Export-Vertrag, ADR 0006). Das gilt auch für MCP-Tokens (`/api/cards` ist für Tokens erlaubt, das Werkzeug `list_cards` gibt jedoch nur Metadaten aus).
2. `GET /api/modules/{id}/quiz-questions` zeigt Lösungen dem **Eigentümer** (Lernende erhalten 403).
3. Freitext-Musterlösungen (`explanation`, `reference_answer`) stehen im Karten-JSON.
4. Die Antworten von `/quiz/start` und `/cards/{id}/quiz` enthalten keine Lösungen (getestet); Ergebnisse zeigen Lösungen nur, wenn `show_correct_answers` es erlaubt. Der Verlauf speichert die bereinigte Fassung.
5. Eine Sitzung kann beliebig oft neu gestartet werden (sofern Wiederholungen erlaubt sind) und Fragen sind nach dem ersten Versuch bekannt.
Auftrag 3 muss für zertifizierende Quizze daher mindestens entscheiden: Lösungen aus dem Karten-JSON für Lernende ausblenden (eigene Prüfungsfragen, Prüfungspool getrennt vom Lernstoff), Versuche begrenzen, Fragenauswahl pro Versuch zufällig/neu, Ergebnisse nur über `GetVerifiedResultAsync`.

## Nachtrag 2026-10-26: Nutzung durch den Karteneditor (Auftrag 2)

Der visuelle Karteneditor ([ADR 0020](0020-sichere-visuelle-bearbeitung-formatierter-lerninhalte.md)) verwendet die vorhandenen Schnittstellen unverändert (`ListQuestionsAsync`, `CreateQuestionAsync`, `UpdateQuestionAsync`, `SetQuestionActiveAsync`, Konfiguration). Neu ist nur `QuizPoolService.CopyCardQuestionsAsync` (Editor-/KI-Fragen beim Duplizieren einer Karte).
`card_json`-Fragen werden im Editor als Teil der Karte bearbeitet (Speichern mit der Karte); `ai`/`editor`-Fragen separat und sofort — die Oberfläche beschriftet beide Bereiche. Die Quizbewertung und die Sicherheitsgrenze (Lösungen im Karten-JSON sichtbar) sind unverändert.

## Offene Punkte

- Lernende sehen `quiz[]`-Lösungen im Karten-JSON freigegebener/globaler Module (Vertrag aus ADR 0006); für zertifizierende Quizze (Auftrag 3) ggf. Lösungen dort ausblenden.
- Teilpunkte für Multiple Response und Fragen-Gewichtung.
- Sitzungen: Bereinigung ist umgesetzt (siehe Nachtrag); die Aufbewahrung (4 h gültig / 24 h) ist fest codiert, nicht konfigurierbar.

## Betroffener Code

`Domain/Quiz.cs`, `Domain/Models.cs` (`CardQuizQuestion`, DTOs), `Services/QuizService.cs`, `Services/QuizPoolService.cs`, `Services/CardRepository*.cs` (Sync, Kopie bei Einreichung), `Data/Schema.cs`/`PostgresDatabase.cs`/`SqliteDatabase.cs`, `Api/ApiEndpoints.cs`, `Api/CardImportSchema.cs`,
`Components/Shared/QuizRunner.razor`, `QuizPanel.razor`, `CardQuizPanel.razor`, `QuizConfigPanel.razor`, `Components/Pages/ModulePage.razor`, `Help.razor`, `wwwroot/app.css`.
