using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Certificates;

public interface IChainClientFactory
{
    IChainClient For(string network);
}

public sealed class ChainClientFactory : IChainClientFactory, IDisposable
{
    private readonly BlockchainSettings _cfg;
    private readonly Dictionary<string, IChainClient> _clients = new();
    private readonly object _gate = new();
    public ChainClientFactory(AppConfig config) => _cfg = config.Blockchain;

    public IChainClient For(string network)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(network, out var c)) return c;
            c = network switch
            {
                ChainNetworks.Simulation => new SimulatedChainClient(_cfg.ChainId),
                ChainNetworks.Amoy => new JsonRpcChainClient(_cfg.RpcUrlAmoy, _cfg.HttpTimeoutSeconds),
                ChainNetworks.Mainnet => new JsonRpcChainClient(_cfg.RpcUrlMainnet, _cfg.HttpTimeoutSeconds),
                _ => throw new ChainException("Unbekanntes Netzwerk: " + network),
            };
            return _clients[network] = c;
        }
    }

    public void Dispose() { foreach (var c in _clients.Values.OfType<IDisposable>()) c.Dispose(); }
}

public sealed record ProcessSummary(int Planned, int Submitted, int Confirmed, int Blocked, int Failed, string? Note);

/// <summary>
/// Gebündelte, kostenbegrenzte Blockchain-Verankerung (ADR 0022). Ablauf: Nachweise einer Warteschlange → Batch (Merkle-Wurzel) → harte Kostenprüfung (zweimal, mit frischen Daten) →
/// Signierung → Senden → Bestätigung/Finalität → Verifikation der On-Chain-Daten. Ohne belastbare Kostenschätzung, ohne Mainnet-Freigabe, ohne Signierer oder ohne Guthaben wird <b>nie</b> gesendet;
/// die Nachweise bleiben ausstehend. Wiederholungen senden dieselbe signierte Transaktion erneut (gleicher Hash) und können die Kosten nie erhöhen.
/// </summary>
public sealed class AnchorService
{
    public const string Magic = "LCv1";
    private static readonly TimeSpan RebroadcastAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(90);

    private readonly IDatabase _db;
    private readonly AppConfig _cfg;
    private readonly ChainSettingsStore _settings;
    private readonly IChainClientFactory _clients;
    private readonly IPriceFeed _price;
    private readonly IAnchorSigner _signer;
    private readonly UserService _users;
    private readonly Microsoft.Extensions.Logging.ILogger<AnchorService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Func<DateTime> _now = () => DateTime.UtcNow;

    public AnchorService(IDatabase db, AppConfig cfg, ChainSettingsStore settings, IChainClientFactory clients, IPriceFeed price, IAnchorSigner signer, UserService users,
        Microsoft.Extensions.Logging.ILogger<AnchorService> log)
    {
        _db = db; _cfg = cfg; _settings = settings; _clients = clients; _price = price; _signer = signer; _users = users; _log = log;
    }

    /// <summary>Nur für Tests: Uhr überschreiben.</summary>
    public void UseClock(Func<DateTime> now) => _now = now;

    public IAnchorSigner Signer => _signer;

    /// <summary>On-Chain-Daten eines Ankers: ASCII „LCv1“ gefolgt von der 32-Byte-Merkle-Wurzel. Keine personenbezogenen Daten.</summary>
    public static string AnchorData(string rootHex) => "0x" + Convert.ToHexString(Encoding.ASCII.GetBytes(Magic)).ToLowerInvariant() + rootHex.ToLowerInvariant();

    // ─── Verarbeitung ───────────────────────────────────────────────────────

