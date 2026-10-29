using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Services.Certificates.Ots;

/// <summary>Zustände des Zeitnachweises (ADR 0039 Punkt 13).</summary>
public static class OtsState
{
    public const string None = "none";                 // kein Nachweis erzeugt (OTS aus oder Calendars nicht erreichbar)
    public const string Pending = "pending";           // Receipt vorhanden, kein unabhängig geprüfter Bitcoin-Einschluss
    public const string Anchored = "anchored";         // Block-Header unabhängig geprüft, Tiefe unter der Schwelle
    public const string Confirmed = "confirmed";       // wie anchored, Tiefe ≥ Schwelle
    public const string Failed = "failed";             // nachweislich ungültig
    public const string Unverifiable = "unverifiable"; // keine Datenquelle / Netzwerkausfall
}

public sealed record BitcoinHeader(string BlockHash, string MerkleRootHex, DateTime Time, long TipHeight);

/// <summary>Quelle für Bitcoin-Block-Header — eigener Knoten oder unabhängig gesicherte Blockdaten (ADR 0039 Punkt 14).</summary>
public interface IBitcoinHeaderSource
{
    string Description { get; }
    /// <summary>Wahr nur, wenn Kettenzugehörigkeit und Tiefe aus eigener Hand stammen (eigener Bitcoin-Core-Knoten); ein Explorer ist eine Zusatzannahme.</summary>
    bool Independent => false;
    Task<BitcoinHeader> GetHeaderAsync(long height, CancellationToken ct);
}

/// <summary>Schnittstelle zum OpenTimestamps-Client (Stempeln, Upgrade); Standard: <see cref="NativeOtsClient"/> (ADR 0040), austauschbar für Tests.</summary>
public interface IOtsClient
{
    Task<string?> VersionAsync(CancellationToken ct);
    /// <summary>Stempelt die exakten Bytes (SHA-256) bei den konfigurierten Calendars; liefert die <c>.ots</c>-Datei oder wirft <see cref="OtsClientException"/>.</summary>
    Task<byte[]> StampAsync(byte[] fileBytes, IReadOnlyList<string> calendars, CancellationToken ct);
    /// <summary>Aktualisiert eine Receipt (<c>ots upgrade</c>) auf einer Kopie; liefert die neue Datei (gleich der Eingabe, wenn nichts zu aktualisieren war).</summary>
    Task<byte[]> UpgradeAsync(byte[] ots, CancellationToken ct);
}

public sealed class OtsClientException : Exception { public OtsClientException(string m) : base(m) { } }

/// <summary>Header über die JSON-RPC-Schnittstelle eines eigenen Bitcoin-Knotens (<c>getblockcount</c>, <c>getblockhash</c>, <c>getblockheader</c>).</summary>
public sealed class BitcoinRpcHeaderSource : IBitcoinHeaderSource
{
    private readonly OtsSettings _s; private readonly HttpClient _http;
    public BitcoinRpcHeaderSource(OtsSettings s, HttpClient http) { _s = s; _http = http; }
    public string Description => "eigener Bitcoin-Knoten (JSON-RPC)";
    public bool Independent => true;

    private async Task<JsonNode?> CallAsync(string method, object[] args, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _s.BitcoinRpcUrl) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "1.0", ["id"] = "learny", ["method"] = method, ["params"] = new JsonArray(args.Select(a => JsonValue.Create(a)).ToArray()) }.ToJsonString(), Encoding.UTF8, "application/json") };
        if (_s.BitcoinRpcUser.Length > 0) req.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_s.BitcoinRpcUser + ":" + _s.BitcoinRpcPassword)));
        using var res = await _http.SendAsync(req, ct);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        if (body?["error"] is { } err && err.GetValueKind() != System.Text.Json.JsonValueKind.Null) throw new InvalidOperationException("Bitcoin-RPC: " + err["message"]);
        return body?["result"];
    }

    public async Task<BitcoinHeader> GetHeaderAsync(long height, CancellationToken ct)
    {
        var tip = (long)(await CallAsync("getblockcount", Array.Empty<object>(), ct))!;
        var hash = (string)(await CallAsync("getblockhash", new object[] { height }, ct))!;
        var h = (await CallAsync("getblockheader", new object[] { hash }, ct))!;
        return new(hash, (string)h["merkleroot"]!, DateTimeOffset.FromUnixTimeSeconds((long)h["time"]!).UtcDateTime, tip);
    }
}

