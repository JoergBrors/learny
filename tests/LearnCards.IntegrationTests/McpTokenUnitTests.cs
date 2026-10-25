using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;

/// <summary>
/// Schutz von MCP-Tokens, Codes und OAuth-Clients bei Zugriff auf die Datenbank (ADR 0025): Angreifer mit Lese- und Schreibrechten auf die Tabellen
/// können weder Tokens ablesen noch gültige herstellen oder bestehende Zugänge verändern.
/// </summary>
public static class McpTokenUnitTests
{
    private sealed class Env : IAsyncDisposable
    {
        public SqliteDatabase Db = null!;
        public AppConfig Cfg = null!;
        public UserService Users = null!;
        public McpTokenProtector Prot = null!;
        public McpLinkService Links = null!;
        public McpOAuthClientStore Clients = null!;
        public byte[] Key = null!;

        public static async Task<Env> CreateAsync(string dir, byte[]? key = null, DateTime? legacyUntil = null, List<byte[]>? previous = null)
        {
            Directory.CreateDirectory(dir);
            var e = new Env { Key = key ?? RandomNumberGenerator.GetBytes(32) };
            e.Cfg = new AppConfig
            {
                McpTokenKey = e.Key, McpTokenKeysPrevious = previous ?? new(), McpLegacyTokenUntil = legacyUntil ?? DateTime.UtcNow.AddDays(30),
                McpPublicUrls = new() { "https://mcp.test" },
            };
            e.Db = new SqliteDatabase(Path.Combine(dir, "t.db"));
            await e.Db.InitAsync();
            e.Users = new UserService(e.Db, e.Cfg);
            e.Wire(e.Cfg);
            return e;
        }

        public void Wire(AppConfig cfg)
        {
            Prot = new McpTokenProtector(cfg);
            Links = new McpLinkService(Db, cfg, Users, Prot);
            Clients = new McpOAuthClientStore(Db, Prot);
        }