    /// <summary>Plant fällige Batches und verarbeitet alle offenen Batches des wirksamen Netzwerks. <paramref name="force"/> bündelt auch unvollständige/noch nicht fällige Warteschlangen.</summary>
    public async Task<ProcessSummary> ProcessAsync(bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var eff = await _settings.GetAsync();
            if (!eff.Enabled) return new(0, 0, 0, 0, 0, "Blockchain-Verankerung ist ausgeschaltet.");
            await DropOrphanBatchesAsync();
            await ReleaseRevokedAsync();
            var planned = await PlanBatchesAsync(eff, force);
            int submitted = 0, confirmed = 0, blocked = 0, failed = 0; string? note = null;
            var open = await _db.QueryAsync("SELECT * FROM anchor_batches WHERE network = @n AND status IN ('planned','cost_blocked','submitted') ORDER BY created_at", new[] { ("n", (object?)eff.Network) });
            foreach (var b in open.Select(MapBatch))
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await HandleBatchAsync(b, eff, ct);
                switch (outcome.Status)
                {
                    case BatchStatus.Submitted: submitted++; break;
                    case BatchStatus.Confirmed: confirmed++; break;
                    case BatchStatus.CostBlocked: blocked++; break;
                    case BatchStatus.Failed: failed++; break;
                }
                note ??= outcome.Note;
            }
            return new(planned, submitted, confirmed, blocked, failed, note);
        }
        finally { _gate.Release(); }
    }

    private async Task DropOrphanBatchesAsync()
    {
        // Batch ohne zugeordnete Nachweise (Absturz zwischen Anlegen und Zuordnen) und ohne Transaktion wird verworfen
        await _db.ExecuteAsync("""
            DELETE FROM anchor_batches WHERE status = 'planned' AND tx_hash IS NULL AND created_at < @t
              AND NOT EXISTS (SELECT 1 FROM certificates c WHERE c.batch_id = anchor_batches.id)
            """, new[] { ("t", (object?)_now().AddMinutes(-5)) });
    }

    /// <summary>Widerrufene, noch nicht verankerte Nachweise werden nicht verankert (sie verlassen die Warteschlange); bereits verankerte bleiben.</summary>
    private Task ReleaseRevokedAsync() => _db.ExecuteAsync("UPDATE certificates SET chain_status = 'none', chain_error = 'widerrufen vor der Verankerung' WHERE status = 'revoked' AND chain_status IN ('pending','cost_blocked')");

    private async Task<int> PlanBatchesAsync(EffectiveChainSettings eff, bool force)
    {
        var rows = await _db.QueryAsync("SELECT * FROM certificates WHERE chain_status = 'pending' AND status = 'active' AND chain_network = @n ORDER BY issued_at, id", new[] { ("n", (object?)eff.Network) });
        var pending = rows.Select(CertificateService.Map).ToList();
        if (pending.Count == 0) return 0;
        var size = eff.BatchEnabled ? eff.BatchMaxSize : 1;
        var due = force || pending.Count >= size || (_now() - pending.Min(c => c.IssuedAt)) >= TimeSpan.FromMinutes(eff.BatchMaxWaitMinutes) || !eff.BatchEnabled;
        if (!due) return 0;
        var planned = 0;
        for (var i = 0; i < pending.Count; i += size)
        {
            var chunk = pending.Skip(i).Take(size).ToList();
            if (chunk.Count < size && !force && eff.BatchEnabled && (_now() - chunk.Min(c => c.IssuedAt)) < TimeSpan.FromMinutes(eff.BatchMaxWaitMinutes)) break;   // Rest wartet auf Füllung oder Fristablauf
            await CreateBatchAsync(chunk, eff);
            planned++;
        }
        return planned;
    }

    private async Task<string> CreateBatchAsync(List<Certificate> chunk, EffectiveChainSettings eff)
    {
        var (root, proofs, _) = MerkleTree.Build(chunk.Select(c => MerkleTree.FromHex(c.BadgeHash)).ToList());
        var existing = await _db.QueryAsync("SELECT id FROM anchor_batches WHERE network = @n AND merkle_root = @r", new (string, object?)[] { ("n", eff.Network), ("r", root) });
        string batchId;
        if (existing.Count > 0) batchId = existing[0]["id"]!;   // idempotent: gleiche Menge → gleicher Batch, nie eine zweite Verankerung
        else
        {
            var b = new AnchorBatch { Network = eff.Network, ChainId = eff.ChainId, MerkleRoot = root, LeafCount = chunk.Count, Simulated = eff.Simulate, CreatedAt = _now(), UpdatedAt = _now() };
            await _db.ExecuteAsync("""
                INSERT INTO anchor_batches (id, network, chain_id, merkle_root, leaf_count, status, simulated, error, attempts, created_at, updated_at)
                VALUES (@id, @network, @chain_id, @root, @count, 'planned', @sim, '', 0, @t, @t)
                """, new (string, object?)[] { ("id", b.Id), ("network", b.Network), ("chain_id", b.ChainId), ("root", root), ("count", chunk.Count), ("sim", eff.Simulate ? 1 : 0), ("t", _now()) });
            batchId = b.Id;
        }
        for (var i = 0; i < chunk.Count; i++)
        {
            var proofJson = JsonSerializer.Serialize(proofs[i].Select(s => new { sibling_is_left = s.SiblingIsLeft, hash = s.Hash }));
            await _db.ExecuteAsync("UPDATE certificates SET batch_id = @b, merkle_proof_json = @p, chain_status = 'batched', chain_error = '', chain_updated_at = @t WHERE id = @id AND chain_status = 'pending'",
                new (string, object?)[] { ("b", batchId), ("p", proofJson), ("t", _now()), ("id", chunk[i].Id) });
        }
        return batchId;
    }

    private sealed record Outcome(string Status, string? Note);

    private async Task<Outcome> HandleBatchAsync(AnchorBatch b, EffectiveChainSettings eff, CancellationToken ct)
    {
        try
        {
            return b.Status == BatchStatus.Submitted ? await CheckSubmittedAsync(b, eff, ct) : await TrySubmitAsync(b, eff, ct);
        }
        catch (ChainException e)
        {
            // RPC/Netzwerkfehler: kein Kostenrisiko, kein Verlust — der Batch bleibt offen und wird später erneut versucht
            await SetBatchAsync(b, b.Status, "Netzwerkfehler: " + Sanitize(e.Message), attemptsInc: true);
            _log.LogWarning("Verankerung: Batch {Batch} — {Message}", b.Id, e.Message);
            return new Outcome(b.Status, e.Message);
        }
    }

    private async Task<Outcome> TrySubmitAsync(AnchorBatch b, EffectiveChainSettings eff, CancellationToken ct)
    {
        if (b.TxHash is not null) return await CheckSubmittedAsync(b, eff, ct);   // bereits gesendet (Absturz nach dem Senden): nie ein zweites Mal senden
        if (!_signer.IsAvailable || string.IsNullOrEmpty(_signer.Address))
        {
            await Hold(b, BatchStatus.Planned, "Kein Transaktionssignierer konfiguriert — Verankerung bleibt ausstehend (ADR 0022).");
            return new Outcome(BatchStatus.Planned, "Kein Signierer konfiguriert.");
        }
        if (eff.IsMainnet && !eff.MainnetUnlocked)
        {
            await Hold(b, BatchStatus.Planned, "Mainnet ist nicht freigegeben (Umgebung und ausdrückliche administrative Freigabe erforderlich).");
            return new Outcome(BatchStatus.Planned, "Mainnet nicht freigegeben.");
        }
        var client = _clients.For(eff.Network);
        var chainId = await client.GetChainIdAsync(ct);
        if (chainId != eff.ChainId)
        {
            await Hold(b, BatchStatus.Planned, $"Falsche Chain-ID {chainId} (erwartet {eff.ChainId}) — nicht gesendet.");
            return new Outcome(BatchStatus.Planned, "Chain-ID passt nicht.");
        }

        var from = _signer.Address!;
        var data = AnchorData(b.MerkleRoot);
        // 1. Kostenprüfung mit frischen Daten
        var d1 = await EvaluateAsync(client, eff, from, data, b.LeafCount, ct);
        if (!d1.Allowed) return await Block(b, d1);

        var balance = await client.GetBalanceAsync(from, ct);
        var needed = new BigInteger(d1.GasLimit) * d1.MaxFeePerGasWei;
        if (balance < needed)
        {
            await Hold(b, BatchStatus.Planned, "Wallet-Guthaben reicht nicht für die Gebühr — nicht gesendet.");
            return new Outcome(BatchStatus.Planned, "Guthaben unzureichend.");
        }

        // 2. Erneute Prüfung unmittelbar vor dem Signieren (Preise können sich bewegt haben); die Transaktion übernimmt die jeweils engere Grenze
        var d2 = await EvaluateAsync(client, eff, from, data, b.LeafCount, ct);
        if (!d2.Allowed) return await Block(b, d2);
        var maxFee = BigInteger.Min(d1.MaxFeePerGasWei, d2.MaxFeePerGasWei);
        var gasLimit = Math.Max(d1.GasLimit, d2.GasLimit);
        var tip = BigInteger.Min(await client.GetPriorityFeeAsync(ct) ?? maxFee, maxFee);
        var nonce = await client.GetNonceAsync(from, ct);

        var tx = new TxRequest(eff.ChainId, from, from, data, nonce, gasLimit, maxFee, tip);
        var raw = await _signer.SignAsync(tx, ct);
        // Persistieren VOR dem Senden: ein Absturz danach führt zu „erneut senden derselben Transaktion“ statt zu einer neuen
        var quote = await _price.GetQuoteAsync(ct);
        await _db.ExecuteAsync("""
            UPDATE anchor_batches SET raw_tx = @raw, nonce = @nonce, gas_limit = @gl, max_fee_per_gas = @mf, rate_eur_per_pol = @rate, rate_at = @rate_at, est_total_eur = @est,
                simulated_cost_eur = @sim, status = 'submitted', submitted_at = @t, updated_at = @t, attempts = attempts + 1, error = ''
            WHERE id = @id AND tx_hash IS NULL
            """, new (string, object?)[]
        {
            ("raw", raw), ("nonce", nonce.ToString(CultureInfo.InvariantCulture)), ("gl", gasLimit.ToString(CultureInfo.InvariantCulture)), ("mf", maxFee.ToString(CultureInfo.InvariantCulture)),
            ("rate", quote is null ? null : (double)quote.EurPerPol), ("rate_at", quote?.At), ("est", (double)d2.TotalEur), ("sim", d2.SimulatedEur is null ? null : (double)d2.SimulatedEur.Value),
            ("t", _now()), ("id", b.Id),
        });
        var hash = await client.SendRawTransactionAsync(raw, ct);
        await _db.ExecuteAsync("UPDATE anchor_batches SET tx_hash = @h WHERE id = @id", new (string, object?)[] { ("h", hash), ("id", b.Id) });
        await _db.ExecuteAsync("UPDATE certificates SET chain_status = 'anchoring', chain_error = '', chain_updated_at = @t WHERE batch_id = @b AND chain_status IN ('batched','pending','cost_blocked')", new (string, object?)[] { ("t", _now()), ("b", b.Id) });
        return new Outcome(BatchStatus.Submitted, null);
    }

    private async Task<CostDecision> EvaluateAsync(IChainClient client, EffectiveChainSettings eff, string from, string data, int certCount, CancellationToken ct)
    {
        ulong? gas = null; BigInteger? gasPrice = null;
        try { gas = await client.EstimateGasAsync(from, from, data, ct); } catch (ChainException) { }
        try { gasPrice = await client.GetGasPriceAsync(ct); } catch (ChainException) { }
        var quote = await _price.GetQuoteAsync(ct);
        return AnchorCostGuard.Evaluate(new CostInput(eff.IsMainnet, gas, gasPrice, quote?.EurPerPol, quote?.At, _now(), certCount, eff.MaxCostEur,
            _cfg.Blockchain.GasSafetyFactor, _cfg.Blockchain.RateSafetyFactor, TimeSpan.FromMinutes(_cfg.Blockchain.PriceMaxAgeMinutes)));
    }

    private async Task<Outcome> Block(AnchorBatch b, CostDecision d)
    {
        var text = d.Code == "cost_limit" ? d.Reason : "Kosten nicht belastbar: " + d.Reason;
        await _db.ExecuteAsync("UPDATE anchor_batches SET status = 'cost_blocked', error = @e, attempts = attempts + 1, updated_at = @t, est_total_eur = @est WHERE id = @id",
            new (string, object?)[] { ("e", text), ("t", _now()), ("est", d.TotalEur > 0 ? (double)d.TotalEur : null), ("id", b.Id) });
        // Das signierte Badge bleibt unberührt; die Verankerung bleibt ausstehend und wird später erneut geprüft
        await _db.ExecuteAsync("UPDATE certificates SET chain_status = 'cost_blocked', chain_error = @e, chain_attempts = chain_attempts + 1, chain_updated_at = @t WHERE batch_id = @b AND chain_status IN ('batched','cost_blocked')",
            new (string, object?)[] { ("e", d.Code == "cost_limit" ? "Kostenlimit überschritten" : "Kosten unbekannt: " + d.Reason), ("t", _now()), ("b", b.Id) });
        return new Outcome(BatchStatus.CostBlocked, text);
    }

    private async Task Hold(AnchorBatch b, string status, string reason)
    {
        await _db.ExecuteAsync("UPDATE anchor_batches SET status = @s, error = @e, updated_at = @t WHERE id = @id", new (string, object?)[] { ("s", status), ("e", reason), ("t", _now()), ("id", b.Id) });
        await _db.ExecuteAsync("UPDATE certificates SET chain_error = @e, chain_updated_at = @t WHERE batch_id = @b AND chain_status IN ('batched','cost_blocked')", new (string, object?)[] { ("e", reason), ("t", _now()), ("b", b.Id) });
    }

    private async Task SetBatchAsync(AnchorBatch b, string status, string error, bool attemptsInc)
    {
        await _db.ExecuteAsync($"UPDATE anchor_batches SET status = @s, error = @e, updated_at = @t{(attemptsInc ? ", attempts = attempts + 1" : "")} WHERE id = @id", new (string, object?)[] { ("s", status), ("e", error), ("t", _now()), ("id", b.Id) });
    }

    private async Task<Outcome> CheckSubmittedAsync(AnchorBatch b, EffectiveChainSettings eff, CancellationToken ct)
    {
        var client = _clients.For(b.Network);
        if (b.TxHash is null) { await Hold(b, BatchStatus.Planned, "Transaktion wurde nicht übermittelt."); return new Outcome(BatchStatus.Planned, null); }
        var receipt = await client.GetReceiptAsync(b.TxHash, ct);
        if (receipt is null)
        {
            var age = _now() - (b.SubmittedAt ?? b.CreatedAt);
            var known = await client.GetTransactionAsync(b.TxHash, ct);
            if (known is null && b.RawTx is not null && age > RebroadcastAfter && age <= GiveUpAfter)
            {
                await client.SendRawTransactionAsync(b.RawTx, ct);   // dieselbe signierte Transaktion (gleicher Hash, gleiche Kostenobergrenze)
                await _db.ExecuteAsync("UPDATE anchor_batches SET attempts = attempts + 1, updated_at = @t WHERE id = @id", new (string, object?)[] { ("t", _now()), ("id", b.Id) });
            }
            else if (known is null && age > GiveUpAfter)
            {
                await Fail(b, "Die Transaktion wurde im Netzwerk nicht aufgenommen (Zeitüberschreitung). Erneutes Einplanen durch einen Administrator möglich.");
                return new Outcome(BatchStatus.Failed, "Transaktion nicht gefunden.");
            }
            return new Outcome(BatchStatus.Submitted, null);
        }
        if (!receipt.Success) { await Fail(b, "Die Transaktion wurde ausgeführt, aber vom Netzwerk abgelehnt (Status 0)."); return new Outcome(BatchStatus.Failed, "Transaktion abgelehnt."); }

        var head = await client.GetBlockNumberAsync(ct);
        var confirmations = head >= receipt.BlockNumber ? head - receipt.BlockNumber + 1 : 0;
        await _db.ExecuteAsync("UPDATE anchor_batches SET block_number = @bn, updated_at = @t WHERE id = @id", new (string, object?)[] { ("bn", receipt.BlockNumber.ToString(CultureInfo.InvariantCulture)), ("t", _now()), ("id", b.Id) });
        if (confirmations < (ulong)_cfg.Blockchain.Confirmations) return new Outcome(BatchStatus.Submitted, $"{confirmations}/{_cfg.Blockchain.Confirmations} Bestätigungen");

        // Ausreichend bestätigt: On-Chain-Daten prüfen (Absender, Daten) — erst danach „blockchain-bestätigt“
        var tx = await client.GetTransactionAsync(b.TxHash, ct);
        if (tx is null || !string.Equals(tx.InputHex, AnchorData(b.MerkleRoot), StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(_signer.Address) && !string.Equals(tx.From, _signer.Address, StringComparison.OrdinalIgnoreCase)))
        {
            await Fail(b, "Die On-Chain-Daten stimmen nicht mit dem erwarteten Anker überein.");
            return new Outcome(BatchStatus.Failed, "Anker stimmt nicht.");
        }
        var feeWei = receipt.GasUsed * receipt.EffectiveGasPrice;
        var feePol = (decimal)feeWei / 1_000_000_000_000_000_000m;
        var rate = b.RateEurPerPol is { } r ? (decimal)r : (decimal?)null;
        double? feeEur = eff.IsMainnet && !b.Simulated ? (rate is null ? null : (double)Math.Round(feePol * rate.Value, 10)) : 0.0;   // Testnet/Simulation: keine echten Kosten
        double? perCert = feeEur is null ? null : feeEur / Math.Max(1, b.LeafCount);
        double? simPerCert = rate is null ? b.SimulatedCostEur : (double)Math.Round(feePol * rate.Value / Math.Max(1, b.LeafCount), 10);
        double? saved = null;
        if (rate is not null && b.LeafCount > 1)
        {
            // Einzelverankerung hätte je Nachweis denselben Gaspreis, aber das Basis-Gas einer eigenen Transaktion verursacht (belastbar nur mit Kurs und Gaspreis)
            var singleGas = 21_000m + 16m * 36m;
            var singleFeePol = singleGas * (decimal)receipt.EffectiveGasPrice / 1_000_000_000_000_000_000m;
            saved = (double)Math.Round(singleFeePol * rate.Value * b.LeafCount - feePol * rate.Value, 10);
            if (saved < 0) saved = 0;
        }
        await _db.ExecuteAsync("""
            UPDATE anchor_batches SET status = 'confirmed', gas_used = @gu, effective_gas_price = @egp, fee_wei = @fw, fee_eur = @fe, cost_per_cert_eur = @pc, simulated_cost_eur = @sim, saved_eur = @sv,
                confirmed_at = @t, updated_at = @t, error = ''
            WHERE id = @id
            """, new (string, object?)[]
        {
            ("gu", receipt.GasUsed.ToString(CultureInfo.InvariantCulture)), ("egp", receipt.EffectiveGasPrice.ToString(CultureInfo.InvariantCulture)), ("fw", feeWei.ToString(CultureInfo.InvariantCulture)),
            ("fe", feeEur), ("pc", perCert), ("sim", simPerCert), ("sv", saved), ("t", _now()), ("id", b.Id),
        });
        await _db.ExecuteAsync("UPDATE certificates SET chain_status = 'confirmed', chain_error = '', anchored_at = @t, chain_updated_at = @t WHERE batch_id = @b", new (string, object?)[] { ("t", _now()), ("b", b.Id) });
        return new Outcome(BatchStatus.Confirmed, null);
    }

    private async Task Fail(AnchorBatch b, string reason)
    {
        await _db.ExecuteAsync("UPDATE anchor_batches SET status = 'failed', error = @e, updated_at = @t WHERE id = @id", new (string, object?)[] { ("e", reason), ("t", _now()), ("id", b.Id) });
        await _db.ExecuteAsync("UPDATE certificates SET chain_status = 'failed', chain_error = @e, chain_attempts = chain_attempts + 1, chain_updated_at = @t WHERE batch_id = @b AND chain_status <> 'confirmed'", new (string, object?)[] { ("e", reason), ("t", _now()), ("b", b.Id) });
    }

    private static string Sanitize(string s) => s.Length > 300 ? s[..300] : s;

    // ─── Verwaltung (Administratoren) ──────────────────────────────────────

    /// <summary>Fehlgeschlagene oder gesperrte Verankerung erneut einplanen. Es gilt wieder die volle Kostenprüfung; eine bereits gesendete Transaktion wird nie ein zweites Mal ausgelöst.</summary>
    public async Task RetryBatchAsync(string actorId, string batchId)
    {
        var actor = await _users.RequireRoleAsync(actorId, Roles.Admin);
        var rows = await _db.QueryAsync("SELECT * FROM anchor_batches WHERE id = @id", new[] { ("id", (object?)batchId) });
        if (rows.Count == 0) throw new KeyNotFoundException("Batch nicht gefunden.");
        var b = MapBatch(rows[0]);
        if (b.Status == BatchStatus.Confirmed) throw new InvalidOperationException("Der Batch ist bereits bestätigt.");
        if (b.Status == BatchStatus.Submitted) throw new InvalidOperationException("Der Batch wurde bereits gesendet und wird überwacht.");
        // Ein fehlgeschlagener Batch mit gesendeter Transaktion kann nur mit neuem Anker erneut verankert werden: Transaktionsdaten verwerfen (die alte Transaktion kann nicht mehr aufgenommen werden, wenn sie verworfen wurde)
        await _db.ExecuteAsync("UPDATE anchor_batches SET status = 'planned', error = '', tx_hash = NULL, raw_tx = NULL, nonce = NULL, updated_at = @t WHERE id = @id", new (string, object?)[] { ("t", _now()), ("id", b.Id) });
        await _db.ExecuteAsync("UPDATE certificates SET chain_status = 'batched', chain_error = '', chain_updated_at = @t WHERE batch_id = @b AND chain_status IN ('failed','cost_blocked')", new (string, object?)[] { ("t", _now()), ("b", b.Id) });
        await _users.WriteAuditAsync(actor.Id, "blockchain.retry", b.Id, $"root={b.MerkleRoot}");
    }

    public async Task<List<AnchorBatch>> ListBatchesAsync(string? status = null, int limit = 100)
    {
        var sql = "SELECT * FROM anchor_batches";
        var args = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(status)) { sql += " WHERE status = @s"; args.Add(("s", status)); }
        return (await _db.QueryAsync(sql + $" ORDER BY created_at DESC LIMIT {Math.Clamp(limit, 1, 500)}", args)).Select(MapBatch).ToList();
    }

    // ─── Kosten und Überwachung ────────────────────────────────────────────

    public async Task<JsonObject> GetStatsAsync()
    {
        async Task<int> Count(string sql) => DbValue.ToInt((await _db.QueryAsync(sql))[0]["n"]);
        var batches = (await _db.QueryAsync("SELECT * FROM anchor_batches")).Select(MapBatch).ToList();
        JsonObject Section(Func<AnchorBatch, bool> f, bool real)
        {
            var confirmed = batches.Where(b => b.Status == BatchStatus.Confirmed && f(b)).ToList();
            var monthStart = new DateTime(_now().Year, _now().Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return new JsonObject
            {
                ["confirmed_batches"] = confirmed.Count, ["anchored_certificates"] = confirmed.Sum(b => b.LeafCount),
                ["avg_batch_size"] = confirmed.Count == 0 ? null : Math.Round(confirmed.Average(b => (double)b.LeafCount), 1),
                ["open_batches"] = batches.Count(b => f(b) && b.Status is BatchStatus.Planned or BatchStatus.CostBlocked or BatchStatus.Submitted),
                ["gas_used_total"] = confirmed.Sum(b => decimal.TryParse(b.GasUsed, out var g) ? g : 0m),
                ["gas_used_avg"] = confirmed.Count == 0 ? null : Math.Round(confirmed.Average(b => decimal.TryParse(b.GasUsed, out var g) ? (double)g : 0), 0),
                ["real_cost_total_eur"] = real ? Math.Round(confirmed.Sum(b => b.FeeEur ?? 0), 8) : 0.0,
                ["real_cost_month_eur"] = real ? Math.Round(confirmed.Where(b => b.ConfirmedAt >= monthStart).Sum(b => b.FeeEur ?? 0), 8) : 0.0,
                ["real_cost_per_cert_eur"] = real && confirmed.Sum(b => b.LeafCount) > 0 ? Math.Round(confirmed.Sum(b => b.FeeEur ?? 0) / confirmed.Sum(b => b.LeafCount), 10) : (real ? null : 0.0),
                ["simulated_mainnet_cost_per_cert_eur"] = real ? null : (confirmed.Where(b => b.SimulatedCostEur is not null).Select(b => b.SimulatedCostEur!.Value).DefaultIfEmpty(-1).Average() is var avg && avg >= 0 ? Math.Round(avg, 10) : null),
                ["saved_eur_estimate"] = confirmed.Any(b => b.SavedEur is not null) ? Math.Round(confirmed.Sum(b => b.SavedEur ?? 0), 8) : null,
            };
        }
        return new JsonObject
        {
            ["issued_certificates"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE is_test = 0"),
            ["revoked_certificates"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE status = 'revoked' AND is_test = 0"),
            ["waiting"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE chain_status IN ('pending','batched','cost_blocked') AND status = 'active'"),
            ["cost_blocked"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE chain_status = 'cost_blocked'"),
            ["anchoring"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE chain_status = 'anchoring'"),
            ["confirmed"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE chain_status = 'confirmed'"),
            ["failed"] = await Count("SELECT COUNT(*) AS n FROM certificates WHERE chain_status = 'failed'"),
            ["testnet"] = Section(b => b.Network == ChainNetworks.Amoy, real: false),
            ["simulation"] = Section(b => b.Network == ChainNetworks.Simulation, real: false),
            ["mainnet"] = Section(b => b.Network == ChainNetworks.Mainnet, real: true),
        };
    }

    public async Task<JsonObject> GetWalletStatusAsync(CancellationToken ct = default)
    {
        var eff = await _settings.GetAsync();
        var o = new JsonObject
        {
            ["network"] = eff.Network, ["network_label"] = ChainNetworks.Label(eff.Network), ["chain_id"] = eff.ChainId, ["address"] = _signer.Address ?? (_cfg.Blockchain.WalletAddress.Length > 0 ? _cfg.Blockchain.WalletAddress : null),
            ["signer"] = _signer.Name, ["signer_available"] = _signer.IsAvailable, ["rpc_reachable"] = false, ["balance_pol"] = null, ["chain_id_ok"] = null,
        };
        if (!eff.Enabled) return o;
        try
        {
            var client = _clients.For(eff.Network);
            o["chain_id_ok"] = await client.GetChainIdAsync(ct) == eff.ChainId;
            o["rpc_reachable"] = true;
            if (o["address"] is not null) o["balance_pol"] = (double)((decimal)await client.GetBalanceAsync(o["address"]!.GetValue<string>(), ct) / 1_000_000_000_000_000_000m);
        }
        catch (ChainException e) { o["rpc_error"] = Sanitize(e.Message); }
        return o;
    }

    // ─── Unabhängige Prüfung ───────────────────────────────────────────────

    /// <summary>
    /// Prüft den Nachweis ohne Vertrauen in die Learny-Datenbank: Merkle-Pfad bis zur Wurzel, dann die Transaktion über RPC (Absender, Anker-Daten, Status, Bestätigungen, Chain-ID).
    /// Das Netzwerk kommt aus dem Nachweis, die RPC-Adresse nur aus der Konfiguration (nie aus dem Nachweis).
    /// </summary>
    public async Task<JsonObject> VerifyProofPackageAsync(JsonNode pkg, CancellationToken ct = default)
    {
        var r = new JsonObject { ["merkle_valid"] = false, ["tx_found"] = null, ["tx_data_ok"] = null, ["tx_sender_ok"] = null, ["receipt_success"] = null, ["confirmations"] = null, ["network_ok"] = null, ["simulated"] = false };
        try
        {
            var steps = (pkg["merkle_proof"]?.AsArray() ?? new JsonArray()).Select(s => new MerkleStep(s!["sibling_is_left"]!.GetValue<bool>(), s["hash"]!.GetValue<string>())).ToList();
            var root = pkg["merkle_root"]?.GetValue<string>() ?? "";
            r["merkle_valid"] = MerkleTree.Verify(pkg["badge_hash"]?.GetValue<string>() ?? "", steps, root);
            var network = pkg["network"]?.GetValue<string>() ?? "";
            r["network"] = network; r["network_label"] = ChainNetworks.Label(network);
            if (network == ChainNetworks.Simulation) { r["simulated"] = true; r["note"] = "Simulierter Anker — nicht auf einer Blockchain."; return r; }
            if (network is not (ChainNetworks.Amoy or ChainNetworks.Mainnet)) { r["note"] = "Unbekanntes Netzwerk."; return r; }
            var txHash = pkg["transaction_hash"]?.GetValue<string>();
            if (string.IsNullOrEmpty(txHash)) { r["note"] = "Noch nicht verankert."; return r; }
            var client = _clients.For(network);
            r["network_ok"] = await client.GetChainIdAsync(ct) == ChainNetworks.ChainId(network) && (pkg["chain_id"]?.GetValue<int>() ?? 0) == ChainNetworks.ChainId(network);
            var tx = await client.GetTransactionAsync(txHash, ct);
            r["tx_found"] = tx is not null;
            if (tx is null) return r;
            r["tx_data_ok"] = root.Length == 64 && string.Equals(tx.InputHex, AnchorData(root), StringComparison.OrdinalIgnoreCase);
            var wallet = pkg["anchor_wallet"]?.GetValue<string>() ?? _cfg.Blockchain.WalletAddress;
            r["tx_sender_ok"] = !string.IsNullOrEmpty(wallet) && string.Equals(tx.From, wallet, StringComparison.OrdinalIgnoreCase);
            var receipt = await client.GetReceiptAsync(txHash, ct);
            r["receipt_success"] = receipt?.Success;
            if (receipt is not null) r["confirmations"] = (long)(await client.GetBlockNumberAsync(ct) - receipt.BlockNumber + 1);
        }
        catch (ChainException e) { r["note"] = "Blockchain nicht erreichbar: " + Sanitize(e.Message); }
        catch (Exception e) when (e is FormatException or InvalidOperationException or NullReferenceException or JsonException) { r["note"] = "Der Nachweis ist unvollständig oder beschädigt."; }
        return r;
    }

    public static AnchorBatch MapBatch(Dictionary<string, string?> r) => new()
    {
        Id = r["id"] ?? "", Network = r["network"] ?? "", ChainId = DbValue.ToInt(r["chain_id"]), MerkleRoot = r["merkle_root"] ?? "", LeafCount = DbValue.ToInt(r["leaf_count"]), Status = r["status"] ?? "",
        TxHash = N(r, "tx_hash"), Nonce = N(r, "nonce"), RawTx = N(r, "raw_tx"), BlockNumber = N(r, "block_number"), GasLimit = N(r, "gas_limit"), GasUsed = N(r, "gas_used"), MaxFeePerGas = N(r, "max_fee_per_gas"),
        EffectiveGasPrice = N(r, "effective_gas_price"), FeeWei = N(r, "fee_wei"), RateEurPerPol = D(r, "rate_eur_per_pol"), RateAt = T(r, "rate_at"), EstTotalEur = D(r, "est_total_eur"), FeeEur = D(r, "fee_eur"),
        CostPerCertEur = D(r, "cost_per_cert_eur"), SimulatedCostEur = D(r, "simulated_cost_eur"), SavedEur = D(r, "saved_eur"), Simulated = DbValue.ToInt(r["simulated"]) != 0, Error = r.GetValueOrDefault("error") ?? "",
        Attempts = DbValue.ToInt(r["attempts"]), CreatedAt = DbValue.ToDateTime(r["created_at"]), SubmittedAt = T(r, "submitted_at"), ConfirmedAt = T(r, "confirmed_at"), UpdatedAt = DbValue.ToDateTime(r["updated_at"]),
    };

    private static string? N(Dictionary<string, string?> r, string k) => r.GetValueOrDefault(k) is { Length: > 0 } v ? v : null;
    private static double? D(Dictionary<string, string?> r, string k) => r.GetValueOrDefault(k) is { Length: > 0 } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static DateTime? T(Dictionary<string, string?> r, string k) => r.GetValueOrDefault(k) is { Length: > 0 } v ? DbValue.ToDateTime(v) : null;
}

/// <summary>Verarbeitet die Warteschlange regelmäßig (nur wenn automatische Verankerung aktiv ist).</summary>
public sealed class AnchorWorker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly AnchorService _anchors;
    private readonly ChainSettingsStore _settings;
    private readonly Microsoft.Extensions.Logging.ILogger<AnchorWorker> _log;

    public AnchorWorker(AnchorService anchors, ChainSettingsStore settings, Microsoft.Extensions.Logging.ILogger<AnchorWorker> log) { _anchors = anchors; _settings = settings; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var eff = await _settings.GetAsync();
                if (eff.Enabled && eff.AutoAnchor) await _anchors.ProcessAsync(false, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning("Verankerungs-Worker: {Message}", e.Message); }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ContinueWith(_ => { });
        }
    }
}
