using System.Numerics;
using System.Text.Json.Nodes;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Services.Certificates;

public sealed record PriceQuote(decimal EurPerPol, DateTime At, string Source);

public interface IPriceFeed
{
    /// <summary>Aktueller POL/EUR-Kurs oder der zuletzt erfolgreich abgerufene (mit seinem Zeitstempel); <c>null</c> = nie erfolgreich.</summary>
    Task<PriceQuote?> GetQuoteAsync(CancellationToken ct = default);
}

/// <summary>Preisquelle über HTTP (konfigurierbar, JSON-Pfad). Es gibt keinen fest hinterlegten Kurs; schlägt der Abruf fehl, bleibt der alte Kurs mit seinem Alter sichtbar — die Kostenschranke lehnt veraltete Kurse ab.</summary>
public sealed class HttpPriceFeed : IPriceFeed, IDisposable
{
    private readonly BlockchainSettings _cfg;
    private readonly HttpClient _http;
    private PriceQuote? _last;

    public HttpPriceFeed(AppConfig config)
    {
        _cfg = config.Blockchain;
        _http = SafeHttp.CreateClient(allowPrivateNetwork: Uri.TryCreate(_cfg.PriceUrl, UriKind.Absolute, out var u) && u.IsLoopback, _cfg.HttpTimeoutSeconds);
    }

    public async Task<PriceQuote?> GetQuoteAsync(CancellationToken ct = default)
    {
        if (_last is not null && DateTime.UtcNow - _last.At < TimeSpan.FromSeconds(30)) return _last;
        try
        {
            using var res = await _http.GetAsync(_cfg.PriceUrl, ct);
            if (!res.IsSuccessStatusCode) return _last;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 100_000) return _last;
            JsonNode? n = JsonNode.Parse(bytes);
            foreach (var part in _cfg.PriceJsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) n = n?[part];
            if (n is null || !decimal.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)) return _last;
            if (rate <= 0m || rate > 10_000m) return _last;   // offensichtlich unplausibel
            return _last = new PriceQuote(rate, DateTime.UtcNow, new Uri(_cfg.PriceUrl).Host);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or ImageStorageException) { return _last; }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record CostInput(
    bool Mainnet, ulong? GasEstimate, BigInteger? GasPriceWei, decimal? EurPerPol, DateTime? RateAt, DateTime Now,
    int CertCount, decimal LimitPerCertEur, decimal GasSafetyFactor, decimal RateSafetyFactor, TimeSpan MaxRateAge);

public sealed record CostDecision(
    bool Allowed, string Code, string Reason, ulong GasLimit, BigInteger MaxFeePerGasWei, decimal TotalEur, decimal PerCertEur, decimal LimitEur, decimal? SimulatedEur);

/// <summary>
/// Harte Kostenschranke (ADR 0022). Rein rechnerisch und ohne Nebenwirkungen — der Aufrufer darf nur senden, wenn <see cref="CostDecision.Allowed"/> gilt, und muss
/// <see cref="CostDecision.GasLimit"/> und <see cref="CostDecision.MaxFeePerGasWei"/> unverändert in die Transaktion übernehmen: dadurch begrenzt die Freigabe die Transaktion technisch
/// (mehr als GasLimit × MaxFee kann nie bezahlt werden).
/// <para>Konservativ: Kosten = Gas-Limit (Schätzung + 20 %) × (Gaspreis × Sicherheitsfaktor) × (Kurs × Sicherheitsfaktor). Unbekannte oder veraltete Eingaben sperren.</para>
/// </summary>
public static class AnchorCostGuard
{
    private const decimal WeiPerPol = 1_000_000_000_000_000_000m;

    public static CostDecision Evaluate(CostInput i)
    {
        var limit = Math.Min(i.LimitPerCertEur, BlockchainSettings.HardMaxCostEur);
        CostDecision Blocked(string code, string reason, ulong gas = 0, BigInteger? fee = null, decimal total = 0, decimal per = 0, decimal? sim = null) =>
            new(false, code, reason, gas, fee ?? BigInteger.Zero, total, per, limit, sim);

        if (i.CertCount <= 0) return Blocked("empty", "Der Batch enthält keine Nachweise.");
        if (limit <= 0) return Blocked("limit_invalid", "Das Kostenlimit ist ungültig.");
        if (i.GasEstimate is null or 0) return Blocked("cost_unknown", "Die Gas-Schätzung fehlt — Kosten unbekannt.");
        if (i.GasPriceWei is null || i.GasPriceWei <= 0) return Blocked("cost_unknown", "Der Gaspreis fehlt — Kosten unbekannt.");

        var gasLimit = (ulong)Math.Ceiling(i.GasEstimate.Value * 1.2m);
        var maxFee = (BigInteger)Math.Ceiling((decimal)i.GasPriceWei.Value * i.GasSafetyFactor);
        var feePol = (decimal)(new BigInteger(gasLimit) * maxFee) / WeiPerPol;

        // Kurs: ohne frischen Kurs sind die EUR-Kosten nicht bekannt. Auf Testnet ist das unkritisch (keine echten Kosten), auf Mainnet sperrt es.
        decimal? simulated = null;
        if (i.EurPerPol is > 0 && i.RateAt is not null) simulated = Math.Round(feePol * i.EurPerPol.Value * i.RateSafetyFactor / i.CertCount, 8);
        if (!i.Mainnet) return new(true, "testnet", "Testnet: keine echten Gebühren.", gasLimit, maxFee, 0m, 0m, limit, simulated);

        if (i.EurPerPol is not > 0 || i.RateAt is null) return Blocked("price_unavailable", "Kein POL/EUR-Kurs verfügbar — Kosten unbekannt.", gasLimit, maxFee);
        var age = i.Now - i.RateAt.Value;
        if (age > i.MaxRateAge || age < TimeSpan.FromMinutes(-2)) return Blocked("price_stale", $"Der POL/EUR-Kurs ist veraltet (Alter {Math.Max(0, (int)age.TotalMinutes)} min, erlaubt {(int)i.MaxRateAge.TotalMinutes} min).", gasLimit, maxFee);

        var totalEur = feePol * i.EurPerPol.Value * i.RateSafetyFactor;
        var perCert = totalEur / i.CertCount;
        if (perCert > limit)
            return Blocked("cost_limit", $"Kostenlimit überschritten: {perCert:0.########} EUR je Nachweis > {limit:0.########} EUR (Batch {i.CertCount}, gesamt {totalEur:0.########} EUR).", gasLimit, maxFee, totalEur, perCert, simulated);

        // Technische Begrenzung: höchste je Gas zahlbare Gebühr, sodass der Batch das Limit auch im ungünstigsten Fall einhält
        var capWeiPerGas = (BigInteger)Math.Floor(limit * i.CertCount / (i.EurPerPol.Value * i.RateSafetyFactor) * WeiPerPol / gasLimit);
        if (capWeiPerGas < maxFee) maxFee = capWeiPerGas;
        if (maxFee < i.GasPriceWei.Value) return Blocked("cost_limit", "Die zulässige Maximalgebühr liegt unter dem aktuellen Gaspreis.", gasLimit, maxFee, totalEur, perCert, simulated);
        return new(true, "ok", $"Freigegeben: höchstens {perCert:0.########} EUR je Nachweis (inkl. Sicherheitsaufschlag).", gasLimit, maxFee, totalEur, perCert, limit, simulated);
    }
}
