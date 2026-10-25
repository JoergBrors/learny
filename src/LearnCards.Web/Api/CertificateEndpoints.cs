using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Certificates;

namespace LearnCards.Web.Api;

/// <summary>Zertifikate, Open Badges, öffentliche Prüfung, Auditor- und Blockchain-Verwaltung (ADR 0021/0022). Für MCP-Tokens nicht erreichbar (kein Präfix in <c>TokenAllowedPrefixes</c>).</summary>
public static partial class ApiEndpoints
{
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Window)> PublicHits = new();

    /// <summary>Einfache Missbrauchsbremse für anonyme Prüf-Endpunkte (je Adresse und Minute).</summary>
    private static bool PublicAllowed(HttpContext http, int perMinute)
    {
        var key = http.Connection.RemoteIpAddress?.ToString() ?? "?";
        var now = DateTime.UtcNow;
        var entry = PublicHits.AddOrUpdate(key, _ => (1, now), (_, e) => now - e.Window > TimeSpan.FromMinutes(1) ? (1, now) : (e.Count + 1, e.Window));
        return entry.Count <= perMinute;
    }

    private static object CertificateDto(Certificate c, bool includeLearner = true) => new
    {
        id = c.Id, number = c.Number, module_id = c.ModuleId, module_name = c.ModuleName, module_version = c.ModuleVersion, scope = c.ModuleScope, issued_at = c.IssuedAt,
        score_percent = c.ScorePercent, grade = c.Grade, grade_label = GermanGrade.Label(c.Grade), pass_percent = c.PassPercent, learner_name = includeLearner ? c.LearnerName : null, badge_hash = c.BadgeHash,
        status = c.IsRevoked ? "revoked" : "active", revoked_at = c.RevokedAt, revoked_reason = c.IsRevoked ? c.RevokedReason : null,
        chain = new
        {
            status = c.ChainStatus, label = ChainStatus.Label(c.ChainStatus, c.IsRevoked), network = c.ChainNetwork, network_label = ChainNetworks.Label(c.ChainNetwork), chain_id = c.ChainId,
            simulated = c.ChainNetwork == ChainNetworks.Simulation, batch_id = c.BatchId, error = string.IsNullOrEmpty(c.ChainError) ? null : c.ChainError, attempts = c.ChainAttempts, anchored_at = c.AnchoredAt,
        },
    };

    private static void MapCertificateEndpoints(WebApplication app, RouteGroupBuilder secured)
    {
        // ─── Öffentlich (ohne Anmeldung, datensparsam: keine Lernendendaten) ───
        app.MapGet("/.well-known/jwks.json", (BadgeSigner signer) => Results.Json(new { keys = new[] { signer.PublicJwk() } })).AllowAnonymous();

        app.MapGet("/badges/issuer", (CertificateService certs, BadgeSigner signer, AppConfig cfg) => Results.Json(new
        {
            context = new[] { "https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json" },
            id = certs.IssuerId, type = new[] { "Profile" }, name = "LearnCards", url = certs.BaseUrl,
            jwks = new { keys = new[] { signer.PublicJwk() } }, verification_url = certs.BaseUrl + "/verify",
            anchor_wallet = cfg.Blockchain.WalletAddress.Length > 0 ? cfg.Blockchain.WalletAddress : null,
        })).AllowAnonymous();

        app.MapGet("/api/public/certificates/{id}/status", async (string id, CertificateService certs, HttpContext http) =>
            !PublicAllowed(http, 120) ? Results.StatusCode(429)
            : await certs.GetPublicStatusAsync(id) is { } s ? Results.Ok(s) : Results.Json(new { detail = "Certificate not found" }, statusCode: 404)).AllowAnonymous();

        app.MapGet("/api/public/certificates/{id}/proof", async (string id, CertificateService certs, HttpContext http) =>
        {
            if (!PublicAllowed(http, 120)) return Results.StatusCode(429);
            var c = await certs.GetRawAsync(id);
            var pkg = c is null ? null : await certs.BuildProofPackageAsync(c);
            return pkg is null ? Results.Json(new { detail = "Kein Verankerungsnachweis vorhanden." }, statusCode: 404) : Results.Json(pkg);
        }).AllowAnonymous();

        app.MapGet("/api/public/revocation-list", async (CertificateService certs) => Results.Json(await certs.RevocationListAsync())).AllowAnonymous();

        // Prüft ein Badge (JSON mit proof.jwt oder kompakte JWS). Eingabe begrenzt; Antwort ohne Lernendendaten.
        app.MapPost("/api/public/verify", async (HttpContext http, CertificateService certs) =>
        {
            if (!PublicAllowed(http, 30)) return Results.StatusCode(429);
            using var reader = new StreamReader(http.Request.Body);
            var buffer = new char[200_001];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > 200_000) return Results.Json(new { detail = "Die Eingabe ist zu groß (höchstens 200 000 Zeichen)." }, statusCode: 413);
            var v = await certs.VerifyAsync(new string(buffer, 0, read));
            return Results.Json(new { valid = v.Valid, signature_valid = v.SignatureValid, signature_error = v.SignatureError, content_matches = v.ContentMatches, issuer_valid = v.IssuerValid, known = v.Known, revoked = v.Revoked,
                revoked_at = v.RevokedAt, revoked_reason = v.RevokedReason, badge_hash = v.BadgeHash, certificate_id = v.CertificateId, number = v.Number, module_name = v.ModuleName, module_version = v.ModuleVersion,
                issued_at = v.IssuedAt, chain_status = v.ChainStatus, chain_network = v.ChainNetwork, tx_hash = v.TxHash });
        }).AllowAnonymous().DisableAntiforgery();

        // Unabhängige Blockchain-Prüfung eines Nachweis-Pakets (RPC-Ziel nur aus der Konfiguration, nie aus dem Paket)
        app.MapPost("/api/public/verify/chain", async (HttpContext http, AnchorService anchors) =>
        {
            if (!PublicAllowed(http, 20)) return Results.StatusCode(429);
            using var reader = new StreamReader(http.Request.Body);
            var buffer = new char[100_001];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > 100_000) return Results.Json(new { detail = "Die Eingabe ist zu groß." }, statusCode: 413);
            JsonNode? pkg;
            try { pkg = JsonNode.Parse(new string(buffer, 0, read)); } catch (System.Text.Json.JsonException) { return Results.Json(new { detail = "Kein gültiges JSON." }, statusCode: 400); }
            return pkg is null ? Results.Json(new { detail = "Leere Eingabe." }, statusCode: 400) : Results.Json(await anchors.VerifyProofPackageAsync(pkg, http.RequestAborted));
        }).AllowAnonymous().DisableAntiforgery();

        // ─── Eigene Zertifikate (Cookie-Login) ───
        var mine = secured.MapGroup("/certificates");
        mine.MapGet("/", async (CertificateService certs, HttpContext http) => Results.Ok((await certs.ListForUserAsync(Uid(http))).Select(c => CertificateDto(c))));
        mine.MapGet("/{id}", async (string id, CertificateService certs, HttpContext http) =>
            await certs.GetForUserAsync(Uid(http), id) is { } c ? Results.Ok(CertificateDto(c)) : Results.Json(new { detail = "Certificate not found" }, statusCode: 404));
        mine.MapGet("/{id}/badge", async (string id, CertificateService certs, HttpContext http) =>
        {
            if (await certs.GetForUserAsync(Uid(http), id) is not { } c) return Results.Json(new { detail = "Certificate not found" }, statusCode: 404);
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"badge-{c.Number}.json\"";
            return Results.Text(certs.SignedBadgeJson(c).ToJsonString(new() { WriteIndented = true }), "application/ld+json");
        });
        // Druckbares Zertifikat (HTML; im Browser als PDF speichern). ?view=1 zeigt es im Browser, sonst Download.
        mine.MapGet("/{id}/certificate.html", async (string id, CertificateService certs, HttpContext http, bool view = false) =>
        {
            if (await certs.GetForUserAsync(Uid(http), id) is not { } c) return Results.Json(new { detail = "Certificate not found" }, statusCode: 404);
            http.Response.Headers.ContentDisposition = (view ? "inline" : "attachment") + $"; filename=\"Zertifikat-{c.Number}.html\"";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'";
            return Results.Text(certs.RenderCertificateHtml(c), "text/html; charset=utf-8");
        });
        mine.MapGet("/{id}/badge.jwt", async (string id, CertificateService certs, HttpContext http) =>
        {
            if (await certs.GetForUserAsync(Uid(http), id) is not { } c) return Results.Json(new { detail = "Certificate not found" }, statusCode: 404);
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"badge-{c.Number}.jwt\"";
            return Results.Text(c.BadgeJws, "application/jwt");
        });
        mine.MapGet("/{id}/proof", async (string id, CertificateService certs, HttpContext http) =>
        {
            if (await certs.GetForUserAsync(Uid(http), id) is not { } c) return Results.Json(new { detail = "Certificate not found" }, statusCode: 404);
            var pkg = await certs.BuildProofPackageAsync(c);
            if (pkg is null) return Results.Json(new { detail = "Noch kein Verankerungsnachweis vorhanden (Verankerung ausstehend)." }, statusCode: 404);
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"proof-{c.Number}.json\"";
            return Results.Text(pkg.ToJsonString(new() { WriteIndented = true }), "application/json");
        });

        // ─── Auditor (nur global veröffentlichte Module; keine Quizantworten, Chats oder privaten Module) ───
        var audit = secured.MapGroup("/audit");
        audit.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.Items.ContainsKey(ViaKeyItem) || http.Items.ContainsKey(ViaTokenItem))
                return Results.Json(new { detail = "Prüfbereiche sind nur über die Browser-Anmeldung verfügbar." }, statusCode: 403);
            try { await http.RequestServices.GetRequiredService<UserService>().RequireAnyRoleAsync(Uid(http), Roles.Auditor, Roles.Admin); }
            catch (UnauthorizedAccessException) { return Results.Json(new { detail = "Auditor- oder Administratorrechte erforderlich." }, statusCode: 403); }
            return await next(ctx);
        });
        audit.MapGet("/certificates", (CertificateService certs, HttpContext http, string? learner, string? module, DateTime? from, DateTime? to, string? status, string? network, string? chain_status) =>
            Guard(async () => Results.Ok((await certs.ListForAuditorAsync(Uid(http), new CertificateService.AuditorFilter(learner, module, from, to, status, network, chain_status))).Select(c => CertificateDto(c)))));
        audit.MapGet("/certificates/{id}", (string id, CertificateService certs, HttpContext http) =>
            Guard(async () => await certs.GetForAuditorAsync(Uid(http), id) is { } c ? Results.Ok(CertificateDto(c)) : Results.Json(new { detail = "Certificate not found" }, statusCode: 404)));
        audit.MapPost("/verify", async (HttpContext http, CertificateService certs) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var buffer = new char[200_001];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > 200_000) return Results.Json(new { detail = "Die Eingabe ist zu groß." }, statusCode: 413);
            var v = await certs.VerifyAsync(new string(buffer, 0, read));
            await certs.RecordVerificationAsync(Uid(http), $"hash={v.BadgeHash};valid={v.Valid};number={v.Number}");
            return Results.Json(new { valid = v.Valid, signature_valid = v.SignatureValid, issuer_valid = v.IssuerValid, content_matches = v.ContentMatches, known = v.Known, revoked = v.Revoked, number = v.Number, module_name = v.ModuleName, chain_status = v.ChainStatus, chain_network = v.ChainNetwork });
        }).DisableAntiforgery();
        audit.MapGet("/history", (CertificateService certs, HttpContext http) => Guard(async () => Results.Ok(await certs.VerificationHistoryAsync(Uid(http)))));

        // ─── Administration: Zertifikate und Blockchain ───
        var admin = secured.MapGroup("/admin");
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.Items.ContainsKey(ViaKeyItem) || http.Items.ContainsKey(ViaTokenItem))
                return Results.Json(new { detail = "Administrative Funktionen sind nur über die Browser-Anmeldung verfügbar." }, statusCode: 403);
            try { await http.RequestServices.GetRequiredService<UserService>().RequireRoleAsync(Uid(http), Roles.Admin); }
            catch (UnauthorizedAccessException) { return Results.Json(new { detail = "Administratorrechte erforderlich." }, statusCode: 403); }
            return await next(ctx);
        });

        admin.MapGet("/certificates", (CertificateService certs, HttpContext http, string? filter) => Guard(async () => Results.Ok(await certs.ListForAdminAsync(Uid(http), filter))));
        admin.MapPost("/certificates/{id}/revoke", (string id, RevokeRequest req, CertificateService certs, HttpContext http) =>
            Guard(async () => { await certs.RevokeAsync(Uid(http), id, req.Reason); return Results.NoContent(); }));

        admin.MapGet("/blockchain", async (ChainSettingsStore settings, AnchorService anchors, AppConfig cfg, HttpContext http) =>
        {
            var eff = await settings.GetAsync();
            return Results.Json(new
            {
                settings = new
                {
                    enabled = eff.Enabled, auto_anchor = eff.AutoAnchor, batch_enabled = eff.BatchEnabled, batch_max_size = eff.BatchMaxSize, batch_max_wait_minutes = eff.BatchMaxWaitMinutes,
                    max_cost_eur = eff.MaxCostEur, hard_max_cost_eur = BlockchainSettings.HardMaxCostEur, network = eff.Network, network_label = ChainNetworks.Label(eff.Network), chain_id = eff.ChainId,
                    simulate = eff.Simulate, mainnet_env_enabled = eff.MainnetEnvEnabled, mainnet_unlocked = eff.MainnetUnlocked, mainnet_approval = eff.Approval is null ? null : new { by = eff.Approval.By, at = eff.Approval.At },
                    confirmations = cfg.Blockchain.Confirmations, gas_safety_factor = cfg.Blockchain.GasSafetyFactor, rate_safety_factor = cfg.Blockchain.RateSafetyFactor, price_max_age_minutes = cfg.Blockchain.PriceMaxAgeMinutes,
                    env_max_batch_size = cfg.Blockchain.BatchMaxSize, env_max_cost_eur = cfg.Blockchain.MaxCostEur,
                },
                wallet = await anchors.GetWalletStatusAsync(http.RequestAborted),
                stats = await anchors.GetStatsAsync(),
                batches = (await anchors.ListBatchesAsync()).Select(BatchDto),
            });
        });

        admin.MapPut("/blockchain/settings", (BlockchainSettingsRequest req, ChainSettingsStore settings, AppConfig cfg, UserService users, HttpContext http) =>
            Guard(async () =>
            {
                var actor = Uid(http);
                if (req.MaxCostEur is { } mc && (mc <= 0 || mc > Math.Min(cfg.Blockchain.MaxCostEur, BlockchainSettings.HardMaxCostEur)))
                    throw new ArgumentException($"Das Kostenlimit muss größer 0 und höchstens {Math.Min(cfg.Blockchain.MaxCostEur, BlockchainSettings.HardMaxCostEur).ToString(System.Globalization.CultureInfo.InvariantCulture)} EUR sein (harte Obergrenze 0,001 EUR).");
                if (req.BatchMaxSize is { } bs && (bs < 1 || bs > cfg.Blockchain.BatchMaxSize)) throw new ArgumentException($"Die Batch-Größe muss zwischen 1 und {cfg.Blockchain.BatchMaxSize} liegen.");
                if (req.BatchMaxWaitMinutes is { } bw && (bw < 1 || bw > 10080)) throw new ArgumentException("Die Wartezeit muss zwischen 1 und 10080 Minuten liegen.");
                if (req.Enabled is { } en) await settings.SetAsync("chain.enabled", en ? "true" : "false", actor);
                if (req.AutoAnchor is { } au) await settings.SetAsync("chain.auto_anchor", au ? "true" : "false", actor);
                if (req.BatchMaxSize is { } b2) await settings.SetAsync("chain.batch_max_size", b2.ToString(System.Globalization.CultureInfo.InvariantCulture), actor);
                if (req.BatchMaxWaitMinutes is { } w2) await settings.SetAsync("chain.batch_max_wait_minutes", w2.ToString(System.Globalization.CultureInfo.InvariantCulture), actor);
                if (req.MaxCostEur is { } c2) await settings.SetAsync("chain.max_cost_eur", c2.ToString(System.Globalization.CultureInfo.InvariantCulture), actor);
                await users.WriteAuditAsync(actor, "blockchain.settings", "", System.Text.Json.JsonSerializer.Serialize(req, AppJson.Options));
                return Results.NoContent();
            }));

        admin.MapPost("/blockchain/process", (BlockchainProcessRequest? req, AnchorService anchors, UserService users, HttpContext http) =>
            Guard(async () =>
            {
                await users.RequireRoleAsync(Uid(http), Roles.Admin);
                var result = await anchors.ProcessAsync(req?.Force == true, http.RequestAborted);
                return Results.Ok(new { planned = result.Planned, submitted = result.Submitted, confirmed = result.Confirmed, blocked = result.Blocked, failed = result.Failed, note = result.Note });
            }));

        admin.MapPost("/blockchain/batches/{id}/retry", (string id, AnchorService anchors, HttpContext http) =>
            Guard(async () => { await anchors.RetryBatchAsync(Uid(http), id); return Results.NoContent(); }));

        // Mainnet-Freigabe: eigener, ausdrücklich bestätigter Betriebsakt (zusätzlich zur Umgebungskonfiguration); nie automatisch
        admin.MapPost("/blockchain/mainnet-approval", (MainnetApprovalRequest req, ChainSettingsStore settings, UserService users, HttpContext http) =>
            Guard(async () =>
            {
                if (!string.Equals((req.Confirm ?? "").Trim(), "MAINNET FREIGEBEN", StringComparison.Ordinal))
                    throw new ArgumentException("Zur Freigabe muss der Text \"MAINNET FREIGEBEN\" exakt bestätigt werden.");
                await settings.ApproveMainnetAsync(Uid(http));
                await users.WriteAuditAsync(Uid(http), "blockchain.mainnet.approve", "", "Mainnet für echte Transaktionen freigegeben (Kostenlimit bleibt wirksam)");
                return Results.NoContent();
            }));

        admin.MapDelete("/blockchain/mainnet-approval", (ChainSettingsStore settings, UserService users, HttpContext http) =>
            Guard(async () => { await settings.RevokeMainnetApprovalAsync(); await users.WriteAuditAsync(Uid(http), "blockchain.mainnet.revoke", "", "Mainnet-Freigabe zurückgenommen"); return Results.NoContent(); }));
    }

    private static object BatchDto(AnchorBatch b) => new
    {
        id = b.Id, network = b.Network, network_label = ChainNetworks.Label(b.Network), chain_id = b.ChainId, simulated = b.Simulated, merkle_root = b.MerkleRoot, certificate_count = b.LeafCount, status = b.Status,
        tx_hash = b.TxHash, explorer_url = b.TxHash is { Length: > 0 } && !b.Simulated ? ChainNetworks.ExplorerTx(b.Network, b.TxHash) : null, block_number = b.BlockNumber, gas_limit = b.GasLimit, gas_used = b.GasUsed,
        fee_wei = b.FeeWei, rate_eur_per_pol = b.RateEurPerPol, rate_at = b.RateAt, est_total_eur = b.EstTotalEur, fee_eur = b.FeeEur, cost_per_cert_eur = b.CostPerCertEur, simulated_cost_per_cert_eur = b.SimulatedCostEur,
        saved_eur = b.SavedEur, error = string.IsNullOrEmpty(b.Error) ? null : b.Error, attempts = b.Attempts, created_at = b.CreatedAt, submitted_at = b.SubmittedAt, confirmed_at = b.ConfirmedAt,
    };
}

public class RevokeRequest { public string Reason { get; set; } = ""; }
public class BlockchainSettingsRequest
{
    public bool? Enabled { get; set; }
    public bool? AutoAnchor { get; set; }
    public int? BatchMaxSize { get; set; }
    public int? BatchMaxWaitMinutes { get; set; }
    public decimal? MaxCostEur { get; set; }
}
public class BlockchainProcessRequest { public bool Force { get; set; } }
public class MainnetApprovalRequest { public string? Confirm { get; set; } }
