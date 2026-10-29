using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LearnCards.Web.Services.Pdf;

public sealed record PdfAttachment(string Name, byte[] Data)
{
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Data)).ToLowerInvariant();
}

/// <summary>Befund einer PDF-Datei: Signatur (CMS) und Unversehrtheit, eingebettete Dateien. Bewertet nichts inhaltlich — das tut <c>PdfVerificationService</c>.</summary>
public sealed class PdfInspection
{
    public bool IsPdf { get; set; }
    public int Length { get; set; }
    public int SignatureCount { get; set; }
    public bool SignaturePresent => SignatureCount > 0;
    public string? SubFilter { get; set; }
    /// <summary>ByteRange ist wohlgeformt: beginnt bei 0, lässt genau den Signaturwert (<c>/Contents</c>) aus und endet am Dateiende — es wurde nichts angehängt.</summary>
    public bool ByteRangeCoversFile { get; set; }
    public bool DigestMatches { get; set; }
    public CmsVerification? Cms { get; set; }
    public DateTime? ClaimedSigningTime { get; set; }
    public List<PdfAttachment> Attachments { get; } = new();
    /// <summary>Anzahl der Dateispezifikationen im PDF — größer als <see cref="Attachments"/>, wenn Anhänge nicht lesbar sind (nicht stillschweigend ignorieren, ADR 0039).</summary>
    public int FilespecCount { get; set; }
    public List<string> Problems { get; } = new();
}

/// <summary>
/// Liest von LearnCards erzeugte PDF-Dateien (klassische Querverweistabelle, direkte Stream-Längen, Flate-komprimierte Anhänge) ohne Fremdbibliothek: findet die Signatur,
/// prüft ByteRange und CMS, und extrahiert die eingebetteten Dateien aus der signierten Byte-Folge. Fremde oder nachträglich (inkrementell) geänderte PDFs werden nicht
/// bestätigt: Angehängte Daten verletzen den ByteRange und gelten als „nach der Signierung verändert“.
/// </summary>
public static class PdfInspector
{
    public const int MaxAttachmentBytes = 25 * 1024 * 1024;

