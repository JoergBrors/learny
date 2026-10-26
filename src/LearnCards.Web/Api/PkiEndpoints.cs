using System.Text;
using LearnCards.Web.Domain;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Pki;

namespace LearnCards.Web.Api;

/// <summary>
/// Learny-PKI (ADR 0035). Öffentlich (ohne Anmeldung, nur öffentliche Daten): CRLs und CA-Zertifikate unter <c>/pki/*</c>.
/// Administration unter <c>/api/pki/*</c>: nur Browser-Anmeldung (nie MCP-Token oder Dienstschlüssel — der Präfix steht nicht in <c>TokenAllowedPrefixes</c>), Lesen für Administrator und Auditor,
/// Schreiben nur für Administratoren (zusätzlich im Dienst gegen die DB geprüft). Es gibt keine Endpunkte, die Schlüssel, CSR-Inhalte oder zusätzliche Roots entgegennehmen oder ausgeben.
/// </summary>
public static partial class ApiEndpoints
{
    private static string Pem(byte[] der, string label) => $"-----BEGIN {label}-----\n{Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks).Replace("\r", "")}\n-----END {label}-----\n";

    private static object PkiCertDto(PkiCertificate c) => new
    {
        id = c.Id, ca_id = c.CaId, type = c.CertificateType, serial_number = c.SerialNumber, subject = c.Subject, issuer = c.Issuer, status = c.Status,
        not_before = c.NotBefore, not_after = c.NotAfter, issued_at = c.IssuedAt, issued_by = c.IssuedBy, revoked_at = c.RevokedAt, revocation_reason = c.RevocationReason,
        public_key_algorithm = c.PublicKeyAlgorithm, signature_algorithm = c.SignatureAlgorithm, sha256 = c.CertificateSha256, subject_key_id = c.SubjectKeyId,
        download = $"/pki/certificates/{c.Id}.cer",
    };

    private static object PkiCrlDto(PkiCrl c) => new
    {
        id = c.Id, issuer_ca_id = c.IssuerCaId, crl_number = c.CrlNumber, this_update = c.ThisUpdate, next_update = c.NextUpdate, entry_count = c.EntryCount,
        signature_algorithm = c.SignatureAlgorithm, sha256 = c.Sha256, current = c.IsFresh(DateTime.UtcNow), download = $"/pki/crl/{c.IssuerCaId}.crl",
    };

