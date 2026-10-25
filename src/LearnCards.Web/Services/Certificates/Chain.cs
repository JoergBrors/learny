using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Services.Certificates;

/// <summary>EIP-1559-Transaktion für die Anker-Verankerung (Nullwert-Transaktion an die eigene Adresse mit den Anker-Daten).</summary>
public sealed record TxRequest(int ChainId, string From, string To, string DataHex, ulong Nonce, ulong GasLimit, BigInteger MaxFeePerGas, BigInteger MaxPriorityFeePerGas);

public sealed record ChainReceipt(string TxHash, bool Success, ulong BlockNumber, BigInteger GasUsed, BigInteger EffectiveGasPrice);

public sealed record ChainTx(string Hash, string From, string To, string InputHex, ulong? BlockNumber);

public class ChainException : Exception
{
    public ChainException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Lesender und sendender Zugriff auf eine EVM-Kette (JSON-RPC). Es gibt nur die Methoden, die die Verankerung braucht.</summary>
public interface IChainClient
{
    Task<int> GetChainIdAsync(CancellationToken ct = default);
    Task<BigInteger> GetGasPriceAsync(CancellationToken ct = default);
    Task<BigInteger?> GetPriorityFeeAsync(CancellationToken ct = default);
    Task<ulong> EstimateGasAsync(string from, string to, string dataHex, CancellationToken ct = default);
    Task<BigInteger> GetBalanceAsync(string address, CancellationToken ct = default);
    Task<ulong> GetNonceAsync(string address, CancellationToken ct = default);
    Task<string> SendRawTransactionAsync(string rawHex, CancellationToken ct = default);
    Task<ChainReceipt?> GetReceiptAsync(string txHash, CancellationToken ct = default);
    Task<ChainTx?> GetTransactionAsync(string txHash, CancellationToken ct = default);
    Task<ulong> GetBlockNumberAsync(CancellationToken ct = default);
}

/// <summary>
/// Signiert Transaktionen. Die Standardimplementierung ist <see cref="UnavailableAnchorSigner"/>: Der private Schlüssel gehört nie in diese Anwendung, solange keine
/// geprüfte Signierlösung gewählt ist (ADR 0022: EVM-Signierung braucht Keccak-256 und secp256k1 — beides nicht in der .NET-Basisbibliothek, eigene Kryptografie ist ausgeschlossen).
/// </summary>
public interface IAnchorSigner
{
    string Name { get; }
    bool IsAvailable { get; }
    /// <summary>Adresse der Wallet (öffentlich) — Absender der Verankerung.</summary>
    string? Address { get; }
    Task<string> SignAsync(TxRequest tx, CancellationToken ct = default);
}

public sealed class UnavailableAnchorSigner : IAnchorSigner
{
    private readonly string? _address;
    public UnavailableAnchorSigner(string? address) => _address = string.IsNullOrEmpty(address) ? null : address;
    public string Name => "nicht konfiguriert";
    public bool IsAvailable => false;
    public string? Address => _address;
    public Task<string> SignAsync(TxRequest tx, CancellationToken ct = default) =>
        throw new ChainException("Es ist kein Transaktionssignierer konfiguriert (ADR 0022). Die Verankerung bleibt ausstehend.");
}

/// <summary>
/// Pseudo-Signierer für Simulation und Tests gegen einen lokalen Mock-RPC: erzeugt eine Pseudo-Rohtransaktion ("0xSIM…" + JSON), die nur
/// <see cref="SimulatedChainClient"/> bzw. ein Test-Mock versteht. Wird nie gegen ein öffentliches Netzwerk akzeptiert (Konfigurationsprüfung, RPC-Host muss localhost sein).
/// </summary>
public sealed class PseudoAnchorSigner : IAnchorSigner
{
    public const string Prefix = "0x53494d";   // "SIM"
    public PseudoAnchorSigner(string address) => Address = address;
    public string Name => "Pseudo-Signierer (Simulation/Test)";
    public bool IsAvailable => true;
    public string? Address { get; }

    public Task<string> SignAsync(TxRequest tx, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { chain_id = tx.ChainId, from = tx.From, to = tx.To, data = tx.DataHex, nonce = tx.Nonce, gas = tx.GasLimit, max_fee = tx.MaxFeePerGas.ToString(), tip = tx.MaxPriorityFeePerGas.ToString() });
        return Task.FromResult(Prefix + Convert.ToHexString(Encoding.UTF8.GetBytes(json)).ToLowerInvariant());
    }

