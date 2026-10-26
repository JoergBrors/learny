using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LearnCards.Web.Services.Pdf;

/// <summary>Textmaße und WinAnsi-Kodierung der Standardschriften Helvetica/Helvetica-Bold (nicht eingebettet; PDF/A wird daher nicht beansprucht).</summary>
public static class PdfText
{
    private static readonly int[] Regular = BuildWidths(278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333,
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584);
    private static readonly int[] Bold = BuildWidths(278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333,
        556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584);

    private static int[] BuildWidths(params int[] w) => w;   // Zeichen 32–126

    private static readonly Dictionary<char, byte> WinAnsiExtra = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E,
        ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    /// <summary>Bytes einer Zeichenkette in WinAnsi (cp1252); nicht darstellbare Zeichen werden zu „?“ (z. B. Emojis).</summary>
    public static byte[] ToWinAnsi(string s)
    {
        var bytes = new List<byte>(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\t') bytes.Add(0x20);
            else if (ch < 0x20) continue;
            else if (ch < 0x80 || (ch >= 0xA0 && ch <= 0xFF)) bytes.Add((byte)ch);
            else if (WinAnsiExtra.TryGetValue(ch, out var b)) bytes.Add(b);
            else if (char.IsLowSurrogate(ch)) continue;
            else bytes.Add((byte)'?');
        }
        return bytes.ToArray();
    }

    public static string Sanitize(string s) => Encoding.Latin1.GetString(ToWinAnsi(s));

    private static int CharWidth(char c, bool bold)
    {
        var t = bold ? Bold : Regular;
        if (c >= 32 && c <= 126) return t[c - 32];
        if (c == ' ' || c == ' ') return t[0];
        if (c == '€' || c == '„' || c == '“' || c == '”' || c == '‘' || c == '’') return c == '€' ? 556 : c is '„' or '“' or '”' ? 333 : 222;
        if (c is '–') return 556; if (c is '—') return 1000; if (c is '…') return 1000; if (c is '•') return 350;
        var d = c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];   // ä → a usw.
        if (d >= 32 && d <= 126) return t[d - 32];
        return c == 'ß' ? (bold ? 611 : 611) : 556;
    }

    public static double Width(string s, double size, bool bold = false)
    {
        double w = 0;
        foreach (var c in s) w += CharWidth(c, bold);
        return w * size / 1000.0;
    }

    /// <summary>Bricht Text in Zeilen der Breite <paramref name="maxWidth"/> um; zu lange Wörter werden zeichenweise getrennt (nichts wird abgeschnitten).</summary>
    public static List<string> Wrap(string text, double size, double maxWidth, bool bold = false)
    {
        var lines = new List<string>();
        foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var words = para.Split(' ');
            var cur = "";
            foreach (var wordRaw in words)
            {
                var word = wordRaw;
                while (Width(word, size, bold) > maxWidth)
                {
                    if (cur.Length > 0) { lines.Add(cur); cur = ""; }
                    var n = 1;
                    while (n < word.Length && Width(word[..(n + 1)], size, bold) <= maxWidth) n++;
                    lines.Add(word[..n]); word = word[n..];
                }
                var trial = cur.Length == 0 ? word : cur + " " + word;
                if (Width(trial, size, bold) <= maxWidth) cur = trial;
                else { lines.Add(cur); cur = word; }
            }
            lines.Add(cur);
        }
        return lines;
    }
}

