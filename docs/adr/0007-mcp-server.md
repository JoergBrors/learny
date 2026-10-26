# 0007 — Eigenständiger MCP-Server als API-Client

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** *Nachtrag 2026-10-26 unten* (`get_card`, `update_card`, `export_cards`), [0016](0016-bilder-in-lernkarten.md) (`add_card`/`import_cards` mit `images[]`, keine Bildlogik im MCP-Server), [0013](0013-mcp-benutzergebundene-anmeldung.md) (Authentifizierung: benutzergebundene Tokens statt gemeinsamem Schlüssel; OAuth-Anmeldung über Learny), [0015](0015-freigaben-globale-lernmodule.md) (`list_modules` mit `scope`)

## Kontext

KI-Clients (z. B. Claude Desktop) sollen Karten lesen, anlegen und importieren können, ohne eigene Datenbankzugriffe.

## Entscheidung

- `LearnCards.McpServer` ist ein separates Projekt ohne Datenbankzugriff. Es ruft ausschließlich die Web-API
  (`LEARNCARDS_API_URL`, Auth via `X-MCP-Key`) über `LearnCardsApiClient` auf.
- Transporte: `stdio` und/oder HTTP (`/mcp`), konfiguriert über `mcpsettings.json` (`McpSettings.cs`).
- *(überholt durch [0013](0013-mcp-benutzergebundene-anmeldung.md): kein `client_credentials` mehr, Anmeldung bei Learny)* OAuth für HTTP-Clients in `ProprietaryOAuthService`
  inkl. `/.well-known/oauth-authorization-server` und `/oauth/token`.
- Registrierte Tools (`McpProtocol.RegisterTool`): `get_import_schema`, `whoami` (seit ADR 0013), `add_card`, `import_cards`,
  `list_modules` (seit ADR 0015 mit `scope`), `list_cards`, `archive_card`, `restore_card`; seit dem Nachtrag 2026-10-26 `get_card`, `update_card`, `export_cards`.
> **Stand 2026-10-24:** Die Authentifizierung per gemeinsamem `X-MCP-Key` ist durch [ADR 0013](0013-mcp-benutzergebundene-anmeldung.md) ersetzt: Daten-Aufrufe laufen mit dem Token des Benutzers. Zusätzlich gibt es das Tool `whoami`.

- Clients werden angewiesen, vor dem Import das Schema abzurufen und keine Felder zu erfinden.
- Im IIS-Betrieb startet der MCP-Client den Server selbst (`learncards-mcp.cmd`); kein Windows-Dienst.

## Konsequenzen

- Jede neue Fähigkeit braucht erst einen Web-API-Endpunkt, dann ein MCP-Tool.
- Das MCP-Protokoll ist selbst implementiert (`McpProtocol.cs`), nicht über ein SDK.

## Leitplanken für Änderungen

- Neues Tool: `engine.RegisterTool(new ToolDefinition(...), handler)` in `Program.cs`, klare Beschreibung und JSON-Schema der Argumente.
- **Keine** Datenbank- oder Domänenlogik in den MCP-Server duplizieren — immer über die API.
- Secrets (`mcpsettings.json`, Client-Secrets) nie committen; nur `*.example.json` und `deploy/iis/*`-Templates.
- Zerstörende Tools (Löschen) nur nach ausdrücklicher Anforderung; vorhandene Tools archivieren statt löschen.

## Nachtrag 2026-10-26 — Karten vollständig lesen, ändern, exportieren

Drei Werkzeuge ergänzen die bestehenden (keine Änderung der Architektur, keine neuen Token-Präfixe; `/api/cards` und `/api/modules` waren bereits erlaubt):

- `get_card` → `GET /api/cards/{id}?include_image_data=true`: alle kanonischen Felder inkl. `quiz[]` und `images[]` mit echtem Base64; zusätzlich `module` (Name), `module_context` (`private|shared|global|topic`) und `can_edit`. Leserechte nur über `ResolveModuleAccessAsync` (eigen, freigegeben, global, Themenkontext); keine Speicherschlüssel/Provider/SAS-URLs.
- `update_card` → `PUT /api/cards/{id}` (der einzige Schreibpfad, `CardRepository.UpdateCardAsync`): nur Karten eigener privater Module. Freigegebene/globale/eingefrorene Themenkarten gehören nie dem Benutzer → lesbar: **404** mit `code: card_read_only` (Vertrag unverändert), sonst **404**. Konflikterkennung über `expected_updated_at` → Header `X-Card-Updated-At` (409 `card_conflict`). Bildsemantik unverändert (ADR 0016): `images` fehlt = behalten, `[]` = entfernen, Liste = ersetzen (Einträge nur mit `image_id` behalten ein Bild). Textfelder, `archived` und `sort_order` werden vollständig ersetzt — Clients senden die ganze, mit `get_card` gelesene Karte. `quiz[]` läuft über `SyncCardQuizAsync` (ADR 0019).
- `export_cards` → `GET /api/modules/{id}/export/cards?include_archived&offset&limit` (`ModuleTransferService.ExportCardsAsync`): kanonisches `{"cards":[…]}`, direkt in `import_cards` einsetzbar. Eigene Module und aktive globale Module wie `export_module` (freigegebene Module anderer sind nicht exportierbar); IDs nur bei eigenen Modulen; keine Eigentümerangaben; archivierte Karten nur für den Eigentümer. **Stapel:** höchstens 200 Karten (Standard 50) und rund 4 MiB Base64-Bilder je Aufruf (mindestens eine Karte); Antwort mit `total`, `offset`, `count`, `has_more`, `next_offset`; stabile Sortierung (`sort_order`, Kategorie, Begriff, ID).

Der MCP-Server bleibt ein dünner API-Client; Veröffentlichen, Freigeben, Löschen und Administration bleiben außerhalb von MCP.

## Betroffener Code

- `src/LearnCards.McpServer/*`