    public static JsonNode? Decode(string rawHex)
    {
        if (!rawHex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        try { return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromHexString(rawHex[Prefix.Length..]))); } catch { return null; }
    }

    public static string TxHash(string rawHex) => "0x" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(rawHex.ToLowerInvariant()))).ToLowerInvariant();
}

/// <summary>Echter JSON-RPC-Client (Polygon). Ziel-URL nur aus der Konfiguration; SSRF-Schutz über <see cref="SafeHttp"/> (keine Weiterleitungen, keine privaten Ziele außer localhost-Tests).</summary>
public sealed class JsonRpcChainClient : IChainClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _url;
    private int _id;

    public JsonRpcChainClient(string url, int timeoutSeconds)
    {
        _url = new Uri(url);
        _http = SafeHttp.CreateClient(allowPrivateNetwork: _url.IsLoopback, timeoutSeconds);
    }

    private async Task<JsonNode?> CallAsync(string method, JsonArray args, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _id), ["method"] = method, ["params"] = args };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new ChainException($"RPC antwortet mit HTTP {(int)res.StatusCode}.");
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 2_000_000) throw new ChainException("RPC-Antwort ist zu groß.");
            var json = JsonNode.Parse(bytes);
            if (json?["error"] is { } err) throw new ChainException("RPC-Fehler: " + (err["message"]?.GetValue<string>() ?? "unbekannt"));
            return json?["result"];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ChainException("Der RPC-Endpunkt ist nicht erreichbar oder antwortet ungültig.", e);
        }
    }

    public async Task<int> GetChainIdAsync(CancellationToken ct = default) => (int)ParseQty(await CallAsync("eth_chainId", new JsonArray(), ct));
    public async Task<BigInteger> GetGasPriceAsync(CancellationToken ct = default) => ParseQty(await CallAsync("eth_gasPrice", new JsonArray(), ct));
    public async Task<BigInteger?> GetPriorityFeeAsync(CancellationToken ct = default)
    {
        try { return ParseQty(await CallAsync("eth_maxPriorityFeePerGas", new JsonArray(), ct)); } catch (ChainException) { return null; }
    }
    public async Task<ulong> EstimateGasAsync(string from, string to, string dataHex, CancellationToken ct = default) =>
        (ulong)ParseQty(await CallAsync("eth_estimateGas", new JsonArray(new JsonObject { ["from"] = from, ["to"] = to, ["data"] = dataHex, ["value"] = "0x0" }), ct));
    public async Task<BigInteger> GetBalanceAsync(string address, CancellationToken ct = default) => ParseQty(await CallAsync("eth_getBalance", new JsonArray(address, "latest"), ct));
    public async Task<ulong> GetNonceAsync(string address, CancellationToken ct = default) => (ulong)ParseQty(await CallAsync("eth_getTransactionCount", new JsonArray(address, "pending"), ct));
    public async Task<string> SendRawTransactionAsync(string rawHex, CancellationToken ct = default) =>
        (await CallAsync("eth_sendRawTransaction", new JsonArray(rawHex), ct))?.GetValue<string>() ?? throw new ChainException("RPC lieferte keine Transaktions-ID.");
    public async Task<ulong> GetBlockNumberAsync(CancellationToken ct = default) => (ulong)ParseQty(await CallAsync("eth_blockNumber", new JsonArray(), ct));

    public async Task<ChainReceipt?> GetReceiptAsync(string txHash, CancellationToken ct = default)
    {
        var r = await CallAsync("eth_getTransactionReceipt", new JsonArray(txHash), ct);
        if (r is null) return null;
        return new ChainReceipt(txHash, ParseQty(r["status"]) == 1, (ulong)ParseQty(r["blockNumber"]), ParseQty(r["gasUsed"]), ParseQty(r["effectiveGasPrice"]));
    }

    public async Task<ChainTx?> GetTransactionAsync(string txHash, CancellationToken ct = default)
    {
        var t = await CallAsync("eth_getTransactionByHash", new JsonArray(txHash), ct);
        if (t is null) return null;
        return new ChainTx(txHash, t["from"]?.GetValue<string>() ?? "", t["to"]?.GetValue<string>() ?? "", t["input"]?.GetValue<string>() ?? "0x",
            t["blockNumber"] is { } bn && bn.GetValue<string?>() is not null ? (ulong)ParseQty(bn) : null);
    }

    public static BigInteger ParseQty(JsonNode? n)
    {
        var s = n?.GetValue<string>() ?? throw new ChainException("Erwarteter Zahlenwert fehlt in der RPC-Antwort.");
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length < 3 || s.Length > 66) throw new ChainException("Ungültiger Zahlenwert in der RPC-Antwort.");
        return BigInteger.Parse("0" + s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Lokale Simulation einer EVM-Kette für Entwicklung und Tests (kein Netzwerkzugriff, keine Kosten). Blockhöhe wächst mit der Zeit; Transaktionen werden
/// sofort aufgenommen. Alles, was hierüber „verankert“ wird, ist <b>nicht</b> auf einer Blockchain und wird in Anzeigen als „simuliert“ geführt.
/// </summary>
public sealed class SimulatedChainClient : IChainClient
{
    private readonly int _chainId;
    private readonly Dictionary<string, (JsonNode Tx, ulong Block)> _txs = new();
    private readonly object _gate = new();
    private readonly DateTime _start = DateTime.UtcNow;
    private ulong _nonce;
    public BigInteger GasPriceWei { get; set; } = new BigInteger(30_000_000_000);

    public SimulatedChainClient(int chainId) => _chainId = chainId;
    public Task<int> GetChainIdAsync(CancellationToken ct = default) => Task.FromResult(_chainId);
    public Task<BigInteger> GetGasPriceAsync(CancellationToken ct = default) => Task.FromResult(GasPriceWei);
    public Task<BigInteger?> GetPriorityFeeAsync(CancellationToken ct = default) => Task.FromResult<BigInteger?>(GasPriceWei / 2);
    public Task<ulong> EstimateGasAsync(string from, string to, string dataHex, CancellationToken ct = default) => Task.FromResult(21_000UL + (ulong)((dataHex.Length - 2) / 2) * 16);
    public Task<BigInteger> GetBalanceAsync(string address, CancellationToken ct = default) => Task.FromResult(BigInteger.Pow(10, 21));
    public Task<ulong> GetNonceAsync(string address, CancellationToken ct = default) { lock (_gate) return Task.FromResult(_nonce); }
    public Task<ulong> GetBlockNumberAsync(CancellationToken ct = default) => Task.FromResult(CurrentBlock());
    private ulong CurrentBlock() => 1_000_000UL + (ulong)((DateTime.UtcNow - _start).TotalSeconds / 2);

    public Task<string> SendRawTransactionAsync(string rawHex, CancellationToken ct = default)
    {
        var tx = PseudoAnchorSigner.Decode(rawHex) ?? throw new ChainException("Unbekanntes Transaktionsformat (Simulation akzeptiert nur Pseudo-Transaktionen).");
        lock (_gate)
        {
            var hash = PseudoAnchorSigner.TxHash(rawHex);
            if (!_txs.ContainsKey(hash)) { _txs[hash] = (tx, CurrentBlock()); _nonce++; }
            return Task.FromResult(hash);
        }
    }

    public Task<ChainReceipt?> GetReceiptAsync(string txHash, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_txs.TryGetValue(txHash, out var t)) return Task.FromResult<ChainReceipt?>(null);
            var gas = (ulong)(t.Tx["gas"]?.GetValue<long>() ?? 21_000);
            return Task.FromResult<ChainReceipt?>(new ChainReceipt(txHash, true, t.Block, 21_000 + (ulong)((t.Tx["data"]?.GetValue<string>().Length ?? 2) - 2) / 2 * 16, GasPriceWei));
        }
    }

    public Task<ChainTx?> GetTransactionAsync(string txHash, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_txs.TryGetValue(txHash, out var t)) return Task.FromResult<ChainTx?>(null);
            return Task.FromResult<ChainTx?>(new ChainTx(txHash, t.Tx["from"]?.GetValue<string>() ?? "", t.Tx["to"]?.GetValue<string>() ?? "", t.Tx["data"]?.GetValue<string>() ?? "0x", t.Block));
        }
    }
}
