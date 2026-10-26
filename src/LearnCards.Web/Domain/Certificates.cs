namespace LearnCards.Web.Domain;

/// <summary>Status der Blockchain-Verankerung eines Zertifikatsnachweises (ADR 0022). „none“ = Verankerung ausgeschaltet; das signierte Badge existiert unabhängig davon.</summary>
public static class ChainStatus
{
    public const string None = "none";
    public const string Pending = "pending";
    public const string Batched = "batched";
    public const string CostBlocked = "cost_blocked";
    public const string Anchoring = "anchoring";
    public const string Confirmed = "confirmed";
    public const string Failed = "failed";

    public static string Label(string status, bool revoked = false) => revoked ? "Widerrufen" : status switch
    {
        Pending => "Verankerung ausstehend",
        Batched => "Im Batch eingeplant",
        CostBlocked => "Kostenlimit überschritten",
        Anchoring => "Verankerung läuft",
        Confirmed => "Blockchain-bestätigt",
        Failed => "Verankerung fehlgeschlagen",
        _ => "Signiert (ohne Blockchain)",
    };
}

public static class BatchStatus
{
    public const string Planned = "planned";
    public const string CostBlocked = "cost_blocked";
    public const string Submitted = "submitted";
    public const string Confirmed = "confirmed";
    public const string Failed = "failed";
}

/// <summary>Deutsche Notenskala (IHK-Schlüssel) aus dem Ergebnis in Prozent: 100–92 → 1, 91–81 → 2, 80–67 → 3, 66–50 → 4, 49–30 → 5, darunter 6.</summary>
public static class GermanGrade
{
    public static int From(double percent) => percent >= 92 ? 1 : percent >= 81 ? 2 : percent >= 67 ? 3 : percent >= 50 ? 4 : percent >= 30 ? 5 : 6;
    public static string Label(int grade) => grade switch { 1 => "sehr gut", 2 => "gut", 3 => "befriedigend", 4 => "ausreichend", 5 => "mangelhaft", _ => "ungenügend" };
    /// <summary>Kurzform, z. B. „Note 2 (gut)“.</summary>
    public static string Text(double percent) { var g = From(percent); return $"Note {g} ({Label(g)})"; }
}

/// <summary>Zertifikat über den erfolgreichen Abschluss eines Lernmoduls (ADR 0021). Enthält das signierte Open Badge 3.0 (VC-JWT) und den Verankerungsstatus.</summary>
public class Certificate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Menschenlesbare Zertifikatsnummer (z. B. LC-2026-7K3Q9XPD).</summary>
    public string Number { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
    /// <summary>Globale Modulversion; 0 bei privaten und freigegebenen Modulen.</summary>
    public int ModuleVersion { get; set; }
    /// <summary>private | shared | global — Kontext, in dem das Modul abgeschlossen wurde.</summary>
    public string ModuleScope { get; set; } = "private";
    public string? ResultId { get; set; }
    public string LearnerName { get; set; } = "";
    public double ScorePercent { get; set; }
    public int PassPercent { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public string BadgeJws { get; set; } = "";
    /// <summary>SHA-256 (Hex) der kompakten JWS — der „Prüfnachweis“ des Badges.</summary>
    public string BadgeHash { get; set; } = "";
    /// <summary>Merkle-Blatt (Hex): SHA256(0x00 ‖ BadgeHash).</summary>
    public string LeafHash { get; set; } = "";
    /// <summary>active | revoked</summary>
    public string Status { get; set; } = "active";
    public DateTime? RevokedAt { get; set; }
    public string RevokedReason { get; set; } = "";
    public string? RevokedBy { get; set; }
    public string ChainStatus { get; set; } = Domain.ChainStatus.None;
    public string ChainNetwork { get; set; } = "";
    public int ChainId { get; set; }
    public string? BatchId { get; set; }
    public string? MerkleProofJson { get; set; }
    public string ChainError { get; set; } = "";
    public int ChainAttempts { get; set; }
    public DateTime? ChainUpdatedAt { get; set; }
    public DateTime? AnchoredAt { get; set; }
    /// <summary>Unveränderlicher Prüfungssnapshot des Abschlusses (ADR 0031); null bei Altzertifikaten.</summary>
    public string? SnapshotId { get; set; }
    public string SnapshotHash { get; set; } = "";
    /// <summary><c>module</c> (Modulabschluss) oder <c>topic</c> (Themenabschluss: <c>BadgeJws</c> = Master-Badge, <c>ClrJws</c> = CLR; <c>ModuleId</c>/<c>ModuleName</c>/<c>ModuleVersion</c> = Thema).</summary>
    public string Kind { get; set; } = "module";
    /// <summary>Signiertes ClrCredential (VC-JWT) — nur bei Themenabschlüssen.</summary>
    public string? ClrJws { get; set; }
    public string ClrHash { get; set; } = "";
    /// <summary>Tatsächlicher Abschlusszeitpunkt (nicht Ausstellung, nicht letzte Lernaktivität); null bei Altzertifikaten (dann gilt <c>IssuedAt</c>).</summary>
    public DateTime? CompletedAt { get; set; }
    public bool IsTopic => Kind == "topic";

    public bool IsRevoked => Status == "revoked";
    /// <summary>Note nach deutscher Skala (1–6), berechnet aus dem Ergebnis in Prozent.</summary>
    public int Grade => GermanGrade.From(ScorePercent);
}

public class AnchorBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Network { get; set; } = "";
    public int ChainId { get; set; }
    public string MerkleRoot { get; set; } = "";
    public int LeafCount { get; set; }
    public string Status { get; set; } = BatchStatus.Planned;
    public string? TxHash { get; set; }
    public string? Nonce { get; set; }
    public string? RawTx { get; set; }
    public string? BlockNumber { get; set; }
    public string? GasLimit { get; set; }
    public string? GasUsed { get; set; }
    public string? MaxFeePerGas { get; set; }
    public string? EffectiveGasPrice { get; set; }
    public string? FeeWei { get; set; }
    public double? RateEurPerPol { get; set; }
    public DateTime? RateAt { get; set; }
    public double? EstTotalEur { get; set; }
    /// <summary>Tatsächliche Netzwerkgebühr in EUR — nur Mainnet; auf Testnet/Simulation immer 0 (es fallen keine echten Kosten an).</summary>
    public double? FeeEur { get; set; }
    public double? CostPerCertEur { get; set; }
    /// <summary>Auf Testnet berechnete, hypothetische Mainnet-Kosten je Nachweis (nur Information, keine Ausgabe).</summary>
    public double? SimulatedCostEur { get; set; }
    public double? SavedEur { get; set; }
    public bool Simulated { get; set; }
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