/// <summary>
/// Header über eine Esplora-kompatible API (z. B. mempool.space, blockstream.info). ZUSATZANNAHME: Zugehörigkeit zur besten Kette, Höhe und Tiefe stammen vom Betreiber des Explorers.
/// Selbst geprüft wird die Echtheit des Headers: Doppel-SHA-256 = Blockhash und Proof-of-Work gegen das Ziel aus nBits.
/// </summary>
public sealed class EsploraHeaderSource : IBitcoinHeaderSource
{
    private readonly Uri _base; private readonly HttpClient _http;
    public EsploraHeaderSource(string baseUrl, HttpClient http) { _base = new Uri(baseUrl.TrimEnd('/') + "/"); _http = http; }
    public string Description => $"Explorer {_base.Host} (Zusatzannahme: Kettenzugehörigkeit/Tiefe vom Betreiber; Header-PoW selbst geprüft)";

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(new Uri(_base, path), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException("Explorer: HTTP " + (int)res.StatusCode);
        return (Encoding.ASCII.GetString(await SafeHttp.ReadCappedAsync(res, "explorer", 4096, ct))).Trim();
    }

    public async Task<BitcoinHeader> GetHeaderAsync(long height, CancellationToken ct)
    {
        var hash = await GetAsync($"api/block-height/{height}", ct);
        var raw = Convert.FromHexString(await GetAsync($"api/block/{hash}/header", ct));
        if (raw.Length != 80) throw new InvalidOperationException("Header hat nicht 80 Byte.");
        var calc = Convert.ToHexString(SHA256.HashData(SHA256.HashData(raw)).Reverse().ToArray()).ToLowerInvariant();
        if (!string.Equals(calc, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Header passt nicht zum Blockhash.");
        var bits = BitConverter.ToUInt32(raw, 72);
        var target = new System.Numerics.BigInteger(bits & 0xFFFFFF) * System.Numerics.BigInteger.Pow(256, (int)(bits >> 24) - 3);
        if (new System.Numerics.BigInteger(SHA256.HashData(SHA256.HashData(raw)), isUnsigned: true, isBigEndian: false) > target) throw new InvalidOperationException("Proof-of-Work ungültig.");
        var tip = long.Parse(await GetAsync("api/blocks/tip/height", ct), System.Globalization.CultureInfo.InvariantCulture);
        return new(hash, Convert.ToHexString(raw[36..68].Reverse().ToArray()).ToLowerInvariant(), DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToUInt32(raw, 68)).UtcDateTime, tip);
    }
}

/// <summary>
/// Ergebnis der Bewertung. <see cref="State"/> bleibt der öffentliche Statusvertrag (none/pending/anchored/confirmed/failed/unverifiable); <see cref="Detail"/> ergänzt intern:
/// receipt_valid · pending · bitcoin_attestation_present · header_verified · explorer_verified · independently_confirmed · not_verifiable · failed.
/// <see cref="Independent"/> ist nur bei eigenem Knoten wahr.
/// </summary>
public sealed record OtsEvaluation(string State, string? Error, bool BindingOk, string? Calendar, long? Height, string? BlockHash, DateTime? BlockTime, long Confirmations, string Source, string Detail = "", bool Independent = false);

/// <summary>Bewertung einer Receipt: Bindung an die exakten JWS-Bytes, Attestierungen, unabhängige Header-Prüfung. Keine Zustandsänderung ohne belegbares Ergebnis.</summary>
public static class OtsEvaluator
{
    public static async Task<OtsEvaluation> EvaluateAsync(byte[] jwsBytes, byte[] ots, IBitcoinHeaderSource? headers, int requiredConfirmations, string? previousBlockHash, CancellationToken ct)
    {
        var e = await EvaluateCoreAsync(jwsBytes, ots, headers, requiredConfirmations, previousBlockHash, ct);
        var independent = headers?.Independent == true;
        var detail = e.State switch
        {
            OtsState.Failed => "failed",
            OtsState.Pending => e.Error is null ? "pending" : "pending",
            OtsState.Unverifiable => e.Height is not null ? "bitcoin_attestation_present" : "not_verifiable",
            OtsState.Anchored or OtsState.Confirmed => independent ? (e.State == OtsState.Confirmed ? "independently_confirmed" : "header_verified") : "explorer_verified",
            _ => "receipt_valid",
        };
        return e with { Detail = detail, Independent = independent && e.State is OtsState.Anchored or OtsState.Confirmed };
    }

