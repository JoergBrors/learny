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
                revoked_at   TEXT NULL,
                hash_alg     TEXT NOT NULL DEFAULT 'sha256',
                key_id       TEXT NOT NULL DEFAULT '',
                row_mac      TEXT NOT NULL DEFAULT ''
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_mcp_links_user ON mcp_links(user_id)";
        // ADR 0025: keine Token-Teile in der Datenbank — alte Hinweise (letzte Zeichen des Tokens) werden durch eine Kennung aus der Zeilen-ID ersetzt
        yield return "UPDATE mcp_links SET token_hint = '#' || SUBSTR(CAST(id AS TEXT), 1, 4) WHERE token_hint <> '' AND token_hint NOT LIKE '#%'";

        yield return $"""
            CREATE TABLE IF NOT EXISTS mcp_link_codes (
                code_hash   TEXT PRIMARY KEY,
                user_id     {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                client_name TEXT NOT NULL DEFAULT '',
                created_at  TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                used_at     TEXT NULL,
                key_id      TEXT NOT NULL DEFAULT '',
                row_mac     TEXT NOT NULL DEFAULT ''
            )
            """;

        // ADR 0025: dynamisch registrierte öffentliche OAuth-Clients des MCP-Servers — Datensatz AES-256-GCM-verschlüsselt (Schlüssel aus der .env), mit Zeilen-MAC
        yield return """
            CREATE TABLE IF NOT EXISTS mcp_oauth_clients (
                client_id    TEXT PRIMARY KEY,
                key_id       TEXT NOT NULL,
                record_enc   TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                last_used_at TEXT NULL,
                row_mac      TEXT NOT NULL
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
                competencies_json TEXT NOT NULL DEFAULT '[]',
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

        // ─── Zertifikate, Open Badges und Blockchain-Verankerung (ADR 0021/0022) ───
        // Ein Verankerungs-Batch bündelt viele Nachweise unter einer Merkle-Wurzel (eine Transaktion). Kosten und Netzwerk gehören eindeutig zum Batch.
        yield return $"""
            CREATE TABLE IF NOT EXISTS anchor_batches (
                id                 {id} PRIMARY KEY,
                network            TEXT NOT NULL,
                chain_id           INTEGER NOT NULL,
                merkle_root        TEXT NOT NULL,
                leaf_count         INTEGER NOT NULL,
                status             TEXT NOT NULL,
                tx_hash            TEXT NULL,
                nonce              TEXT NULL,
                raw_tx             TEXT NULL,
                block_number       TEXT NULL,
                gas_limit          TEXT NULL,
                gas_used           TEXT NULL,
                max_fee_per_gas    TEXT NULL,
                effective_gas_price TEXT NULL,
                fee_wei            TEXT NULL,
                rate_eur_per_pol   {real} NULL,
                rate_at            TEXT NULL,
                est_total_eur      {real} NULL,
                fee_eur            {real} NULL,
                cost_per_cert_eur  {real} NULL,
                simulated_cost_eur {real} NULL,
                saved_eur          {real} NULL,
                simulated          INTEGER NOT NULL DEFAULT 0,
                error              TEXT NOT NULL DEFAULT '',
                attempts           INTEGER NOT NULL DEFAULT 0,
                created_at         TEXT NOT NULL,
                submitted_at       TEXT NULL,
                confirmed_at       TEXT NULL,
                updated_at         TEXT NOT NULL
            )
            """;

        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_anchor_batches_root ON anchor_batches(network, merkle_root)";
        yield return "CREATE INDEX IF NOT EXISTS ix_anchor_batches_status ON anchor_batches(status, network)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS certificates (
                id                 {id} PRIMARY KEY,
                number             TEXT NOT NULL,
                user_id            {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                module_id          {id} NOT NULL,
                module_name        TEXT NOT NULL,
                module_version     INTEGER NOT NULL DEFAULT 0,
                module_scope       TEXT NOT NULL DEFAULT 'private',
                result_id          {id} NULL,
                learner_name       TEXT NOT NULL,
                score_percent      {real} NOT NULL,
                pass_percent       INTEGER NOT NULL,
                issued_at          TEXT NOT NULL,
                badge_jws          TEXT NOT NULL,
                badge_hash         TEXT NOT NULL,
                leaf_hash          TEXT NOT NULL,
                status             TEXT NOT NULL DEFAULT 'active',
                revoked_at         TEXT NULL,
                revoked_reason     TEXT NOT NULL DEFAULT '',
                revoked_by         TEXT NULL,
                chain_status       TEXT NOT NULL DEFAULT 'none',
                chain_network      TEXT NOT NULL DEFAULT '',
                chain_id           INTEGER NOT NULL DEFAULT 0,
                batch_id           {id} NULL REFERENCES anchor_batches(id) ON DELETE SET NULL,
                merkle_proof_json  TEXT NULL,
                chain_error        TEXT NOT NULL DEFAULT '',
                chain_attempts     INTEGER NOT NULL DEFAULT 0,
                chain_updated_at   TEXT NULL,
                anchored_at        TEXT NULL,
                snapshot_id        {id} NULL,
                snapshot_hash      TEXT NOT NULL DEFAULT '',
                kind               TEXT NOT NULL DEFAULT 'module',
                clr_jws            TEXT NULL,
                clr_hash           TEXT NOT NULL DEFAULT '',
                completed_at       TEXT NULL,
                is_test            INTEGER NOT NULL DEFAULT 0,
                issued_by          TEXT NULL,
                issued_by_name     TEXT NOT NULL DEFAULT '',
                issue_source       TEXT NOT NULL DEFAULT 'unknown',
                parent_id          TEXT NULL
            )
            """;

        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_certificates_number ON certificates(number)";
        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_certificates_user_module ON certificates(user_id, module_id, module_version)";
        yield return "CREATE INDEX IF NOT EXISTS ix_certificates_chain ON certificates(chain_status, chain_network)";
        yield return "CREATE INDEX IF NOT EXISTS ix_certificates_user ON certificates(user_id, issued_at)";
        yield return "CREATE INDEX IF NOT EXISTS ix_certificates_batch ON certificates(batch_id)";
        yield return "CREATE INDEX IF NOT EXISTS ix_certificates_issued ON certificates(issued_at)";   // Ausstellungsregister (ADR 0037): Sortierung/Zeitfilter
        yield return "CREATE INDEX IF NOT EXISTS ix_certificates_badge_hash ON certificates(badge_hash)";   // Suche nach Fingerprint
        yield return "CREATE INDEX IF NOT EXISTS ix_certificates_clr_hash ON certificates(clr_hash)";

        // Administrativ änderbare Betriebseinstellungen (Blockchain). Nie Geheimnisse; nur Werte, die zusätzlich zur Umgebungskonfiguration gelten dürfen.
        yield return """
            CREATE TABLE IF NOT EXISTS app_settings (
                name        TEXT PRIMARY KEY,
                value       TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                updated_by  TEXT NULL
            )
            """;

        // ─── Themen, Lernpfade, Versionierung und Abschlussprüfungen (ADR 0027) ───
        // Absichtlich keine Fremdschlüssel auf modules: Die Tabelle wird bei alten Datenbanken von Migrationen neu aufgebaut; verwaiste Zuordnungen werden beim Lesen ausgeblendet.
        foreach (var statement in TopicStatements(id, real)) yield return statement;
    }

    private static IEnumerable<string> TopicStatements(string id, string real)
    {
        yield return $"""
            CREATE TABLE IF NOT EXISTS topics (
                id                 {id} PRIMARY KEY,
                owner_user_id      {id} NULL REFERENCES users(id) ON DELETE SET NULL,
                name               TEXT NOT NULL,
                description        TEXT NOT NULL DEFAULT '',
                status             TEXT NOT NULL DEFAULT 'active',
                draft_revision     INTEGER NOT NULL DEFAULT 1,
                published_revision INTEGER NOT NULL DEFAULT 0,
                global_version     INTEGER NOT NULL DEFAULT 0,
                created_at         TEXT NOT NULL,
                updated_at         TEXT NOT NULL
            )
            """;
        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_topics_owner_name ON topics(owner_user_id, name)";
        yield return "CREATE INDEX IF NOT EXISTS ix_topics_owner ON topics(owner_user_id, status)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_modules (
                topic_id   {id} NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                module_id  {id} NOT NULL,
                sort_order INTEGER NOT NULL,
                added_at   TEXT NOT NULL,
                PRIMARY KEY (topic_id, module_id)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_modules_module ON topic_modules(module_id)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_exam_configs (
                topic_id    {id} PRIMARY KEY REFERENCES topics(id) ON DELETE CASCADE,
                config_json TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_questions (
                id           {id} PRIMARY KEY,
                topic_id     {id} NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                type         TEXT NOT NULL,
                question     TEXT NOT NULL,
                options_json TEXT NOT NULL DEFAULT '[]',
                correct_json TEXT NOT NULL DEFAULT '[]',
                explanation  TEXT NOT NULL DEFAULT '',
                active       INTEGER NOT NULL DEFAULT 1,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_questions_topic ON topic_questions(topic_id, active)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_versions (
                id              {id} PRIMARY KEY,
                topic_id        {id} NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                version_number  INTEGER NOT NULL,
                status          TEXT NOT NULL,
                global_status   TEXT NOT NULL DEFAULT 'none',
                name            TEXT NOT NULL,
                description     TEXT NOT NULL DEFAULT '',
                snapshot_json   TEXT NOT NULL DEFAULT '{"{}"}',
                created_at      TEXT NOT NULL,
                created_by      TEXT NULL,
                published_at    TEXT NULL,
                published_by    TEXT NULL,
                submitted_at    TEXT NULL,
                decided_at      TEXT NULL,
                decided_by      TEXT NULL,
                decision_reason TEXT NOT NULL DEFAULT '',
                UNIQUE (topic_id, version_number)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_versions_global ON topic_versions(global_status, topic_id)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_version_modules (
                version_id       {id} NOT NULL REFERENCES topic_versions(id) ON DELETE CASCADE,
                module_id        {id} NOT NULL,
                role             TEXT NOT NULL DEFAULT 'module',
                position         INTEGER NOT NULL,
                source_module_id {id} NULL,
                content_hash     TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (version_id, module_id)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_version_modules_module ON topic_version_modules(module_id)";

        // Modulversionen (ADR 0030): unveränderliche Veröffentlichungsstände eigener Module; der Inhalt liegt in einer eingefrorenen Modulkopie (scope = 'topic')
        yield return $"""
            CREATE TABLE IF NOT EXISTS module_versions (
                id                 {id} PRIMARY KEY,
                module_id          {id} NOT NULL,
                version_number     INTEGER NOT NULL,
                snapshot_module_id {id} NOT NULL,
                content_hash       TEXT NOT NULL,
                note               TEXT NOT NULL DEFAULT '',
                published_at       TEXT NOT NULL,
                published_by       TEXT NULL,
                UNIQUE (module_id, version_number)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_module_versions_snapshot ON module_versions(snapshot_module_id)";

        // ─── Learny-PKI (ADR 0035): Zertifikatsregister, Widerrufsregister, CRL-Historie, Ereignisse; private Schlüssel liegen nie in der Datenbank ───
        yield return $"""
            CREATE TABLE IF NOT EXISTS pki_state (
                id              INTEGER PRIMARY KEY,
                status          TEXT NOT NULL,
                initialized_at  TEXT NULL,
                initialized_by  TEXT NULL,
                root_cert_id    {id} NULL,
                issuing_cert_id {id} NULL,
                key_algorithm   TEXT NOT NULL DEFAULT 'rsa',
                base_url        TEXT NOT NULL DEFAULT '',
                note            TEXT NOT NULL DEFAULT '',
                updated_at      TEXT NOT NULL
            )
            """;
        yield return $"""
            CREATE TABLE IF NOT EXISTS pki_certificates (
                id                   {id} PRIMARY KEY,
                ca_id                {id} NOT NULL,
                serial_number        TEXT NOT NULL,
                subject              TEXT NOT NULL,
                issuer               TEXT NOT NULL,
                certificate_type     TEXT NOT NULL,
                certificate_pem      TEXT NOT NULL,
                certificate_sha256   TEXT NOT NULL,
                subject_key_id       TEXT NOT NULL DEFAULT '',
                public_key_algorithm TEXT NOT NULL,
                signature_algorithm  TEXT NOT NULL,
                not_before           TEXT NOT NULL,
                not_after            TEXT NOT NULL,
                issued_at            TEXT NOT NULL,
                issued_by            TEXT NULL,
                status               TEXT NOT NULL DEFAULT 'active',
                revoked_at           TEXT NULL,
                revocation_reason    TEXT NULL,
                key_reference        TEXT NOT NULL DEFAULT '',
                created_at           TEXT NOT NULL,
                UNIQUE (ca_id, serial_number)
            )
            """;
        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_pki_certificates_sha ON pki_certificates(certificate_sha256)";
        yield return "CREATE INDEX IF NOT EXISTS ix_pki_certificates_type ON pki_certificates(certificate_type, status)";
        yield return $"""
            CREATE TABLE IF NOT EXISTS pki_certificate_events (
                id             {id} PRIMARY KEY,
                certificate_id {id} NOT NULL,
                event          TEXT NOT NULL,
                detail         TEXT NOT NULL DEFAULT '',
                actor          TEXT NULL,
                created_at     TEXT NOT NULL
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_pki_events_cert ON pki_certificate_events(certificate_id, created_at)";
        yield return $"""
            CREATE TABLE IF NOT EXISTS pki_revocations (
                id                      {id} PRIMARY KEY,
                certificate_id          {id} NOT NULL,
                issuing_ca_id           {id} NOT NULL,
                certificate_serial_number TEXT NOT NULL,
                revocation_time         TEXT NOT NULL,
                reason_code             INTEGER NOT NULL DEFAULT 0,
                invalidity_date         TEXT NULL,
                revoked_by              TEXT NULL,
                created_at              TEXT NOT NULL,
                audit_reference         TEXT NOT NULL DEFAULT '',
                released_at             TEXT NULL
            )
            """;
        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_pki_revocations_active ON pki_revocations(certificate_id) WHERE released_at IS NULL";
        yield return "CREATE INDEX IF NOT EXISTS ix_pki_revocations_ca ON pki_revocations(issuing_ca_id, released_at)";
        yield return $"""
            CREATE TABLE IF NOT EXISTS pki_crls (
                id                  {id} PRIMARY KEY,
                issuer_ca_id        {id} NOT NULL,
                crl_number          INTEGER NOT NULL,
                this_update         TEXT NOT NULL,
                next_update         TEXT NOT NULL,
                crl_base64          TEXT NOT NULL,
                crl_sha256          TEXT NOT NULL,
                signature_algorithm TEXT NOT NULL,
                entry_count         INTEGER NOT NULL DEFAULT 0,
                created_at          TEXT NOT NULL,
                UNIQUE (issuer_ca_id, crl_number)
            )
            """;

        // Ausgestellte, signierte Zertifikats-PDFs (ADR 0033): einmal erzeugt, danach unverändert abrufbar (Base64 im TEXT-Feld, datenbankunabhängig)
        yield return $"""
            CREATE TABLE IF NOT EXISTS certificate_pdfs (
                certificate_id   {id} PRIMARY KEY,
                pdf_base64       TEXT NOT NULL,
                pdf_sha256       TEXT NOT NULL,
                size_bytes       INTEGER NOT NULL,
                signed           INTEGER NOT NULL DEFAULT 1,
                signer_thumbprint TEXT NOT NULL DEFAULT '',
                signer_subject   TEXT NOT NULL DEFAULT '',
                signed_at        TEXT NOT NULL,
                created_at       TEXT NOT NULL
            )
            """;

        // Integritätsmanifest und OpenTimestamps-Nachweise (ADR 0039): je Zertifikat und Proof-Version eine unveränderliche Zeile (ältere Versionen bleiben erhalten)
        yield return $"""
            CREATE TABLE IF NOT EXISTS integrity_proofs (
                certificate_id    {id} NOT NULL,
                proof_version     INTEGER NOT NULL,
                manifest_sha256   TEXT NOT NULL,
                jws_sha256        TEXT NOT NULL,
                jws               TEXT NOT NULL,
                ots_base64        TEXT NULL,
                calendar_status   TEXT NOT NULL DEFAULT 'none',
                anchor_status     TEXT NOT NULL DEFAULT 'none',
                bitcoin_height    INTEGER NULL,
                bitcoin_block_hash TEXT NULL,
                bitcoin_block_time TEXT NULL,
                confirmations     INTEGER NOT NULL DEFAULT 0,
                last_checked_at   TEXT NULL,
                last_verified_at  TEXT NULL,
                error             TEXT NOT NULL DEFAULT '',
                ots_client_version TEXT NOT NULL DEFAULT '',
                pdf_sha256        TEXT NOT NULL DEFAULT '',
                created_at        TEXT NOT NULL,
                PRIMARY KEY (certificate_id, proof_version)
            )
            """;

        // Kompetenzmodell eines Moduls (ADR 0031): Lernziele und Kompetenzen; Teil der Modulversion (eingefrorene Kopien erhalten eine Kopie)
        yield return $"""
            CREATE TABLE IF NOT EXISTS module_competencies (
                module_id       {id} PRIMARY KEY,
                definition_json TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            )
            """;

        // Unveränderliche Nachweissnapshots verifizierter Modulabschlüsse (ADR 0031)
        yield return $"""
            CREATE TABLE IF NOT EXISTS exam_snapshots (
                id             {id} PRIMARY KEY,
                result_id      {id} NOT NULL,
                user_id        {id} NOT NULL,
                module_id      {id} NOT NULL,
                module_version INTEGER NOT NULL DEFAULT 0,
                kind           TEXT NOT NULL DEFAULT 'module',
                snapshot_json  TEXT NOT NULL,
                snapshot_hash  TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                UNIQUE (result_id)
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_shares (
                id             {id} PRIMARY KEY,
                topic_id       {id} NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                target_user_id {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                permission     TEXT NOT NULL DEFAULT 'learn',
                created_by     TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                revoked_at     TEXT NULL,
                UNIQUE (topic_id, target_user_id)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_shares_target ON topic_shares(target_user_id, revoked_at)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_assignments (
                id          {id} PRIMARY KEY,
                topic_id    {id} NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                user_id     {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                assigned_by TEXT NOT NULL,
                assigned_at TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'active',
                revoked_at  TEXT NULL,
                topic_version INTEGER NULL,
                mandatory   INTEGER NOT NULL DEFAULT 1,
                due_at      TEXT NULL,
                UNIQUE (topic_id, user_id)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_assignments_user ON topic_assignments(user_id, status)";

        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_user_state (
                topic_id         {id} NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                user_id          {id} NOT NULL,
                seen_version     INTEGER NOT NULL DEFAULT 0,
                last_activity_at TEXT NULL,
                PRIMARY KEY (topic_id, user_id)
            )
            """;

        // Prüfungsversuche bleiben dauerhaft erhalten (keine Fremdschlüssel von Benutzern/Modulen, nie kaskadierend gelöscht); das Ergebnis liegt zusätzlich in quiz_results.
        yield return $"""
            CREATE TABLE IF NOT EXISTS topic_exam_attempts (
                id               {id} PRIMARY KEY,
                user_id          {id} NOT NULL,
                topic_id         {id} NOT NULL,
                topic_version_id {id} NOT NULL,
                topic_version    INTEGER NOT NULL,
                kind             TEXT NOT NULL,
                module_id        {id} NOT NULL,
                session_id       TEXT NOT NULL,
                result_id        TEXT NULL,
                score            {real} NULL,
                max_score        {real} NULL,
                percent          {real} NULL,
                passed           INTEGER NULL,
                started_at       TEXT NOT NULL,
                completed_at     TEXT NULL
            )
            """;
        yield return "CREATE UNIQUE INDEX IF NOT EXISTS ux_topic_attempts_session ON topic_exam_attempts(session_id)";
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_attempts_user ON topic_exam_attempts(user_id, topic_id)";
        yield return "CREATE INDEX IF NOT EXISTS ix_topic_attempts_version ON topic_exam_attempts(topic_id, topic_version)";
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
                reference_id  TEXT NULL,
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
