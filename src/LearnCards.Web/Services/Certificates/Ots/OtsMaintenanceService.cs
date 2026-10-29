using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Certificates.Ots;

/// <summary>Gespeicherter Stand einer Proof-Version (Tabelle <c>integrity_proofs</c>).</summary>
public sealed record IntegrityProofRow(string CertificateId, int ProofVersion, string ManifestSha256, string JwsSha256, string Jws, byte[]? Ots, string CalendarStatus, string AnchorStatus,
    long? BitcoinHeight, string? BlockHash, DateTime? BlockTime, long Confirmations, DateTime? LastCheckedAt, DateTime? LastVerifiedAt, string Error, string ClientVersion, string PdfSha256, DateTime CreatedAt);

public sealed record StampResult(byte[]? Ots, string CalendarStatus, string Error, string ClientVersion);

/// <summary>
/// Erzeugung, Ablage und Pflege der OpenTimestamps-Nachweise (ADR 0039). Proof-Versionen werden nur angefügt, nie überschrieben; das gespeicherte PDF bleibt unberührt.
/// </summary>
public sealed class OtsMaintenanceService
{
    private readonly IDatabase _db; private readonly OtsSettings _s; private readonly IOtsClient _client; private readonly IBitcoinHeaderSource? _headers;
    private Func<DateTime> _now = () => DateTime.UtcNow;

    public OtsMaintenanceService(IDatabase db, AppConfig cfg, IOtsClient client, IBitcoinHeaderSource? headers = null)
    {
        _db = db; _s = cfg.Ots; _client = client; _headers = headers;
        if (_headers is null && cfg.Ots.BitcoinRpcUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            _headers = new BitcoinRpcHeaderSource(cfg.Ots, new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.Ots.TimeoutSeconds) });
        else if (_headers is null && cfg.Ots.BitcoinExplorerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            _headers = new EsploraHeaderSource(cfg.Ots.BitcoinExplorerUrl, LearnCards.Web.Services.ImageStorage.SafeHttp.CreateClient(false, cfg.Ots.TimeoutSeconds));
    }

    public OtsSettings Settings => _s;
    public void UseClock(Func<DateTime> now) => _now = now;

