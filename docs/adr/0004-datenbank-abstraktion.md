# 0004 — `IDatabase`: SQLite lokal, PostgreSQL produktiv

- **Status:** Akzeptiert
- **Datum:** 2026-10-24
- **Ergänzt durch:** [0011](0011-mehrbenutzerarchitektur.md) (Besitzspalten, Migrationen), [0013](0013-mcp-benutzergebundene-anmeldung.md) (MCP-Tabellen)

## Kontext

Lokale Entwicklung soll ohne Docker laufen; Produktion (Docker, Azure) nutzt PostgreSQL. Kein ORM (siehe [0002](0002-keine-nuget-pakete.md)).

## Entscheidung

- `IDatabase` bietet nur `InitAsync`, `QueryAsync`, `ExecuteAsync` mit gebundenen `@name`-Parametern.
- Werte werden **als Text** übertragen; `DbValue` (`ToText`, `ToBool`, `ToInt`, …) und `CardRepository` konvertieren.
- Auswahl über `DB_PROVIDER` (`auto` → SQLite ohne `POSTGRES_HOST`, sonst PostgreSQL) in `AppConfig`.
- Schema liegt in `Data/Schema.cs` (`CREATE TABLE/INDEX IF NOT EXISTS`) und wird beim Start angelegt.
  Tabellen: `users`, `user_identities`, `user_roles`, `audit_log`, `mcp_links`, `mcp_link_codes`, `modules`, `cards`, `user_preferences`, `user_card_states`, `chat_history`, `quiz_results`
  (Details: [database-schema.md](../database-schema.md)).

## Konsequenzen

- Ein SQL-Dialekt muss auf beiden Providern laufen.
- Es gibt kein Migrationsframework; Schemaänderungen müssen idempotent und rückwärtskompatibel sein.

## Leitplanken für Änderungen

- SQL **immer** mit Parametern, niemals String-Konkatenation.
- Jede Abfrage muss in SQLite **und** PostgreSQL funktionieren (kein providerspezifisches SQL in `CardRepository`).
- PostgreSQL ist die Referenz; Änderungen zuerst dafür entwerfen, SQLite muss nachziehen. Schemaübersicht: [database-schema.md](../database-schema.md).
- Neue Spalte: `CREATE TABLE IF NOT EXISTS` aktualisiert bestehende Tabellen nicht. Daher (1) Spalte in `Schema.cs` aufnehmen,
  (2) in `PostgresDatabase.InitAsync` per `EnsureTableColumnAsync`/`EnsureCardColumnAsync` migrieren, (3) in `SqliteDatabase.InitAsync`
  per `EnsureColumn` migrieren, (4) `docs/database-schema.md` aktualisieren. Neue Spalten brauchen einen `DEFAULT` oder `NULL`.
  Außerdem Modell (`Domain/Models.cs`), Mapping im `CardRepository` und Karten-JSON/Schema ([0006](0006-kanonisches-karten-json.md)) mitziehen.
- Upserts sollen atomar sein (`INSERT … ON CONFLICT`); das aktuelle Lesen-dann-Schreiben liefert auf PostgreSQL bei Gleichzeitigkeit HTTP 500 (Prüfbericht F4, Vorschlag in [0014](0014-sitzungs-und-betriebsmodus-haertung.md)).
- Keine Daten löschen ohne explizite Anforderung; Karten werden bevorzugt archiviert (`archived`).

## Betroffener Code

- `src/LearnCards.Web/Data/*`, `Services/CardRepository.cs`, `Infrastructure/AppConfig.cs`
