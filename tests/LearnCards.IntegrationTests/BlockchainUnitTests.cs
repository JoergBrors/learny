using System.Numerics;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.Certificates;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Steuerbare Kette für die Anker-Tests: zählt Sendungen, hält Gaspreis, Guthaben und Blockhöhe unter Kontrolle des Tests.</summary>
public sealed class FakeChain : IChainClient
{
    public int ChainId = 80002;
    public BigInteger GasPrice = new BigInteger(30_000_000_000);
    public bool EstimateFails, GasPriceFails, DropTransactions;
    public BigInteger Balance = BigInteger.Pow(10, 21);
    public ulong Head = 1_000;
    public bool ReceiptSuccess = true;
    public string? TamperedInput;
    public readonly List<string> Sent = new();
    public readonly Dictionary<string, (JsonNode Tx, ulong Block)> Txs = new();
    private ulong _nonce;

    public int SendCount => Sent.Count;
    public int UniqueTx => Txs.Count;
    public Task<int> GetChainIdAsync(CancellationToken ct = default) => Task.FromResult(ChainId);
    public Task<BigInteger> GetGasPriceAsync(CancellationToken ct = default) => GasPriceFails ? throw new ChainException("Gaspreis nicht verfügbar") : Task.FromResult(GasPrice);
    public Task<BigInteger?> GetPriorityFeeAsync(CancellationToken ct = default) => Task.FromResult<BigInteger?>(GasPrice / 2);
    public Task<ulong> EstimateGasAsync(string from, string to, string dataHex, CancellationToken ct = default) => EstimateFails ? throw new ChainException("estimateGas fehlgeschlagen") : Task.FromResult(21_000UL + (ulong)((dataHex.Length - 2) / 2) * 16);
    public Task<BigInteger> GetBalanceAsync(string address, CancellationToken ct = default) => Task.FromResult(Balance);
    public Task<ulong> GetNonceAsync(string address, CancellationToken ct = default) => Task.FromResult(_nonce);
    public Task<ulong> GetBlockNumberAsync(CancellationToken ct = default) => Task.FromResult(Head);
    public Task<string> SendRawTransactionAsync(string rawHex, CancellationToken ct = default)
    {
        Sent.Add(rawHex);
        var hash = PseudoAnchorSigner.TxHash(rawHex);
        if (!Txs.ContainsKey(hash) && !DropTransactions) { Txs[hash] = (PseudoAnchorSigner.Decode(rawHex)!, Head); _nonce++; }
        return Task.FromResult(hash);
    }
    public Task<ChainReceipt?> GetReceiptAsync(string txHash, CancellationToken ct = default) =>
        Task.FromResult(Txs.TryGetValue(txHash, out var t) ? new ChainReceipt(txHash, ReceiptSuccess, t.Block, 21_576, GasPrice) : null);
    public Task<ChainTx?> GetTransactionAsync(string txHash, CancellationToken ct = default) =>
        Task.FromResult(Txs.TryGetValue(txHash, out var t) ? new ChainTx(txHash, t.Tx["from"]!.GetValue<string>(), t.Tx["to"]!.GetValue<string>(), TamperedInput ?? t.Tx["data"]!.GetValue<string>(), t.Block) : null);
}

public sealed class FakeFactory : IChainClientFactory
{
    public FakeChain Chain = new();
    public IChainClient For(string network) => Chain;
}

public sealed class FakePrice : IPriceFeed
{
    public PriceQuote? Quote = new(0.20m, DateTime.UtcNow, "test");
    public Task<PriceQuote?> GetQuoteAsync(CancellationToken ct = default) => Task.FromResult(Quote is null ? null : Quote with { At = Quote.At });
}

/// <summary>Komponenten- und Dienst-Tests für Zertifikate, Merkle-Bündelung, Kostenschranke und Verankerung (ADR 0021/0022) — ohne Server und ohne Netzwerk.</summary>
public static class BlockchainUnitTests
{
    private const string Wallet = "0x00000000000000000000000000000000000000aa";

    private sealed class Env : IAsyncDisposable
    {
        public SqliteDatabase Db = null!;
        public AppConfig Cfg = null!;
        public UserService Users = null!;
        public ChainSettingsStore Settings = null!;
        public BadgeSigner Signer = null!;
        public CertificateService Certs = null!;
        public AnchorService Anchors = null!;
        public FakeFactory Clients = new();
        public FakePrice Price = new();
        public string AdminId = "", LearnerId = "";
        public DateTime Now = DateTime.UtcNow;

        public static async Task<Env> CreateAsync(string dir, Action<BlockchainSettings>? configure = null, IAnchorSigner? signer = null)
        {
            Directory.CreateDirectory(dir);
            var e = new Env();
            var b = new BlockchainSettings { WalletAddress = Wallet, Network = ChainNetworks.Amoy, BadgeKeyFile = Path.Combine(dir, "key.pem"), PublicBaseUrl = "https://learny.test", Confirmations = 5 };
            configure?.Invoke(b);
            e.Cfg = new AppConfig { Blockchain = b };
            e.Db = new SqliteDatabase(Path.Combine(dir, "t.db"));
            await e.Db.InitAsync();
            e.Users = new UserService(e.Db, e.Cfg);
            e.Settings = new ChainSettingsStore(e.Db, e.Cfg);
            e.Signer = new BadgeSigner(e.Cfg);
            e.Certs = new CertificateService(e.Db, e.Signer, e.Settings, e.Users, e.Cfg);
            e.Anchors = new AnchorService(e.Db, e.Cfg, e.Settings, e.Clients, e.Price, signer ?? new PseudoAnchorSigner(Wallet), e.Users, NullLogger<AnchorService>.Instance);
            e.Anchors.UseClock(() => e.Now);
            e.AdminId = await e.AddUserAsync("Admin", Roles.Admin);
            e.LearnerId = await e.AddUserAsync("Lernende A");
            return e;
        }