    private static async Task<OtsEvaluation> EvaluateCoreAsync(byte[] jwsBytes, byte[] ots, IBitcoinHeaderSource? headers, int requiredConfirmations, string? previousBlockHash, CancellationToken ct)
    {
        var parsed = OtsFile.Parse(ots);
        var src = headers?.Description ?? "keine Bitcoin-Datenquelle";
        if (!parsed.Valid && parsed.Unsupported) return new(OtsState.Unverifiable, parsed.Error, false, null, null, null, null, 0, src);
        if (!parsed.Valid) return new(OtsState.Failed, "OTS-Datei ungültig: " + parsed.Error, false, null, null, null, null, 0, src);
        var binding = parsed.DigestAlgorithm == "SHA256" && CryptographicOperations.FixedTimeEquals(parsed.FileDigest, SHA256.HashData(jwsBytes));
        if (!binding) return new(OtsState.Failed, "Der Zeitnachweis gehört nicht zu dieser Manifest-JWS (Digest weicht ab).", false, null, null, null, null, 0, src);
        var calendar = parsed.Attestations.FirstOrDefault(a => a.Kind == "pending")?.CalendarUri;
        var btc = parsed.Bitcoin.ToList();
        if (btc.Count == 0)
            return parsed.HasPending ? new(OtsState.Pending, null, true, calendar, null, null, null, 0, src)
                : new(OtsState.Failed, "Die Receipt enthält keine verwertbare Attestierung.", true, null, null, null, null, 0, src);
        if (headers is null) return new(OtsState.Unverifiable, "Bitcoin-Einschluss behauptet (Höhe " + btc[0].BitcoinHeight + "), aber keine unabhängige Datenquelle konfiguriert.", true, calendar, btc[0].BitcoinHeight, null, null, 0, src);

        string? mismatch = null; Exception? unreachable = null;
        foreach (var a in btc.OrderBy(x => x.BitcoinHeight))
        {
            try
            {
                var h = await headers.GetHeaderAsync(a.BitcoinHeight, ct);
                var root = Convert.FromHexString(h.MerkleRootHex).Reverse().ToArray();   // Anzeige (Big-Endian) → interne Byte-Reihenfolge
                if (!CryptographicOperations.FixedTimeEquals(a.Message, root)) { mismatch = $"Merkle-Root des Blocks {a.BitcoinHeight} weicht ab."; continue; }
                if (previousBlockHash is { Length: > 0 } && !string.Equals(previousBlockHash, h.BlockHash, StringComparison.OrdinalIgnoreCase))
                    return new(OtsState.Pending, "Reorganisation: Der zuvor nachgewiesene Block liegt nicht mehr in der besten Kette.", true, calendar, a.BitcoinHeight, h.BlockHash, h.Time, 0, src);
                var depth = h.TipHeight - a.BitcoinHeight + 1;
                return new(depth >= requiredConfirmations ? OtsState.Confirmed : OtsState.Anchored, null, true, calendar, a.BitcoinHeight, h.BlockHash, h.Time, depth, src);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or InvalidCastException or NullReferenceException) { unreachable = e; }
        }
        if (mismatch is not null && unreachable is null) return new(OtsState.Failed, mismatch + " (falscher Block oder Reorganisation)", true, calendar, btc[0].BitcoinHeight, null, null, 0, src);
        return new(OtsState.Unverifiable, "Bitcoin-Datenquelle nicht erreichbar oder unvollständig.", true, calendar, btc[0].BitcoinHeight, null, null, 0, src);
    }
}
