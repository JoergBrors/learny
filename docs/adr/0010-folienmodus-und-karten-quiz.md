# 0010 — Folienmodus und statische Karten-Quizze

- **Status:** Akzeptiert — Quizlogik teilweise ersetzt durch [0019](0019-einheitliches-quizsystem.md) (Folienmodus und `quiz[]`-Felder gelten weiter)
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0016](0016-bilder-in-lernkarten.md) (Bilder erscheinen auch im Folienmodus), [0011](0011-mehrbenutzerarchitektur.md) (Karten und ihr statisches Quiz gehören dem Eigentümer der Karte), [0015](0015-freigaben-globale-lernmodule.md) (Folienmodus und Karten-Quiz funktionieren auch in freigegebenen und globalen Modulen)

## Kontext

Neben dem klassischen Karteikarten-Lernen sollen Foliendecks (Schulungs-/Prüfungsvorbereitung) mit Zeitvorgabe und
Seitenquiz abbildbar sein. Per LLM erzeugte Quizze ([0008](0008-ki-integration-und-fallback.md)) sind für feste
Folien zu unzuverlässig und brauchen einen API-Key.

## Entscheidung

- Karten können `slide_number`, `target_time_sec` und ein statisches `quiz[]` (`CardQuizQuestion`, z. B. `single_choice` mit
  `correct_index` und `explanation`) enthalten; diese Felder sind Teil des kanonischen JSON ([0006](0006-kanonisches-karten-json.md)).
- `CardQuizPanel.razor` spielt das statische Quiz direkt auf der Karte (Folienmodus) ohne LLM ab.
- Modulweite, KI-gestützte Quizze (`QuizPanel`/`QuizRunner`, `/api/quiz/*`) bleiben davon getrennt;
  `POST /api/cards/{id}/quiz` erzeugt Quizfragen für eine einzelne Karte.
- Beispiel: `demoodule/kvno_folien_lernkarten.json`, Vorlage `wwwroot/templates/slide-deck-template.json`.

## Konsequenzen

- Zwei Quiz-Arten mit unterschiedlicher Herkunft (statisch vs. LLM) und getrennter Auswertung.

## Leitplanken für Änderungen

- Statische Quizze dürfen nie einen LLM-Aufruf voraussetzen.
- Neue Fragetypen: `CardQuizQuestion`, Schema (`CardImportSchema.cs`), Vorlage, `CardQuizPanel.razor` und Help-Seite gemeinsam ändern.
- `correct_index` ist 0-basiert; Validierung beim Import beibehalten.

## Betroffener Code

- `Domain/Models.cs`, `Components/Shared/CardQuizPanel.razor`, `FlashCardView.razor`, `Api/CardImportSchema.cs`, `wwwroot/templates/slide-deck-template.json`

## Nachtrag 2026-10-25

Die in dieser ADR beschriebene Trennung von Karten-Quiz (statisch, clientseitig ausgewertet) und Modul-Quiz (KI, Freitext) ist durch die **einheitliche Quiz-Engine** ([ADR 0019](0019-einheitliches-quizsystem.md)) abgelöst: gemeinsame Sitzung, Bewertung, Konfiguration und Ergebnisspeicherung.
`CardQuizPanel` bleibt als Einstiegskomponente erhalten. `quiz[]` bleibt der kanonische Import-/Export-Vertrag (jetzt optional auch `multiple_response` und `free_text`); die Fragen werden zusätzlich im Fragenpool geführt.
