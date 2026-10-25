using System.Security.Cryptography;

namespace LearnCards.Web.Services.Certificates;

/// <summary>Ein Schritt eines Merkle-Pfads: Hash des Nachbarknotens und dessen Seite.</summary>
public sealed record MerkleStep(bool SiblingIsLeft, string Hash);

/// <summary>
/// Deterministischer Merkle-Baum (SHA-256, RFC-6962-Domänentrennung) für die gebündelte Verankerung (ADR 0022).
/// Blatt = SHA256(0x00 ‖ Nachweis-Hash), innerer Knoten = SHA256(0x01 ‖ links ‖ rechts); ein ungerader letzter Knoten wird unverändert eine Ebene höher übernommen
/// (keine Duplikation — dadurch gibt es keine zwei Blattmengen mit demselben Wurzelwert). Reihenfolge der Blätter = Reihenfolge der Eingabe.
/// Der Nachweis ist ohne Learny prüfbar: Blatt-Hash und Pfad genügen, um die Wurzel (und damit den On-Chain-Anker) nachzurechnen.
/// </summary>
public static class MerkleTree
{
    public static byte[] Leaf(byte[] proofHash) => Hash(0x00, proofHash);

    public static byte[] Node(byte[] left, byte[] right) => Hash(0x01, left, right);

    private static byte[] Hash(byte prefix, params byte[][] parts)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(new[] { prefix }, 0, 1, null, 0);
        foreach (var p in parts) sha.TransformBlock(p, 0, p.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    /// <summary>Berechnet Wurzel und je Blatt den Pfad. <paramref name="proofHashes"/> sind die (bereits berechneten) SHA-256-Hashes der Nachweise.</summary>
    public static (string Root, List<List<MerkleStep>> Proofs, List<string> LeafHashes) Build(IReadOnlyList<byte[]> proofHashes)
    {
        if (proofHashes.Count == 0) throw new ArgumentException("Ein Merkle-Baum braucht mindestens ein Blatt.");
        var leaves = proofHashes.Select(Leaf).ToList();
        var level = leaves.Select((h, i) => (Hash: h, Indices: new List<int> { i })).ToList();
        var proofs = leaves.Select(_ => new List<MerkleStep>()).ToList();

        while (level.Count > 1)
        {
            var next = new List<(byte[] Hash, List<int> Indices)>();
            for (var i = 0; i < level.Count; i += 2)
            {
                if (i + 1 >= level.Count) { next.Add(level[i]); continue; }   // ungerader Knoten: unverändert nach oben
                var (l, r) = (level[i], level[i + 1]);
                foreach (var leaf in l.Indices) proofs[leaf].Add(new MerkleStep(SiblingIsLeft: false, Hex(r.Hash)));
                foreach (var leaf in r.Indices) proofs[leaf].Add(new MerkleStep(SiblingIsLeft: true, Hex(l.Hash)));
                next.Add((Node(l.Hash, r.Hash), l.Indices.Concat(r.Indices).ToList()));
            }
            level = next;
        }
        return (Hex(level[0].Hash), proofs, leaves.Select(Hex).ToList());
    }

    /// <summary>Prüft einen Pfad: rechnet vom Blatt-Hash zur Wurzel. Ungültige Hex-Werte ergeben <c>false</c>.</summary>
    public static bool Verify(string proofHashHex, IEnumerable<MerkleStep> steps, string rootHex)
    {
        try
        {
            var current = Leaf(FromHex(proofHashHex));
            foreach (var s in steps)
            {
                var sibling = FromHex(s.Hash);
                if (sibling.Length != 32) return false;
                current = s.SiblingIsLeft ? Node(sibling, current) : Node(current, sibling);
            }
            return CryptographicOperations.FixedTimeEquals(current, FromHex(rootHex));
        }
        catch (FormatException) { return false; }
    }

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public static byte[] FromHex(string hex)
    {
        var h = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (h.Length != 64) throw new FormatException("Ein SHA-256-Wert hat 64 Hexzeichen.");
        return Convert.FromHexString(h);
    }
}
