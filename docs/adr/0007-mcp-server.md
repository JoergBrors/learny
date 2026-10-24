# 0007 — Eigenständiger MCP-Server als API-Client

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0013](0013-mcp-benutzergebundene-anmeldung.md) (Authentifizierung: benutzergebundene Tokens statt gemeinsamem Schlüssel; OAuth-Anmeldung über Learny)

## Kontext

KI-Clients (z. B. Claude Desktop) sollen Karten lesen, anlegen und importieren können, ohne eigene Datenbankzugriffe.

## Entscheidung

- `LearnCards.McpServer` ist ein separates Projekt ohne Datenbankzugriff. Es ruft ausschließlich die Web-API
  (`LEARNCARDS_API_URL`, Auth via `X-MCP-Key`) über `LearnCardsApiClient` auf.
- Transporte: `stdio` und/oder HTTP (`/mcp`), konfiguriert über `mcpsettings.json` (`McpSettings.cs`).
- *(überholt durch [0013](0013-mcp-benutzergebundene-anmeldung.md): kein `client_credentials` mehr, Anmeldung bei Learny)* OAuth für HTTP-Clients in `ProprietaryOAuthService`
  inkl. `/.well-known/oauth-authorization-server` und `/oauth/token`.
- Registrierte Tools (`McpProtocol.RegisterTool`): `get_import_schema`, `add_card`, `import_cards`,
  `list_modules`, `list_cards`, `archive_card`, `restore_card`.
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

## Betroffener Code

- `src/LearnCards.McpServer/*`
