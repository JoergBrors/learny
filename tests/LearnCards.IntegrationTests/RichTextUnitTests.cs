using LearnCards.Web.Domain;

/// <summary>Komponententests der sicheren Rich-Text-Verarbeitung (ADR 0020) — ohne Server.</summary>
public static class RichTextUnitTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Komponententests Rich-Text (ADR 0020)");

        // Klartext und Format
        check(RichText.ToHtml("Einfacher Text") == "<p>Einfacher Text</p>", "R1 Rich-Text: Klartext wird zum Absatz");
        check(RichText.ToHtml("a\nb\n\nc") == "<p>a<br>b</p><p>c</p>", "R1 Rich-Text: Zeilenumbruch und Absatz");
        check(RichText.ToHtml("") == "" && RichText.ToHtml(null) == "" && RichText.ToHtml("  \n ") == "", "R1 Rich-Text: leere Werte ergeben nichts");
        check(RichText.ToHtml("**fett** *kursiv* _auch_ ++unter++") == "<p><strong>fett</strong> <em>kursiv</em> <em>auch</em> <u>unter</u></p>", "R2 Rich-Text: fett, kursiv, unterstrichen");
        check(RichText.ToHtml("**_beides_**") == "<p><strong><em>beides</em></strong></p>", "R2 Rich-Text: Kombination fett+kursiv");
        check(RichText.ToHtml("# H1\n## H2\n### H3") == "<h4>H1</h4><h5>H2</h5><h6>H3</h6>", "R2 Rich-Text: Überschriften (h4–h6, die Seitenüberschriften bleiben h1/h2)");
        check(RichText.ToHtml("- a\n- b\n\n1. x\n2. y") == "<ul><li>a</li><li>b</li></ul><ol><li>x</li><li>y</li></ol>", "R2 Rich-Text: Aufzählung und Nummerierung");
        check(RichText.ToHtml("[Doku](https://learn.microsoft.com/a?b=1&c=2)") == "<p><a href=\"https://learn.microsoft.com/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">Doku</a></p>", "R2 Rich-Text: https-Link mit rel=noopener");
        check(RichText.ToHtml(@"\*kein kursiv\* und \[keine\](https://x.de)") == "<p>*kein kursiv* und [keine](https://x.de)</p>", "R3 Rich-Text: Maskierung mit Backslash");

        // Bestehende Texte bleiben unverändert lesbar
        check(RichText.ToHtml("C++/C++ und 2 * 3 * 4 und snake_case_name") == "<p>C++/C++ und 2 * 3 * 4 und snake_case_name</p>", "R4 Rich-Text: typische Klartexte (C++, Malzeichen, snake_case) werden nicht umformatiert");
        check(RichText.ToHtml("Preis: 5 € & mehr <ok>") == "<p>Preis: 5 € &amp; mehr &lt;ok&gt;</p>", "R4 Rich-Text: Sonderzeichen werden kodiert");

        // Sicherheit
        var attacks = new[]
        {
            "<script>alert(1)</script>", "<img src=x onerror=alert(1)>", "<svg onload=alert(1)>", "<iframe src=//evil></iframe>", "<a href=\"javascript:alert(1)\">x</a>",
            "<b onclick=\"alert(1)\">x</b>", "\"><script>alert(1)</script>", "<style>*{display:none}</style>", "<object data=x></object>", "<math><mi xlink:href=\"javascript:alert(1)\">",
        };
        var noMarkup = attacks.All(a => { var h = RichText.ToHtml(a); return !h.Contains("<script") && !h.Contains("<img") && !h.Contains("<svg") && !h.Contains("<iframe") && !h.Contains("<style") && !h.Contains("<object") && !h.Contains("<math") && !h.Contains("<b ") && !h.Contains("onerror=\"") && !System.Text.RegularExpressions.Regex.IsMatch(h, "<(?!/?(p|br|strong|em|u|h[4-6]|ul|ol|li|a)[ >/])"); });
        check(noMarkup, "R5 Rich-Text-Sicherheit: Skript-, Bild-, SVG-, iframe-, style- und Event-Handler-Angriffe erscheinen nur als kodierter Text");
        var badLinks = new[]
        {
            "[x](javascript:alert(1))", "[x](JaVaScRiPt:alert(1))", "[x](data:text/html;base64,PHNjcmlwdD4=)", "[x](//evil.com)", "[x](ftp://host/f)", "[x](vbscript:x)",
            "[x](https://user:pw@host/)", "[x](https://host/a\"onmouseover=\"alert(1))", "[x](/relativ)", "[x](mailto:a@b.de)", "[x](https://host/ a)", "[x](file:///etc/passwd)",
        };
        check(badLinks.All(l => !RichText.ToHtml(l).Contains("<a ")), "R6 Rich-Text-Sicherheit: nur absolute http(s)-Links ohne Zugangsdaten werden zu Links (javascript:, data:, //, ftp:, mailto:, relative, file: nicht)");
        var evilUrl = RichText.ToHtml("[t](https://ex.com/?a=**x**&b=++y++)");
        check(evilUrl.Contains("href=\"https://ex.com/?a=**x**&amp;b=++y++\"") && !evilUrl.Contains("<strong>") && !evilUrl.Contains("<u>"), "R6 Rich-Text-Sicherheit: Markup innerhalb einer URL verändert die Adresse nicht");
        var hrefs = System.Text.RegularExpressions.Regex.Matches(RichText.ToHtml("[a](https://x.de/\"onmouseover=\"y) [b](https://ok.de)"), "href=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        check(hrefs.Count == 1 && hrefs[0] == "https://ok.de", "R6 Rich-Text-Sicherheit: Anführungszeichen in URLs können kein Attribut ausbrechen");
        check(RichText.ToHtml("\uE100" + "0" + "\uE101") == "<p>0</p>" && RichText.ToHtml("\uE000" + "*x" + "\uE001") == "<p>*x</p>", "R7 Rich-Text-Sicherheit: interne Platzhalterzeichen im Eingabetext werden entfernt (keine Link-Einschleusung)");
        check(RichText.ToHtml(new string('a', 5000) + "**" + new string('b', 5000)).Length > 0 && RichText.ToHtml(string.Concat(Enumerable.Repeat("*a*", 3000))).Length > 0, "R8 Rich-Text: lange und pathologische Eingaben werden ohne Fehler verarbeitet");

        // Validierung
        check(RichText.Validate(new string('x', RichTextLimits.MaxLength), "definition") is null && RichText.Validate(new string('x', RichTextLimits.MaxLength + 1), "definition") is not null, "R9 Rich-Text-Grenzen: 20 000 Zeichen erlaubt, 20 001 abgelehnt");
        check(RichText.Validate("a\u0000b", "definition") is not null && RichText.Validate("a\r\nb\tc", "definition") is null, "R9 Rich-Text-Grenzen: Steuerzeichen werden abgelehnt, Zeilenumbruch und Tab sind erlaubt");
        check(RichText.ToPlainText("**a** [b](https://x.de)\n- c") == "a b\nc", "R10 Rich-Text: Klartext-Ableitung ohne Formatierung");
    }
}