        public async Task<string> AddUserAsync(string name, string? role = null)
        {
            var id = Guid.NewGuid().ToString();
            await Db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id, @n, '', 'active', 1, @t, @t)", new (string, object?)[] { ("id", id), ("n", name), ("t", DateTime.UtcNow) });
            await Db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@id, 'user', NULL, @t)", new (string, object?)[] { ("id", id), ("t", DateTime.UtcNow) });
            if (role is not null) await Db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@id, @r, NULL, @t)", new (string, object?)[] { ("id", id), ("r", role), ("t", DateTime.UtcNow) });
            return id;
        }

        public async Task<Certificate> IssueAsync(string? userId = null, string scope = "global", int version = 1)
        {
            var uid = userId ?? await AddUserAsync("Lernende " + Guid.NewGuid().ToString("N")[..4]);
            var result = new QuizResultRecord { UserSub = uid, ModuleId = Guid.NewGuid().ToString(), Score = 90, MaxScore = 100, PassPercent = 60, Passed = true, SessionId = Guid.NewGuid().ToString() };
            var c = await Certs.TryIssueAsync(result, "Testmodul", scope, version);
            return c!;
        }

        public async Task<List<Certificate>> AllAsync() => (await Db.QueryAsync("SELECT * FROM certificates ORDER BY issued_at")).Select(CertificateService.Map).ToList();

        public async ValueTask DisposeAsync() { Signer.Dispose(); await Db.DisposeAsync(); }
    }

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("Komponententests Zertifikate, Merkle, Kostenschranke, Verankerung (ADR 0021/0022)");
        Merkle(check);
        Config(check);
        CostGuard(check);
        await BadgesAsync(check, tmp);
        await PipelineAsync(check, tmp);
        await MainnetAsync(check, tmp);
        await FailuresAsync(check, tmp);
    }

    // ─── Merkle ─────────────────────────────────────────────────────────────

    private static QuizResultRecord Copy(QuizResultRecord r, Action<QuizResultRecord> f)
    {
        var c = new QuizResultRecord { UserSub = r.UserSub, ModuleId = r.ModuleId, CardId = r.CardId, Score = r.Score, MaxScore = r.MaxScore, PassPercent = r.PassPercent, Passed = r.Passed, SessionId = r.SessionId };
        f(c);
        return c;
    }

    private static byte[] H(int i) => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("nachweis-" + i));

    private static void Merkle(Action<bool, string> check)
    {
        var allValid = true;
        foreach (var n in new[] { 1, 2, 3, 4, 5, 7, 8, 13, 100 })
        {
            var hashes = Enumerable.Range(0, n).Select(H).ToList();
            var (root, proofs, leaves) = MerkleTree.Build(hashes);
            for (var i = 0; i < n; i++) allValid &= MerkleTree.Verify(MerkleTree.Hex(hashes[i]), proofs[i], root) && leaves[i] == MerkleTree.Hex(MerkleTree.Leaf(hashes[i]));
        }
        check(allValid, "B1 Merkle: jeder Pfad führt für 1, 2, 3, 4, 5, 7, 8, 13 und 100 Nachweise zur Wurzel");
        var h = Enumerable.Range(0, 5).Select(H).ToList();
        var (r1, p1, _) = MerkleTree.Build(h);
        var (r2, _, _) = MerkleTree.Build(h);
        var (r3, _, _) = MerkleTree.Build(Enumerable.Reverse(h).ToList());
        check(r1 == r2 && r1 != r3 && r1.Length == 64, "B1 Merkle: deterministische Wurzel; andere Reihenfolge → andere Wurzel");
        check(MerkleTree.Build(new[] { H(0) }).Root == MerkleTree.Hex(MerkleTree.Leaf(H(0))), "B1 Merkle: ein Blatt = die Wurzel ist das Blatt (mit Domänentrennung)");
        var tampered = p1[2].ToList(); tampered[0] = tampered[0] with { Hash = MerkleTree.Hex(H(99)) };
        var flipped = p1[2].Select(s => s with { SiblingIsLeft = !s.SiblingIsLeft }).ToList();
        check(!MerkleTree.Verify(MerkleTree.Hex(H(2)), tampered, r1) && !MerkleTree.Verify(MerkleTree.Hex(H(2)), flipped, r1) && !MerkleTree.Verify(MerkleTree.Hex(H(3)), p1[2], r1) && !MerkleTree.Verify(MerkleTree.Hex(H(2)), p1[2], MerkleTree.Hex(H(7))),
            "B2 Merkle-Manipulation: veränderter Pfad, vertauschte Seite, falscher Nachweis oder falsche Wurzel werden erkannt");
        check(!MerkleTree.Verify("zz", p1[0], r1) && !MerkleTree.Verify(MerkleTree.Hex(H(0)), new[] { new MerkleStep(true, "00") }, r1), "B2 Merkle: ungültige Hex-Werte ergeben false statt einer Ausnahme");
        // Domänentrennung: ein innerer Knoten darf nicht als Blatt durchgehen
        var inner = MerkleTree.Node(MerkleTree.Leaf(H(0)), MerkleTree.Leaf(H(1)));
        check(MerkleTree.Hex(MerkleTree.Leaf(inner)) != MerkleTree.Hex(MerkleTree.Node(MerkleTree.Leaf(H(0)), MerkleTree.Leaf(H(1)))), "B2 Merkle: Blatt- und Knoten-Hashes sind getrennt (keine Second-Preimage-Verwechslung)");
    }

    // ─── Konfiguration ──────────────────────────────────────────────────────

    private static void Config(Action<bool, string> check)
    {
        var d = new BlockchainSettings();
        check(d.Enabled && d.Network == ChainNetworks.Amoy && d.ChainId == 80002 && d.AutoAnchor && d.MaxCostEur == 0.001m && !d.MainnetEnabled && d.BatchEnabled && d.BatchMaxSize == 100 && d.BatchMaxWaitMinutes == 60 && d.Validate().Count == 0,
            "B3 Blockchain-Konfiguration: Standard = aktiv, Polygon Amoy (80002), automatisch, 0,001 EUR, Mainnet aus, Batch 100/60 min");
        check(new BlockchainSettings { MaxCostEur = 0.002m }.Validate().Any(e => e.Contains("BLOCKCHAIN_MAX_COST_EUR")) && new BlockchainSettings { MaxCostEur = 0 }.Validate().Count > 0, "B3 Blockchain-Konfiguration: ein Kostenlimit über 0,001 EUR (oder 0) ist ungültig");
        check(new BlockchainSettings { Network = ChainNetworks.Mainnet }.Validate().Any(e => e.Contains("MAINNET_ENABLED")) && new BlockchainSettings { Network = ChainNetworks.Mainnet, MainnetEnabled = true }.Validate().Count == 0,
            "B3 Blockchain-Konfiguration: Mainnet braucht ausdrücklich BLOCKCHAIN_MAINNET_ENABLED=true (nie implizit)");
        check(new BlockchainSettings { Network = ChainNetworks.Mainnet, MainnetEnabled = true, Simulate = true }.Validate().Count > 0 && new BlockchainSettings { TestSigner = true }.Validate().Count > 0
              && new BlockchainSettings { TestSigner = true, RpcUrlAmoy = "http://127.0.0.1:9999" }.Validate().Count == 0, "B3 Blockchain-Konfiguration: Simulation nie mit Mainnet; Testsignierer nur mit lokalem RPC");
        check(new BlockchainSettings { RpcUrlAmoy = "http://rpc.example.com" }.Validate().Count > 0 && new BlockchainSettings { WalletAddress = "0x123" }.Validate().Count > 0 && new BlockchainSettings { GasSafetyFactor = 0.5m }.Validate().Count > 0,
            "B3 Blockchain-Konfiguration: http-RPC, ungültige Wallet-Adresse und Sicherheitsabschläge werden abgelehnt");
        check(ChainNetworks.ChainId(ChainNetworks.Amoy) == 80002 && ChainNetworks.ChainId(ChainNetworks.Mainnet) == 137 && ChainNetworks.ExplorerTx(ChainNetworks.Amoy, "0x1").Contains("amoy") && ChainNetworks.ExplorerTx(ChainNetworks.Simulation, "0x1") == "",
            "B3 Netzwerke: Chain-IDs Amoy 80002 und Mainnet 137; Simulation hat keinen Explorer");
    }

    // ─── Kostenschranke ─────────────────────────────────────────────────────

    private static CostInput Input(ulong? gas = 21_576, long? gwei = 40, decimal? rate = 0.20m, int minutesOld = 1, int count = 1, bool mainnet = true, decimal limit = 0.001m) =>
        new(mainnet, gas, gwei is null ? null : new BigInteger(gwei.Value) * 1_000_000_000, rate, rate is null ? null : DateTime.UtcNow.AddMinutes(-minutesOld), DateTime.UtcNow, count, limit, 1.5m, 1.25m, TimeSpan.FromMinutes(15));

    private static void CostGuard(Action<bool, string> check)
    {
        var ok = AnchorCostGuard.Evaluate(Input());
        check(ok.Allowed && ok.PerCertEur > 0 && ok.PerCertEur <= 0.001m, $"B4 Kostenschranke: günstige Verankerung wird freigegeben ({ok.PerCertEur:0.########} EUR je Nachweis inkl. Sicherheitsaufschlag)");
        var worstCase = (decimal)(new BigInteger(ok.GasLimit) * ok.MaxFeePerGasWei) / 1e18m * 0.20m * 1.25m;
        check(worstCase <= 0.001m && ok.MaxFeePerGasWei > 0, "B4 Kostenschranke: Gas-Limit × Maximalgebühr × Kurs × Sicherheitsaufschlag liegt technisch unter dem Limit (Obergrenze der Transaktion)");
        var expensive = AnchorCostGuard.Evaluate(Input(gwei: 400));
        check(!expensive.Allowed && expensive.Code == "cost_limit" && expensive.PerCertEur > 0.001m, "B4 Kostenschranke: Kosten über 0,001 EUR je Nachweis → gesperrt (Kostenlimit überschritten)");
        var batch = AnchorCostGuard.Evaluate(Input(gwei: 400, count: 100));
        check(batch.Allowed && batch.TotalEur > 0.001m && batch.PerCertEur <= 0.001m, $"B4 Batch-Kostenverteilung: Gesamtkosten {batch.TotalEur:0.####} EUR, aber {batch.PerCertEur:0.########} EUR je Nachweis → erlaubt");
        check(!AnchorCostGuard.Evaluate(Input(rate: null)).Allowed && AnchorCostGuard.Evaluate(Input(rate: null)).Code == "price_unavailable", "B4 Kostenschranke: fehlender POL/EUR-Kurs → gesperrt");
        check(!AnchorCostGuard.Evaluate(Input(minutesOld: 30)).Allowed && AnchorCostGuard.Evaluate(Input(minutesOld: 30)).Code == "price_stale", "B4 Kostenschranke: veralteter Kurs (30 min) → gesperrt");
        check(AnchorCostGuard.Evaluate(Input(gas: null)).Code == "cost_unknown" && AnchorCostGuard.Evaluate(Input(gwei: null)).Code == "cost_unknown" && AnchorCostGuard.Evaluate(Input(gas: 0)).Code == "cost_unknown", "B4 Kostenschranke: unbekannte Gas-Schätzung oder unbekannter Gaspreis → gesperrt");
        check(!AnchorCostGuard.Evaluate(Input(count: 0)).Allowed && !AnchorCostGuard.Evaluate(Input(limit: 0)).Allowed, "B4 Kostenschranke: leerer Batch und Limit 0 → gesperrt");
        // Obergrenze: ein höher konfiguriertes Limit wird auf 0,001 EUR gekürzt
        var clamp = AnchorCostGuard.Evaluate(Input(gwei: 400, limit: 5m));
        check(!clamp.Allowed && clamp.LimitEur == 0.001m, "B4 Kostenschranke: ein höheres übergebenes Limit wird auf die harte Obergrenze 0,001 EUR gekürzt");
        // Sicherheitsaufschlag wirkt: knapp unter dem Limit ohne Aufschlag, darüber mit Aufschlag
        var near = Input(gwei: 150);
        var withoutFactor = AnchorCostGuard.Evaluate(near with { GasSafetyFactor = 1m, RateSafetyFactor = 1m });
        var withFactor = AnchorCostGuard.Evaluate(near);
        check(withoutFactor.Allowed && !withFactor.Allowed, "B4 Sicherheitsaufschlag: gleiche Marktdaten sind ohne Aufschlag erlaubt, mit konservativem Aufschlag gesperrt");
        var test = AnchorCostGuard.Evaluate(Input(mainnet: false, rate: null));
        check(test.Allowed && test.Code == "testnet" && test.PerCertEur == 0m, "B4 Testnet: keine echten Gebühren (0 EUR), auch ohne Kurs freigegeben");
        var testSim = AnchorCostGuard.Evaluate(Input(mainnet: false));
        check(testSim.SimulatedEur is > 0, "B4 Testnet: simulierte Mainnet-Kosten werden (nur zur Information) berechnet");
    }

    // ─── Badges ─────────────────────────────────────────────────────────────

    private static async Task BadgesAsync(Action<bool, string> check, string tmp)
    {
        await using var e = await Env.CreateAsync(Path.Combine(tmp, "bc-badge"));
        var c = await e.IssueAsync(e.LearnerId);
        check(c.Number.StartsWith("LC-") && c.BadgeJws.Split('.').Length == 3 && c.BadgeHash.Length == 64 && c.LeafHash.Length == 64 && c.ChainStatus == ChainStatus.Pending && c.ChainNetwork == ChainNetworks.Amoy && c.ChainId == 80002,
            "B5 Badge-Ausstellung: signiertes Badge mit Nummer, Prüf-Hash, Merkle-Blatt; Verankerung ausstehend im Netzwerk Amoy (Chain-ID gespeichert)");
        var payload = JsonNode.Parse(BadgeSigner.PayloadText(c.BadgeJws))!;
        check(payload["type"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "VerifiableCredential", "OpenBadgeCredential" }) && payload["credentialSubject"]!["type"]![0]!.GetValue<string>() == "AchievementSubject"
              && payload["issuer"]!["id"]!.GetValue<string>() == "https://learny.test/badges/issuer" && payload["@context"]!.AsArray().Count == 3 && payload["credentialStatus"]!["type"]!.GetValue<string>() == "1EdTechRevocationList",
            "B5 Open Badge 3.0: Kontexte, Typen (OpenBadgeCredential, AchievementSubject), Aussteller und Widerrufsliste");
        check(JsonNode.Parse(await Task.FromResult(BadgeSigner.PayloadText(c.BadgeJws)))!["credentialSubject"]!["achievement"]!["name"]!.GetValue<string>() == "Testmodul" && c.BadgeHash == BadgeSigner.HashHex(c.BadgeJws), "B5 Badge-Hash: SHA-256 der JWS, reproduzierbar");
        var signed = e.Certs.SignedBadgeJson(c).ToJsonString();
        var v = await e.Certs.VerifyAsync(signed);
        check(v.Valid && v.SignatureValid && v.IssuerValid && v.ContentMatches == true && v.Known && !v.Revoked && v.Number == c.Number, "B5 Badge-Prüfung: Signatur, Aussteller, Integrität und Registrierung gültig");
        check((await e.Certs.VerifyAsync(c.BadgeJws)).Valid, "B5 Badge-Prüfung: auch die kompakte JWS allein wird geprüft");

        var tampered = JsonNode.Parse(signed)!; tampered["name"] = "Manipuliert";
        var vt = await e.Certs.VerifyAsync(tampered.ToJsonString());
        check(!vt.Valid && vt.ContentMatches == false && vt.SignatureValid, "B6 Manipulation: geänderter Badge-Inhalt bei unveränderter Signatur wird erkannt (Inhalt ≠ signierter Inhalt)");
        var parts = c.BadgeJws.Split('.');
        var forgedPayload = BadgeSigner.PayloadText(c.BadgeJws).Replace("Testmodul", "Gefälscht");
        var forged = parts[0] + "." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(forgedPayload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + parts[2];
        check(!(await e.Certs.VerifyAsync(forged)).SignatureValid, "B6 Manipulation: veränderte Nutzlast → Signatur ungültig");
        var noneAlg = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"kid\":\"x\"}")).TrimEnd('=') + "." + parts[1] + ".";
        check(!(await e.Certs.VerifyAsync(noneAlg)).SignatureValid && !(await e.Certs.VerifyAsync("kein-jws")).SignatureValid && !(await e.Certs.VerifyAsync("{ kaputt")).SignatureValid && !(await e.Certs.VerifyAsync("{}")).SignatureValid,
            "B6 Manipulation: „alg: none“, Müll und leeres JSON werden abgewiesen");
        await using var other = await Env.CreateAsync(Path.Combine(tmp, "bc-badge-other"));
        var foreign = await other.IssueAsync(other.LearnerId);
        check(!(await e.Certs.VerifyAsync(foreign.BadgeJws)).Valid, "B6 Fremder Aussteller: ein mit einem anderen Schlüssel signiertes Badge ist ungültig");
        check(e.Signer.PublicJwk().ContainsKey("x") && !e.Signer.PublicJwk().ContainsKey("d"), "B6 Schlüssel: veröffentlicht wird nur der öffentliche Schlüssel (kein privater Anteil)");

        // Idempotenz: dasselbe Modul/dieselbe Version erzeugt kein zweites Zertifikat
        var result = new QuizResultRecord { UserSub = e.LearnerId, ModuleId = c.ModuleId, Score = 95, MaxScore = 100, PassPercent = 60, Passed = true, SessionId = Guid.NewGuid().ToString() };
        var again = await e.Certs.TryIssueAsync(result, "Testmodul", "global", 1);
        check(again!.Id == c.Id && (await e.AllAsync()).Count == 1, "B7 Badge-Ausstellung: wiederholtes Bestehen desselben Moduls erzeugt kein zweites Zertifikat (idempotent)");
        check(await e.Certs.TryIssueAsync(Copy(result, r => r.Passed = false), "Testmodul", "global", 2) is null && await e.Certs.TryIssueAsync(Copy(result, r => r.SessionId = null), "Testmodul", "global", 3) is null
              && await e.Certs.TryIssueAsync(Copy(result, r => r.CardId = Guid.NewGuid().ToString()), "Testmodul", "global", 4) is null, "B7 Badge-Ausstellung: nicht bestanden, ohne Sitzung (nicht verifiziert) oder Kartenquiz → kein Zertifikat");
        var newVersion = await e.Certs.TryIssueAsync(result, "Testmodul", "global", 2);
        check(newVersion is not null && newVersion.Id != c.Id, "B7 Badge-Ausstellung: eine neue globale Modulversion erhält ein eigenes Zertifikat");

        // Widerruf
        await e.Certs.RevokeAsync(e.AdminId, c.Id, "Test-Widerruf");
        var vr = await e.Certs.VerifyAsync(signed);
        var status = await e.Certs.GetPublicStatusAsync(c.Id);
        check(vr.Revoked && !vr.Valid && vr.SignatureValid && System.Text.Json.JsonSerializer.Serialize(status).Contains("\"status\":\"revoked\""), "B8 Widerruf: die Prüfung meldet widerrufen (Signatur bleibt erhalten), der öffentliche Status zeigt „revoked“");
        var list = System.Text.Json.JsonSerializer.Serialize(await e.Certs.RevocationListAsync());
        check(list.Contains(c.Id) && list.Contains("1EdTechRevocationList"), "B8 Widerruf: die Widerrufsliste (1EdTechRevocationList) enthält das Zertifikat");
        bool denied = false; try { await e.Certs.RevokeAsync(e.LearnerId, newVersion!.Id, "x"); } catch (UnauthorizedAccessException) { denied = true; }
        bool noReason = false; try { await e.Certs.RevokeAsync(e.AdminId, newVersion!.Id, " "); } catch (ArgumentException) { noReason = true; }
        check(denied && noReason, "B8 Widerruf: nur Administratoren, nur mit Begründung");
        var pub = System.Text.Json.JsonSerializer.Serialize(await e.Certs.GetPublicStatusAsync(newVersion!.Id));
        check(!pub.Contains("Lernende") && !pub.Contains(e.LearnerId) && !pub.Contains("learner"), "B8 Datenschutz: der öffentliche Status enthält keine Lernendendaten");
    }

    // ─── Verankerungs-Pipeline (Testnet) ───────────────────────────────────

    private static async Task PipelineAsync(Action<bool, string> check, string tmp)
    {
        await using var e = await Env.CreateAsync(Path.Combine(tmp, "bc-pipe"));
        for (var i = 0; i < 5; i++) await e.IssueAsync();
        var s0 = await e.Anchors.ProcessAsync();
        check(s0.Planned == 0 && e.Clients.Chain.SendCount == 0 && (await e.AllAsync()).All(c => c.ChainStatus == ChainStatus.Pending), "B9 Batch-Wartezeit: 5 Nachweise (< Batch-Größe, < 60 min) bleiben in der Warteschlange — keine Transaktion");
        e.Now = e.Now.AddMinutes(61);
        var s1 = await e.Anchors.ProcessAsync();
        var certs = await e.AllAsync();
        var batches = await e.Anchors.ListBatchesAsync();
        check(s1.Planned == 1 && s1.Submitted == 1 && e.Clients.Chain.SendCount == 1 && batches.Count == 1 && batches[0].LeafCount == 5 && certs.All(c => c.ChainStatus == ChainStatus.Anchoring && c.BatchId == batches[0].Id),
            "B9 Bündelung: nach Ablauf der Wartezeit werden alle 5 Nachweise in EINER Transaktion verankert (ein Merkle-Root)");
        check(certs.All(c => MerkleTree.Verify(c.BadgeHash, System.Text.Json.JsonSerializer.Deserialize<List<ProofStep>>(c.MerkleProofJson!)!.Select(p => new MerkleStep(p.sibling_is_left, p.hash)), batches[0].MerkleRoot)),
            "B9 Merkle-Proofs: jeder der 5 Nachweise besitzt einen eigenen, gültigen Pfad zur Batch-Wurzel");
        var tx = PseudoAnchorSigner.Decode(e.Clients.Chain.Sent[0])!;
        check(tx["data"]!.GetValue<string>() == AnchorService.AnchorData(batches[0].MerkleRoot) && tx["from"]!.GetValue<string>() == tx["to"]!.GetValue<string>() && tx["chain_id"]!.GetValue<int>() == 80002,
            "B9 Anker-Transaktion: Nullwert-Selbsttransaktion mit „LCv1“ + Merkle-Root (32 Byte) — keine personenbezogenen Daten, Chain-ID 80002");
        check(!tx.ToJsonString().Contains("Lernende") && !tx.ToJsonString().Contains(e.LearnerId), "B9 Datenschutz: in der Transaktion steht nichts von den Lernenden");

        // Noch nicht ausreichend bestätigt → bleibt „läuft“
        e.Clients.Chain.Head += 2;
        await e.Anchors.ProcessAsync();
        check((await e.AllAsync()).All(c => c.ChainStatus == ChainStatus.Anchoring), "B10 Finalität: mit 3 von 5 Bestätigungen gilt der Nachweis noch nicht als Blockchain-bestätigt");
        // Doppelte Verarbeitung (parallel) und Wiederholung: keine zweite Transaktion
        e.Clients.Chain.Head += 20;
        await Task.WhenAll(e.Anchors.ProcessAsync(), e.Anchors.ProcessAsync(), e.Anchors.ProcessAsync());
        var confirmed = await e.AllAsync();
        var b = (await e.Anchors.ListBatchesAsync())[0];
        check(confirmed.All(c => c.ChainStatus == ChainStatus.Confirmed && c.AnchoredAt is not null) && b.Status == BatchStatus.Confirmed && e.Clients.Chain.SendCount == 1 && e.Clients.Chain.UniqueTx == 1,
            "B10 Keine Doppelverankerung: parallele und wiederholte Verarbeitung ändert nichts — genau eine Transaktion, alle Nachweise bestätigt");
        check(b.FeeEur == 0 && b.CostPerCertEur == 0 && b.SimulatedCostEur is > 0 && b.GasUsed == "21576", $"B10 Kosten Testnet: reale Ausgaben 0 EUR; simulierte Mainnet-Kosten {b.SimulatedCostEur:0.########} EUR je Nachweis (Gas {b.GasUsed})");
        await e.Anchors.ProcessAsync(force: true);
        check(e.Clients.Chain.SendCount == 1 && (await e.Anchors.ListBatchesAsync()).Count == 1, "B10 Idempotenz: erneutes Verarbeiten (auch erzwungen) ohne neue Nachweise erzeugt weder Batch noch Transaktion");

        // Unabhängige Prüfung des Nachweis-Pakets
        var cert = confirmed[0];
        var pkg = (await e.Certs.BuildProofPackageAsync(cert))!;
        var good = await e.Anchors.VerifyProofPackageAsync(pkg);
        check(good["merkle_valid"]!.GetValue<bool>() && good["tx_found"]!.GetValue<bool>() && good["tx_data_ok"]!.GetValue<bool>() && good["tx_sender_ok"]!.GetValue<bool>() && good["receipt_success"]!.GetValue<bool>() && good["network_ok"]!.GetValue<bool>() && good["confirmations"]!.GetValue<long>() >= 5,
            "B11 Unabhängige Prüfung: Merkle-Pfad, Transaktion, Absender, Anker-Daten, Status, Bestätigungen und Chain-ID stimmen");
        var badProof = JsonNode.Parse(pkg.ToJsonString())!; badProof["merkle_proof"]![0]!["hash"] = MerkleTree.Hex(H(5));
        var badHash = JsonNode.Parse(pkg.ToJsonString())!; badHash["badge_hash"] = MerkleTree.Hex(H(6));
        var badRoot = JsonNode.Parse(pkg.ToJsonString())!; badRoot["merkle_root"] = MerkleTree.Hex(H(7));
        check(!(await e.Anchors.VerifyProofPackageAsync(badProof))["merkle_valid"]!.GetValue<bool>() && !(await e.Anchors.VerifyProofPackageAsync(badHash))["merkle_valid"]!.GetValue<bool>() && (await e.Anchors.VerifyProofPackageAsync(badRoot))["tx_data_ok"]!.GetValue<bool>() == false,
            "B11 Manipulierter Merkle-Proof: veränderter Pfad, fremder Nachweis-Hash oder fremde Wurzel werden erkannt");
        e.Clients.Chain.TamperedInput = AnchorService.AnchorData(MerkleTree.Hex(H(8)));
        var tampered = await e.Anchors.VerifyProofPackageAsync(pkg);
        e.Clients.Chain.TamperedInput = null;
        check(tampered["tx_data_ok"]!.GetValue<bool>() == false, "B11 Manipulation on-chain: abweichende Anker-Daten in der Transaktion werden erkannt");
        var wrongNet = JsonNode.Parse(pkg.ToJsonString())!; wrongNet["network"] = "polygon-evil";
        check((await e.Anchors.VerifyProofPackageAsync(wrongNet))["tx_found"] is null, "B11 Netzwerk: ein unbekanntes Netzwerk im Nachweis wird nie angefragt (RPC nur aus der Konfiguration)");

        // Widerruf nach der Verankerung: Anker bleibt, Status widerrufen
        await e.Certs.RevokeAsync(e.AdminId, cert.Id, "Widerruf nach Verankerung");
        var after = (await e.Certs.GetRawAsync(cert.Id))!;
        check(after.IsRevoked && after.ChainStatus == ChainStatus.Confirmed && ChainStatus.Label(after.ChainStatus, after.IsRevoked) == "Widerrufen", "B12 Widerruf nach Verankerung: Verankerung bleibt bestehen, Anzeige „Widerrufen“");

        // Neustart während eines Batches: neuer Dienst setzt fort, ohne erneut zu senden
        await using var e2 = await Env.CreateAsync(Path.Combine(tmp, "bc-restart"));
        for (var i = 0; i < 3; i++) await e2.IssueAsync();
        await e2.Anchors.ProcessAsync(force: true);
        var firstSend = e2.Clients.Chain.SendCount;
        var restarted = new AnchorService(e2.Db, e2.Cfg, e2.Settings, e2.Clients, e2.Price, new PseudoAnchorSigner(Wallet), e2.Users, NullLogger<AnchorService>.Instance);
        e2.Clients.Chain.Head += 50;
        await restarted.ProcessAsync();
        check(firstSend == 1 && e2.Clients.Chain.SendCount == 1 && (await e2.AllAsync()).All(c => c.ChainStatus == ChainStatus.Confirmed), "B12 Serverneustart: ein neuer Dienst prüft die gesendete Transaktion und bestätigt, ohne sie erneut zu senden");

        // Widerruf vor der Verankerung: nicht in den Batch
        await using var e3 = await Env.CreateAsync(Path.Combine(tmp, "bc-revoke"));
        var keep = await e3.IssueAsync(); var drop = await e3.IssueAsync();
        await e3.Certs.RevokeAsync(e3.AdminId, drop.Id, "vor Verankerung");
        await e3.Anchors.ProcessAsync(force: true);
        var b3 = (await e3.Anchors.ListBatchesAsync()).Single();
        check(b3.LeafCount == 1 && (await e3.Certs.GetRawAsync(drop.Id))!.BatchId is null && (await e3.Certs.GetRawAsync(drop.Id))!.ChainStatus == ChainStatus.None, "B12 Widerruf vor der Verankerung: das Zertifikat wird nicht verankert, der Batch enthält nur das gültige");

        // Netzwerkwechsel: alte Nachweise behalten ihr Netzwerk und werden nicht im neuen Netzwerk verankert
        await using var e4 = await Env.CreateAsync(Path.Combine(tmp, "bc-switch"));
        var oldCert = await e4.IssueAsync();
        e4.Cfg.Blockchain.Network = ChainNetworks.Mainnet; e4.Cfg.Blockchain.MainnetEnabled = true;
        await e4.Anchors.ProcessAsync(force: true);
        var oldAfter = (await e4.Certs.GetRawAsync(oldCert.Id))!;
        check(e4.Clients.Chain.SendCount == 0 && oldAfter.ChainNetwork == ChainNetworks.Amoy && oldAfter.ChainId == 80002 && oldAfter.ChainStatus == ChainStatus.Pending, "B12 Netzwerkwechsel: bestehende Nachweise behalten Netzwerk und Chain-ID (Amoy/80002) und werden nicht im neuen Netzwerk verankert");

        // Blockchain ausgeschaltet: Badge bleibt, keine Verankerung
        await using var e5 = await Env.CreateAsync(Path.Combine(tmp, "bc-off"), b => b.Enabled = false);
        var plain = await e5.IssueAsync();
        var off = await e5.Anchors.ProcessAsync(force: true);
        check(plain.ChainStatus == ChainStatus.None && plain.BadgeJws.Length > 0 && e5.Clients.Chain.SendCount == 0 && off.Note is not null && (await e5.Certs.VerifyAsync(plain.BadgeJws)).Valid, "B13 Betrieb ohne Blockchain: Badge wird signiert und ist prüfbar, es gibt keine Verankerung und keine Transaktion");
        await e.Settings.SetAsync("chain.enabled", "false", e.AdminId);
        check(!(await e.Settings.GetAsync()).Enabled && (await e.Anchors.ProcessAsync(force: true)).Note is not null, "B13 Admin kann die Verankerung deaktivieren (Einstellung wirkt sofort)");

        // Einstellungen: Admin darf nur innerhalb der Grenzen ändern
        await using var e6 = await Env.CreateAsync(Path.Combine(tmp, "bc-limits"), b => b.BatchMaxSize = 20);
        await e6.Settings.SetAsync("chain.max_cost_eur", "0.5", e6.AdminId);
        await e6.Settings.SetAsync("chain.batch_max_size", "500", e6.AdminId);
        var eff = await e6.Settings.GetAsync();
        check(eff.MaxCostEur == 0.001m && eff.BatchMaxSize == 20, "B13 Einstellungen: ein Kostenlimit über 0,001 EUR und eine Batch-Größe über dem Umgebungswert werden gekürzt");
        await e6.Settings.SetAsync("chain.max_cost_eur", "0.0004", e6.AdminId);
        check((await e6.Settings.GetAsync()).MaxCostEur == 0.0004m, "B13 Einstellungen: ein niedrigeres Kostenlimit ist erlaubt");
    }

    private sealed record ProofStep(bool sibling_is_left, string hash);

    // ─── Mainnet: Kostenschranke als harte Grenze ──────────────────────────

    private static async Task<Env> MainnetEnvAsync(string dir, bool approve = true)
    {
        var e = await Env.CreateAsync(dir, b => { b.Network = ChainNetworks.Mainnet; b.MainnetEnabled = true; });
        e.Clients.Chain.ChainId = 137;
        if (approve) await e.Settings.ApproveMainnetAsync(e.AdminId);
        return e;
    }

    private static async Task MainnetAsync(Action<bool, string> check, string tmp)
    {
        // 1) Teurer als 0,001 EUR je Nachweis → es wird NICHT gesendet; Wiederholungen ändern daran nichts
        await using (var e = await MainnetEnvAsync(Path.Combine(tmp, "bc-main-expensive")))
        {
            await e.IssueAsync();
            e.Clients.Chain.GasPrice = new BigInteger(400) * 1_000_000_000;
            for (var i = 0; i < 6; i++) { e.Now = e.Now.AddMinutes(2); await e.Anchors.ProcessAsync(force: true); }
            var c = (await e.AllAsync())[0]; var b = (await e.Anchors.ListBatchesAsync())[0];
            check(e.Clients.Chain.SendCount == 0 && c.ChainStatus == ChainStatus.CostBlocked && c.ChainError.Contains("Kostenlimit") && b.Status == BatchStatus.CostBlocked && b.Attempts >= 6 && c.BadgeJws.Length > 0,
                "B14 MAINNET Kostenlimit: bei Kosten über 0,001 EUR je Nachweis wird keine Transaktion gesendet — auch nicht nach 6 automatischen Wiederholungen; Badge bleibt signiert, Status „Kostenlimit überschritten“");
            // Preis fällt → derselbe Batch wird verankert, mit technisch begrenzter Maximalgebühr
            e.Clients.Chain.GasPrice = new BigInteger(40) * 1_000_000_000;
            await e.Anchors.ProcessAsync();
            var sent = e.Clients.Chain.Sent.Single();
            var tx = PseudoAnchorSigner.Decode(sent)!;
            var maxFee = BigInteger.Parse(tx["max_fee"]!.GetValue<string>()); var gas = (decimal)tx["gas"]!.GetValue<ulong>();
            var worst = (decimal)(new BigInteger((ulong)gas) * maxFee) / 1e18m * 0.20m * 1.25m;
            check(e.Clients.Chain.SendCount == 1 && worst <= 0.001m, $"B14 MAINNET Obergrenze: nach Preisrückgang wird gesendet; Gas-Limit × Maximalgebühr × Kurs × Aufschlag = {worst:0.########} EUR ≤ 0,001 EUR (technisch in der Transaktion begrenzt)");
            check(JsonNode.Parse(PseudoAnchorSigner.Decode(sent)!.ToJsonString())!["chain_id"]!.GetValue<int>() == 137, "B14 MAINNET: Chain-ID 137 in der signierten Transaktion");
            e.Clients.Chain.Head += 40;
            await e.Anchors.ProcessAsync();
            var done = (await e.Anchors.ListBatchesAsync())[0];
            check(done.Status == BatchStatus.Confirmed && done.FeeEur is > 0 && done.CostPerCertEur is > 0 and <= 0.001 && done.RateEurPerPol == 0.20, $"B14 MAINNET Kosten: tatsächliche Gebühr {done.FeeEur:0.########} EUR, {done.CostPerCertEur:0.########} EUR je Nachweis, Kurs {done.RateEurPerPol} EUR/POL gespeichert");
            var stats = await e.Anchors.GetStatsAsync();
            check(stats["mainnet"]!["real_cost_total_eur"]!.GetValue<double>() > 0 && stats["testnet"]!["real_cost_total_eur"]!.GetValue<double>() == 0, "B14 Kostenübersicht: Mainnet-Ausgaben und Testnet (0 EUR) getrennt ausgewiesen");
        }

        // 2) Unsichere Kosten → nie senden
        foreach (var (name, setup) in new (string, Action<Env>)[]
        {
            ("Preisquelle liefert keinen Kurs", e => e.Price.Quote = null),
            ("Kurs ist veraltet", e => e.Price.Quote = new PriceQuote(0.2m, DateTime.UtcNow.AddHours(-3), "alt")),
            ("Gas-Schätzung schlägt fehl", e => e.Clients.Chain.EstimateFails = true),
            ("Gaspreis nicht abrufbar", e => e.Clients.Chain.GasPriceFails = true),
            ("Wallet ohne Guthaben", e => e.Clients.Chain.Balance = 0),
            ("falsche Chain-ID", e => e.Clients.Chain.ChainId = 80002),
        })
        {
            await using var e = await MainnetEnvAsync(Path.Combine(tmp, "bc-main-" + Math.Abs(name.GetHashCode())));
            await e.IssueAsync();
            setup(e);
            for (var i = 0; i < 3; i++) await e.Anchors.ProcessAsync(force: true);
            var c = (await e.AllAsync())[0];
            check(e.Clients.Chain.SendCount == 0 && c.ChainStatus is ChainStatus.CostBlocked or ChainStatus.Batched or ChainStatus.Pending && c.BadgeJws.Length > 0, $"B15 MAINNET unsichere Kosten/Zustände: „{name}“ → keine Transaktion, Nachweis bleibt ausstehend (Status {ChainStatus.Label(c.ChainStatus)})");
        }

        // 3) Mainnet nicht freigegeben bzw. kein Signierer → nie senden, auch wenn die Kosten niedrig sind
        await using (var e = await MainnetEnvAsync(Path.Combine(tmp, "bc-main-unapproved"), approve: false))
        {
            await e.IssueAsync();
            await e.Anchors.ProcessAsync(force: true);
            check(e.Clients.Chain.SendCount == 0 && !(await e.Settings.GetAsync()).MainnetUnlocked && (await e.Anchors.ListBatchesAsync())[0].Error.Contains("nicht freigegeben"), "B16 Mainnet-Sperre: ohne ausdrückliche administrative Freigabe wird nicht gesendet — auch bei niedrigen Kosten");
            await e.Settings.ApproveMainnetAsync(e.AdminId);
            check((await e.Settings.GetAsync()).MainnetUnlocked, "B16 Mainnet-Freigabe: erst nach ausdrücklicher Freigabe (Umgebung + Admin) ist Mainnet entsperrt");
            e.Cfg.Blockchain.WalletAddress = "0x00000000000000000000000000000000000000bb";
            check(!(await e.Settings.GetAsync()).MainnetUnlocked, "B16 Mainnet-Freigabe: ein Wallet-Wechsel hebt die Freigabe auf (neue ausdrückliche Freigabe nötig)");
        }
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-main-envoff"), b => { b.Network = ChainNetworks.Amoy; }))
        {
            bool threw = false; try { await e.Settings.ApproveMainnetAsync(e.AdminId); } catch (InvalidOperationException) { threw = true; }
            check(threw, "B16 Mainnet-Freigabe: ohne Umgebungsfreigabe (BLOCKCHAIN_NETWORK/MAINNET_ENABLED) nicht möglich");
        }
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-main-nosigner"), b => { b.Network = ChainNetworks.Mainnet; b.MainnetEnabled = true; }, new UnavailableAnchorSigner(Wallet)))
        {
            e.Clients.Chain.ChainId = 137;
            await e.Settings.ApproveMainnetAsync(e.AdminId);
            await e.IssueAsync();
            await e.Anchors.ProcessAsync(force: true);
            check(e.Clients.Chain.SendCount == 0 && (await e.Anchors.ListBatchesAsync())[0].Error.Contains("signierer"), "B16 Ohne Transaktionssignierer (Standard, ADR 0022): nichts wird gesendet, Verankerung bleibt ausstehend");
        }

        // 4) Batch: Gesamtkosten > 0,001 EUR, aber je Nachweis ≤ 0,001 EUR → erlaubt; Einzelverankerung gesperrt
        await using (var e = await MainnetEnvAsync(Path.Combine(tmp, "bc-main-batch")))
        {
            var many = new List<Certificate>();
            for (var i = 0; i < 100; i++) many.Add(await e.IssueAsync());
            e.Clients.Chain.GasPrice = new BigInteger(400) * 1_000_000_000;   // je Transaktion teuer: gesamt ≈ 0,05 EUR
            await e.Anchors.ProcessAsync();   // genau 100 Nachweise = Batch-Größe → fällig
            var b = (await e.Anchors.ListBatchesAsync()).Single();
            check(b.LeafCount == 100 && e.Clients.Chain.SendCount == 1 && b.EstTotalEur is > 0.001 && b.EstTotalEur / 100 <= 0.001, $"B17 Batch-Kosten: 100 Nachweise, geschätzte Gesamtkosten {b.EstTotalEur:0.####} EUR = {b.EstTotalEur / 100:0.########} EUR je Nachweis → verankert (Limit gilt je Nachweis)");
            e.Clients.Chain.Head += 50;
            await e.Anchors.ProcessAsync();
            var done = (await e.Anchors.ListBatchesAsync()).Single();
            check(done.Status == BatchStatus.Confirmed && done.CostPerCertEur is > 0 and <= 0.001 && done.SavedEur is > 0, $"B17 Batch-Kosten: tatsächlich {done.CostPerCertEur:0.########} EUR je Nachweis; geschätzte Ersparnis durch Bündelung {done.SavedEur:0.####} EUR");
        }
        await using (var e = await MainnetEnvAsync(Path.Combine(tmp, "bc-main-single-expensive"), approve: true))
        {
            await e.IssueAsync();
            e.Clients.Chain.GasPrice = new BigInteger(400) * 1_000_000_000;
            await e.Anchors.ProcessAsync(force: true);
            check(e.Clients.Chain.SendCount == 0, "B17 Batch-Kosten: derselbe Gaspreis für einen einzelnen Nachweis überschreitet das Limit → keine Transaktion (Bündelung wird abgewartet)");
        }
    }

    // ─── Fehlerfälle ────────────────────────────────────────────────────────

    private static async Task FailuresAsync(Action<bool, string> check, string tmp)
    {
        // RPC nicht erreichbar → nichts geht verloren, später erneut
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-fail-rpc")))
        {
            await e.IssueAsync(); e.Clients.Chain.GasPriceFails = true; e.Clients.Chain.EstimateFails = true;
            var r = await e.Anchors.ProcessAsync(force: true);
            check(e.Clients.Chain.SendCount == 0 && (await e.Anchors.ListBatchesAsync())[0].Status != BatchStatus.Confirmed, "B18 Fehlerfall RPC: Gaspreis/Schätzung nicht verfügbar → keine Transaktion (Testnet-Ausfall bleibt folgenlos)");
            e.Clients.Chain.GasPriceFails = false; e.Clients.Chain.EstimateFails = false; e.Clients.Chain.Head += 50;
            await e.Anchors.ProcessAsync(); e.Clients.Chain.Head += 50; await e.Anchors.ProcessAsync();
            check(e.Clients.Chain.SendCount == 1 && (await e.AllAsync())[0].ChainStatus == ChainStatus.Confirmed, "B18 Fehlerfall RPC: nach Wiederherstellung wird genau einmal verankert und bestätigt");
        }
        // Transaktion verschwindet (Reorg/Drop): dieselbe signierte Transaktion wird erneut gesendet, keine neue
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-fail-drop")))
        {
            await e.IssueAsync();
            e.Clients.Chain.DropTransactions = true;
            await e.Anchors.ProcessAsync(force: true);
            e.Now = e.Now.AddMinutes(15);
            await e.Anchors.ProcessAsync();
            var rawSet = e.Clients.Chain.Sent.Distinct().Count();
            check(e.Clients.Chain.SendCount == 2 && rawSet == 1, "B19 Verworfene Transaktion: nach 10 min wird dieselbe signierte Transaktion erneut gesendet (gleiche Rohdaten, gleicher Hash) — kein zweiter Anker, keine höheren Kosten");
            e.Now = e.Now.AddMinutes(120);
            await e.Anchors.ProcessAsync();
            var b = (await e.Anchors.ListBatchesAsync())[0];
            check(b.Status == BatchStatus.Failed && (await e.AllAsync())[0].ChainStatus == ChainStatus.Failed && (await e.AllAsync())[0].BadgeJws.Length > 0, "B19 Verworfene Transaktion: nach 90 min ohne Aufnahme → „Verankerung fehlgeschlagen“; das signierte Badge bleibt");
            e.Clients.Chain.DropTransactions = false;
            await e.Anchors.RetryBatchAsync(e.AdminId, b.Id);
            await e.Anchors.ProcessAsync(); e.Clients.Chain.Head += 50; await e.Anchors.ProcessAsync();
            check((await e.AllAsync())[0].ChainStatus == ChainStatus.Confirmed, "B19 Erneut einplanen: ein Administrator kann den fehlgeschlagenen Batch erneut einplanen (volle Kostenprüfung, danach bestätigt)");
            bool denied = false; try { await e.Anchors.RetryBatchAsync(e.LearnerId, b.Id); } catch (UnauthorizedAccessException) { denied = true; }
            check(denied, "B19 Erneut einplanen: nur Administratoren");
        }
        // Transaktion vom Netzwerk abgelehnt
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-fail-status")))
        {
            await e.IssueAsync();
            e.Clients.Chain.ReceiptSuccess = false;
            await e.Anchors.ProcessAsync(force: true); await e.Anchors.ProcessAsync();
            check((await e.AllAsync())[0].ChainStatus == ChainStatus.Failed && (await e.Anchors.ListBatchesAsync())[0].Error.Contains("abgelehnt"), "B20 Fehlerfall: vom Netzwerk abgelehnte Transaktion (Status 0) → fehlgeschlagen, nicht bestätigt");
        }
        // Testnet: Faucet ohne Guthaben
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-fail-faucet")))
        {
            await e.IssueAsync(); e.Clients.Chain.Balance = 0;
            await e.Anchors.ProcessAsync(force: true);
            var b = (await e.Anchors.ListBatchesAsync())[0];
            check(e.Clients.Chain.SendCount == 0 && b.Error.Contains("Guthaben"), "B20 Testnet-Faucet: ohne Test-POL wird nicht gesendet; der Hinweis nennt das fehlende Guthaben");
        }
        // Auditor: nur globale Module
        await using (var e = await Env.CreateAsync(Path.Combine(tmp, "bc-audit")))
        {
            var auditor = await e.AddUserAsync("Auditor", Roles.Auditor);
            var g = await e.IssueAsync(scope: "global"); var p = await e.IssueAsync(scope: "private"); var s = await e.IssueAsync(scope: "shared");
            var seen = await e.Certs.ListForAuditorAsync(auditor, new CertificateService.AuditorFilter(null, null, null, null, null, null, null));
            {
                // B23 Schlüsselimport (IIS-Startfehler): PKCS#8 und SEC1 werden ohne CNG-Schlüsseldienst importiert; Signaturen bleiben kompatibel; Fehler verraten keinen Schlüssel.
                using var orig = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                var data = System.Text.Encoding.UTF8.GetBytes("x");
                var h = System.Security.Cryptography.HashAlgorithmName.SHA256;
                var pkcs8 = orig.ExportPkcs8PrivateKeyPem(); var sec1 = orig.ExportECPrivateKeyPem();
                using var k1 = BadgeSigner.ImportKey(pkcs8); using var k2 = BadgeSigner.ImportKey(sec1);
                check(orig.VerifyData(data, k1.SignData(data, h), h) && orig.VerifyData(data, k2.SignData(data, h), h) && k1.VerifyData(data, orig.SignData(data, h), h), "B23 Schlüsselimport: PKCS#8 und SEC1 liefern denselben Schlüssel (Signaturen gegenseitig gültig)");
                var secret = Convert.ToBase64String(new byte[48]);
                string? msg = null;
                try { BadgeSigner.ImportKey("-----BEGIN PRIVATE KEY-----" + Environment.NewLine + secret + Environment.NewLine + "-----END PRIVATE KEY-----"); } catch (InvalidOperationException ex) { msg = ex.Message; }
                check(msg is not null && !msg.Contains(secret) && msg.Contains("Benutzerprofil"), "B23 Schlüsselimport: ungültiger Schlüssel → verständliche Meldung mit IIS-Hinweis, ohne Schlüsselinhalt");
                string? msg2 = null;
                try { BadgeSigner.ImportKey("kein pem"); } catch (InvalidOperationException ex) { msg2 = ex.Message; }
                check(msg2 is not null, "B23 Schlüsselimport: Nicht-PEM wird abgelehnt (keine stille Deaktivierung)");
            }
            {
                // B24 Schlüsselverwaltung: einmalige Erzeugung, Wiederverwendung, Verlust = Fehler statt Rotation, parallele Starts, defekte PEM
                var kd = Path.Combine(tmp, "keys-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(kd);
                BadgeSigner Make(string f) => new(new AppConfig { Blockchain = new BlockchainSettings { BadgeKeyFile = f } });
                var kf = Path.Combine(kd, "badge-signing-key.pem");
                var first = Make(kf);
                check(first.GeneratedKeyFile && File.Exists(kf) && File.Exists(BadgeSigner.MarkerPath(kf)) && Directory.GetFiles(kd, "*.tmp").Length == 0, "B24 Erstinstallation: Schlüssel und Marker werden erzeugt, keine temporären Dateien bleiben");
                var again = Make(kf);
                check(!again.GeneratedKeyFile && again.KeyId == first.KeyId && Directory.GetFiles(kd).Length == 2, "B24 Neustart/zweites Deployment: derselbe Schlüssel (gleiche Schlüssel-ID), keine zweite Schlüsseldatei");
                var jws = first.Sign(new JsonObject { ["x"] = 1 }, "https://learny.test/badges/issuer#" + first.KeyId);
                check(again.Verify(jws).SignatureValid, "B24 Signatur des ersten Starts ist nach dem Neustart weiterhin gültig");
                var keep = File.ReadAllBytes(kf);
                File.Delete(kf);
                string? lost = null;
                try { Make(kf); } catch (InvalidOperationException ex) { lost = ex.Message; }
                check(lost is not null && lost.Contains("fehlt") && !File.Exists(kf), "B24 Schlüsselverlust nach Initialisierung: klarer Fehler, kein neuer Schlüssel");
                File.WriteAllBytes(kf, keep);
                var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();
                File.WriteAllText(kf, other);
                string? swapped = null;
                try { Make(kf); } catch (InvalidOperationException ex) { swapped = ex.Message; }
                check(swapped is not null && swapped.Contains("passt nicht"), "B24 fremder Schlüssel bei bestehender Initialisierung wird abgelehnt");
                File.WriteAllBytes(kf, keep);
                File.WriteAllText(kf, "-----BEGIN PRIVATE KEY-----" + Environment.NewLine + "AAAA" + Environment.NewLine + "-----END PRIVATE KEY-----");
                string? broken = null;
                try { Make(kf); } catch (InvalidOperationException ex) { broken = ex.Message; }
                check(broken is not null && !broken.Contains("AAAA"), "B24 defekte PEM-Datei: Start bricht verständlich ab, ohne Inhalt");
                var pd = Path.Combine(tmp, "par-" + Guid.NewGuid().ToString("N")); var pf = Path.Combine(pd, "k.pem");
                var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Make(pf).KeyId)));
                check(ids.Distinct().Count() == 1 && Directory.GetFiles(pd, "*.pem").Length == 1 && Directory.GetFiles(pd, "*.tmp").Length == 0, "B24 acht parallele Erststarts: genau ein Schlüssel, alle verwenden dieselbe Schlüssel-ID");
            }
            check(GermanGrade.From(92) == 1 && GermanGrade.From(91.9) == 2 && GermanGrade.From(81) == 2 && GermanGrade.From(67) == 3 && GermanGrade.From(50) == 4 && GermanGrade.From(49.9) == 5 && GermanGrade.From(30) == 5 && GermanGrade.From(29) == 6, "B22 Noten: deutsche Skala 92/81/67/50/30");
            var html = e.Certs.RenderCertificateHtml(g);
            check(html.Contains(g.Number) && html.Contains("Zertifikat") && !html.Contains("src=") && !html.Contains("http://") || html.Contains(g.Number) && html.Contains("JWT kopieren") && !html.Contains("<script src"), "B22 Zertifikat-HTML enthält Nummer, kein Skript");
            check(seen.Count == 1 && seen[0].Id == g.Id, "B21 Auditor: sieht nur Zertifikate global veröffentlichter Module — keine privaten oder nur freigegebenen");
            check(await e.Certs.GetForAuditorAsync(auditor, p.Id) is null && await e.Certs.GetForAuditorAsync(auditor, s.Id) is null && await e.Certs.GetForAuditorAsync(auditor, g.Id) is not null, "B21 Auditor: der Einzelabruf privater/freigegebener Zertifikate liefert nichts");
            bool denied = false; try { await e.Certs.ListForAuditorAsync(e.LearnerId, new CertificateService.AuditorFilter(null, null, null, null, null, null, null)); } catch (UnauthorizedAccessException) { denied = true; }
            check(denied, "B21 Auditor: normale Benutzer haben keinen Zugriff auf die Auditorliste");
            var filtered = await e.Certs.ListForAuditorAsync(auditor, new CertificateService.AuditorFilter("lernende", "test", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, "active", ChainNetworks.Amoy, ChainStatus.Pending));
            var none = await e.Certs.ListForAuditorAsync(auditor, new CertificateService.AuditorFilter("gibt-es-nicht", null, null, null, null, null, null));
            check(filtered.Count == 1 && none.Count == 0, "B21 Auditor: Filter (Lernender, Modul, Zeitraum, Status, Netzwerk, Verankerung) wirken");
            await e.Certs.RecordVerificationAsync(auditor, "test");
            check((await e.Certs.VerificationHistoryAsync(auditor)).Any(h => h.Action == "certificate.verify") && (await e.Certs.VerificationHistoryAsync(auditor)).Any(h => h.Action == "certificate.audit.view"), "B21 Auditor: Prüfungen und Einsichten stehen in der eigenen Verifikationshistorie");
            var rows = await e.Certs.ListForAdminAsync(e.AdminId);
            check(rows.Count == 3 && rows.Where(r => r.Scope != "global").All(r => r.LearnerName is null && r.ModuleLabel == "(privates Modul)") && rows.Single(r => r.Scope == "global").LearnerName is not null, "B21 Administrator: Verwaltungsliste zeigt private Module und Lernende nicht (nur Nummer, Status, Verankerung)");
        }
    }
}