public readonly record struct PdfColor(double R, double G, double B)
{
    public static readonly PdfColor Black = new(0, 0, 0), White = new(1, 1, 1), Grey = new(0.35, 0.38, 0.38), LightGrey = new(0.92, 0.94, 0.94), Teal = new(0.114, 0.514, 0.475), Red = new(0.7, 0.15, 0.12), Green = new(0.1, 0.5, 0.25);
    public string Fill => $"{F(R)} {F(G)} {F(B)} rg";
    public string Stroke => $"{F(R)} {F(G)} {F(B)} RG";
    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Eine PDF-Seite (A4) als Folge von Zeichenbefehlen. Koordinaten in Punkt, Ursprung links unten.</summary>
public sealed class PdfPage
{
    public const double Width = 595.28, Height = 841.89;
    internal readonly StringBuilder Ops = new();
    internal readonly List<(double X, double Y, double W, double H, string Uri)> Links = new();
    private static string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Esc(string s)
    {
        var sb = new StringBuilder("(");
        foreach (var c in PdfText.Sanitize(s)) { if (c is '(' or ')' or '\\') sb.Append('\\'); sb.Append(c); }
        return sb.Append(')').ToString();
    }

    public void Text(double x, double y, string text, double size = 10, bool bold = false, PdfColor? color = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        Ops.Append($"BT {(color ?? PdfColor.Black).Fill} /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(y)} Td {Esc(text)} Tj ET\n");
    }

    public void TextRight(double xRight, double y, string text, double size = 10, bool bold = false, PdfColor? color = null) =>
        Text(xRight - PdfText.Width(text, size, bold), y, text, size, bold, color);

    public void TextCenter(double xCenter, double y, string text, double size = 10, bool bold = false, PdfColor? color = null) =>
        Text(xCenter - PdfText.Width(text, size, bold) / 2, y, text, size, bold, color);

    public void Rect(double x, double y, double w, double h, PdfColor? fill = null, PdfColor? stroke = null, double lineWidth = 0.8)
    {
        Ops.Append($"q {(fill is { } f ? f.Fill + " " : "")}{(stroke is { } s ? s.Stroke + $" {N(lineWidth)} w " : "")}{N(x)} {N(y)} {N(w)} {N(h)} re {(fill is not null && stroke is not null ? "B" : fill is not null ? "f" : "S")} Q\n");
    }

    public void Line(double x1, double y1, double x2, double y2, double width = 0.6, PdfColor? color = null) =>
        Ops.Append($"q {(color ?? PdfColor.Grey).Stroke} {N(width)} w {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S Q\n");

    public void Circle(double cx, double cy, double r, PdfColor? fill = null, PdfColor? stroke = null, double lineWidth = 1)
    {
        const double k = 0.5522847498;
        var c = r * k;
        Ops.Append($"q {(fill is { } f ? f.Fill + " " : "")}{(stroke is { } s ? s.Stroke + $" {N(lineWidth)} w " : "")}{N(cx + r)} {N(cy)} m " +
            $"{N(cx + r)} {N(cy + c)} {N(cx + c)} {N(cy + r)} {N(cx)} {N(cy + r)} c {N(cx - c)} {N(cy + r)} {N(cx - r)} {N(cy + c)} {N(cx - r)} {N(cy)} c " +
            $"{N(cx - r)} {N(cy - c)} {N(cx - c)} {N(cy - r)} {N(cx)} {N(cy - r)} c {N(cx + c)} {N(cy - r)} {N(cx + r)} {N(cy - c)} {N(cx + r)} {N(cy)} c " +
            $"{(fill is not null && stroke is not null ? "B" : fill is not null ? "f" : "S")} Q\n");
    }

    /// <summary>Haken (Prüfzeichen) als Vektorzug.</summary>
    public void Check(double cx, double cy, double size, PdfColor color, double lineWidth = 2.2) =>
        Ops.Append($"q {color.Stroke} {N(lineWidth)} w 1 J 1 j {N(cx - size * 0.45)} {N(cy)} m {N(cx - size * 0.1)} {N(cy - size * 0.38)} l {N(cx + size * 0.5)} {N(cy + size * 0.42)} l S Q\n");

    /// <summary>QR-Code als Vektorquadrate (inkl. Ruhezone von 2 Modulen innerhalb von <paramref name="size"/>).</summary>
    public void Qr(double x, double y, double size, QrCode qr)
    {
        var quiet = 2; var cell = size / (qr.Size + quiet * 2);
        Ops.Append($"q {PdfColor.White.Fill} {N(x)} {N(y)} {N(size)} {N(size)} re f {PdfColor.Black.Fill}\n");
        for (var row = 0; row < qr.Size; row++)
        {
            var col = 0;
            while (col < qr.Size)
            {
                if (!qr.Get(col, row)) { col++; continue; }
                var start = col; while (col < qr.Size && qr.Get(col, row)) col++;
                Ops.Append($"{N(x + (start + quiet) * cell)} {N(y + size - (row + quiet + 1) * cell)} {N((col - start) * cell + 0.05)} {N(cell + 0.05)} re\n");
            }
        }
        Ops.Append("f Q\n");
    }

    public void Link(double x, double y, double w, double h, string uri) => Links.Add((x, y, w, h, uri));
}

public sealed record PdfEmbeddedFile(string Name, byte[] Data, string MimeType, string Relationship, string Description);

/// <summary>Parameter der PDF-Signatur (CMS/CAdES, <c>ETSI.CAdES.detached</c>): Zertifikat mit privatem Schlüssel, optionale Zusatzzertifikate (Kette) und Texte.</summary>
public sealed class PdfSignOptions
{
    public required X509Certificate2 Certificate { get; init; }
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = Array.Empty<X509Certificate2>();
    public string Name { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Location { get; init; } = "";
    public string ContactInfo { get; init; } = "";
    public DateTime SigningTime { get; init; } = DateTime.UtcNow;
    /// <summary>Reservierter Platz für die CMS-Signatur in Bytes.</summary>
    public int ReservedBytes { get; init; } = 14000;
}

/// <summary>
/// Schreibt ein PDF 1.7 ohne externe Bibliothek: Seiten, Standardschriften, Vektorgrafik, Link-Annotationen, eingebettete Dateien (<c>/EmbeddedFiles</c> und <c>/AF</c>)
/// und optional eine CMS-Signatur über die gesamte Datei (außer dem Signaturwert selbst). Es wird weder PDF/A noch PAdES-LTV beansprucht.
/// </summary>
public sealed class PdfBuilder
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Creator { get; set; } = "LearnCards";
    public string Producer { get; set; } = "LearnCards PDF (ohne Fremdbibliothek)";
    public DateTime CreationDate { get; set; } = DateTime.UtcNow;
    public List<PdfPage> Pages { get; } = new();
    public List<PdfEmbeddedFile> Files { get; } = new();

    public PdfPage AddPage() { var p = new PdfPage(); Pages.Add(p); return p; }

    private static string PdfString(string s)
    {
        if (s.All(c => c >= 32 && c < 127))
            return "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    private static string PdfName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c.ToString() : "#" + ((int)c).ToString("X2")));
    private static string Date(DateTime t) => "D:" + t.ToUniversalTime().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z";

    public byte[] Build(PdfSignOptions? sign = null)
    {
        if (Pages.Count == 0) AddPage();
        var ms = new MemoryStream();
        var offsets = new List<long> { 0 };   // Objekt 0 ist frei
        void Raw(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Bytes(byte[] b) => ms.Write(b, 0, b.Length);

        // Objektnummern vorab vergeben
        var next = 1;
        int catalog = next++, pagesTree = next++, f1 = next++, f2 = next++, info = next++;
        var pageObjs = new List<(int Content, int Page, List<int> Annots)>();
        foreach (var p in Pages) { var annots = new List<int>(); for (var i = 0; i < p.Links.Count; i++) annots.Add(next++); pageObjs.Add((next++, next++, annots)); }
        var fileObjs = Files.Select(f => (Stream: next++, Spec: next++)).ToList();
        int sigObj = 0, widget = 0;
        if (sign is not null) { sigObj = next++; widget = next++; }
        var total = next;

        void BeginObj(int n) { while (offsets.Count <= n) offsets.Add(0); offsets[n] = ms.Position; Raw($"{n} 0 obj\n"); }
        void EndObj() => Raw("endobj\n");

        Raw("%PDF-1.7\n%âãÏÓ\n");

        // Katalog
        BeginObj(catalog);
        var sortedFiles = Files.Select((f, i) => (f, i)).OrderBy(x => x.f.Name, StringComparer.Ordinal).ToList();
        var names = Files.Count == 0 ? "" : "/Names << /EmbeddedFiles << /Names [" + string.Join(" ", sortedFiles.Select(x => $"{PdfString(x.f.Name)} {fileObjs[x.i].Spec} 0 R")) + "] >> >> " +
            "/AF [" + string.Join(" ", fileObjs.Select(o => $"{o.Spec} 0 R")) + "] /PageMode /UseAttachments ";
        Raw($"<< /Type /Catalog /Pages {pagesTree} 0 R /Lang (de-DE) {names}{(sign is not null ? $"/AcroForm << /Fields [{widget} 0 R] /SigFlags 3 >> " : "")}>>\n"); EndObj();

        BeginObj(pagesTree);
        Raw($"<< /Type /Pages /Count {Pages.Count} /Kids [{string.Join(" ", pageObjs.Select(p => $"{p.Page} 0 R"))}] >>\n"); EndObj();
        BeginObj(f1); Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\n"); EndObj();
        BeginObj(f2); Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\n"); EndObj();

        BeginObj(info);
        Raw($"<< /Title {PdfString(Title)} /Author {PdfString(Author)} /Subject {PdfString(Subject)} /Keywords {PdfString(Keywords)} /Creator {PdfString(Creator)} /Producer {PdfString(Producer)} /CreationDate ({Date(CreationDate)}) /ModDate ({Date(CreationDate)}) >>\n"); EndObj();

        for (var i = 0; i < Pages.Count; i++)
        {
            var p = Pages[i]; var o = pageObjs[i];
            for (var k = 0; k < p.Links.Count; k++)
            {
                var l = p.Links[k]; BeginObj(o.Annots[k]);
                Raw($"<< /Type /Annot /Subtype /Link /Rect [{F(l.X)} {F(l.Y)} {F(l.X + l.W)} {F(l.Y + l.H)}] /Border [0 0 0] /A << /S /URI /URI {PdfString(l.Uri)} >> >>\n"); EndObj();
            }
            var content = Compress(Encoding.Latin1.GetBytes(p.Ops.ToString()));
            BeginObj(o.Content); Raw($"<< /Filter /FlateDecode /Length {content.Length} >>\nstream\n"); Bytes(content); Raw("\nendstream\n"); EndObj();
            var annots = o.Annots.ToList(); if (i == 0 && sign is not null) annots.Add(widget);
            BeginObj(o.Page);
            Raw($"<< /Type /Page /Parent {pagesTree} 0 R /MediaBox [0 0 {F(PdfPage.Width)} {F(PdfPage.Height)}] /Resources << /Font << /F1 {f1} 0 R /F2 {f2} 0 R >> >> /Contents {o.Content} 0 R{(annots.Count > 0 ? $" /Annots [{string.Join(" ", annots.Select(a => $"{a} 0 R"))}]" : "")} >>\n"); EndObj();
        }

        for (var i = 0; i < Files.Count; i++)
        {
            var f = Files[i];
            var data = Compress(f.Data);
            BeginObj(fileObjs[i].Stream);
            Raw($"<< /Type /EmbeddedFile /Subtype /{PdfName(f.MimeType)} /Filter /FlateDecode /Length {data.Length} /Params << /Size {f.Data.Length} /ModDate ({Date(CreationDate)}) >> >>\nstream\n");
            Bytes(data); Raw("\nendstream\n"); EndObj();
            BeginObj(fileObjs[i].Spec);
            Raw($"<< /Type /Filespec /F {PdfString(f.Name)} /UF {PdfString(f.Name)} /Desc {PdfString(f.Description)} /AFRelationship /{f.Relationship} /EF << /F {fileObjs[i].Stream} 0 R /UF {fileObjs[i].Stream} 0 R >> >>\n"); EndObj();
        }

        long contentsStart = 0, contentsEnd = 0, rangeStart = 0;
        if (sign is not null)
        {
            BeginObj(sigObj);
            Raw($"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached /ByteRange [");
            rangeStart = ms.Position; Raw("0 0000000000 0000000000 0000000000]");
            Raw(" /Contents "); contentsStart = ms.Position; Raw("<" + new string('0', sign.ReservedBytes * 2) + ">"); contentsEnd = ms.Position;
            Raw($" /M ({Date(sign.SigningTime)}) /Name {PdfString(sign.Name)} /Reason {PdfString(sign.Reason)} /Location {PdfString(sign.Location)} /ContactInfo {PdfString(sign.ContactInfo)} >>\n"); EndObj();
            BeginObj(widget);
            Raw($"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Learny-Signatur) /V {sigObj} 0 R /Rect [0 0 0 0] /F 132 /P {pageObjs[0].Page} 0 R >>\n"); EndObj();
        }

        var xref = ms.Position;
        Raw($"xref\n0 {total}\n0000000000 65535 f \n");
        for (var n = 1; n < total; n++) Raw($"{offsets[n]:0000000000} 00000 n \n");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Title + Date(CreationDate) + Guid.NewGuid()))[..16]);
        Raw($"trailer\n<< /Size {total} /Root {catalog} 0 R /Info {info} 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");

        var pdf = ms.ToArray();
        if (sign is null) return pdf;

        // ByteRange eintragen (feste Breite), dann über alles außer dem Signaturwert signieren
        var range = $"0 {contentsStart:0000000000} {contentsEnd:0000000000} {pdf.Length - contentsEnd:0000000000}]";
        Encoding.Latin1.GetBytes(range).CopyTo(pdf, rangeStart);
        using var sha = SHA256.Create();
        sha.TransformBlock(pdf, 0, (int)contentsStart, null, 0);
        sha.TransformFinalBlock(pdf, (int)contentsEnd, pdf.Length - (int)contentsEnd);
        var cms = PdfCms.CreateDetached(sha.Hash!, sign.Certificate, sign.Chain, sign.SigningTime);
        if (cms.Length > sign.ReservedBytes) throw new InvalidOperationException($"Die PDF-Signatur ({cms.Length} Bytes) passt nicht in den reservierten Platz ({sign.ReservedBytes} Bytes).");
        var hex = Convert.ToHexString(cms);
        Encoding.Latin1.GetBytes(hex).CopyTo(pdf, contentsStart + 1);
        return pdf;
    }

    private static byte[] Compress(byte[] data)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return o.ToArray();
    }
}
