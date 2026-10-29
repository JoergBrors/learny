using System.Security.Cryptography;
using System.Text;

namespace LearnCards.Web.Services.Certificates.Ots;

/// <summary>Eine Attestierung am Blatt des OpenTimestamps-Baums, zusammen mit dem dort berechneten Digest.</summary>
public sealed record OtsAttestation(string Kind, long BitcoinHeight, string? CalendarUri, byte[] Message);

/// <summary>Rohe Attestierung im Baum: 8-Byte-Tag und Nutzlast (unverändert, damit Serialisierung bytegenau zurückführt).</summary>
public sealed class OtsRawAttestation
{
    public byte[] Tag { get; }
    public byte[] Payload { get; }
    public OtsRawAttestation(byte[] tag, byte[] payload) { Tag = tag; Payload = payload; }
    public bool SameAs(OtsRawAttestation o) => Tag.AsSpan().SequenceEqual(o.Tag) && Payload.AsSpan().SequenceEqual(o.Payload);
}

/// <summary>Knoten des Zeitstempelbaums (<c>Timestamp</c> im Referenzclient): Nachricht, Attestierungen und Operationen mit Kindknoten.</summary>
public sealed class OtsNode
{
    public byte[] Msg { get; }
    public List<OtsRawAttestation> Attestations { get; } = new();
    public List<(byte Tag, byte[] Arg, OtsNode Child)> Ops { get; } = new();
    public OtsNode(byte[] msg) { Msg = msg; }

    /// <summary>Fügt eine Operation an (oder liefert den vorhandenen Kindknoten bei gleicher Operation und gleichem Argument).</summary>
    public OtsNode AddOp(byte tag, byte[] arg)
    {
        foreach (var o in Ops) if (o.Tag == tag && o.Arg.AsSpan().SequenceEqual(arg)) return o.Child;
        var child = new OtsNode(OtsFile.Apply(tag, arg, Msg));
        Ops.Add((tag, arg, child));
        return child;
    }

    /// <summary>Vereinigt einen anderen Baum derselben Nachricht in diesen (Mengenvereinigung von Attestierungen und Operationen, rekursiv).</summary>
    public void Merge(OtsNode other)
    {
        if (!Msg.AsSpan().SequenceEqual(other.Msg)) throw new InvalidOperationException("Zeitstempel unterschiedlicher Nachrichten lassen sich nicht vereinigen.");
        foreach (var a in other.Attestations) if (!Attestations.Any(x => x.SameAs(a))) Attestations.Add(a);
        foreach (var (tag, arg, child) in other.Ops) AddOp(tag, arg).Merge(child);
    }
}

/// <summary>Ergebnis des Lesens einer <c>.ots</c>-Datei (Detached Timestamp File, Format Version 1).</summary>
public sealed class OtsParseResult
{
    public bool Valid { get; init; }
    public string? Error { get; init; }
    /// <summary>Wahr, wenn die Datei Operationen nutzt, die dieser Leser nicht berechnen kann (z. B. Keccak-256) — „nicht überprüfbar“, nicht „ungültig“.</summary>
    public bool Unsupported { get; init; }
    public string DigestAlgorithm { get; init; } = "";
    public byte DigestTag { get; init; }
    public byte[] FileDigest { get; init; } = Array.Empty<byte>();
    public OtsNode? Root { get; init; }
    public List<OtsAttestation> Attestations { get; init; } = new();
    public bool HasPending => Attestations.Any(a => a.Kind == "pending");
    public IEnumerable<OtsAttestation> Bitcoin => Attestations.Where(a => a.Kind == "bitcoin");
}