        public async Task<string> AddUserAsync(string name)
        {
            var id = Guid.NewGuid().ToString();
            await Db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id, @n, '', 'active', 1, @t, @t)", new (string, object?)[] { ("id", id), ("n", name), ("t", DateTime.UtcNow) });
            await Db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@id, 'user', NULL, @t)", new (string, object?)[] { ("id", id), ("t", DateTime.UtcNow) });
            return id;
        }

        /// <summary>Der Angreifer schreibt direkt in die Tabelle (SQL-Zugriff).</summary>
        public Task<int> SqlAsync(string sql, params (string, object?)[] args) => Db.ExecuteAsync(sql, args);

        public async Task<string> DumpAsync(string table) => string.Join("\n", (await Db.QueryAsync($"SELECT * FROM {table}")).Select(r => string.Join("|", r.Values)));
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    private static string NewToken() => McpLinkService.TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        // ─── Grundverhalten und Datenbankinhalt ─────────────────────────────
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "mcp-a")))
        {
            var alice = await e.AddUserAsync("Alice");
            var bob = await e.AddUserAsync("Bob");
            var (token, link) = await e.Links.CreateLinkAsync(alice, "Test", "token", TimeSpan.FromDays(30));
            check((await e.Links.ValidateAsync(token))?.User.Id == alice, "M1 MCP-Token: gültiges Token wird dem Benutzer zugeordnet");
            var dump = await e.DumpAsync("mcp_links");
            var row = (await e.Db.QueryAsync("SELECT * FROM mcp_links WHERE id=@id", new[] { ("id", (object?)link.Id) }))[0];
            check(row["token_hash"] == e.Prot.HashToken(token) && row["token_hash"] != McpTokenProtector.LegacyHash(token) && row["hash_alg"] == "hmac1" && row["row_mac"]!.Length == 64,
                "M2 Datenbank: Token liegt als HMAC (nicht als reiner SHA-256) mit Zeilen-MAC vor");
            check(!dump.Contains(token) && !dump.Contains(Convert.ToBase64String(e.Key)) && row["token_hint"] != "…" + token[^4..] && row["token_hint"]!.StartsWith('#'),
                "M3 Datenbank: weder Token, Schlüssel noch Token-Teile (Hinweis) sind in der Tabelle");

            // ─── Angriff: eingeschleuste Zeilen ─────────────────────────────
            string Mk() => Guid.NewGuid().ToString();
            async Task InsertForgedAsync(string forgedToken, string hash, string alg, string keyId, string mac, string? userId = null) =>
                await e.SqlAsync("INSERT INTO mcp_links (id, user_id, name, kind, token_hash, token_hint, created_at, expires_at, hash_alg, key_id, row_mac) VALUES (@id, @u, 'x', 'token', @h, '', @n, @x, @a, @k, @m)",
                    ("id", Mk()), ("u", userId ?? bob), ("h", hash), ("n", DateTime.UtcNow), ("x", DateTime.UtcNow.AddDays(30)), ("a", alg), ("k", keyId), ("m", mac));

            var f1 = NewToken();   // Angreifer berechnet SHA-256 seines Tokens und trägt ihn als Opfer ein (wie vor ADR 0025 möglich)
            await InsertForgedAsync(f1, McpTokenProtector.LegacyHash(f1), "hmac1", e.Prot.ActiveKeyId, "");
            check(await e.Links.ValidateAsync(f1) is null, "M4 Angriff: eingeschleuste Zeile mit SHA-256 des eigenen Tokens (als hmac1) wird abgelehnt");
            var f2 = NewToken();   // beliebiger MAC
            await InsertForgedAsync(f2, e.Prot.HashToken(f2), "hmac1", e.Prot.ActiveKeyId, new string('a', 64));
            check(await e.Links.ValidateAsync(f2) is null, "M5 Angriff: selbst gewählter MAC (ohne Schlüssel) wird abgelehnt — auch wenn der Hash zufällig stimmt");
            var f3 = NewToken();   // Zeile mit eigenem Schlüssel erzeugt
            await using (var other = await Env.CreateAsync(Path.Combine(tmp, "mcp-a-attacker")))
            {
                var (foreignToken, foreignLink) = await other.Links.CreateLinkAsync(await other.AddUserAsync("X"), "x", "token", TimeSpan.FromDays(30));
                var fr = (await other.Db.QueryAsync("SELECT * FROM mcp_links WHERE id=@id", new[] { ("id", (object?)foreignLink.Id) }))[0];
                await InsertForgedAsync(foreignToken, fr["token_hash"]!, "hmac1", fr["key_id"]!, fr["row_mac"]!, alice);
                check(await e.Links.ValidateAsync(foreignToken) is null, "M6 Angriff: Zeile mit Schlüssel einer anderen Installation wird abgelehnt");
            }

            // ─── Angriff: Manipulation bestehender Zeilen ───────────────────
            var (t1, l1) = await e.Links.CreateLinkAsync(alice, "A", "token", TimeSpan.FromDays(1));
            check(await e.Links.ValidateAsync(t1) is not null, "M7 Ausgangslage: frisches Token ist gültig");
            await e.SqlAsync("UPDATE mcp_links SET user_id=@u WHERE id=@id", ("u", bob), ("id", l1.Id));
            check(await e.Links.ValidateAsync(t1) is null, "M8 Angriff: user_id auf anderen Benutzer umgebogen → abgelehnt");
            await e.SqlAsync("UPDATE mcp_links SET user_id=@u WHERE id=@id", ("u", alice), ("id", l1.Id));
            check(await e.Links.ValidateAsync(t1) is not null, "M9 Rückgängig gemacht → wieder gültig (der MAC bindet die Originalwerte)");
            await e.SqlAsync("UPDATE mcp_links SET expires_at=@x WHERE id=@id", ("x", DateTime.UtcNow.AddDays(365)), ("id", l1.Id));
            check(await e.Links.ValidateAsync(t1) is null, "M10 Angriff: Ablaufdatum verlängert → abgelehnt");
            await e.SqlAsync("UPDATE mcp_links SET expires_at=@x WHERE id=@id", ("x", l1.ExpiresAt), ("id", l1.Id));
            await e.SqlAsync("UPDATE mcp_links SET kind='oauth' WHERE id=@id", ("id", l1.Id));
            check(await e.Links.ValidateAsync(t1) is null, "M11 Angriff: Art des Zugangs geändert → abgelehnt");
            await e.SqlAsync("UPDATE mcp_links SET kind='token' WHERE id=@id", ("id", l1.Id));
            var f4 = NewToken();
            await e.SqlAsync("UPDATE mcp_links SET token_hash=@h WHERE id=@id", ("h", e.Prot.HashToken(f4)), ("id", l1.Id));
            check(await e.Links.ValidateAsync(f4) is null, "M12 Angriff: token_hash einer bestehenden Zeile gegen den Hash des eigenen Tokens getauscht → abgelehnt");

            // ─── Widerruf ───────────────────────────────────────────────────
            var (t2, l2) = await e.Links.CreateLinkAsync(alice, "B", "token", TimeSpan.FromDays(30));
            check(await e.Links.RevokeAsync(alice, l2.Id) && await e.Links.ValidateAsync(t2) is null, "M13 Widerruf: Token ist danach ungültig");
            check(!await e.Links.RevokeAsync(bob, l2.Id), "M14 Fremde Zugänge lassen sich nicht widerrufen");
            await e.SqlAsync("UPDATE mcp_links SET revoked_at=NULL WHERE id=@id", ("id", l2.Id));
            check(await e.Links.ValidateAsync(t2) is null, "M15 Angriff: Widerruf in der Datenbank zurückgesetzt → bleibt abgelehnt (MAC enthält den Widerruf)");
            check((await e.Db.QueryAsync("SELECT 1 FROM audit_log WHERE action='mcp.token.integrity_failure'")).Count > 0, "M16 Manipulationsversuche stehen im Audit (ohne Token oder Hash)");
            var audit = await e.DumpAsync("audit_log");
            check(!audit.Contains(t1) && !audit.Contains(e.Prot.HashToken(t1)), "M17 Audit enthält weder Token noch Hash");

            // ─── Einmal-Codes ───────────────────────────────────────────────
            string CodeFrom(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["code"]!;
            var code = CodeFrom(await e.Links.CompleteConsentAsync(alice, "https://mcp.test/oauth/callback", "st", "Claude", true));
            var codeRow = (await e.Db.QueryAsync("SELECT * FROM mcp_link_codes"))[0];
            check(codeRow["code_hash"] == e.Prot.HashCode(code) && codeRow["row_mac"]!.Length == 64 && !(await e.DumpAsync("mcp_link_codes")).Contains(code), "M18 Code: nur als HMAC mit Zeilen-MAC gespeichert");
            var ex1 = await e.Links.ExchangeCodeAsync(code);
            check(ex1 is not null && ex1.Value.Link.UserId == alice && await e.Links.ValidateAsync(ex1.Value.Token) is not null, "M19 Code-Tausch liefert ein benutzergebundenes, gültiges Token");
            check(await e.Links.ExchangeCodeAsync(code) is null, "M20 Code ist nur einmal verwendbar");
            var code2 = CodeFrom(await e.Links.CompleteConsentAsync(alice, "https://mcp.test/oauth/callback", "st", "Claude", true));
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => e.Links.ExchangeCodeAsync(code2))));
            check(results.Count(r => r is not null) == 1, "M21 Code: bei acht parallelen Tauschversuchen genau ein Erfolg");
            var forgedCode = "forged" + Guid.NewGuid().ToString("N");
            await e.SqlAsync("INSERT INTO mcp_link_codes (code_hash, user_id, client_name, created_at, expires_at, key_id, row_mac) VALUES (@h, @u, 'x', @n, @x, @k, '')",
                ("h", McpTokenProtector.LegacyHash(forgedCode)), ("u", bob), ("n", DateTime.UtcNow), ("x", DateTime.UtcNow.AddMinutes(5)), ("k", e.Prot.ActiveKeyId));
            check(await e.Links.ExchangeCodeAsync(forgedCode) is null, "M22 Angriff: eingeschleuster Code (SHA-256) wird abgelehnt");
            await e.SqlAsync("INSERT INTO mcp_link_codes (code_hash, user_id, client_name, created_at, expires_at, key_id, row_mac) VALUES (@h, @u, 'x', @n, @x, @k, @m)",
                ("h", e.Prot.HashCode(forgedCode)), ("u", bob), ("n", DateTime.UtcNow), ("x", DateTime.UtcNow.AddMinutes(5)), ("k", e.Prot.ActiveKeyId), ("m", new string('b', 64)));
            check(await e.Links.ExchangeCodeAsync(forgedCode) is null, "M23 Angriff: eingeschleuster Code mit falschem MAC wird abgelehnt (und ist danach verbraucht)");
            var code3 = CodeFrom(await e.Links.CompleteConsentAsync(alice, "https://mcp.test/oauth/callback", "st", "Claude", true));
            await e.SqlAsync("UPDATE mcp_link_codes SET user_id=@u WHERE code_hash=@h", ("u", bob), ("h", e.Prot.HashCode(code3)));
            check(await e.Links.ExchangeCodeAsync(code3) is null, "M24 Angriff: user_id eines Codes verändert → abgelehnt (Token würde sonst für den falschen Benutzer ausgestellt)");

            // ─── OAuth-Clients (verschlüsselt) ──────────────────────────────
            var clientId = "learncards-" + Guid.NewGuid().ToString("N");
            var rec = new McpOAuthClientRecord { ClientId = clientId, ClientName = "Claude Geheimname", RedirectUris = new() { "https://claude.ai/api/mcp/auth_callback" }, Scopes = new() { "mcp" } };
            check(await e.Clients.RegisterAsync(rec) is null, "M25 OAuth-Client: öffentliche Registrierung wird gespeichert");
            var back = await e.Clients.GetAsync(clientId);
            check(back is not null && back.ClientName == "Claude Geheimname" && back.RedirectUris[0] == "https://claude.ai/api/mcp/auth_callback" && back.TokenEndpointAuthMethod == "none", "M26 OAuth-Client: Datensatz wird vollständig zurückgelesen");
            var cdump = await e.DumpAsync("mcp_oauth_clients");
            check(!cdump.Contains("Claude Geheimname") && !cdump.Contains("claude.ai") && !cdump.Contains(Convert.ToBase64String(e.Key)), "M27 Datenbank: Name und Redirect-URIs des Clients liegen nur verschlüsselt vor");
            var cr = (await e.Db.QueryAsync("SELECT * FROM mcp_oauth_clients WHERE client_id=@id", new[] { ("id", (object?)clientId) }))[0];
            var tampered = cr["record_enc"]![..^2] + (cr["record_enc"]![^1] == 'A' ? "BB" : "AA");
            await e.SqlAsync("UPDATE mcp_oauth_clients SET record_enc=@v WHERE client_id=@id", ("v", tampered), ("id", clientId));
            check(await e.Clients.GetAsync(clientId) is null, "M28 Angriff: verschlüsselter Datensatz verändert → abgelehnt");
            await e.SqlAsync("UPDATE mcp_oauth_clients SET record_enc=@v WHERE client_id=@id", ("v", cr["record_enc"]), ("id", clientId));
            check(await e.Clients.GetAsync(clientId) is not null, "M29 Original wiederhergestellt → wieder lesbar");
            var swapId = "learncards-" + Guid.NewGuid().ToString("N");
            await e.SqlAsync("INSERT INTO mcp_oauth_clients (client_id, key_id, record_enc, created_at, row_mac) VALUES (@id, @k, @e, @c, @m)",
                ("id", swapId), ("k", cr["key_id"]), ("e", cr["record_enc"]), ("c", DateTime.UtcNow), ("m", cr["row_mac"]));
            check(await e.Clients.GetAsync(swapId) is null, "M30 Angriff: Datensatz unter fremder client_id kopiert (andere Redirect-URIs bleiben gebunden) → abgelehnt");
            check(await e.Clients.RegisterAsync(new McpOAuthClientRecord { ClientId = "learncards-" + Guid.NewGuid().ToString("N"), TokenEndpointAuthMethod = "client_secret_post", RedirectUris = new() { "https://a.example/cb" } }) is not null
                  && await e.Clients.RegisterAsync(new McpOAuthClientRecord { ClientId = "learncards-" + Guid.NewGuid().ToString("N"), RedirectUris = new() { "javascript:alert(1)" } }) is not null
                  && await e.Clients.RegisterAsync(new McpOAuthClientRecord { ClientId = "learncards-" + Guid.NewGuid().ToString("N"), RedirectUris = new() { "http://evil.example/cb" } }) is not null
                  && await e.Clients.RegisterAsync(new McpOAuthClientRecord { ClientId = "x", RedirectUris = new() { "https://a.example/cb" } }) is not null
                  && await e.Clients.RegisterAsync(new McpOAuthClientRecord { ClientId = "learncards-" + Guid.NewGuid().ToString("N"), RedirectUris = new() { "http://localhost:6274/cb" } }) is null,
                "M31 OAuth-Client: vertrauliche Clients, unsichere Redirect-URIs und ungültige IDs werden abgelehnt; Loopback-http ist erlaubt");
            await using (var wrongKey = await Env.CreateAsync(Path.Combine(tmp, "mcp-a-wrong")))
            {
                var wrongStore = new McpOAuthClientStore(e.Db, wrongKey.Prot);
                check(await wrongStore.GetAsync(clientId) is null, "M32 OAuth-Client: mit anderem Schlüssel nicht lesbar");
            }
        }

        // ─── Altbestand: befristete Umstellung ─────────────────────────────
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "mcp-legacy"), legacyUntil: DateTime.UtcNow.AddDays(30)))
        {
            var user = await e.AddUserAsync("Alt");
            var legacyToken = NewToken();
            var id = Guid.NewGuid().ToString();
            await e.SqlAsync("INSERT INTO mcp_links (id, user_id, name, kind, token_hash, token_hint, created_at, expires_at) VALUES (@id, @u, 'alt', 'token', @h, @hint, @n, @x)",
                ("id", id), ("u", user), ("h", McpTokenProtector.LegacyHash(legacyToken)), ("hint", "…" + legacyToken[^4..]), ("n", DateTime.UtcNow), ("x", DateTime.UtcNow.AddDays(10)));
            check((await e.Links.ValidateAsync(legacyToken))?.User.Id == user, "M33 Altbestand: SHA-256-Token ist innerhalb der Frist gültig");
            var up = (await e.Db.QueryAsync("SELECT * FROM mcp_links WHERE id=@id", new[] { ("id", (object?)id) }))[0];
            check(up["hash_alg"] == "hmac1" && up["token_hash"] == e.Prot.HashToken(legacyToken) && up["row_mac"]!.Length == 64 && up["token_hint"] == "#" + id[..4],
                "M34 Altbestand: beim ersten Gebrauch auf HMAC mit Zeilen-MAC umgestellt, Token-Hinweis ersetzt");
            check(await e.Links.ValidateAsync(legacyToken) is not null, "M35 Altbestand: nach der Umstellung weiterhin gültig");
        }
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "mcp-legacy-expired"), legacyUntil: DateTime.UtcNow.AddDays(-1)))
        {
            var user = await e.AddUserAsync("Alt");
            var legacyToken = NewToken();
            await e.SqlAsync("INSERT INTO mcp_links (id, user_id, name, kind, token_hash, token_hint, created_at, expires_at) VALUES (@id, @u, 'alt', 'token', @h, '', @n, @x)",
                ("id", Guid.NewGuid().ToString()), ("u", user), ("h", McpTokenProtector.LegacyHash(legacyToken)), ("n", DateTime.UtcNow), ("x", DateTime.UtcNow.AddDays(10)));
            check(await e.Links.ValidateAsync(legacyToken) is null, "M36 Altbestand: nach Ablauf der Frist wird ein SHA-256-Token abgelehnt (Neuanmeldung nötig)");
        }

        // ─── Schlüsselrotation ─────────────────────────────────────────────
        {
            var keyA = RandomNumberGenerator.GetBytes(32);
            var keyB = RandomNumberGenerator.GetBytes(32);
            await using var e = await Env.CreateAsync(Path.Combine(tmp, "mcp-rot"), keyA);
            var user = await e.AddUserAsync("Rot");
            var (tokenA, linkA) = await e.Links.CreateLinkAsync(user, "A", "token", TimeSpan.FromDays(30));
            e.Wire(new AppConfig { McpTokenKey = keyB, McpTokenKeysPrevious = new() { keyA }, McpLegacyTokenUntil = e.Cfg.McpLegacyTokenUntil, McpPublicUrls = e.Cfg.McpPublicUrls });
            check(await e.Links.ValidateAsync(tokenA) is not null, "M37 Rotation: Token des Vorgängerschlüssels bleibt gültig");
            var rotated = (await e.Db.QueryAsync("SELECT key_id, token_hash FROM mcp_links WHERE id=@id", new[] { ("id", (object?)linkA.Id) }))[0];
            check(rotated["key_id"] == e.Prot.ActiveKeyId && rotated["token_hash"] == e.Prot.HashToken(tokenA), "M38 Rotation: Zeile wird beim Gebrauch auf den aktiven Schlüssel umgestellt");
            var (tokenB, _) = await e.Links.CreateLinkAsync(user, "B", "token", TimeSpan.FromDays(30));
            e.Wire(new AppConfig { McpTokenKey = keyA, McpLegacyTokenUntil = e.Cfg.McpLegacyTokenUntil, McpPublicUrls = e.Cfg.McpPublicUrls });
            check(await e.Links.ValidateAsync(tokenB) is null, "M39 Schlüsselverlust: Ohne den Schlüssel (nur ein anderer) ist kein Token gültig — kein Datenzugriff");
        }

        // ─── Schlüssel-Konfiguration ───────────────────────────────────────
        {
            var dir = Path.Combine(tmp, "mcp-keys");
            string? Err(Func<byte[]> f) { try { f(); return null; } catch (InvalidOperationException ex) { return ex.Message; } }
            var good = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var missing = Err(() => McpTokenKeys.Resolve(null, false, dir));
            check(missing is not null && missing.Contains("Pflicht"), "M40 MCP_TOKEN_KEY fehlt in Produktion → Startabbruch mit klarer Meldung");
            check(Err(() => McpTokenKeys.Resolve(Convert.ToBase64String(new byte[16]), false, dir)) is not null && Err(() => McpTokenKeys.Resolve("kein base64!", false, dir)) is not null, "M41 Zu kurzer oder ungültiger Schlüssel → Startabbruch");
            var shortMsg = Err(() => McpTokenKeys.Resolve(Convert.ToBase64String(new byte[16]), false, dir));
            check(shortMsg is not null && !shortMsg.Contains(Convert.ToBase64String(new byte[16])), "M42 Fehlermeldungen enthalten nie den Schlüsselwert");
            check(McpTokenKeys.Resolve(good, false, dir).Length == 32, "M43 Gültiger Schlüssel wird akzeptiert");
            var d1 = McpTokenKeys.Resolve(null, true, dir);
            var d2 = McpTokenKeys.Resolve(null, true, dir);
            check(d1.SequenceEqual(d2) && File.Exists(Path.Combine(dir, "mcp-token.key")) && Directory.GetFiles(dir, "*.tmp").Length == 0, "M44 Development: Schlüssel wird einmalig lokal erzeugt und danach wiederverwendet");
        }
    }
}
