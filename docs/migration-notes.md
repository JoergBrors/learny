# Migration von der Python-Ursprungslösung

Diese .NET-10-Lösung ist eine vollständige Neuimplementierung derselben Fachlichkeit
(FastAPI + React + Python-MCP-Server → Blazor Web App + integrierte API + .NET-MCP-Server).
Datenmodell, Karten-JSON-Format und API-Routen sind bewusst kompatibel gehalten.

## Was identisch geblieben ist

- Datenmodell: `modules`, `cards`, `quiz_results` mit denselben Feldern
- Kanonisches Karten-JSON-Format (`module`, `category`, `term`, `question`, `definition`,
  `how_it_works`, `context`, `key_fact`, `chat_prompt`, `archived`, `sort_order`)
- API-Routen unter `/api/*` (Module, Karten, Import, Quiz, Chat) mit denselben Pfaden und
  JSON-Feldnamen (snake_case)
- MCP-Tools (`add_card`, `import_cards`, `list_modules`, `list_cards`, `archive_card`,
  `restore_card`) mit identischen Namen/Schemas
- Grundidee der Auth: OIDC-geschützte API + separater `X-MCP-Key` für MCP-Zugriff

## Was sich geändert hat

| Bereich | Python-Original | .NET-10-Lösung |
|---|---|---|
| Frontend | React 18 + Vite (separates SPA) | Blazor Web App, Server-Interaktivität (eine Einheit mit der API) |
| Backend | FastAPI + SQLAlchemy (async) | ASP.NET Core Minimal API + eigene `IDatabase`-Abstraktion |
| Datenbank lokal | PostgreSQL aus Docker (Pflicht) | SQLite-Datei, kein Docker nötig |
| Datenbank produktiv | PostgreSQL (asyncpg) | PostgreSQL (natives Wire-Protokoll v3, kein ORM) |
| OIDC | `python-jose` gegen JWKS | Eigene RS256-Validierung gegen JWKS (`System.Security.Cryptography`) |
| OpenAI | `openai`-Python-SDK | Eigener `HttpClient`-basierter Chat-Completions-Client |
| MCP-Server | Python (`mcp`-Paket), separater Prozess | .NET-Konsolenanwendung, eigene stdio-JSON-RPC-Implementierung |
| Reverse Proxy | nginx zwingend für Full-Stack | nginx optional (nur für TLS-Terminierung im Docker-Betrieb) |

## Warum keine NuGet-Pakete?

Siehe [architecture.md](architecture.md#warum-ohne-nuget-pakete). Kurzfassung: Damit Build und
Deployment auch in Umgebungen mit eingeschränktem Internetzugriff zuverlässig funktionieren.
Die Code-Struktur (insbesondere `IDatabase`) ist so gehalten, dass ein späterer Wechsel auf
EF Core/Npgsql/das offizielle OpenAI-SDK ohne Änderungen an API oder UI möglich ist.

## Datenmigration von PostgreSQL (Python-Version) zu dieser Lösung

Das Schema ist identisch (`modules`, `cards`, `quiz_results`), daher genügt ein Dump/Restore:

```bash
pg_dump --data-only --table=modules --table=cards --table=quiz_results \
  -h <alter-host> -U learncards learncards > data.sql
psql -h <neuer-host> -U learncards learncards < data.sql
```

Alternativ: Karten über `GET /api/cards/` aus der alten API exportieren und über
`POST /api/import/cards` (bzw. das MCP-Tool `import_cards`) in die neue Lösung importieren —
das Karten-JSON-Format ist unverändert kompatibel.
