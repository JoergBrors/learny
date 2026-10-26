namespace LearnCards.Web.Services.Pdf;

/// <summary>
/// Einfacher Textfluss über mehrere A4-Seiten: automatische Seitenumbrüche, Zeilenumbruch nach tatsächlicher Textbreite (nichts wird abgeschnitten) und Tabellen mit
/// wiederholter Kopfzeile. Seitenkopf und -fuß zeichnet <c>chrome</c> bzw. der Aufrufer nach dem Layout (Seitenzahlen kennt man erst am Ende).
/// </summary>
public sealed class PdfFlow
{
    public const double Left = 50, Right = PdfPage.Width - 50, Top = PdfPage.Height - 78, Bottom = 62;
    private readonly PdfBuilder _doc;
    private readonly Action<PdfPage> _chrome;
    public PdfPage Page { get; private set; } = null!;
    public double Y { get; set; }
    public double Width => Right - Left;

    public PdfFlow(PdfBuilder doc, Action<PdfPage> chrome) { _doc = doc; _chrome = chrome; NewPage(); }

    public void NewPage() { Page = _doc.AddPage(); _chrome(Page); Y = Top; }
    public void Ensure(double height) { if (Y - height < Bottom) NewPage(); }
    public void Space(double h) => Y -= h;

    public void Heading(string text, double size = 13)
    {
        Ensure(size * 2.6);
        Y -= size * 0.6;
        Page.Text(Left, Y - size, text, size, true, PdfColor.Teal);
        Y -= size * 1.5;
        Page.Line(Left, Y, Right, Y, 0.5, PdfColor.LightGrey);
        Y -= 6;
    }

    public void Paragraph(string text, double size = 10, bool bold = false, PdfColor? color = null, double indent = 0, double gap = 3)
    {
        var lineH = size * 1.32;
        foreach (var line in PdfText.Wrap(text, size, Width - indent, bold))
        {
            Ensure(lineH);
            Page.Text(Left + indent, Y - size, line, size, bold, color);
            Y -= lineH;
        }
        Y -= gap;
    }

    public void KeyValue(string key, string value, double keyWidth = 150, double size = 9.5)
    {
        var lineH = size * 1.32;
        var valueLines = PdfText.Wrap(value.Length == 0 ? "–" : value, size, Width - keyWidth);
        Ensure(lineH * Math.Min(valueLines.Count, 2));
        var first = true;
        foreach (var line in valueLines)
        {
            Ensure(lineH);
            if (first) { Page.Text(Left, Y - size, key, size, true, PdfColor.Grey); first = false; }
            Page.Text(Left + keyWidth, Y - size, line, size);
            Y -= lineH;
        }
        Y -= 2;
    }

    /// <summary>Tabelle mit festen Spaltenbreiten; Zellen werden umgebrochen, die Kopfzeile wiederholt sich auf Folgeseiten.</summary>
    public void Table(string[] headers, double[] widths, IEnumerable<string[]> rows, double size = 8.5)
    {
        var lineH = size * 1.3; var pad = 3.0;
        void Header()
        {
            var h = lineH + pad * 2;
            Ensure(h + lineH * 2);
            Page.Rect(Left, Y - h, Width, h, PdfColor.LightGrey);
            var x = Left;
            for (var i = 0; i < headers.Length; i++) { Page.Text(x + pad, Y - pad - size, headers[i], size, true); x += widths[i]; }
            Y -= h;
        }
        Header();
        foreach (var row in rows)
        {
            var cells = row.Select((c, i) => PdfText.Wrap(c, size, widths[i] - pad * 2)).ToList();
            var h = cells.Max(c => c.Count) * lineH + pad * 2;
            if (Y - h < Bottom) { NewPage(); Header(); }
            var x = Left;
            for (var i = 0; i < cells.Count; i++)
            {
                for (var l = 0; l < cells[i].Count; l++) Page.Text(x + pad, Y - pad - size - l * lineH, cells[i][l], size);
                x += widths[i];
            }
            Y -= h;
            Page.Line(Left, Y, Right, Y, 0.3, PdfColor.LightGrey);
        }
        Y -= 6;
    }

    /// <summary>Prüfsiegel „Learny Verified“ (Vektor): Ring mit Haken, Titel und Unterzeile. Es bestätigt die Prüfung durch Learny — keine Zertifizierung durch 1EdTech oder LinkedIn.</summary>
    public static void DrawSeal(PdfPage page, double x, double yTop, bool revoked = false)
    {
        var teal = revoked ? PdfColor.Red : PdfColor.Teal;
        const double r = 17;
        var cx = x + r + 2; var cy = yTop - r - 2;
        page.Circle(cx, cy, r, null, teal, 2.2);
        page.Circle(cx, cy, r - 4.5, teal);
        page.Check(cx, cy - 1, 11, PdfColor.White, 2.4);
        page.Text(x + r * 2 + 10, yTop - 14, "Learny Verified", 13, true, teal);
        page.Text(x + r * 2 + 10, yTop - 26, "Digitally Signed Credential", 8, false, PdfColor.Grey);
    }
}