    /// <summary>Stempelt die exakten Bytes der Manifest-JWS, falls OTS betriebsbereit konfiguriert ist; Fehler führen nie zum Abbruch der Ausstellung (Ergebnis „none“ mit Grund).</summary>
    public async Task<StampResult> TryStampAsync(byte[] jwsBytes, CancellationToken ct)
    {
        if (_s.Problem() is { } problem) return new(null, OtsState.None, problem, "");
        try
        {
            var version = await _client.VersionAsync(ct) ?? "";
            var ots = await _client.StampAsync(jwsBytes, _s.Calendars, ct);
            // Rückprobe: die Receipt muss strukturell gültig und an genau diese Bytes gebunden sein (nie die Ausgabe eines Clients ungeprüft einbetten)
            var parsed = OtsFile.Parse(ots);
            if (!parsed.Valid || !CryptographicOperations.FixedTimeEquals(parsed.FileDigest, SHA256.HashData(jwsBytes)) || parsed.DigestAlgorithm != "SHA256")
                return new(null, OtsState.Failed, "Die vom ots-Client erzeugte Receipt ist ungültig oder nicht an die Manifest-JWS gebunden.", version);
            return new(ots, "submitted", "", version);
        }
        catch (OtsClientException e) { return new(null, OtsState.None, e.Message, ""); }
        catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException) { return new(null, OtsState.None, "Calendar/Client nicht erreichbar: " + e.GetType().Name, ""); }
    }

    private static IntegrityProofRow Map(Dictionary<string, string?> r) => new(r["certificate_id"]!, int.Parse(r["proof_version"]!), r["manifest_sha256"]!, r["jws_sha256"]!, r["jws"]!,
        r["ots_base64"] is { Length: > 0 } o ? Convert.FromBase64String(o) : null, r["calendar_status"] ?? "none", r["anchor_status"] ?? "none",
        r["bitcoin_height"] is { Length: > 0 } h ? long.Parse(h) : null, r["bitcoin_block_hash"], r["bitcoin_block_time"] is { Length: > 0 } bt ? DbValue.ToDateTime(bt) : null, long.Parse(r["confirmations"] ?? "0"),
        r["last_checked_at"] is { Length: > 0 } lc ? DbValue.ToDateTime(lc) : null, r["last_verified_at"] is { Length: > 0 } lv ? DbValue.ToDateTime(lv) : null, r["error"] ?? "", r["ots_client_version"] ?? "", r["pdf_sha256"] ?? "", DbValue.ToDateTime(r["created_at"]!));

    public async Task<IntegrityProofRow?> GetLatestAsync(string certificateId)
    {
        if (!UserService.IsGuid(certificateId)) return null;
        var rows = await _db.QueryAsync("SELECT * FROM integrity_proofs WHERE certificate_id = @c ORDER BY proof_version DESC LIMIT 1", new[] { ("c", (object?)certificateId) });
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task<List<IntegrityProofRow>> GetVersionsAsync(string certificateId)
    {
        if (!UserService.IsGuid(certificateId)) return new();
        return (await _db.QueryAsync("SELECT * FROM integrity_proofs WHERE certificate_id = @c ORDER BY proof_version", new[] { ("c", (object?)certificateId) })).Select(Map).ToList();
    }

    /// <summary>Legt Proof-Version 1 an (nach der PDF-Erzeugung). Idempotent: existiert bereits eine Version, passiert nichts.</summary>
    public async Task RecordInitialAsync(string certificateId, byte[] manifestBytes, string jws, StampResult stamp, string pdfSha256)
    {
        if (await GetLatestAsync(certificateId) is not null) return;
        var state = stamp.Ots is not null ? OtsState.Pending : stamp.CalendarStatus == OtsState.Failed ? OtsState.Failed : OtsState.None;
        await InsertAsync(certificateId, 1, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(jws))).ToLowerInvariant(), jws,
            stamp.Ots, stamp.CalendarStatus, state, null, null, null, 0, stamp.Error, stamp.ClientVersion, pdfSha256, null);
    }

    private Task InsertAsync(string certId, int version, string manifestSha, string jwsSha, string jws, byte[]? ots, string calendar, string anchor, long? height, string? blockHash, DateTime? blockTime,
        long confirmations, string error, string clientVersion, string pdfSha, DateTime? verifiedAt)
        => _db.ExecuteAsync("""
            INSERT INTO integrity_proofs (certificate_id, proof_version, manifest_sha256, jws_sha256, jws, ots_base64, calendar_status, anchor_status, bitcoin_height, bitcoin_block_hash, bitcoin_block_time,
                confirmations, last_checked_at, last_verified_at, error, ots_client_version, pdf_sha256, created_at)
            VALUES (@c, @v, @m, @j, @jws, @ots, @cal, @an, @h, @bh, @bt, @conf, @now, @ver, @err, @cv, @pdf, @now)
            """, new (string, object?)[] { ("c", certId), ("v", version), ("m", manifestSha), ("j", jwsSha), ("jws", jws), ("ots", ots is null ? null : Convert.ToBase64String(ots)), ("cal", calendar), ("an", anchor), ("h", height),
                ("bh", blockHash), ("bt", blockTime), ("conf", confirmations), ("now", _now()), ("ver", verifiedAt), ("err", error), ("cv", clientVersion), ("pdf", pdfSha) });

    /// <summary>Bewertet die jüngste Receipt eines Zertifikats ohne Netzwerkzugriff auf Calendars (nur die konfigurierte Bitcoin-Quelle) — für die Anzeige.</summary>
    public async Task<(IntegrityProofRow? Row, OtsEvaluation? Eval)> EvaluateLatestAsync(string certificateId, CancellationToken ct)
    {
        var row = await GetLatestAsync(certificateId);
        if (row?.Ots is null) return (row, null);
        var eval = await OtsEvaluator.EvaluateAsync(Encoding.ASCII.GetBytes(row.Jws), row.Ots, _headers, _s.Confirmations, row.BlockHash, ct);
        return (row, eval);
    }

    /// <summary>Bewertet eine (eingebettete) Receipt gegen die exakten JWS-Bytes — ohne Learny-Datenbank; Quelle der Bitcoin-Header ist die konfigurierte Datenquelle (sonst „nicht überprüfbar“).</summary>
    public Task<OtsEvaluation> EvaluateBytesAsync(byte[] jwsBytes, byte[] ots, CancellationToken ct) => OtsEvaluator.EvaluateAsync(jwsBytes, ots, _headers, _s.Confirmations, null, ct);

    /// <summary>
    /// Idempotenter Wartungslauf: lädt die jüngsten Versionen mit Receipt im Zustand pending/anchored/unverifiable, aktualisiert die Receipt (<c>ots upgrade</c>), bewertet den Bitcoin-Nachweis
    /// und fügt bei geändertem Stand eine <b>neue Proof-Version</b> an. Die ursprüngliche Receipt bleibt unverändert; Netzwerkausfälle ändern keinen kryptografischen Zustand.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var all = (await _db.QueryAsync("SELECT * FROM integrity_proofs ORDER BY certificate_id, proof_version")).Select(Map).GroupBy(r => r.CertificateId).Select(g => g.Last()).ToList();
        var changed = 0;
        foreach (var row in all.Where(NeedsCheck))
        {
            ct.ThrowIfCancellationRequested();
            if ((await ProcessRowAsync(row, ct)).Changed) changed++;
        }
        return changed;
    }

    private static bool NeedsCheck(IntegrityProofRow r) => r.Ots is not null && r.AnchorStatus is OtsState.Pending or OtsState.Anchored or OtsState.Unverifiable;

    public sealed record RowCheck(bool Changed, string UpgradeError, OtsEvaluation Eval);

    /// <summary>Upgrade (soweit konfiguriert) und Bewertung einer Proof-Zeile; fügt bei geändertem Stand eine neue Proof-Version an. Die Logik gilt für Wartungslauf und Einzelprüfung gleichermaßen.</summary>
    private async Task<RowCheck> ProcessRowAsync(IntegrityProofRow row, CancellationToken ct)
    {
        var ots = row.Ots!; var upgradeError = "";
        if (_s.Problem() is null)
        {
            try { ots = await _client.UpgradeAsync(row.Ots!, ct); }
            catch (OtsClientException e) { upgradeError = e.Message; }
            catch (Exception e) when (e is IOException or OperationCanceledException) { upgradeError = "Upgrade nicht möglich: " + e.GetType().Name; }
        }
        if (OtsFile.Parse(ots) is not { Valid: true }) { ots = row.Ots!; upgradeError = "Aktualisierte Receipt ungültig, bisherige bleibt maßgeblich."; }
        var eval = await OtsEvaluator.EvaluateAsync(Encoding.ASCII.GetBytes(row.Jws), ots, _headers, _s.Confirmations, row.BlockHash, ct);
        var otsChanged = !ots.AsSpan().SequenceEqual(row.Ots);
        // „unverifiable“ ist keine Aussage über den Nachweis: den bisherigen belegten Stand nicht überschreiben
        var newState = eval.State == OtsState.Unverifiable && row.AnchorStatus is OtsState.Pending or OtsState.Anchored ? row.AnchorStatus : eval.State;
        if (otsChanged || newState != row.AnchorStatus || eval.BlockHash != row.BlockHash || eval.Confirmations != row.Confirmations && eval.State is OtsState.Anchored or OtsState.Confirmed)
        {
            await InsertAsync(row.CertificateId, row.ProofVersion + 1, row.ManifestSha256, row.JwsSha256, row.Jws, ots, otsChanged ? "upgraded" : row.CalendarStatus, newState, eval.Height ?? row.BitcoinHeight,
                eval.BlockHash ?? row.BlockHash, eval.BlockTime ?? row.BlockTime, eval.Confirmations, eval.Error ?? upgradeError, row.ClientVersion, row.PdfSha256,
                eval.State is OtsState.Anchored or OtsState.Confirmed ? _now() : row.LastVerifiedAt);
            return new(true, upgradeError, eval);
        }
        await _db.ExecuteAsync("UPDATE integrity_proofs SET last_checked_at = @t WHERE certificate_id = @c AND proof_version = @v", new (string, object?)[] { ("t", _now()), ("c", row.CertificateId), ("v", row.ProofVersion) });
        return new(false, upgradeError, eval);
    }

    public enum CheckKind { NoReceipt, NotNeeded, NotConfigured, Throttled, Checked }
    public sealed record SingleCheck(CheckKind Kind, IntegrityProofRow? Row, RowCheck? Result);

    /// <summary>
    /// Einzelprüfung der jüngsten Receipt eines Zertifikats (Live-Anzeige auf der Prüfseite): dieselbe Logik wie der Wartungslauf, aber höchstens alle <paramref name="minInterval"/>
    /// (Drosselung über <c>last_checked_at</c>, damit Besucher keine Calendar-Abfragen erzwingen können). Bereits bestätigte Nachweise werden nicht erneut abgefragt.
    /// </summary>
    public async Task<SingleCheck> CheckLatestAsync(string certificateId, TimeSpan minInterval, CancellationToken ct)
    {
        var row = await GetLatestAsync(certificateId);
        if (row?.Ots is null) return new(CheckKind.NoReceipt, row, null);
        if (!NeedsCheck(row)) return new(CheckKind.NotNeeded, row, null);
        if (_s.Problem() is not null) return new(CheckKind.NotConfigured, row, null);
        if (row.LastCheckedAt is { } last && _now() - last < minInterval) return new(CheckKind.Throttled, row, null);
        var result = await ProcessRowAsync(row, ct);
        return new(CheckKind.Checked, await GetLatestAsync(certificateId), result);
    }
}
