using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services.AvatarGallery;

/// <summary>Fehler beim Lesen/Schreiben eines Archivs mit einer für Benutzer geeigneten Meldung (nie Pfade, Passwörter oder Inhalte).</summary>
public class ArchiveException : Exception
{
    public ArchiveException(string userMessage) : base(userMessage) { }
}

/// <summary>Das Archiv ist verschlüsselt, aber das Passwort fehlt oder ist falsch (oder die Daten sind beschädigt, was nicht unterscheidbar ist).</summary>
public sealed class ArchivePasswordException : ArchiveException
{
    public bool Missing { get; }
    public ArchivePasswordException(bool missing) : base(missing ? "Das Archiv ist passwortgeschützt. Bitte gib das Passwort ein." : "Das Passwort ist falsch oder das Archiv ist beschädigt. Es wurde nichts importiert.") => Missing = missing;
}

/// <summary>Eine Datei im Archiv (Name im Archiv, Inhalt).</summary>
public sealed record ZipFileData(string Name, byte[] Data);

/// <summary>
/// Minimales ZIP-Format (Speichern/Deflate) mit optionaler <b>WinZip-AES-Verschlüsselung (AE-2, AES-256)</b> — ohne Paket (ADR 0002) und ohne Prozesse.
/// Kryptografie ausschließlich aus BCL-Bausteinen nach der veröffentlichten WinZip-AES-Spezifikation: PBKDF2-HMAC-SHA1 (1000 Iterationen, durch die Spezifikation vorgegeben),
/// AES-ECB der .NET-Bibliothek für den CTR-Schlüsselstrom (Zähler little-endian ab 1), HMAC-SHA1 über den Chiffretext (10 Byte) und 2-Byte-Passwortprüfwert.
/// Die klassische ZIP-Verschlüsselung (ZipCrypto) wird weder geschrieben noch gelesen. Dateinamen und Größen sind im ZIP-Format unverschlüsselt (Eigenschaft des Formats).
/// Das Lesen erfolgt streng aus dem Arbeitsspeicher mit Grenzen gegen ZIP-Bomben; nichts wird auf das Dateisystem extrahiert.
/// </summary>
public static class SecureZip
{
    private const uint SigLocal = 0x04034b50, SigCentral = 0x02014b50, SigEocd = 0x06054b50;
    private const ushort MethodStored = 0, MethodDeflate = 8, MethodAes = 99;
    private const int AesKeyBytes = 32, AesSaltBytes = 16, AuthBytes = 10, Pbkdf2Iterations = 1000;

    // ─── Schreiben ──────────────────────────────────────────────────────

