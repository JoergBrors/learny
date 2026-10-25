using System.Globalization;

namespace LearnCards.Web.Infrastructure;

/// <summary>Bekannte Netzwerke (ADR 0022). Die Chain-ID ist fest an das Netzwerk gebunden; RPC-Adressen sind konfigurierbar.</summary>
public static class ChainNetworks
{
    public const string Amoy = "polygon-amoy";
    public const string Mainnet = "polygon-mainnet";
    /// <summary>Lokale Simulation ohne Blockchain (nur Entwicklung/Tests); wird in Anzeigen deutlich als „simuliert“ gekennzeichnet.</summary>
    public const string Simulation = "simulation";

    public static int ChainId(string network) => network switch { Amoy => 80002, Mainnet => 137, _ => 0 };
    public static bool IsMainnet(string network) => network == Mainnet;
    public static string Label(string network) => network switch { Amoy => "Polygon Amoy (Testnet)", Mainnet => "Polygon PoS (Mainnet)", Simulation => "Simulation (keine Blockchain)", _ => network };
    public static string ExplorerTx(string network, string tx) => network switch
    {
        Amoy => "https://amoy.polygonscan.com/tx/" + tx,
        Mainnet => "https://polygonscan.com/tx/" + tx,
        _ => "",
    };
}

/// <summary>
/// Konfiguration der Blockchain-Verankerung (ADR 0022). Secrets (Wallet-Schlüssel) gehören nie hierher: sie liegen ausschließlich im Signierdienst
/// (<c>IAnchorSigner</c>) bzw. in einer Secret-Verwaltung. Die Mainnet-Freigabe ist niemals eine Folge der ASP.NET-Umgebung.
/// </summary>
public class BlockchainSettings
{
    /// <summary>Absolute Obergrenze der Netzwerkgebühr je Zertifikatsnachweis in EUR — nicht über Konfiguration oder UI überschreitbar.</summary>
    public const decimal HardMaxCostEur = 0.001m;

    public bool Enabled { get; set; } = true;
    public string Network { get; set; } = ChainNetworks.Amoy;
    public bool AutoAnchor { get; set; } = true;
    public decimal MaxCostEur { get; set; } = HardMaxCostEur;
    /// <summary>Umgebungsseitige Voraussetzung für Mainnet. Zusätzlich ist eine ausdrückliche administrative Freigabe erforderlich.</summary>
    public bool MainnetEnabled { get; set; }
    public bool BatchEnabled { get; set; } = true;
    public int BatchMaxSize { get; set; } = 100;
    public int BatchMaxWaitMinutes { get; set; } = 60;
    public string RpcUrlAmoy { get; set; } = "https://rpc-amoy.polygon.technology";
    public string RpcUrlMainnet { get; set; } = "https://polygon-rpc.com";
    /// <summary>Öffentliche Adresse der technischen Wallet (kein Geheimnis). Wird für die Verifikation (Absender der Anker-Transaktion) benötigt.</summary>
    public string WalletAddress { get; set; } = "";
    public int Confirmations { get; set; } = 30;
    public string PriceUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=polygon-ecosystem-token&vs_currencies=eur";
    public string PriceJsonPath { get; set; } = "polygon-ecosystem-token.eur";
    public int PriceMaxAgeMinutes { get; set; } = 15;
    /// <summary>Aufschlag auf den Gaspreis (Obergrenze der Gebühr je Gas) — Schutz vor Preisbewegungen zwischen Schätzung und Aufnahme.</summary>
    public decimal GasSafetyFactor { get; set; } = 1.5m;
    /// <summary>Aufschlag auf den POL/EUR-Kurs — Schutz vor Kursbewegungen zwischen Berechnung und Abrechnung.</summary>
    public decimal RateSafetyFactor { get; set; } = 1.25m;
    public int HttpTimeoutSeconds { get; set; } = 15;
    /// <summary>Lokale Simulation der Blockchain (kein Netzwerkzugriff). Nur für Entwicklung/Tests; nie gleichzeitig mit Mainnet.</summary>
    public bool Simulate { get; set; }
    /// <summary>Testsignierer (nur Tests gegen einen lokalen Mock-RPC; nie mit öffentlichen RPC-Hosts).</summary>
    public bool TestSigner { get; set; }
    /// <summary>Öffentliche Basis-URL für Aussteller-ID und Verifikationslinks (z. B. https://learny.example.org).</summary>
    public string PublicBaseUrl { get; set; } = "";
    /// <summary>PKCS#8-PEM des Badge-Signaturschlüssels (ES256) oder Pfad dazu. Leer = Datei im Datenverzeichnis wird erzeugt (nur Entwicklung).</summary>
    public string BadgeKeyPem { get; set; } = "";
    public string BadgeKeyFile { get; set; } = "";

    public string RpcUrl => Network == ChainNetworks.Mainnet ? RpcUrlMainnet : RpcUrlAmoy;
    public int ChainId => ChainNetworks.ChainId(Network);