    private static void MapPkiEndpoints(WebApplication app, RouteGroupBuilder secured)
    {
        // ─── Öffentlich ─────────────────────────────────────────────────────
        app.MapGet("/pki/crl/{file}", async (string file, PkiService pki, HttpContext http) =>
        {
            if (!PublicAllowed(http, 240)) return Results.StatusCode(429);
            var id = file.EndsWith(".crl", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
            if (!Guid.TryParse(id, out _)) return Results.NotFound();
            var crl = await pki.GetCurrentCrlAsync(id);
            if (crl is null) return Results.Json(new { detail = "Keine aktuelle CRL verfügbar." }, statusCode: 503);
            http.Response.Headers.CacheControl = $"public, max-age={(int)Math.Clamp((crl.NextUpdate - DateTime.UtcNow).TotalSeconds / 2, 60, 3600)}";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(crl.Der, "application/pkix-crl", $"{id}.crl");
        }).AllowAnonymous();

        app.MapGet("/pki/certificates/{which}", async (string which, PkiService pki, HttpContext http) =>
        {
            if (!PublicAllowed(http, 240)) return Results.StatusCode(429);
            var st = await pki.GetStatusAsync();
            PkiCertificate? cert = which.ToLowerInvariant() switch
            {
                "root" => st.Root,
                "issuing" => st.Issuing,
                _ => Guid.TryParse(which.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) ? which[..^4] : which, out _) ? await pki.GetCertificateAsync(which.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) ? which[..^4] : which) : null,
            };
            if (cert is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(PkiService.ToX509(cert).RawData, "application/pkix-cert", $"{cert.CertificateType}-{cert.SerialNumber[..Math.Min(8, cert.SerialNumber.Length)]}.cer");
        }).AllowAnonymous();

        app.MapGet("/pki/chain.pem", async (PkiService pki, HttpContext http) =>
        {
            if (!PublicAllowed(http, 240)) return Results.StatusCode(429);
            var st = await pki.GetStatusAsync();
            if (st.Root is null || st.Issuing is null) return Results.NotFound();
            var pem = Pem(PkiService.ToX509(st.Issuing).RawData, "CERTIFICATE") + Pem(PkiService.ToX509(st.Root).RawData, "CERTIFICATE");
            return Results.Text(pem, "application/x-pem-file", Encoding.ASCII);
        }).AllowAnonymous();

        // ─── Administration ─────────────────────────────────────────────────
        var pkiApi = secured.MapGroup("/pki");
        pkiApi.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.Items.ContainsKey(ViaKeyItem) || http.Items.ContainsKey(ViaTokenItem))
                return Results.Json(new { detail = "Die PKI-Verwaltung ist nur über die Browser-Anmeldung verfügbar." }, statusCode: 403);
            try { await http.RequestServices.GetRequiredService<UserService>().RequireAnyRoleAsync(Uid(http), Roles.Admin, Roles.Auditor); }
            catch (UnauthorizedAccessException) { return Results.Json(new { detail = "Administrator- oder Auditorrechte erforderlich." }, statusCode: 403); }
            if (!HttpMethods.IsGet(http.Request.Method) && !CurrentUser(http).Roles.Contains(Roles.Admin))
                return Results.Json(new { detail = "Änderungen an der PKI sind Administratoren vorbehalten (Auditoren dürfen nur lesen)." }, statusCode: 403);
            return await next(ctx);
        });

        pkiApi.MapGet("/status", (PkiService pki, HttpContext http) => Guard(async () =>
        {
            var st = await pki.GetStatusAsync();
            return Results.Ok(new
            {
                state = st.State, message = st.Message, warnings = st.Warnings, key_algorithm = st.KeyAlgorithm, public_url = st.PublicUrl, initialized_at = st.InitializedAt,
                auto_init_enabled = st.AutoInitEnabled, can_initialize = st.CanInitialize, root_key_on_server = st.RootKeyOnServer,
                suggested_public_url = pki.Settings.PublicUrl.Length > 0 ? pki.Settings.PublicUrl : $"{http.Request.Scheme}://{http.Request.Host}",
                root = st.Root is null ? null : PkiCertDto(st.Root), issuing = st.Issuing is null ? null : PkiCertDto(st.Issuing), signing = st.Signing is null ? null : PkiCertDto(st.Signing),
                root_crl = st.RootCrl is null ? null : PkiCrlDto(st.RootCrl), issuing_crl = st.IssuingCrl is null ? null : PkiCrlDto(st.IssuingCrl),
                publicly_trusted = false,
                notice = "Die Learny-PKI begründet keine automatische öffentliche Anerkennung durch Adobe, Betriebssysteme oder Vertrauenslisten.",
            });
        }));
        pkiApi.MapGet("/certificates", (PkiService pki, string? type) => Guard(async () => Results.Ok((await pki.ListCertificatesAsync(type)).Select(PkiCertDto))));
        pkiApi.MapGet("/certificates/{id}", (string id, PkiService pki) => Guard(async () =>
            await pki.GetCertificateAsync(id) is { } c
                ? Results.Ok(new { certificate = PkiCertDto(c), events = (await pki.ListEventsAsync(id)).Select(e => new { e.Event, e.Detail, e.Actor, e.CreatedAt }) })
                : Results.Json(new { detail = "Zertifikat nicht gefunden." }, statusCode: 404)));
        pkiApi.MapGet("/revocations", (PkiService pki) => Guard(async () => Results.Ok((await pki.ListRevocationsAsync()).Select(r => new
        {
            r.Id, r.CertificateId, issuing_ca_id = r.IssuingCaId, serial_number = r.SerialNumber, revocation_time = r.RevocationTime, reason_code = r.ReasonCode, reason = PkiReasons.Name(r.ReasonCode),
            revoked_by = r.RevokedBy, released_at = r.ReleasedAt, active = r.ReleasedAt == null,
        }))));
        pkiApi.MapGet("/crls", (PkiService pki) => Guard(async () => Results.Ok((await pki.ListCrlsAsync()).Select(PkiCrlDto))));
        pkiApi.MapGet("/selftest", (PkiVerifier verifier) => Guard(async () =>
        {
            var checks = await verifier.SelfTestAsync();
            return Results.Ok(new { ok = checks.All(c => c.Ok), checks = checks.Select(c => new { name = c.Name, ok = c.Ok, detail = c.Detail }) });
        }));

        pkiApi.MapPost("/initialize", (PkiInitRequest req, PkiService pki, HttpContext http) => Guard(async () =>
        {
            var st = await pki.InitializeAsync(Uid(http), req.PublicUrl);
            return Results.Ok(new { state = st.State, message = st.Message });
        }));
        pkiApi.MapPost("/reset-incomplete", (PkiConfirmRequest req, PkiService pki, HttpContext http) => Guard(async () => { await pki.ResetIncompleteAsync(Uid(http), req.Confirmation ?? ""); return Results.NoContent(); }));
        pkiApi.MapPost("/signing/renew", (PkiService pki, HttpContext http) => Guard(async () => Results.Ok(PkiCertDto(await pki.RenewSigningCertificateAsync(Uid(http))))));
        pkiApi.MapPost("/certificates/{id}/revoke", (string id, PkiRevokeRequest req, PkiService pki, HttpContext http) => Guard(async () =>
        {
            if (!PkiReasons.TryParse(req.Reason, out var code)) return Results.Json(new { detail = "Ungültiger Widerrufsgrund. Erlaubt: " + string.Join(", ", PkiReasons.Names.Values) }, statusCode: 400);
            await pki.RevokeAsync(Uid(http), id, code, req.ConfirmSerial ?? "", DecodePfx(req.RootPfxBase64), req.RootPassphrase);
            return Results.NoContent();
        }));
        pkiApi.MapPost("/certificates/{id}/release-hold", (string id, PkiRootKeyRequest req, PkiService pki, HttpContext http) => Guard(async () =>
        { await pki.ReleaseHoldAsync(Uid(http), id, DecodePfx(req.RootPfxBase64), req.RootPassphrase); return Results.NoContent(); }));
        pkiApi.MapPost("/crl/issue", (PkiService pki, HttpContext http) => Guard(async () => Results.Ok(PkiCrlDto(await pki.IssueCrlAsync(Uid(http))))));
        pkiApi.MapPost("/crl/root", (PkiRootKeyRequest req, PkiService pki, HttpContext http) => Guard(async () =>
            Results.Ok(PkiCrlDto(await pki.IssueRootCrlAsync(Uid(http), DecodePfx(req.RootPfxBase64), req.RootPassphrase)))));
        pkiApi.MapPost("/root-key/remove", (PkiConfirmRequest req, PkiService pki, HttpContext http) => Guard(async () => { await pki.RemoveRootKeyFromServerAsync(Uid(http), req.Confirmation ?? ""); return Results.NoContent(); }));
    }

    private static byte[]? DecodePfx(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try { var d = Convert.FromBase64String(b64); return d.Length is > 0 and <= 64 * 1024 ? d : throw new ArgumentException("Root-Schlüsseldatei hat eine unzulässige Größe."); }
        catch (FormatException) { throw new ArgumentException("Root-Schlüsseldatei ist kein gültiges Base64."); }
    }
}

public sealed record PkiInitRequest(string? PublicUrl);
public sealed record PkiConfirmRequest(string? Confirmation);
public sealed record PkiRootKeyRequest(string? RootPfxBase64, string? RootPassphrase);
public sealed record PkiRevokeRequest(string? Reason, string? ConfirmSerial, string? RootPfxBase64, string? RootPassphrase);
