using System.Text;

namespace LearnCards.Web.Services.Pdf;

/// <summary>
/// Minimaler QR-Code-Encoder (ISO/IEC 18004, Byte-Modus, UTF-8, Fehlerkorrektur L/M/Q/H, Versionen 1–40, Masken 0–7 mit Strafpunkten).
/// Ohne externe Pakete (ADR 0002); gibt die Modulmatrix zurück, die der PDF-Erzeuger als Vektorgrafik zeichnet.
/// </summary>
public sealed class QrCode
{
    public enum Ecc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

    public int Version { get; }
    public int Size { get; }
    private readonly bool[,] _modules;     // [y, x]; true = dunkel
    private readonly bool[,] _isFunction;

    public bool Get(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    private static readonly int[][] EccCodewordsPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[][] NumErrorCorrectionBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    private static readonly int[] FormatBits = { 1, 0, 3, 2 };   // L, M, Q, H

    private QrCode(int version, Ecc ecl, byte[] dataCodewords)
    {
        Version = version; Size = version * 4 + 17;
        _modules = new bool[Size, Size]; _isFunction = new bool[Size, Size];
        DrawFunctionPatterns(ecl);
        var all = AddEccAndInterleave(dataCodewords, version, ecl);
        DrawCodewords(all);
        var best = 0; var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask); DrawFormatBits(ecl, mask);
            var p = Penalty();
            if (p < bestPenalty) { bestPenalty = p; best = mask; }
            ApplyMask(mask);   // Maske wieder entfernen (XOR)
        }
        ApplyMask(best); DrawFormatBits(ecl, best);
    }

    /// <summary>Kodiert Text (UTF-8, Byte-Modus) in der kleinsten passenden Version.</summary>
    public static QrCode EncodeText(string text, Ecc ecl = Ecc.Medium)
    {
        var data = Encoding.UTF8.GetBytes(text);
        for (var version = 1; version <= 40; version++)
        {
            var capacityBits = NumDataCodewords(version, ecl) * 8;
            var countBits = version <= 9 ? 8 : 16;
            var needed = 4 + countBits + data.Length * 8;
            if (needed > capacityBits) continue;
            var bits = new List<bool>();
            void Append(int value, int len) { for (var i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
            Append(0b0100, 4); Append(data.Length, countBits);
            foreach (var b in data) Append(b, 8);
            Append(0, Math.Min(4, capacityBits - bits.Count));
            Append(0, (8 - bits.Count % 8) % 8);
            for (var pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return new QrCode(version, ecl, codewords);
        }
        throw new ArgumentException("Der Text ist für einen QR-Code zu lang.");
    }

    private static int NumRawDataModules(int ver)
    {
        var result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            var numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int NumDataCodewords(int ver, Ecc ecl) => NumRawDataModules(ver) / 8 - EccCodewordsPerBlock[(int)ecl][ver] * NumErrorCorrectionBlocks[(int)ecl][ver];

    // ─── Funktionsmuster ───
    private void SetFunction(int x, int y, bool dark) { _modules[y, x] = dark; _isFunction[y, x] = true; }

    private void DrawFunctionPatterns(Ecc ecl)
    {
        for (var i = 0; i < Size; i++) { SetFunction(6, i, i % 2 == 0); SetFunction(i, 6, i % 2 == 0); }
        DrawFinder(3, 3); DrawFinder(Size - 4, 3); DrawFinder(3, Size - 4);
        var pos = AlignmentPositions();
        for (var i = 0; i < pos.Count; i++)
            for (var j = 0; j < pos.Count; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == pos.Count - 1) || (i == pos.Count - 1 && j == 0))) DrawAlignment(pos[i], pos[j]);
        DrawFormatBits(ecl, 0);
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy)); var x = cx + dx; var y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size) SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++) SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private List<int> AlignmentPositions()
    {
        if (Version == 1) return new();
        var numAlign = Version / 7 + 2;
        var step = Version == 32 ? 26 : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new List<int> { 6 };
        for (var pos = Size - 7; result.Count < numAlign; pos -= step) result.Insert(1, pos);
        return result;
    }

    private void DrawFormatBits(Ecc ecl, int mask)
    {
        var data = (FormatBits[(int)ecl] << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;
        static bool Bit(int v, int i) => ((v >> i) & 1) != 0;
        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6)); SetFunction(8, 8, Bit(bits, 7)); SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));
        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var rem = Version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = (Version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var dark = ((bits >> i) & 1) != 0; var a = Size - 11 + i % 3; var b = i / 3;
            SetFunction(a, b, dark); SetFunction(b, a, dark);
        }
    }

    // ─── Fehlerkorrektur ───
    private static byte[] AddEccAndInterleave(byte[] data, int ver, Ecc ecl)
    {
        var numBlocks = NumErrorCorrectionBlocks[(int)ecl][ver]; var blockEccLen = EccCodewordsPerBlock[(int)ecl][ver];
        var rawCodewords = NumRawDataModules(ver) / 8;
        var numShort = numBlocks - rawCodewords % numBlocks; var shortLen = rawCodewords / numBlocks;
        var blocks = new List<byte[]>(); var rsDiv = ReedSolomonDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var len = shortLen - blockEccLen + (i < numShort ? 0 : 1);
            var dat = new byte[len]; Array.Copy(data, k, dat, 0, len); k += len;
            var ecc = ReedSolomonRemainder(dat, rsDiv);
            var block = new byte[shortLen + 1];
            Array.Copy(dat, block, len);
            Array.Copy(ecc, 0, block, shortLen + 1 - blockEccLen, blockEccLen);
            blocks.Add(block);
        }
        var result = new List<byte>();
        for (var i = 0; i < shortLen + 1; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortLen - blockEccLen || j >= numShort) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree]; result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1); result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    private static int Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
        return z;
    }

    // ─── Platzieren, Maskieren ───
    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j; var upward = ((right + 1) & 2) == 0; var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8) { _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0; i++; }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0, 4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0, 6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_isFunction[y, x]) _modules[y, x] = !_modules[y, x];
            }
    }

    private int Penalty()
    {
        var result = 0;
        for (var pass = 0; pass < 2; pass++)   // Zeilen, dann Spalten
        {
            for (var a = 0; a < Size; a++)
            {
                var run = 1; var history = new Queue<int>();
                for (var b = 1; b < Size; b++)
                {
                    bool Cell(int k) => pass == 0 ? _modules[a, k] : _modules[k, a];
                    if (Cell(b) == Cell(b - 1)) { run++; if (run == 5) result += 3; else if (run > 5) result++; } else run = 1;
                }
                // Suchmuster 1:1:3:1:1 mit 4 hellen Modulen daneben
                for (var b = 0; b + 10 < Size; b++)
                {
                    bool Cell(int k) => pass == 0 ? _modules[a, b + k] : _modules[b + k, a];
                    var p1 = Cell(0) && !Cell(1) && Cell(2) && Cell(3) && Cell(4) && !Cell(5) && Cell(6) && !Cell(7) && !Cell(8) && !Cell(9) && !Cell(10);
                    var p2 = !Cell(0) && !Cell(1) && !Cell(2) && !Cell(3) && Cell(4) && !Cell(5) && Cell(6) && Cell(7) && Cell(8) && !Cell(9) && Cell(10);
                    if (p1 || p2) result += 40;
                }
            }
        }
        for (var y = 0; y + 1 < Size; y++)
            for (var x = 0; x + 1 < Size; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += 3;
            }
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        var k2 = (int)Math.Ceiling(Math.Abs(dark * 20 - total * 10) / (double)total) - 1;
        return result + Math.Max(0, k2) * 10;
    }
}
