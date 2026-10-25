using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LearnCards.Web.Domain;

/// <summary>Grenzen für formatierte Kartentexte (ADR 0020).</summary>
public static class RichTextLimits
{
    /// <summary>Höchstlänge eines formatierten Feldes (Zeichen des Markups).</summary>
    public const int MaxLength = 20_000;
    public const int MaxLinkLength = 2_000;
}

/// <summary>
/// Begrenztes, eigenes Rich-Text-Format für die Textfelder einer Lernkarte (<c>definition</c>, <c>how_it_works</c>, <c>context</c>, <c>key_fact</c>, <c>reference_answer</c>) — ADR 0020.
/// Gespeichert wird <b>nie HTML</b>, sondern eine kleine Markdown-Teilmenge als normaler Text; bestehender Klartext bleibt gültig.
/// Die Ausgabe (<see cref="ToHtml"/>) entsteht ausschließlich serverseitig aus diesem Markup: der gesamte Text wird zuerst HTML-kodiert, danach werden nur die
/// unten aufgelisteten Elemente erzeugt. Skripte, Event-Handler, fremde URL-Schemata, eingebettete Ressourcen oder SVG sind dadurch nicht darstellbar.
/// <list type="bullet">
/// <item>Absätze: Leerzeile; Zeilenumbruch: einfacher Zeilenwechsel</item>
/// <item>Überschriften: <c># </c>, <c>## </c>, <c>### </c> am Zeilenanfang</item>
/// <item>Fett <c>**text**</c>, kursiv <c>*text*</c> oder <c>_text_</c>, unterstrichen <c>++text++</c> (Kombinationen wie <c>**_text_**</c> sind möglich)</item>
/// <item>Listen: <c>- </c> (Aufzählung) bzw. <c>1. </c> (Nummerierung) am Zeilenanfang</item>
/// <item>Links: <c>[Text](https://adresse)</c> — nur http/https</item>
/// <item>Maskierung: <c>\*</c>, <c>\+</c>, <c>\[</c>, <c>\]</c>, <c>\\</c>, <c>\#</c>, <c>\-</c> ergeben das Zeichen selbst</item>
/// </list>
/// </summary>
public static partial class RichText
{
    // Platzhalter (Private-Use-Zeichen) für maskierte Zeichen und herausgelöste Links
    private const char EscBase = '\uE000';
    private const string EscapableChars = "*+[]\\#-()";

    [GeneratedRegex(@"\\([*+\[\]\\#\-()])")] private static partial Regex EscapeRx();
    [GeneratedRegex(@"\[([^\]\n]{1,300})\]\((https?://[^\s()<>]{1,2000})\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"(?<![\*\w])\*\*(?=[^\s*])(.+?)(?<=[^\s*])\*\*(?![\*\w])")] private static partial Regex BoldRx();
    [GeneratedRegex(@"(?<![\*\w])\*(?=[^\s*])([^*\n]+?)(?<=[^\s*])\*(?![\*\w])")] private static partial Regex ItalicRx();
    [GeneratedRegex(@"(?<![_\w])_(?=[^\s_])([^_\n]+?)(?<=[^\s_])_(?![_\w])")] private static partial Regex ItalicUnderscoreRx();
    [GeneratedRegex(@"(?<![+\w])\+\+(?=[^\s+])(.+?)(?<=[^\s+])\+\+(?![+\w])")] private static partial Regex UnderlineRx();
    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^-\s+(.+)$")] private static partial Regex BulletRx();
    [GeneratedRegex(@"^\d{1,3}[.)]\s+(.+)$")] private static partial Regex OrderedRx();
    [GeneratedRegex(@"\uE100(\d+)\uE101")] private static partial Regex LinkTokenRx();

    /// <summary>Normalisiert Zeilenenden und entfernt Steuerzeichen (außer Zeilenwechsel und Tab). Wirft nichts; Längenprüfung siehe <see cref="Validate"/>.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') continue; sb.Append('\n'); continue; }
            if (c == '\n' || c == '\t') { sb.Append(c); continue; }
            if (char.IsControl(c) || (c >= '\uE000' && c <= '\uF8FF')) continue;   // Steuerzeichen und die internen Platzhalterzeichen nie übernehmen
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Prüft ein formatiertes Feld; Rückgabe: Fehlertext oder <c>null</c>.</summary>
    public static string? Validate(string? text, string fieldName)
    {
        if (text is null) return null;
        if (text.Length > RichTextLimits.MaxLength) return $"'{fieldName}' ist zu lang (höchstens {RichTextLimits.MaxLength} Zeichen).";
        foreach (var c in text)
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) return $"'{fieldName}' enthält unzulässige Steuerzeichen.";
        return null;
    }

    /// <summary>Wahr, wenn die Adresse als Link ausgegeben werden darf (absolut, http/https, ohne Steuerzeichen/Leerraum, begrenzte Länge).</summary>
    public static bool IsSafeLinkUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= RichTextLimits.MaxLinkLength && url.All(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch) && ch is not ('<' or '>' or '"' or '\''))
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>Erzeugt sicheres HTML (Fragment) aus dem Markup. Leerer Text → leerer String.</summary>
    public static string ToHtml(string? markup)
    {
        var text = Normalize(markup);
        if (text.Trim().Length == 0) return "";
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        var paragraph = new List<string>();
        string? listType = null;   // "ul" | "ol"

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            sb.Append("<p>").Append(string.Join("<br>", paragraph.Select(Inline))).Append("</p>");
            paragraph.Clear();
        }
        void CloseList() { if (listType is not null) { sb.Append("</").Append(listType).Append('>'); listType = null; } }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { FlushParagraph(); CloseList(); continue; }

