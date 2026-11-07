namespace InviteRegistration;

/// <summary>Zusätzliche Tabellen des PoC (portabel für SQLite/PostgreSQL). Vorhandene Learny-Tabellen (users, user_identities, user_roles) werden nur genutzt, nicht verändert.</summary>
public static class InviteSchema
{
    public static IEnumerable<string> Statements(string provider)
    {
        var id = provider == "postgres" ? "UUID" : "TEXT";

        yield return $"""
            CREATE TABLE IF NOT EXISTS invitations (
                invitation_id    {id} PRIMARY KEY,
                recipient_email  TEXT NOT NULL,
                token_hash       TEXT NOT NULL UNIQUE,
                target_profile   TEXT NOT NULL,
                status           TEXT NOT NULL DEFAULT 'pending',
                created_by       {id} NOT NULL,
                created_at       TEXT NOT NULL,
                expires_at       TEXT NOT NULL,
                accepted_at      TEXT NULL,
                accepted_user_id {id} NULL,
                revoked_at       TEXT NULL,
                token_version    INTEGER NOT NULL DEFAULT 1
            )
            """;

        // Inhaltszuweisungen getrennt von der Einladung (werden bei Einlösung nach module_grants kopiert).
        yield return $"""
            CREATE TABLE IF NOT EXISTS invitation_grants (
                invitation_id {id} NOT NULL REFERENCES invitations(invitation_id),
                module_id     {id} NOT NULL,
                PRIMARY KEY (invitation_id, module_id)
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS user_profiles (
                user_id              {id} PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                profile              TEXT NOT NULL,
                source_invitation_id {id} NOT NULL,
                created_at           TEXT NOT NULL
            )
            """;

        // Entra-Objekt-ID → Portalbenutzer (für die Token-Anreicherung; keine Personendaten).
        yield return $"""
            CREATE TABLE IF NOT EXISTS user_entra_oids (
                oid        {id} PRIMARY KEY,
                user_id    {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at TEXT NOT NULL
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS poc_modules (
                id    {id} PRIMARY KEY,
                name  TEXT NOT NULL,
                scope TEXT NOT NULL
            )
            """;

        yield return $"""
            CREATE TABLE IF NOT EXISTS module_grants (
                user_id              {id} NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                module_id            {id} NOT NULL REFERENCES poc_modules(id),
                source_invitation_id {id} NULL,
                granted_by           {id} NULL,
                granted_at           TEXT NOT NULL,
                PRIMARY KEY (user_id, module_id)
            )
            """;

        // Versandstatus getrennt vom Einladungsstatus (ADR 0044): pending | submitted | failed | unknown. Nie ein Token.
        yield return $"""
            CREATE TABLE IF NOT EXISTS invitation_deliveries (
                invitation_id   {id} PRIMARY KEY REFERENCES invitations(invitation_id),
                status          TEXT NOT NULL DEFAULT 'pending',
                attempts        INTEGER NOT NULL DEFAULT 0,
                lease_until     TEXT NULL,
                last_attempt_at TEXT NULL,
                submitted_at    TEXT NULL,
                last_error_code TEXT NULL,
                updated_at      TEXT NOT NULL
            )
            """;

        // Ereignisprotokoll (ADR 0046): nur beobachtete Ereignisse, keine Codes/Links/Tokens.
        yield return $"""
            CREATE TABLE IF NOT EXISTS invitation_events (
                id            {id} PRIMARY KEY,
                invitation_id {id} NULL,
                user_id       {id} NULL,
                event_type    TEXT NOT NULL,
                category      TEXT NULL,
                actor_user_id {id} NULL,
                at            TEXT NOT NULL
            )
            """;

        yield return "CREATE INDEX IF NOT EXISTS ix_invitation_events_inv ON invitation_events(invitation_id, at)";

        // Verwaltungsangaben zur Einladung (getrennt von der Einladung selbst).
        yield return $"""
            CREATE TABLE IF NOT EXISTS invitation_meta (
                invitation_id {id} PRIMARY KEY REFERENCES invitations(invitation_id),
                display_name  TEXT NULL,
                description   TEXT NULL,
                note          TEXT NULL
            )
            """;

        // Audit ohne Tokenwerte.
        yield return $"""
            CREATE TABLE IF NOT EXISTS invitation_audit (
                id            {id} PRIMARY KEY,
                invitation_id {id} NULL,
                event         TEXT NOT NULL,
                reason        TEXT NOT NULL,
                at            TEXT NOT NULL
            )
            """;
    }
}