/// <summary>
/// Nativer Leser und Schreiber für das offizielle OpenTimestamps-Format (ADR 0040): DetachedTimestampFile Version 1, Operationen SHA-1, RIPEMD-160, SHA-256, Append, Prepend, Reverse, Hexlify,
/// Attestierungen Bitcoin, Pending (Litecoin/Ethereum/unbekannte werden erhalten, aber nicht bewertet). Die Serialisierung folgt der Referenzimplementierung (python-opentimestamps, sortierte
/// Attestierungen und Operationen), sodass ein gelesener und neu geschriebener Standardbeweis bytegleich bleibt. Alle Eingaben sind nicht vertrauenswürdig: Knoten-, Tiefen-, Operanden- und
/// Nachrichtenlängen sind begrenzt; Unbekanntes führt zu „ungültig“, nicht Berechenbares zu „nicht unterstützt“.
/// </summary>
public static class OtsFile
{
    private static readonly byte[] Magic = { 0x00, 0x4F, 0x70, 0x65, 0x6E, 0x54, 0x69, 0x6D, 0x65, 0x73, 0x74, 0x61, 0x6D, 0x70, 0x73, 0x00, 0x00, 0x50, 0x72, 0x6F, 0x6F, 0x66, 0x00, 0xBF, 0x89, 0xE2, 0xE8, 0x84, 0xE8, 0x92, 0x94 };
    public static readonly byte[] BitcoinTag = { 0x05, 0x88, 0x96, 0x0D, 0x73, 0xD7, 0x19, 0x01 };
    public static readonly byte[] PendingTag = { 0x83, 0xDF, 0xE3, 0x0D, 0x2E, 0xF9, 0x0C, 0x8E };
    private static readonly byte[] LitecoinTag = { 0x06, 0x86, 0x9A, 0x0D, 0x73, 0xD7, 0x1B, 0x45 };
    private static readonly byte[] EthereumTag = { 0x30, 0xFE, 0x80, 0x87, 0xB5, 0xC7, 0xEA, 0xD7 };
    public const byte OpSha1 = 0x02, OpRipemd160 = 0x03, OpSha256 = 0x08, OpKeccak256 = 0x67, OpAppend = 0xF0, OpPrepend = 0xF1, OpReverse = 0xF2, OpHexlify = 0xF3;   // DevSkim: ignore DS126858,DS109733 – nur zum Nachrechnen standardkonformer/älterer OTS-Beweise (ADR 0040), nie für neue Signaturen oder Hashes
    private const int MaxNodes = 20_000, MaxDepth = 256, MaxPayload = 8192, MaxOperand = 4096, MaxMessage = 4096, MaxFile = 1_000_000;

    /// <summary>Baut die Kopfzeile einer OTS-Datei für einen SHA-256-Digest.</summary>
    public static byte[] Header(byte[] sha256Digest) => Magic.Concat(new byte[] { 0x01, OpSha256 }).Concat(sha256Digest).ToArray();

    private sealed class Reader
    {
        private readonly byte[] _d; public int Pos; public int Nodes;
        public Reader(byte[] d) { _d = d; }
        public bool End => Pos >= _d.Length;
        public byte Byte() => Pos < _d.Length ? _d[Pos++] : throw new FormatException("Unerwartetes Dateiende.");
        public byte[] Bytes(int n) { if (n < 0 || Pos + n > _d.Length) throw new FormatException("Unerwartetes Dateiende."); var r = _d[Pos..(Pos + n)]; Pos += n; return r; }
        public long VarUInt() { long v = 0; var shift = 0; while (true) { var b = Byte(); v |= (long)(b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; if (shift > 56) throw new FormatException("Ungültige Länge."); } }
        public byte[] VarBytes(int max) { var n = VarUInt(); if (n > max) throw new FormatException("Operand zu groß."); return Bytes((int)n); }
    }

    // ─── Operationen ───

    /// <summary>Wendet eine Operation auf eine Nachricht an. Nicht berechenbare Operationen → <see cref="NotSupportedException"/>, unbekannte → <see cref="FormatException"/>.</summary>
    public static byte[] Apply(byte tag, byte[] arg, byte[] msg)
    {
        var r = tag switch
        {
            OpSha256 => SHA256.HashData(msg),
            OpSha1 => SHA1.HashData(msg),   // DevSkim: ignore DS126858,DS109733 – nur zum Nachrechnen standardkonformer/älterer OTS-Beweise (ADR 0040), nie für neue Signaturen oder Hashes
            OpRipemd160 => Ripemd160.Hash(msg),   // DevSkim: ignore DS126858,DS109733 – nur zum Nachrechnen standardkonformer/älterer OTS-Beweise (ADR 0040), nie für neue Signaturen oder Hashes
            OpKeccak256 => throw new NotSupportedException("Keccak-256 wird von diesem Leser nicht berechnet; mit dem offiziellen ots-Client prüfen."),
            OpAppend => msg.Concat(arg).ToArray(),
            OpPrepend => arg.Concat(msg).ToArray(),
            OpReverse => msg.Reverse().ToArray(),
            OpHexlify => Encoding.ASCII.GetBytes(Convert.ToHexString(msg).ToLowerInvariant()),
            _ => throw new FormatException($"Unbekannte Operation 0x{tag:x2}."),
        };
        if (r.Length > MaxMessage) throw new FormatException("Nachricht zu lang.");
        if (r.Length == 0) throw new FormatException("Leere Nachricht.");
        return r;
    }