    /// <summary>Baut ein ZIP. Mit Passwort wird jede Datei AES-256-verschlüsselt (WinZip AE-2); ohne Passwort ein gewöhnliches ZIP. Das Passwort wird nicht gespeichert.</summary>
    public static byte[] Build(IReadOnlyList<ZipFileData> files, string? password, DateTime? now = null)
    {
        var (dosTime, dosDate) = ToDos(now ?? DateTime.UtcNow);
        using var ms = new MemoryStream();
        var central = new List<(byte[] Header, byte[] Name, byte[] Extra)>();
        var encrypted = !string.IsNullOrEmpty(password);
        foreach (var f in files)
        {
            var name = Encoding.UTF8.GetBytes(f.Name);
            var compress = f.Data.Length >= 256 && f.Name.EndsWith(".json", StringComparison.Ordinal);   // Bilder (WebP/PNG/JPEG) sind bereits komprimiert
            var payload = compress ? Deflate(f.Data) : f.Data;
            ushort actualMethod = compress ? MethodDeflate : MethodStored;
            var crc = Crc32(f.Data);
            byte[] data, extra; ushort method, flags = 0x0800, versionNeeded = 20; uint crcField = crc;
            if (encrypted)
            {
                data = AesEncrypt(payload, password!);
                method = MethodAes; flags |= 1; versionNeeded = 51; crcField = 0;   // AE-2: keine CRC (die Prüfung übernimmt der HMAC)
                extra = new byte[11];
                BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x9901); BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), 7);
                BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(4), 2); extra[6] = (byte)'A'; extra[7] = (byte)'E'; extra[8] = 3;
                BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(9), actualMethod);
            }
            else { data = payload; method = actualMethod; extra = Array.Empty<byte>(); if (compress) versionNeeded = 20; }
            var offset = checked((uint)ms.Length);
            Span<byte> lh = stackalloc byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(lh, SigLocal); BinaryPrimitives.WriteUInt16LittleEndian(lh[4..], versionNeeded); BinaryPrimitives.WriteUInt16LittleEndian(lh[6..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[8..], method); BinaryPrimitives.WriteUInt16LittleEndian(lh[10..], dosTime); BinaryPrimitives.WriteUInt16LittleEndian(lh[12..], dosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(lh[14..], crcField); BinaryPrimitives.WriteUInt32LittleEndian(lh[18..], checked((uint)data.Length)); BinaryPrimitives.WriteUInt32LittleEndian(lh[22..], checked((uint)f.Data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(lh[26..], (ushort)name.Length); BinaryPrimitives.WriteUInt16LittleEndian(lh[28..], (ushort)extra.Length);
            ms.Write(lh); ms.Write(name); ms.Write(extra); ms.Write(data);
            var ch = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(ch, SigCentral); BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(4), 63); BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(6), versionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(8), flags); BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(10), method); BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(12), dosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(14), dosDate); BinaryPrimitives.WriteUInt32LittleEndian(ch.AsSpan(16), crcField); BinaryPrimitives.WriteUInt32LittleEndian(ch.AsSpan(20), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ch.AsSpan(24), (uint)f.Data.Length); BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(28), (ushort)name.Length); BinaryPrimitives.WriteUInt16LittleEndian(ch.AsSpan(30), (ushort)extra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ch.AsSpan(42), offset);
            central.Add((ch, name, extra));
        }
        var cdStart = checked((uint)ms.Length);
        foreach (var (h, n, e) in central) { ms.Write(h); ms.Write(n); ms.Write(e); }
        var cdSize = checked((uint)ms.Length) - cdStart;
        Span<byte> eo = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eo, SigEocd); BinaryPrimitives.WriteUInt16LittleEndian(eo[8..], (ushort)central.Count); BinaryPrimitives.WriteUInt16LittleEndian(eo[10..], (ushort)central.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(eo[12..], cdSize); BinaryPrimitives.WriteUInt32LittleEndian(eo[16..], cdStart);
        ms.Write(eo);
        return ms.ToArray();
    }

    // ─── Lesen ──────────────────────────────────────────────────────────

    /// <summary>Liest ein ZIP vollständig und streng aus dem Speicher. Bei verschlüsselten Einträgen ist ein Passwort nötig; jede Auffälligkeit bricht den gesamten Lesevorgang ab (keine Teilergebnisse).</summary>
    public static List<ZipFileData> Read(byte[] zip, string? password)
    {
        try { return ReadCore(zip, password); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException) { throw new ArchiveException("Das ZIP-Archiv ist beschädigt."); }
    }

    private static List<ZipFileData> ReadCore(byte[] zip, string? password)
    {
        if (zip.Length < 22) throw new ArchiveException("Das ist kein gültiges ZIP-Archiv.");
        if (zip.Length > GalleryLimits.MaxArchiveBytes) throw new ArchiveException($"Das Archiv ist zu groß (höchstens {GalleryLimits.MaxArchiveBytes / (1024 * 1024)} MB).");
        var eocd = FindEocd(zip);
        var disk = U16(zip, eocd + 4); var cdDisk = U16(zip, eocd + 6); var onDisk = U16(zip, eocd + 8); var total = U16(zip, eocd + 10);
        var cdSize = U32(zip, eocd + 12); var cdOffset = U32(zip, eocd + 16);
        if (disk != 0 || cdDisk != 0 || onDisk != total) throw new ArchiveException("Mehrteilige ZIP-Archive werden nicht unterstützt.");
        if (total == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF) throw new ArchiveException("ZIP64-Archive werden nicht unterstützt.");
        if (total > GalleryLimits.MaxFilesInArchive) throw new ArchiveException($"Das Archiv enthält zu viele Dateien (höchstens {GalleryLimits.MaxFilesInArchive}).");
        if ((long)cdOffset + cdSize > eocd) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");

        var result = new List<ZipFileData>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0; var pos = (int)cdOffset;
        for (var i = 0; i < total; i++)
        {
            if (pos + 46 > zip.Length || U32(zip, pos) != SigCentral) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");
            var versionMadeBy = U16(zip, pos + 4); var flags = U16(zip, pos + 8); var method = U16(zip, pos + 10);
            var crc = U32(zip, pos + 16); var comp = U32(zip, pos + 20); var uncomp = U32(zip, pos + 24);
            var nLen = U16(zip, pos + 28); var xLen = U16(zip, pos + 30); var cLen = U16(zip, pos + 32);
            var extAttr = U32(zip, pos + 38); var lhOffset = U32(zip, pos + 42);
            var next = pos + 46 + nLen + xLen + cLen;
            if (next > zip.Length) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");
            if (comp == 0xFFFFFFFF || uncomp == 0xFFFFFFFF || lhOffset == 0xFFFFFFFF) throw new ArchiveException("ZIP64-Archive werden nicht unterstützt.");
            var name = ValidateName(zip.AsSpan(pos + 46, nLen), (flags & 0x0800) != 0);
            if ((versionMadeBy >> 8) == 3 && ((extAttr >> 16) & 0xF000) is 0xA000 or 0x6000 or 0x2000 or 0x1000) throw new ArchiveException("Das Archiv enthält unzulässige Einträge (Verknüpfungen oder Sonderdateien).");
            var extra = zip.AsSpan(pos + 46 + nLen, xLen).ToArray();
            pos = next;
            if (name.EndsWith('/')) continue;   // Verzeichniseinträge ignorieren (nichts wird extrahiert)
            if (!names.Add(name)) throw new ArchiveException("Das Archiv enthält doppelte Dateinamen.");
            if (uncomp > Math.Max(GalleryLimits.MaxImageBytes, GalleryLimits.MaxManifestBytes)) throw new ArchiveException("Das Archiv enthält eine zu große Datei.");
            declaredTotal += uncomp;
            if (declaredTotal > GalleryLimits.MaxTotalUncompressedBytes) throw new ArchiveException("Das Archiv ist entpackt zu groß.");
            if (uncomp > 65536 && comp > 0 && uncomp / comp > GalleryLimits.MaxCompressionRatio) throw new ArchiveException("Das Archiv enthält ungewöhnlich stark komprimierte Daten und wurde abgelehnt.");

            if (lhOffset + 30L > zip.Length || U32(zip, (int)lhOffset) != SigLocal) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");
            var lhName = U16(zip, (int)lhOffset + 26); var lhExtra = U16(zip, (int)lhOffset + 28);
            var dataStart = (long)lhOffset + 30 + lhName + lhExtra;
            if (dataStart + comp > cdOffset) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");
            var raw = zip.AsSpan((int)dataStart, (int)comp);

            byte[] content;
            if ((flags & 1) != 0)
            {
                if (method != MethodAes) throw new ArchiveException("Das Archiv nutzt die veraltete, unsichere ZIP-Verschlüsselung. Sie wird nicht unterstützt (nur AES).");
                var (version, strength, actual) = ParseAesExtra(extra);
                if (string.IsNullOrEmpty(password)) throw new ArchivePasswordException(missing: true);
                var plain = AesDecrypt(raw, password!, strength);
                content = actual switch { MethodStored => plain, MethodDeflate => Inflate(plain, uncomp), _ => throw new ArchiveException("Das Archiv nutzt ein nicht unterstütztes Kompressionsverfahren.") };
                if (version == 1 && Crc32(content) != crc) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");
            }
            else
            {
                content = method switch { MethodStored => raw.ToArray(), MethodDeflate => Inflate(raw.ToArray(), uncomp), _ => throw new ArchiveException("Das Archiv nutzt ein nicht unterstütztes Kompressionsverfahren.") };
                if (Crc32(content) != crc) throw new ArchiveException("Das ZIP-Archiv ist beschädigt (Prüfsumme).");
            }
            if (content.Length != uncomp) throw new ArchiveException("Das ZIP-Archiv ist beschädigt (Größenangabe).");
            result.Add(new ZipFileData(name, content));
        }
        return result;
    }

    /// <summary>Pfadregeln: UTF-8, relativ, keine Rückwärtssegmente, kein Laufwerk, keine Backslashes, keine Steuerzeichen, begrenzte Länge.</summary>
    public static string ValidateName(ReadOnlySpan<byte> raw, bool utf8)
    {
        string name;
        try { name = new UTF8Encoding(false, true).GetString(raw); } catch (ArgumentException) { throw new ArchiveException("Das Archiv enthält ungültige Dateinamen."); }
        if (name.Length == 0 || name.Length > GalleryLimits.MaxPathLength || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Any(char.IsControl))
            throw new ArchiveException("Das Archiv enthält unzulässige Dateinamen (absolute oder manipulierte Pfade).");
        foreach (var seg in name.TrimEnd('/').Split('/'))
            if (seg.Length == 0 || seg == "." || seg == "..") throw new ArchiveException("Das Archiv enthält unzulässige Dateinamen (absolute oder manipulierte Pfade).");
        return name;
    }

    private static int FindEocd(byte[] zip)
    {
        for (var i = zip.Length - 22; i >= Math.Max(0, zip.Length - 22 - 65535); i--)
            if (U32(zip, i) == SigEocd && i + 22 + U16(zip, i + 20) == zip.Length) return i;
        throw new ArchiveException("Das ist kein gültiges ZIP-Archiv.");
    }

    private static (ushort Version, int Strength, ushort ActualMethod) ParseAesExtra(byte[] extra)
    {
        for (var p = 0; p + 4 <= extra.Length;)
        {
            var id = U16(extra, p); var size = U16(extra, p + 2);
            if (p + 4 + size > extra.Length) break;
            if (id == 0x9901 && size == 7)
            {
                var version = U16(extra, p + 4); var strength = extra[p + 8];
                if (extra[p + 6] != 'A' || extra[p + 7] != 'E' || version is not (1 or 2) || strength != 3) throw new ArchiveException("Das Archiv nutzt eine nicht unterstützte AES-Variante (unterstützt: AES-256).");
                return (version, strength, U16(extra, p + 9));
            }
            p += 4 + size;
        }
        throw new ArchiveException("Das ZIP-Archiv ist beschädigt (AES-Angaben fehlen).");
    }

    // ─── WinZip AES ─────────────────────────────────────────────────────

    private static (byte[] EncKey, byte[] AuthKey, byte[] Pwv) DeriveKeys(string password, byte[] salt)
    {
        var okm = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA1, 2 * AesKeyBytes + 2);
        return (okm[..AesKeyBytes], okm[AesKeyBytes..(2 * AesKeyBytes)], okm[(2 * AesKeyBytes)..]);
    }

    private static byte[] AesEncrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(AesSaltBytes);
        var (encKey, authKey, pwv) = DeriveKeys(password, salt);
        var cipher = Ctr(plain, encKey);
        var auth = HMACSHA1.HashData(authKey, cipher)[..AuthBytes];
        var o = new byte[AesSaltBytes + 2 + cipher.Length + AuthBytes];
        salt.CopyTo(o, 0); pwv.CopyTo(o, AesSaltBytes); cipher.CopyTo(o, AesSaltBytes + 2); auth.CopyTo(o, o.Length - AuthBytes);
        return o;
    }

    private static byte[] AesDecrypt(ReadOnlySpan<byte> raw, string password, int strength)
    {
        if (raw.Length < AesSaltBytes + 2 + AuthBytes) throw new ArchiveException("Das ZIP-Archiv ist beschädigt.");
        var salt = raw[..AesSaltBytes].ToArray();
        var (encKey, authKey, pwv) = DeriveKeys(password, salt);
        if (!CryptographicOperations.FixedTimeEquals(raw.Slice(AesSaltBytes, 2), pwv)) throw new ArchivePasswordException(missing: false);
        var cipher = raw[(AesSaltBytes + 2)..^AuthBytes].ToArray();
        var expected = HMACSHA1.HashData(authKey, cipher)[..AuthBytes];
        if (!CryptographicOperations.FixedTimeEquals(raw[^AuthBytes..], expected)) throw new ArchivePasswordException(missing: false);   // Passwort stimmt (Prüfwert) oder Zufallstreffer → Authentizität maßgeblich
        return Ctr(cipher, encKey);
    }

    /// <summary>AES-CTR im WinZip-Stil: 16-Byte-Zähler little-endian ab 1, Schlüsselstrom per AES-ECB (nur BCL).</summary>
    private static byte[] Ctr(byte[] input, byte[] key)
    {
        using var aes = Aes.Create(); aes.Key = key;
        var output = new byte[input.Length];
        const int blocksPerChunk = 1024;
        var counters = new byte[blocksPerChunk * 16]; var stream = new byte[blocksPerChunk * 16];
        ulong counter = 1;
        for (var off = 0; off < input.Length; off += blocksPerChunk * 16)
        {
            var len = Math.Min(blocksPerChunk * 16, input.Length - off);
            var blocks = (len + 15) / 16;
            for (var b = 0; b < blocks; b++) { Array.Clear(counters, b * 16, 16); BinaryPrimitives.WriteUInt64LittleEndian(counters.AsSpan(b * 16), counter++); }
            aes.EncryptEcb(counters.AsSpan(0, blocks * 16), stream, PaddingMode.None);
            for (var i = 0; i < len; i++) output[off + i] = (byte)(input[off + i] ^ stream[i]);
        }
        return output;
    }

    // ─── Hilfen ─────────────────────────────────────────────────────────

    private static byte[] Deflate(byte[] data)
    {
        using var o = new MemoryStream();
        using (var d = new DeflateStream(o, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        return o.ToArray();
    }

    /// <summary>Entpackt höchstens die angegebene Größe + 1 Byte (ZIP-Bombenschutz): liefert mehr, als die Angabe verspricht, bricht der Vorgang ab.</summary>
    private static byte[] Inflate(byte[] data, uint declared)
    {
        if (declared > GalleryLimits.MaxTotalUncompressedBytes) throw new ArchiveException("Das Archiv enthält eine zu große Datei.");
        using var i = new MemoryStream(data); using var d = new DeflateStream(i, CompressionMode.Decompress);
        var buf = new byte[declared + 1L]; var read = 0;
        try { int n; while (read < buf.Length && (n = d.Read(buf, read, buf.Length - read)) > 0) read += n; }
        catch (InvalidDataException) { throw new ArchiveException("Das ZIP-Archiv ist beschädigt."); }
        if (read != declared) throw new ArchiveException("Das ZIP-Archiv ist beschädigt (Größenangabe).");
        return buf.AsSpan(0, read).ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    /// <summary>CRC-32 (ZIP), nur zur Integritätsprüfung — keine Kryptografie.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data) { var c = 0xFFFFFFFFu; foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFF; }

    private static (ushort Time, ushort Date) ToDos(DateTime t)
    {
        var y = Math.Clamp(t.Year, 1980, 2107);
        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((y - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
