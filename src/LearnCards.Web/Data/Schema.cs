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

        yield return $"""
            CREATE TABLE IF NOT EXISTS modules (
                id          {id} PRIMARY KEY,
                name        TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL DEFAULT '',
                icon        TEXT NOT NULL DEFAULT '📚',
                color       TEXT NOT NULL DEFAULT '#2AA79B',
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS cards (
                id           {id} PRIMARY KEY,
                module_id    {id} NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
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
                archived     INTEGER NOT NULL DEFAULT 0,
                sort_order   INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
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
                completed_at TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_quiz_user ON quiz_results(user_sub, completed_at)";
    }
}
