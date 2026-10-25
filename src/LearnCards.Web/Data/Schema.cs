namespace LearnCards.Web.Data;

/// <summary>Portables Schema — identische Struktur für SQLite und PostgreSQL.</summary>
public static class Schema
{
    // archived als INTEGER (0/1) und Zeitstempel als ISO-8601-TEXT halten beide Provider identisch.
    public static IEnumerable<string> Statements(string provider)
    {
        var real = provider == "postgres" ? "DOUBLE PRECISION" : "REAL";
        var id = provider == "postgres" ? "UUID" : "TEXT";
        var emptyJsonObject = "'{}'";

        // ─── Benutzer, Identitäten, Rollen, Audit (ADR 0011) ───────────────
        yield return $"""
            CREATE TABLE IF NOT EXISTS users (
                id              {id} PRIMARY KEY,
                display_name    TEXT NOT NULL,
                email           TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL DEFAULT 'active',
                legacy_migrated INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL,
                last_login_at   TEXT NOT NULL
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS user_identities (
                provider   TEXT NOT NULL,
                subject    TEXT NOT NULL,
                user_id    {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at TEXT NOT NULL,
                PRIMARY KEY (provider, subject)
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_user_identities_user ON user_identities(user_id)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS user_roles (
                user_id    {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                role       TEXT NOT NULL,
                granted_by TEXT NULL,
                granted_at TEXT NOT NULL,
                PRIMARY KEY (user_id, role)
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS audit_log (
                id            {id} PRIMARY KEY,
                actor_user_id TEXT NULL,
                action        TEXT NOT NULL,
                target        TEXT NOT NULL DEFAULT '',
                detail        TEXT NOT NULL DEFAULT '',
                created_at    TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_audit_log_created ON audit_log(created_at)";

        // ─── MCP-Zugänge (ADR 0013): benutzergebundene Tokens (nur als Hash gespeichert) und einmalige Übergabecodes ───
        yield return $"""
            CREATE TABLE IF NOT EXISTS mcp_links (
                id           {id} PRIMARY KEY,
                user_id      {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                name         TEXT NOT NULL,
                kind         TEXT NOT NULL,
                token_hash   TEXT NOT NULL UNIQUE,
                token_hint   TEXT NOT NULL DEFAULT '',
                created_at   TEXT NOT NULL,
                expires_at   TEXT NOT NULL,
                last_used_at TEXT NULL,
                revoked_at   TEXT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_mcp_links_user ON mcp_links(user_id)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS mcp_link_codes (
                code_hash   TEXT PRIMARY KEY,
                user_id     {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                client_name TEXT NOT NULL DEFAULT '',
                created_at  TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                used_at     TEXT NULL
            )
            """;

        yield return ModulesTable(provider, "modules");

        // ─── Freigaben und globale Veröffentlichung (ADR 0015) ───
        yield return $"""
            CREATE TABLE IF NOT EXISTS module_shares (
                id                {id} PRIMARY KEY,
                module_id         {id} NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
                recipient_user_id {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                shared_by_user_id TEXT NOT NULL,
                created_at        TEXT NOT NULL,
                revoked_at        TEXT NULL,
                UNIQUE (module_id, recipient_user_id)
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_module_shares_recipient ON module_shares(recipient_user_id)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS module_submissions (
                id                {id} PRIMARY KEY,
                publication_id    {id} NOT NULL,
                kind              TEXT NOT NULL,
                source_module_id  {id} NULL REFERENCES modules(id) ON DELETE SET NULL,
                staged_module_id  {id} NULL REFERENCES modules(id) ON DELETE SET NULL,
                submitter_user_id {id} NULL REFERENCES users(id) ON DELETE SET NULL,
                submitter_name    TEXT NOT NULL,
                module_name       TEXT NOT NULL,
                card_count        INTEGER NOT NULL DEFAULT 0,
                version           INTEGER NOT NULL DEFAULT 1,
                status            TEXT NOT NULL,
                reason            TEXT NOT NULL DEFAULT '',
                submitted_at      TEXT NOT NULL,
                decided_by        TEXT NULL,
                decided_at        TEXT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_module_submissions_status ON module_submissions(status, submitted_at)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS cards (
                id           {id} PRIMARY KEY,
                module_id    {id} NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
                owner_user_id {id} NULL REFERENCES users(id) ON DELETE SET NULL,
                category     TEXT NOT NULL,
                term         TEXT NOT NULL,
                question     TEXT NOT NULL,
                definition   TEXT NOT NULL,
                how_it_works TEXT NOT NULL DEFAULT '',
                context      TEXT NOT NULL DEFAULT '',
                key_fact     TEXT NOT NULL DEFAULT '',
                reference_answer TEXT NOT NULL DEFAULT '',
                chat_prompt  TEXT NOT NULL DEFAULT '',
                official_sources_json TEXT NOT NULL DEFAULT '[]',
                slide_number INTEGER NULL,
                target_time_sec INTEGER NULL,
                quiz_json     TEXT NOT NULL DEFAULT '[]',
                images_json   TEXT NOT NULL DEFAULT '[]',
                images_migrated INTEGER NOT NULL DEFAULT 0,
                quiz_synced INTEGER NOT NULL DEFAULT 0,
                archived     INTEGER NOT NULL DEFAULT 0,
                sort_order   INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            )
            """;

        // ─── Bilder: Metadaten + Datenbank-Speicher (ADR 0017) ───
        yield return $"""
            CREATE TABLE IF NOT EXISTS card_images (
                id          {id} PRIMARY KEY,
                card_id     {id} NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                provider    TEXT NOT NULL,
                storage_key TEXT NOT NULL,
                mime_type   TEXT NOT NULL,
                size_bytes  INTEGER NOT NULL,
                checksum    TEXT NOT NULL DEFAULT '',
                alt_text    TEXT NOT NULL DEFAULT '',
                caption     TEXT NOT NULL DEFAULT '',
                sort_order  INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_card_images_card ON card_images(card_id, sort_order)";
        yield return "CREATE INDEX IF NOT EXISTS ix_card_images_blob ON card_images(provider, storage_key)";

        // Binärdaten des Database-Providers als Base64-Text (IDatabase überträgt Werte textbasiert, siehe ADR 0017)
        yield return """
            CREATE TABLE IF NOT EXISTS image_blobs (
                storage_key TEXT PRIMARY KEY,
                data_base64 TEXT NOT NULL,
                size_bytes  INTEGER NOT NULL,
                created_at  TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_cards_module ON cards(module_id)";
        yield return "CREATE INDEX IF NOT EXISTS ix_cards_category ON cards(module_id, category)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS user_preferences (
                user_sub    TEXT PRIMARY KEY,
                theme       TEXT NOT NULL DEFAULT '',
                updated_at  TEXT NOT NULL
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS user_card_states (
                user_sub      TEXT NOT NULL,
                card_id       {id} NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                is_checked    INTEGER NOT NULL DEFAULT 0,
                marked_review INTEGER NOT NULL DEFAULT 0,
                updated_at    TEXT NOT NULL,
                PRIMARY KEY (user_sub, card_id)
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_user_card_states_user ON user_card_states(user_sub, card_id)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS chat_history (
                id                  {id} PRIMARY KEY,
                user_sub            TEXT NOT NULL,
                card_id             {id} NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                original_question   TEXT NOT NULL DEFAULT '',
                normalized_question TEXT NOT NULL DEFAULT '',
                assistant_answer    TEXT NOT NULL DEFAULT '',
                created_at          TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_chat_history_user_card ON chat_history(user_sub, card_id, created_at)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS quiz_results (
                id           {id} PRIMARY KEY,
                module_id    {id} NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
                card_id      {id} NULL REFERENCES cards(id) ON DELETE CASCADE,
                user_sub     TEXT NOT NULL,
                category     TEXT NOT NULL,
                score        {real} NOT NULL,
                max_score    {real} NOT NULL,
                grade        TEXT NOT NULL,
                feedback     TEXT NOT NULL DEFAULT '',
                answers_json TEXT NOT NULL DEFAULT '[]',
                stats_json   TEXT NOT NULL DEFAULT {emptyJsonObject},
                context      TEXT NOT NULL DEFAULT 'private',
                session_id   TEXT NULL,
                passed       INTEGER NULL,
                pass_percent INTEGER NULL,
                module_version INTEGER NULL,
                completed_at TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_quiz_user ON quiz_results(user_sub, completed_at)";

        // ─── Einheitliches Quizsystem (ADR 0019) ───
        // Persistenter Fragenpool je Modul (bzw. globaler Modulversion); eine Frage hat genau eine Quelle (source).
        yield return $"""
            CREATE TABLE IF NOT EXISTS quiz_questions (
                id             {id} PRIMARY KEY,
                module_id      {id} NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
                card_id        {id} NULL REFERENCES cards(id) ON DELETE CASCADE,
                type           TEXT NOT NULL,
                question       TEXT NOT NULL,
                options_json   TEXT NOT NULL DEFAULT '[]',
                correct_json   TEXT NOT NULL DEFAULT '[]',
                explanation    TEXT NOT NULL DEFAULT '',
                origin         TEXT NOT NULL,
                source         TEXT NOT NULL,
                active         INTEGER NOT NULL DEFAULT 1,
                module_version INTEGER NULL,
                fingerprint    TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                updated_at     TEXT NOT NULL
            )
            """;

        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_quiz_questions_fp ON quiz_questions(module_id, fingerprint)";
        yield return "CREATE INDEX IF NOT EXISTS ix_quiz_questions_card ON quiz_questions(card_id)";

        // Quizkonfiguration: genau eine je Modul (als JSON, damit neue Einstellungen ohne Schemaänderung möglich sind)
        yield return $"""
            CREATE TABLE IF NOT EXISTS quiz_configs (
                module_id   {id} PRIMARY KEY REFERENCES modules(id) ON DELETE CASCADE,
                config_json TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            )
            """;

        // Serverseitige Quizsitzungen (enthalten die Lösungen; Grundlage der verifizierten Bewertung)
        yield return $"""
            CREATE TABLE IF NOT EXISTS quiz_sessions (
                id             {id} PRIMARY KEY,
                user_sub       TEXT NOT NULL,
                module_id      {id} NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
                card_id        {id} NULL REFERENCES cards(id) ON DELETE CASCADE,
                category       TEXT NOT NULL DEFAULT '',
                context        TEXT NOT NULL DEFAULT 'private',
                module_version INTEGER NULL,
                config_json    TEXT NOT NULL,
                questions_json TEXT NOT NULL,
                status         TEXT NOT NULL DEFAULT 'open',
                created_at     TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_quiz_sessions_user ON quiz_sessions(user_sub, created_at)";
    }

    /// <summary>
    /// modules-Tabelle. <c>name</c> ist nur pro Eigentümer eindeutig (Index <c>ux_modules_owner_name</c>
    /// in <see cref="OwnershipIndexStatements"/>). Auch für den SQLite-Rebuild unter anderem Namen genutzt.
    /// </summary>
    public static string ModulesTable(string provider, string tableName)
    {
        var id = provider == "postgres" ? "UUID" : "TEXT";
        return $"""
            CREATE TABLE IF NOT EXISTS {tableName} (
                id            {id} PRIMARY KEY,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                icon          TEXT NOT NULL DEFAULT '📚',
                color         TEXT NOT NULL DEFAULT '#2AA79B',
                owner_user_id {id} NULL REFERENCES users(id) ON DELETE SET NULL,
                scope         TEXT NOT NULL DEFAULT 'private',
                publication_id {id} NULL,
                global_version INTEGER NULL,
                global_status TEXT NULL,
                source_module_id {id} NULL REFERENCES modules(id) ON DELETE SET NULL,
                source_owner_user_id {id} NULL REFERENCES users(id) ON DELETE SET NULL,
                source_owner_name TEXT NULL,
                published_at  TEXT NULL,
                published_by  TEXT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            )
            """;
    }

    /// <summary>
    /// Indizes auf Besitz-Spalten. Müssen NACH den Spalten-Migrationen laufen, weil bestehende Datenbanken
    /// die Spalten <c>owner_user_id</c> erst durch die Migration erhalten.
    /// </summary>
    public static IEnumerable<string> OwnershipIndexStatements()
    {
        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_modules_owner_name ON modules(owner_user_id, name)";
        yield return "CREATE INDEX IF NOT EXISTS ix_modules_owner ON modules(owner_user_id)";
        yield return "CREATE INDEX IF NOT EXISTS ix_cards_owner ON cards(owner_user_id)";
    }

    /// <summary>Indizes auf die Freigabe-/Veröffentlichungsspalten (ADR 0015); laufen nach <c>MigrateSharing*</c>.</summary>
    public static IEnumerable<string> SharingIndexStatements()
    {
        yield return "CREATE INDEX IF NOT EXISTS ix_modules_scope ON modules(scope, global_status)";
        yield return "CREATE INDEX IF NOT EXISTS ix_modules_publication ON modules(publication_id)";
    }
}