    public static PdfInspection Inspect(byte[] pdf)
    {
        var r = new PdfInspection { Length = pdf.Length };
        if (pdf.Length < 8 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F' || pdf[4] != '-') { r.Problems.Add("Die Datei ist kein PDF."); return r; }
        r.IsPdf = true;
        var text = Encoding.Latin1.GetString(pdf);   // 1 Byte = 1 Zeichen: Offsets bleiben gültig

        var ranges = Regex.Matches(text, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        r.SignatureCount = ranges.Count;
        if (ranges.Count > 0)
        {
            var m = ranges[^1];
            long b0 = long.Parse(m.Groups[1].Value), b1 = long.Parse(m.Groups[2].Value), b2 = long.Parse(m.Groups[3].Value), b3 = long.Parse(m.Groups[4].Value);
            // der Signaturwert liegt in demselben Wörterbuch: nächstes /Contents <…> hinter dem ByteRange
            var cm = Regex.Match(text[m.Index..], @"/Contents\s*<([0-9A-Fa-f\s]*)>");
            if (!cm.Success) r.Problems.Add("Signaturwert (/Contents) nicht gefunden.");
            else
            {
                var open = m.Index + cm.Index + cm.Value.IndexOf('<'); var close = m.Index + cm.Index + cm.Length;
                var wellFormed = b0 == 0 && b1 == open && b2 == close && b1 > 0 && b3 >= 0 && b2 + b3 <= pdf.Length;   // Bereiche passen zum Signaturwert
                r.ByteRangeCoversFile = wellFormed && b2 + b3 == pdf.Length;
                if (!wellFormed) r.Problems.Add("Der ByteRange der Signatur ist ungültig (Signatur beschädigt).");
                else if (!r.ByteRangeCoversFile) r.Problems.Add("Nach der Signierung wurden Daten angehängt: Der signierte Bereich deckt nicht die gesamte Datei ab.");
                var sm = Regex.Match(text[Math.Max(0, m.Index - 400)..(int)Math.Min(text.Length, close + 400)], @"/SubFilter\s*/([A-Za-z0-9._#]+)");
                r.SubFilter = sm.Success ? sm.Groups[1].Value : null;
                var dm = Regex.Match(text[(int)close..(int)Math.Min(text.Length, close + 400)], @"/M\s*\(D:(\d{14})Z?\)");
                if (dm.Success && DateTime.TryParseExact(dm.Groups[1].Value, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)) r.ClaimedSigningTime = t;
                if (wellFormed)   // die Signatur wird über den ursprünglich signierten Bereich geprüft – auch wenn danach Daten angehängt wurden (dann ist sie gültig, das Dokument aber verändert)
                {
                    using var sha = SHA256.Create();
                    sha.TransformBlock(pdf, 0, (int)b1, null, 0);
                    sha.TransformFinalBlock(pdf, (int)b2, (int)b3);
                    var hash = sha.Hash!;
                    byte[] cms;
                    try { cms = Convert.FromHexString(Regex.Replace(cm.Groups[1].Value, @"\s+", "")); }
                    catch (FormatException) { r.Problems.Add("Der Signaturwert ist kein gültiger Hex-Text."); cms = Array.Empty<byte>(); }
                    if (cms.Length > 0)
                    {
                        r.Cms = PdfCms.VerifyDetached(cms, hash);
                        r.DigestMatches = r.Cms.DigestMatches;
                    }
                }
            }
        }
        ReadAttachments(text, pdf, r);
        return r;
    }

    private static void ReadAttachments(string text, byte[] pdf, PdfInspection r)
    {
        // Objekte indizieren: "N 0 obj" … "endobj"
        var objects = new Dictionary<int, (int Start, int End)>();
        foreach (Match om in Regex.Matches(text, @"(?<=^|[\r\n\s])(\d+)\s+0\s+obj\b"))
        {
            var end = text.IndexOf("endobj", om.Index + om.Length, StringComparison.Ordinal);
            if (end > 0) objects[int.Parse(om.Groups[1].Value)] = (om.Index + om.Length, end);
        }
        foreach (var (num, (start, end)) in objects)
        {
            var body = text.Substring(start, Math.Min(end - start, 1500));
            if (!Regex.IsMatch(body, @"/Type\s*/Filespec")) continue;
            r.FilespecCount++;
            var ef = Regex.Match(body, @"/EF\s*<<\s*/F\s+(\d+)\s+0\s+R");
            var name = ReadName(body);
            if (!ef.Success || name is null || !objects.TryGetValue(int.Parse(ef.Groups[1].Value), out var so)) continue;
            var data = ReadStream(text, pdf, so.Start, so.End, r);
            if (data is not null) r.Attachments.Add(new PdfAttachment(name, data));
        }
    }

    private static string? ReadName(string body)
    {
        var m = Regex.Match(body, @"/UF\s*(\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f]+>)");
        if (!m.Success) m = Regex.Match(body, @"/F\s*(\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f]+>)");
        if (!m.Success) return null;
        var v = m.Groups[1].Value;
        if (v.StartsWith('<'))
        {
            var bytes = Convert.FromHexString(v[1..^1]);
            return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.Latin1.GetString(bytes);
        }
        return Regex.Replace(v[1..^1], @"\\(.)", "$1");
    }

    private static byte[]? ReadStream(string text, byte[] pdf, int start, int end, PdfInspection r)
    {
        // Das Schlüsselwort „stream“ steht direkt hinter dem Wörterbuch (">>"); Medientypen wie „application/octet-stream“ im Wörterbuch dürfen es nicht vortäuschen
        var kw = Regex.Match(text.Substring(start, end - start), @">>\s*stream(?=\r?\n)");
        if (!kw.Success) return null;
        var s = start + kw.Index + kw.Length - 6;
        var dict = text.Substring(start, s - start);
        var len = Regex.Match(dict, @"/Length\s+(\d+)(?!\s+\d+\s+R)");
        if (!len.Success) { r.Problems.Add("Anhang mit nicht lesbarer Stream-Länge."); return null; }
        var dataStart = s + 6;
        if (dataStart < text.Length && text[dataStart] == '\r') dataStart++;
        if (dataStart < text.Length && text[dataStart] == '\n') dataStart++;
        var n = int.Parse(len.Groups[1].Value);
        if (n < 0 || dataStart + n > pdf.Length) { r.Problems.Add("Anhang ragt über das Dateiende hinaus."); return null; }
        var raw = pdf.AsSpan(dataStart, n).ToArray();
        if (!Regex.IsMatch(dict, @"/FlateDecode")) return raw;
        try
        {
            using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var o = new MemoryStream();
            var buf = new byte[81920]; int read;
            while ((read = z.Read(buf, 0, buf.Length)) > 0)
            {
                o.Write(buf, 0, read);
                if (o.Length > MaxAttachmentBytes) { r.Problems.Add("Ein Anhang ist zu groß (Schutz vor Dekompressionsbomben)."); return null; }
            }
            return o.ToArray();
        }
        catch (InvalidDataException) { r.Problems.Add("Ein Anhang ist beschädigt und lässt sich nicht entpacken."); return null; }
    }
}