            if (HeadingRx().Match(line) is { Success: true } h)
            {
                FlushParagraph(); CloseList();
                var level = h.Groups[1].Length + 3;   // # → h4, ## → h5, ### → h6 (die Seitenüberschrift bleibt h1/h2)
                sb.Append("<h").Append(level).Append('>').Append(Inline(h.Groups[2].Value)).Append("</h").Append(level).Append('>');
                continue;
            }
            if (BulletRx().Match(line) is { Success: true } b)
            {
                FlushParagraph();
                if (listType != "ul") { CloseList(); sb.Append("<ul>"); listType = "ul"; }
                sb.Append("<li>").Append(Inline(b.Groups[1].Value)).Append("</li>");
                continue;
            }
            if (OrderedRx().Match(line) is { Success: true } o)
            {
                FlushParagraph();
                if (listType != "ol") { CloseList(); sb.Append("<ol>"); listType = "ol"; }
                sb.Append("<li>").Append(Inline(o.Groups[1].Value)).Append("</li>");
                continue;
            }
            CloseList();
            paragraph.Add(line);
        }
        FlushParagraph(); CloseList();
        return sb.ToString();
    }

    /// <summary>Reiner Text ohne Formatierungszeichen (z. B. für Prompts, Vorschauen in Listen).</summary>
    public static string ToPlainText(string? markup)
    {
        var html = ToHtml(markup);
        var noTags = Regex.Replace(html, "<(br|/p|/li|/h[4-6])>", "\n");
        noTags = Regex.Replace(noTags, "<[^>]+>", "");
        return WebUtility.HtmlDecode(noTags).Trim();
    }

    private static string Inline(string line)
    {
        // 1) Maskierungen durch Platzhalter ersetzen
        var s = EscapeRx().Replace(line, m => ((char)(EscBase + EscapableChars.IndexOf(m.Groups[1].Value[0]))).ToString());
        // 2) Alles kodieren: ab hier ist jedes Zeichen „<“, „>“, „&“, „"“, „'“ neutralisiert
        s = WebUtility.HtmlEncode(s);
        // 3) Links herauslösen (URL wird nicht weiter formatiert)
        var links = new List<(string Text, string Url)>();
        s = LinkRx().Replace(s, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups[2].Value);
            if (!IsSafeLinkUrl(url)) return m.Value;
            links.Add((m.Groups[1].Value, m.Groups[2].Value));
            return $"\uE100{links.Count - 1}\uE101";
        });
        // 4) Hervorhebungen
        s = Emphasis(s);
        // 5) Links einsetzen (Text ebenfalls hervorgehoben)
        s = LinkTokenRx().Replace(s, m =>
        {
            var (text, url) = links[int.Parse(m.Groups[1].Value)];
            return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">{Emphasis(text)}</a>";
        });
        // 6) Platzhalter zurück in (kodierte) Literale
        var outp = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= EscBase && c < EscBase + EscapableChars.Length) outp.Append(WebUtility.HtmlEncode(EscapableChars[c - EscBase].ToString()));
            else outp.Append(c);
        }
        return outp.ToString();
    }

    private static string Emphasis(string s)
    {
        s = BoldRx().Replace(s, "<strong>$1</strong>");
        s = UnderlineRx().Replace(s, "<u>$1</u>");
        s = ItalicRx().Replace(s, "<em>$1</em>");
        s = ItalicUnderscoreRx().Replace(s, "<em>$1</em>");
        return s;
    }
}
