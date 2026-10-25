# 0006 — Kanonisches Karten-JSON (snake_case) und Live-Schema

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0016](0016-bilder-in-lernkarten.md) (optionales Feld `images[]` mit Base64-Bildern), [0011](0011-mehrbenutzerarchitektur.md) (Karten gehören einem Eigentümer; das JSON enthält bewusst **kein** Besitzfeld), [0015](0015-freigaben-globale-lernmodule.md) (Karten-JSON bleibt unverändert; globale Kopien übernehmen alle Kartenfelder inkl. Quiz)

## Kontext

Karten werden per UI-Import, REST-API, MCP-Tools und KI-generierten Dateien erzeugt. Alle Wege müssen dasselbe Format nutzen.

## Entscheidung

- JSON ist global **snake_case** (`ConfigureHttpJsonOptions` in `Program.cs`).
- Kartenfelder: `module`, `category`, `term`, `question`, `definition`, `how_it_works`, `context`, `key_fact`,
  `reference_answer`, `chat_prompt`, `official_sources[]` (`title`, `url`, `publisher`), `slide_number`,
  `target_time_sec`, `quiz[]`, `images[]` (optional, ADR 0016), `archived`, `sort_order`.
- Import akzeptiert ein Array oder `{ "cards": [...] }`.
- Das maschinenlesbare Schema wird zentral in `Api/CardImportSchema.cs` erzeugt und live ausgeliefert:
  `GET /api/schema/import/cards`; MCP: Tool `get_import_schema`, HTTP `/schema/import/cards`.
- Vorlagen für Anwender: `src/LearnCards.Web/wwwroot/templates/*.json`, erläutert auf der Help-Seite.

## Konsequenzen

- Ein neues Feld betrifft mehrere Stellen; vergisst man eine, divergieren UI, API und MCP.

## Leitplanken für Änderungen

Neues/geändertes Kartenfeld — **Checkliste**:

1. `Domain/Models.cs` (`Card`, `CardJson`) und `Data/Schema.cs` (siehe [0004](0004-datenbank-abstraktion.md))
2. `Services/CardRepository.cs` (Lesen/Schreiben/Import)
3. `Api/CardImportSchema.cs` (Schema + Beschreibung)
4. MCP-Tools `add_card`/`import_cards` in `LearnCards.McpServer/Program.cs`, falls Eingabeschema betroffen
5. Vorlagen in `wwwroot/templates/`, `Components/Pages/Help.razor`, README-Beispiel; bei Bildfeldern zusätzlich `Domain/CardImages.cs`, MCP-Tool-Texte und ADR 0016
6. Anzeige in `FlashCardView.razor` (und ggf. `ModulePage.razor`)

- Importierte Karten landen immer im Bestand des importierenden Benutzers; `owner_user_id`/`user_sub` im JSON werden ignoriert. Eine vorgegebene `id` wird nur übernommen, wenn sie global noch nicht existiert (sonst neue ID).
- Bestehende Felder nicht umbenennen oder entfernen; neue Felder müssen optional sein, damit alte Dateien importierbar bleiben.
- `demoodule/*.json` müssen weiterhin importierbar sein.

## Betroffener Code

- `Domain/Models.cs`, `Api/CardImportSchema.cs`, `Services/CardRepository.cs`, `wwwroot/templates/*`, MCP `Program.cs`
