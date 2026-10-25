# 0008 — KI-Integration mit Offline-Fallback und Token-Budget

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0016](0016-bilder-in-lernkarten.md) (Kartenbilder werden nie in Prompts übernommen; Bilder sind ohne LLM/API-Schlüssel nutzbar), [0011](0011-mehrbenutzerarchitektur.md) (Chat-Verlauf und Quiz-Ergebnisse gehören dem Benutzer), [0015](0015-freigaben-globale-lernmodule.md) (Quiz/Chat arbeiten mit eigenen, freigegebenen und globalen Karten; Offline-Fallback unverändert; Ergebnisse tragen den Kontext private/shared/global)

## Kontext

Chat, Quiz-Generierung und Bewertung nutzen ein LLM. Lokale Entwicklung soll ohne API-Key funktionieren;
LLM-Antworten im JSON-Modus werden bei zu knappem `max_tokens` abgeschnitten.

## Entscheidung

- `OpenAiClient` spricht die Chat-Completions-API per `HttpClient` (`OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_BASE_URL`
  — kompatibel zu Azure OpenAI oder lokalen Endpunkten).
- `CompleteJsonAsync` wiederholt bei abgeschnittener oder ungültiger JSON-Antwort mit **erhöhtem Token-Budget**
  (max. 8000). `QuizService` berechnet das Bewertungsbudget dynamisch aus der Anzahl der Antworten.
- Ohne `OPENAI_API_KEY` laufen `ChatService` und `QuizService` in einem **klar gekennzeichneten Offline-Modus**
  (Fragen aus dem Kartenpool, Stichwort-Heuristik für Bewertung).
- Prompts leben in `QuizService`/`ChatService`. Chat-Verlauf wird pro Benutzer und Karte gespeichert;
  Streaming für API-Clients über SSE (`POST /api/chat/`).

## Konsequenzen

- Zwei Codepfade (online/offline) müssen konsistent bleiben.
- LLM-Ausgaben sind nicht deterministisch; Parser müssen tolerant und Fehlerfälle behandelt sein.

## Leitplanken für Änderungen

- Der Offline-Pfad darf nie entfernt werden; jede neue KI-Funktion braucht einen Fallback und eine Kennzeichnung im Ergebnis.
- LLM-Aufrufe mit JSON-Ausgabe nur über `CompleteJsonAsync`; Tokenbudget an die erwartete Ausgabegröße koppeln.
- Chat-Verlauf wird nur von der Blazor-Oberfläche gespeichert (`ChatPanel`), nicht über `POST /api/chat/` (Prüfbericht F10).
- Nur nötige Daten in Prompts senden (keine Secrets, keine unnötigen personenbezogenen Daten).
- Prompt-Änderungen am Antwortformat müssen die Parser in `QuizService` und die Modelle in `Domain/Models.cs` mitziehen.

## Betroffener Code

- `Services/OpenAiClient.cs`, `QuizService.cs`, `ChatService.cs`, `Components/Shared/ChatPanel.razor`, `QuizPanel.razor`, `QuizRunner.razor`
