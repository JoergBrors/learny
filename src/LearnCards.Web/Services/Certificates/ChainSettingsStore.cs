using System.Globalization;
using System.Text.Json;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.Certificates;

public sealed record MainnetApproval(string By, DateTime At, string Network, string Wallet);

/// <summary>Wirksame Blockchain-Einstellungen: Umgebungskonfiguration, die ein Administrator nur <b>einschränken</b> (oder innerhalb der Grenzen anpassen) kann.</summary>
public sealed record EffectiveChainSettings(
    bool Enabled, bool AutoAnchor, bool BatchEnabled, int BatchMaxSize, int BatchMaxWaitMinutes, decimal MaxCostEur,
    string Network, int ChainId, bool Simulate, bool MainnetEnvEnabled, MainnetApproval? Approval)
{
    public bool IsMainnet => Network == ChainNetworks.Mainnet;
    /// <summary>Mainnet darf nur senden, wenn die Umgebung es erlaubt UND ein Administrator es ausdrücklich freigegeben hat.</summary>
    public bool MainnetUnlocked => IsMainnet && MainnetEnvEnabled && Approval is not null && Approval.Network == ChainNetworks.Mainnet;
}

/// <summary>Administrativ änderbare Einstellungen in <c>app_settings</c> (nie Geheimnisse). Grenzen: Kostenlimit höchstens Umgebungswert und höchstens 0,001 EUR; Mainnet nur mit Umgebungsfreigabe plus ausdrücklicher Freigabe.</summary>
public sealed class ChainSettingsStore
{
    public const string ApprovalKey = "chain.mainnet_approval";
    private readonly IDatabase _db;
    private readonly BlockchainSettings _cfg;

    public ChainSettingsStore(IDatabase db, AppConfig config) { _db = db; _cfg = config.Blockchain; }

    public BlockchainSettings Config => _cfg;

    private async Task<Dictionary<string, string>> AllAsync() =>
        (await _db.QueryAsync("SELECT name, value FROM app_settings WHERE name LIKE 'chain.%'")).ToDictionary(r => r["name"] ?? "", r => r["value"] ?? "");

    public async Task<EffectiveChainSettings> GetAsync()
    {
        var o = await AllAsync();
        bool B(string k, bool d) => o.TryGetValue(k, out var v) ? v == "true" : d;
        int I(string k, int d, int min, int max) => o.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : d;
        var maxCost = _cfg.MaxCostEur;
        if (o.TryGetValue("chain.max_cost_eur", out var mc) && decimal.TryParse(mc, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) maxCost = Math.Min(Math.Max(d, 0.0000001m), _cfg.MaxCostEur);
        maxCost = Math.Min(maxCost, BlockchainSettings.HardMaxCostEur);
        MainnetApproval? approval = null;
        if (o.TryGetValue(ApprovalKey, out var ap))
        {
            try { approval = JsonSerializer.Deserialize<MainnetApproval>(ap); } catch (JsonException) { }
            // Freigabe gilt nur für dieselbe Wallet — ein Wallet-Wechsel verlangt eine neue ausdrückliche Freigabe
            if (approval is not null && !string.Equals(approval.Wallet, _cfg.WalletAddress, StringComparison.OrdinalIgnoreCase)) approval = null;
        }
        return new EffectiveChainSettings(
            Enabled: _cfg.Enabled && B("chain.enabled", true),
            AutoAnchor: _cfg.AutoAnchor && B("chain.auto_anchor", true),
            BatchEnabled: _cfg.BatchEnabled,
            BatchMaxSize: I("chain.batch_max_size", _cfg.BatchMaxSize, 1, _cfg.BatchMaxSize),   // Admin darf nur verkleinern
            BatchMaxWaitMinutes: I("chain.batch_max_wait_minutes", _cfg.BatchMaxWaitMinutes, 1, 10080),
            MaxCostEur: maxCost,
            Network: _cfg.Simulate ? ChainNetworks.Simulation : _cfg.Network,
            ChainId: _cfg.ChainId,
            Simulate: _cfg.Simulate,
            MainnetEnvEnabled: _cfg.MainnetEnabled,
            Approval: approval);
    }

    public async Task SetAsync(string name, string value, string? userId)
    {
        var exists = await _db.QueryAsync("SELECT 1 FROM app_settings WHERE name = @n", new[] { ("n", (object?)name) });
        if (exists.Count > 0)
            await _db.ExecuteAsync("UPDATE app_settings SET value = @v, updated_at = @t, updated_by = @u WHERE name = @n", new (string, object?)[] { ("v", value), ("t", DateTime.UtcNow), ("u", userId), ("n", name) });
        else
            await _db.ExecuteAsync("INSERT INTO app_settings (name, value, updated_at, updated_by) VALUES (@n, @v, @t, @u)", new (string, object?)[] { ("n", name), ("v", value), ("t", DateTime.UtcNow), ("u", userId) });
    }

    public Task RemoveAsync(string name) => _db.ExecuteAsync("DELETE FROM app_settings WHERE name = @n", new[] { ("n", (object?)name) });

    /// <summary>Administrative Mainnet-Freigabe — ein eigener Betriebsakt (bewusste Bestätigung durch den Aufrufer), nie automatisch.</summary>
    public async Task ApproveMainnetAsync(string adminId)
    {
        if (_cfg.Network != ChainNetworks.Mainnet || !_cfg.MainnetEnabled) throw new InvalidOperationException("Mainnet ist in der Umgebung nicht freigegeben (BLOCKCHAIN_NETWORK=polygon-mainnet und BLOCKCHAIN_MAINNET_ENABLED=true).");
        if (_cfg.Simulate) throw new InvalidOperationException("Die Simulation ist aktiv; Mainnet kann nicht freigegeben werden.");
        if (string.IsNullOrEmpty(_cfg.WalletAddress)) throw new InvalidOperationException("Es ist keine Wallet-Adresse konfiguriert (BLOCKCHAIN_WALLET_ADDRESS).");
        await SetAsync(ApprovalKey, JsonSerializer.Serialize(new MainnetApproval(adminId, DateTime.UtcNow, ChainNetworks.Mainnet, _cfg.WalletAddress)), adminId);
    }

    public Task RevokeMainnetApprovalAsync() => RemoveAsync(ApprovalKey);
}