    public static BlockchainSettings Load(string dataDir)
    {
        string E(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v.Trim() : fallback;
        bool B(string key, bool fallback) => E(key) is { Length: > 0 } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" : fallback;
        int I(string key, int fallback) => int.TryParse(E(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        decimal D(string key, decimal fallback) => decimal.TryParse(E(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        return new BlockchainSettings
        {
            Enabled = B("BLOCKCHAIN_ENABLED", true),
            Network = E("BLOCKCHAIN_NETWORK", ChainNetworks.Amoy).ToLowerInvariant(),
            AutoAnchor = B("BLOCKCHAIN_AUTO_ANCHOR", true),
            MaxCostEur = D("BLOCKCHAIN_MAX_COST_EUR", HardMaxCostEur),
            MainnetEnabled = B("BLOCKCHAIN_MAINNET_ENABLED", false),
            BatchEnabled = B("BLOCKCHAIN_BATCH_ENABLED", true),
            BatchMaxSize = I("BLOCKCHAIN_BATCH_MAX_SIZE", 100),
            BatchMaxWaitMinutes = I("BLOCKCHAIN_BATCH_MAX_WAIT_MINUTES", 60),
            RpcUrlAmoy = E("BLOCKCHAIN_RPC_URL_AMOY", "https://rpc-amoy.polygon.technology").TrimEnd('/'),
            RpcUrlMainnet = E("BLOCKCHAIN_RPC_URL_MAINNET", "https://polygon-rpc.com").TrimEnd('/'),
            WalletAddress = E("BLOCKCHAIN_WALLET_ADDRESS"),
            Confirmations = I("BLOCKCHAIN_CONFIRMATIONS", 30),
            PriceUrl = E("BLOCKCHAIN_PRICE_URL", "https://api.coingecko.com/api/v3/simple/price?ids=polygon-ecosystem-token&vs_currencies=eur"),
            PriceJsonPath = E("BLOCKCHAIN_PRICE_JSON_PATH", "polygon-ecosystem-token.eur"),
            PriceMaxAgeMinutes = I("BLOCKCHAIN_PRICE_MAX_AGE_MINUTES", 15),
            GasSafetyFactor = D("BLOCKCHAIN_GAS_SAFETY_FACTOR", 1.5m),
            RateSafetyFactor = D("BLOCKCHAIN_RATE_SAFETY_FACTOR", 1.25m),
            HttpTimeoutSeconds = I("BLOCKCHAIN_HTTP_TIMEOUT_SECONDS", 15),
            Simulate = B("BLOCKCHAIN_SIMULATE", false),
            TestSigner = B("BLOCKCHAIN_TEST_SIGNER", false),
            PublicBaseUrl = E("PUBLIC_BASE_URL").TrimEnd('/'),
            BadgeKeyPem = E("BADGE_SIGNING_KEY_PEM"),
            BadgeKeyFile = E("BADGE_SIGNING_KEY_FILE", Path.Combine(dataDir, "badge-signing-key.pem")),
        };
    }

    /// <summary>Alle Konfigurationsfehler (leer = gültig). Wird beim Start geprüft; das Kostenlimit kann nie über 0,001 EUR liegen.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Network is not (ChainNetworks.Amoy or ChainNetworks.Mainnet)) errors.Add($"BLOCKCHAIN_NETWORK='{Network}' ist ungültig. Erlaubt: {ChainNetworks.Amoy}, {ChainNetworks.Mainnet}.");
        if (MaxCostEur <= 0 || MaxCostEur > HardMaxCostEur) errors.Add($"BLOCKCHAIN_MAX_COST_EUR muss größer 0 und höchstens {HardMaxCostEur.ToString(CultureInfo.InvariantCulture)} EUR sein (harte Obergrenze).");
        if (BatchMaxSize is < 1 or > 1000) errors.Add("BLOCKCHAIN_BATCH_MAX_SIZE muss zwischen 1 und 1000 liegen.");
        if (BatchMaxWaitMinutes is < 1 or > 10080) errors.Add("BLOCKCHAIN_BATCH_MAX_WAIT_MINUTES muss zwischen 1 und 10080 liegen.");
        if (Confirmations is < 1 or > 500) errors.Add("BLOCKCHAIN_CONFIRMATIONS muss zwischen 1 und 500 liegen.");
        if (PriceMaxAgeMinutes is < 1 or > 1440) errors.Add("BLOCKCHAIN_PRICE_MAX_AGE_MINUTES muss zwischen 1 und 1440 liegen.");
        if (GasSafetyFactor < 1m || GasSafetyFactor > 10m || RateSafetyFactor < 1m || RateSafetyFactor > 10m) errors.Add("Die Sicherheitsfaktoren müssen zwischen 1 und 10 liegen (kein Abschlag erlaubt).");
        if (Network == ChainNetworks.Mainnet && !MainnetEnabled) errors.Add("BLOCKCHAIN_NETWORK=polygon-mainnet erfordert zusätzlich BLOCKCHAIN_MAINNET_ENABLED=true (und die administrative Freigabe in der Anwendung).");
        if (Simulate && Network == ChainNetworks.Mainnet) errors.Add("BLOCKCHAIN_SIMULATE ist mit Mainnet nicht zulässig.");
        if (TestSigner && Network == ChainNetworks.Mainnet && !IsLoopback(RpcUrlMainnet)) errors.Add("BLOCKCHAIN_TEST_SIGNER ist nur mit einem lokalen RPC-Mock zulässig.");
        if (TestSigner && Network == ChainNetworks.Amoy && !IsLoopback(RpcUrlAmoy)) errors.Add("BLOCKCHAIN_TEST_SIGNER ist nur mit einem lokalen RPC-Mock zulässig.");
        if (WalletAddress.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(WalletAddress, "^0x[0-9a-fA-F]{40}$")) errors.Add("BLOCKCHAIN_WALLET_ADDRESS ist keine gültige Adresse (0x + 40 Hexzeichen).");
        foreach (var (name, url) in new[] { ("BLOCKCHAIN_RPC_URL_AMOY", RpcUrlAmoy), ("BLOCKCHAIN_RPC_URL_MAINNET", RpcUrlMainnet), ("BLOCKCHAIN_PRICE_URL", PriceUrl) })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && IsLoopback(url))) || !string.IsNullOrEmpty(u.UserInfo))
                errors.Add($"{name} muss eine https-Adresse ohne Zugangsdaten sein (http nur für localhost-Tests).");
        }
        return errors;
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Ungültige Blockchain-Konfiguration:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
    }

    private static bool IsLoopback(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback);
}