    private static bool HasArg(byte tag) => tag is OpAppend or OpPrepend;

    // ─── Lesen ───

    public static OtsParseResult Parse(byte[] data)
    {
        try
        {
            if (data.Length > MaxFile) return Fail("Datei zu groß.");
            if (data.Length < Magic.Length + 3 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return Fail("Keine OpenTimestamps-Datei (Magic fehlt).");
            var r = new Reader(data) { Pos = Magic.Length };
            if (r.VarUInt() != 1) return Fail("Nicht unterstützte OTS-Version.");
            var opTag = r.Byte();
            var (alg, len) = opTag switch { OpSha256 => ("SHA256", 32), OpSha1 => ("SHA1", 20), OpRipemd160 => ("RIPEMD160", 20), OpKeccak256 => ("KECCAK256", 32), _ => throw new FormatException("Unbekannter Digest-Typ.") };   // DevSkim: ignore DS126858,DS109733 – nur zum Nachrechnen standardkonformer/älterer OTS-Beweise (ADR 0040), nie für neue Signaturen oder Hashes
            var digest = r.Bytes(len);
            var root = new OtsNode(digest);
            ReadTimestamp(r, root, 0);
            if (!r.End) return Fail("Unerwartete Daten nach dem Zeitstempel.");
            return new OtsParseResult { Valid = true, DigestAlgorithm = alg, DigestTag = opTag, FileDigest = digest, Root = root, Attestations = Leaves(root) };
        }
        catch (FormatException e) { return Fail(e.Message); }
        catch (NotSupportedException e) { return new OtsParseResult { Valid = false, Error = e.Message, Unsupported = true }; }
    }

    /// <summary>Liest einen Zeitstempelbaum ohne Dateikopf (Antwort eines Calendars) für die gegebene Nachricht; die Eingabe muss vollständig verbraucht werden.</summary>
    public static OtsNode ParseTimestamp(byte[] data, byte[] msg)
    {
        if (data.Length > MaxFile) throw new FormatException("Antwort zu groß.");
        var r = new Reader(data); var root = new OtsNode(msg);
        ReadTimestamp(r, root, 0);
        if (!r.End) throw new FormatException("Unerwartete Daten nach dem Zeitstempel.");
        return root;
    }

    private static OtsParseResult Fail(string e) => new() { Valid = false, Error = e };

    private static void ReadTimestamp(Reader r, OtsNode node, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Zeitstempelbaum zu tief.");
        var tag = r.Byte();
        while (tag == 0xFF) { ReadItem(r, r.Byte(), node, depth); tag = r.Byte(); }
        ReadItem(r, tag, node, depth);
    }

    private static void ReadItem(Reader r, byte tag, OtsNode node, int depth)
    {
        if (++r.Nodes > MaxNodes) throw new FormatException("Zeitstempelbaum zu groß.");
        if (tag == 0x00)
        {
            var kind = r.Bytes(8); var payload = r.VarBytes(MaxPayload);
            var att = new OtsRawAttestation(kind, payload);
            if (!node.Attestations.Any(a => a.SameAs(att))) node.Attestations.Add(att);
            return;
        }
        var arg = HasArg(tag) ? r.VarBytes(MaxOperand) : Array.Empty<byte>();
        if (HasArg(tag) && arg.Length == 0) throw new FormatException("Leerer Operand.");
        var child = node.AddOp(tag, arg);
        ReadTimestamp(r, child, depth + 1);
    }

    /// <summary>Alle Blätter (Attestierungen) mit dem dort erreichten Digest, in Baumreihenfolge.</summary>
    public static List<OtsAttestation> Leaves(OtsNode root)
    {
        var list = new List<OtsAttestation>();
        void Walk(OtsNode n)
        {
            foreach (var a in n.Attestations) list.Add(Interpret(a, n.Msg));
            foreach (var o in n.Ops) Walk(o.Child);
        }
        Walk(root);
        return list;
    }

    private static OtsAttestation Interpret(OtsRawAttestation a, byte[] msg)
    {
        try
        {
            if (a.Tag.AsSpan().SequenceEqual(BitcoinTag)) return new("bitcoin", new Reader(a.Payload).VarUInt(), null, msg);
            if (a.Tag.AsSpan().SequenceEqual(PendingTag)) return new("pending", 0, Encoding.UTF8.GetString(new Reader(a.Payload).VarBytes(1000)), msg);
        }
        catch (FormatException) { return new("unknown", 0, null, msg); }
        if (a.Tag.AsSpan().SequenceEqual(LitecoinTag)) return new("litecoin", 0, null, msg);
        if (a.Tag.AsSpan().SequenceEqual(EthereumTag)) return new("ethereum", 0, null, msg);
        return new("unknown", 0, null, msg);
    }

    /// <summary>Pending-Blätter mit Knoten, an die ein Calendar-Upgrade anzuhängen ist.</summary>
    public static List<(OtsNode Node, string Uri)> PendingLeaves(OtsNode root)
    {
        var list = new List<(OtsNode, string)>();
        void Walk(OtsNode n)
        {
            foreach (var a in n.Attestations)
                if (a.Tag.AsSpan().SequenceEqual(PendingTag))
                    try { list.Add((n, Encoding.UTF8.GetString(new Reader(a.Payload).VarBytes(1000)))); } catch (FormatException) { }
            foreach (var o in n.Ops) Walk(o.Child);
        }
        Walk(root);
        return list;
    }

    // ─── Schreiben ───

    private static void WriteVarUInt(Stream s, long v) { while (v >= 0x80) { s.WriteByte((byte)((v & 0x7F) | 0x80)); v >>= 7; } s.WriteByte((byte)v); }
    private static void WriteVarBytes(Stream s, byte[] b) { WriteVarUInt(s, b.Length); s.Write(b); }

    private static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    private static int CompareAttestations(OtsRawAttestation a, OtsRawAttestation b)
    {
        var c = Compare(a.Tag, b.Tag);
        if (c != 0) return c;
        if (a.Tag.AsSpan().SequenceEqual(BitcoinTag)) { try { return new Reader(a.Payload).VarUInt().CompareTo(new Reader(b.Payload).VarUInt()); } catch (FormatException) { } }
        if (a.Tag.AsSpan().SequenceEqual(PendingTag)) { try { return string.CompareOrdinal(Encoding.UTF8.GetString(new Reader(a.Payload).VarBytes(1000)), Encoding.UTF8.GetString(new Reader(b.Payload).VarBytes(1000))); } catch (FormatException) { } }
        return Compare(a.Payload, b.Payload);
    }

    private static void WriteTimestamp(Stream s, OtsNode n)
    {
        var atts = n.Attestations.OrderBy(a => a, Comparer<OtsRawAttestation>.Create(CompareAttestations)).ToList();
        var ops = n.Ops.OrderBy(o => o.Tag).ThenBy(o => o.Arg, Comparer<byte[]>.Create(Compare)).ToList();
        if (atts.Count == 0 && ops.Count == 0) throw new InvalidOperationException("Ein leerer Zeitstempel lässt sich nicht serialisieren.");
        void Att(OtsRawAttestation a) { s.WriteByte(0x00); s.Write(a.Tag); WriteVarBytes(s, a.Payload); }
        if (atts.Count > 1) foreach (var a in atts.Take(atts.Count - 1)) { s.WriteByte(0xFF); Att(a); }
        if (ops.Count == 0) { Att(atts[^1]); return; }
        if (atts.Count > 0) { s.WriteByte(0xFF); Att(atts[^1]); }
        for (var i = 0; i < ops.Count; i++)
        {
            if (i < ops.Count - 1) s.WriteByte(0xFF);
            s.WriteByte(ops[i].Tag);
            if (HasArg(ops[i].Tag)) WriteVarBytes(s, ops[i].Arg);
            WriteTimestamp(s, ops[i].Child);
        }
    }

    /// <summary>Serialisiert einen Zeitstempelbaum ohne Dateikopf (Calendar-Antwortformat).</summary>
    public static byte[] SerializeTimestamp(OtsNode root) { using var ms = new MemoryStream(); WriteTimestamp(ms, root); return ms.ToArray(); }

    /// <summary>Serialisiert eine vollständige <c>.ots</c>-Datei (Kopf, Datei-Digest, Baum).</summary>
    public static byte[] Serialize(byte digestTag, byte[] digest, OtsNode root)
    {
        using var ms = new MemoryStream();
        ms.Write(Magic); WriteVarUInt(ms, 1); ms.WriteByte(digestTag); ms.Write(digest);
        WriteTimestamp(ms, root);
        return ms.ToArray();
    }
}

/// <summary>RIPEMD-160 (in .NET nicht enthalten) — nur zum Nachrechnen älterer OpenTimestamps-Beweise.</summary>
public static class Ripemd160   // DevSkim: ignore DS126858,DS109733 – nur zum Nachrechnen standardkonformer/älterer OTS-Beweise (ADR 0040), nie für neue Signaturen oder Hashes
{
    private static readonly int[] RL = { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15, 7,4,13,1,10,6,15,3,12,0,9,5,2,14,11,8, 3,10,14,4,9,15,8,1,2,7,0,6,13,11,5,12, 1,9,11,10,0,8,12,4,13,3,7,15,14,5,6,2, 4,0,5,9,7,12,2,10,14,1,3,8,11,6,15,13 };
    private static readonly int[] RR = { 5,14,7,0,9,2,11,4,13,6,15,8,1,10,3,12, 6,11,3,7,0,13,5,10,14,15,8,12,4,9,1,2, 15,5,1,3,7,14,6,9,11,8,12,2,10,0,4,13, 8,6,4,1,3,11,15,0,5,12,2,13,9,7,10,14, 12,15,10,4,1,5,8,7,6,2,13,14,0,3,9,11 };
    private static readonly int[] SL = { 11,14,15,12,5,8,7,9,11,13,14,15,6,7,9,8, 7,6,8,13,11,9,7,15,7,12,15,9,11,7,13,12, 11,13,6,7,14,9,13,15,14,8,13,6,5,12,7,5, 11,12,14,15,14,15,9,8,9,14,5,6,8,6,5,12, 9,15,5,11,6,8,13,12,5,12,13,14,11,8,5,6 };
    private static readonly int[] SR = { 8,9,9,11,13,15,15,5,7,7,8,11,14,14,12,6, 9,13,15,7,12,8,9,11,7,7,12,7,6,15,13,11, 9,7,15,11,8,6,6,14,12,13,5,14,13,13,7,5, 15,5,8,11,14,14,6,14,6,9,12,9,12,5,15,8, 8,5,12,9,12,5,14,6,8,13,6,5,15,13,11,11 };
    private static readonly uint[] KL = { 0, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E };
    private static readonly uint[] KR = { 0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0 };
    private static uint Rol(uint x, int n) => (x << n) | (x >> (32 - n));
    private static uint F(int j, uint x, uint y, uint z) => j switch { < 16 => x ^ y ^ z, < 32 => (x & y) | (~x & z), < 48 => (x | ~y) ^ z, < 64 => (x & z) | (y & ~z), _ => x ^ (y | ~z) };

    public static byte[] Hash(byte[] data)
    {
        var len = data.Length; var padded = new byte[((len + 8) / 64 + 1) * 64];
        Array.Copy(data, padded, len); padded[len] = 0x80;
        BitConverter.GetBytes((ulong)len * 8).CopyTo(padded, padded.Length - 8);
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
        var x = new uint[16];
        for (var off = 0; off < padded.Length; off += 64)
        {
            for (var i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(padded, off + i * 4);
            uint a = h0, b = h1, c = h2, d = h3, e = h4, a2 = h0, b2 = h1, c2 = h2, d2 = h3, e2 = h4;
            for (var j = 0; j < 80; j++)
            {
                var t = Rol(a + F(j, b, c, d) + x[RL[j]] + KL[j / 16], SL[j]) + e; a = e; e = d; d = Rol(c, 10); c = b; b = t;
                t = Rol(a2 + F(79 - j, b2, c2, d2) + x[RR[j]] + KR[j / 16], SR[j]) + e2; a2 = e2; e2 = d2; d2 = Rol(c2, 10); c2 = b2; b2 = t;
            }
            var tmp = h1 + c + d2; h1 = h2 + d + e2; h2 = h3 + e + a2; h3 = h4 + a + b2; h4 = h0 + b + c2; h0 = tmp;
        }
        var res = new byte[20];
        BitConverter.GetBytes(h0).CopyTo(res, 0); BitConverter.GetBytes(h1).CopyTo(res, 4); BitConverter.GetBytes(h2).CopyTo(res, 8); BitConverter.GetBytes(h3).CopyTo(res, 12); BitConverter.GetBytes(h4).CopyTo(res, 16);
        return res;
    }
}
