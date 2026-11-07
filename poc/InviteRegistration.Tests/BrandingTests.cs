using System.Text.Json;
using System.Text.RegularExpressions;
using InviteRegistration;

// Branding-Konfiguration der External-ID-Seiten und Mailvorlage (ADR 0045): Assets aus dem Bestand, Farben aus app.css, Kontraste, deutsche Texte, Graph-Grenzen.
static partial class ExtendedTests
{
    static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LearnCards.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository-Wurzel nicht gefunden.");
    }

    static double Luminance(string hex)
    {
        double Ch(int v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var r = Convert.ToInt32(hex.Substring(1, 2), 16); var g = Convert.ToInt32(hex.Substring(3, 2), 16); var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
    }
    static double Contrast(string a, string b) { var (x, y) = (Luminance(a), Luminance(b)); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    static void BrandingTests(Action<string, bool, string?> check)
    {
        Console.WriteLine("Branding und Mailvorlage");
        var root = FindRepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "infra", "entra-external-id", "branding", "branding.json")));
        var cfg = doc.RootElement;
        var css = File.ReadAllText(Path.Combine(root, "src", "LearnCards.Web", "wwwroot", "app.css"));
        string CssVar(string name) => Regex.Match(css, "--" + name + @":\s*(#[0-9A-Fa-f]{6})").Groups[1].Value.ToUpperInvariant();   // erster Treffer = helles Thema

        var colors = cfg.GetProperty("colors");
        var bg = colors.GetProperty("backgroundColor").GetString()!.ToUpperInvariant();
        var text = colors.GetProperty("textColor").GetString()!.ToUpperInvariant();
        var accent = colors.GetProperty("linkAndAccentColor").GetString()!.ToUpperInvariant();
        check("Zielmarke ist 'Learny'; Tenant-Umbenennung wird nicht automatisch angewendet", cfg.GetProperty("targetBrandName").GetString() == "Learny" && cfg.GetProperty("tenantDisplayName").GetProperty("apply").GetBoolean() == false, null);
        check("Markenfarben stammen aus dem Bestand (app.css: --bg, --ink, --brand-700)", bg == CssVar("bg") && text == CssVar("ink") && accent == CssVar("brand-700"), $"{bg}/{CssVar("bg")} {text}/{CssVar("ink")} {accent}/{CssVar("brand-700")}");
        check("Kontrast Text auf Hintergrund ≥ 4,5:1 (WCAG AA)", Contrast(text, bg) >= 4.5, Contrast(text, bg).ToString("F2"));
        check("Kontrast Akzentfarbe auf Hintergrund ≥ 4,5:1", Contrast(accent, bg) >= 4.5, Contrast(accent, bg).ToString("F2"));
        check("Kontrast weiße Schrift auf Markenfarbe (Mail-Kopf) ≥ 4,5:1", Contrast("#FFFFFF", OtpMailTemplate.BrandColor) >= 4.5 && OtpMailTemplate.BrandColor.ToUpperInvariant() == CssVar("brand-700"), Contrast("#FFFFFF", OtpMailTemplate.BrandColor).ToString("F2"));
        check("Kontrast Code-Text auf Code-Fläche der Mail ≥ 4,5:1", Contrast(OtpMailTemplate.InkColor, OtpMailTemplate.PaperColor) >= 4.5, null);

        // Assets: nur Bestand bzw. daraus erzeugt; Graph-Grenzen für bannerLogo
        var assets = cfg.GetProperty("assets");
        var banner = Path.Combine(root, assets.GetProperty("bannerLogo").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var favicon = Path.Combine(root, assets.GetProperty("favicon").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        check("Favicon ist das vorhandene Learny-Asset (wwwroot/brand)", File.Exists(favicon) && favicon.Replace('\\', '/').Contains("/wwwroot/brand/"), null);
        var png = File.ReadAllBytes(banner);
        int W = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], H = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        check("Bannerlogo ist PNG und höchstens 36 × 245 Pixel (Graph-Grenze)", png[0] == 0x89 && png[1] == 0x50 && W <= 245 && H <= 36 && W > 0 && H > 0, $"{W}x{H}");
        check("Bannerlogo klein (< 20 KB)", png.Length < 20 * 1024, png.Length.ToString());
        check("Erzeugungsskript des Banners liegt im Repository", File.Exists(Path.Combine(root, "tools", "brand", "make_entra_banner.py")), null);

        // Texte
        var loc = cfg.GetProperty("localizations").GetProperty("de-DE");
        var signIn = loc.GetProperty("signInPageText").GetString()!; var hint = loc.GetProperty("usernameHintText").GetString()!;
        check("Deutsche Texte (de-DE) vorhanden und deutsch (Einladung, E-Mail, Einmalcode)", signIn.Contains("Einladung") && signIn.Contains("E-Mail") && signIn.Contains("Einmalcode") && hint == "E-Mail-Adresse", null);
        check("Textgrenzen: signInPageText ≤ 1024, usernameHintText ≤ 64, keine Links", signIn.Length <= 1024 && hint.Length <= 64 && !signIn.Contains("http") && !hint.Contains("http"), null);
        check("Standard-Branding und de-DE sind konsistent", cfg.GetProperty("default").GetProperty("signInPageText").GetString() == signIn, null);

        // Skripte: Trockenlauf ist Standard
        string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { root }.Concat(p).ToArray()));
        var brandingPs = Read("infra", "entra-external-id", "bootstrap", "branding.ps1");
        var otpPs = Read("infra", "entra-external-id", "bootstrap", "otp-extension.ps1");
        check("branding.ps1: schreibt nur mit -Apply (Trockenlauf als Standard)", brandingPs.Contains("[switch]$Apply") && brandingPs.Contains("if (-not $Apply) { return }") && !Regex.IsMatch(brandingPs, @"\$Apply\s*=\s*\$true"), null);
        check("otp-extension.ps1: gestuft (-Apply, -Activate), ohne Rückfall auf Microsoft als Standard", otpPs.Contains("[switch]$Apply") && otpPs.Contains("[switch]$Activate") && otpPs.Contains("if (-not $Apply) { return }") && otpPs.Contains("ohne Rueckfall (fail-closed)"), null);
        var tfVars = Read("infra", "entra-external-id", "10-app", "variables.tf");
        check("Terraform: OTP-API-App ist standardmäßig aus (leerer Host)", Regex.IsMatch(tfVars, "otp_extension_api_host\"[^}]*default\\s*=\\s*\"\""), null);

        // Mailvorlage
        var htmlTpl = OtpMailTemplate.BuildHtml("12345678", 30); var textTpl = OtpMailTemplate.BuildText("12345678", 30);
        check("Betreff: 'Ihr Learny-Anmeldecode'", OtpMailTemplate.Subject == "Ihr Learny-Anmeldecode", null);
        check("Mailtext deutsch, vollständig (Anrede, Code, Hinweis, Gruß) in HTML und Text", new[] { "Hallo,", "Sie haben einen Anmeldecode für Learny angefordert.", "Ihr Anmeldecode lautet:", "Geben Sie diesen Code auf der Learny-Anmeldeseite ein.", "Wenn Sie keinen Code angefordert haben, können Sie diese Nachricht ignorieren.", "Ihr Learny-Team" }
            .All(s => htmlTpl.Contains(s) && textTpl.Contains(s)), null);
        check("Gültigkeit wird angegeben und ist konfigurierbar", htmlTpl.Contains("30 Minuten") && OtpMailTemplate.BuildHtml("12345678", 10).Contains("10 Minuten"), null);
        check("HTML: Learny-Kopf, responsive (max-width), keine Bilder/Links/Skripte", htmlTpl.Contains(">Learny<") && htmlTpl.Contains("max-width:480px") && htmlTpl.Contains("lang=\"de\"")
            && !Regex.IsMatch(htmlTpl, "<img|href=|<script|http:|https:", RegexOptions.IgnoreCase), null);
        check("Code wird kodiert eingefügt (kein Markup durch den Code)", OtpMailTemplate.BuildHtml("A&B").Contains("A&amp;B") && !OtpMailTemplate.BuildHtml("A<B").Contains("A<B"), null);
        check("Code-Prüfung: 8 Ziffern erlaubt; Markup, Leerzeichen, zu kurz/lang abgelehnt", OtpMailTemplate.IsValidCode("19916462") && !OtpMailTemplate.IsValidCode("12 34") && !OtpMailTemplate.IsValidCode("<b>1234") && !OtpMailTemplate.IsValidCode("123") && !OtpMailTemplate.IsValidCode(new string('1', 17)) && !OtpMailTemplate.IsValidCode(null), null);

        // Einladungsmail (Learny-Branding, persönliche Begrüßung, Gültigkeit, Sicherheitshinweis)
        var until = new DateTime(2026, 12, 1, 10, 30, 0, DateTimeKind.Utc);
        var inv = InvitationMailTemplate.BuildHtml(new InvitationMail("a@b.test", "https://poc.example.org/invite/ABC123?x=1&y=2", until, "Anna <Admin>"));
        var anon = InvitationMailTemplate.BuildHtml(new InvitationMail("a@b.test", "https://poc.example.org/invite/ABC123", until));
        check("Einladungsmail: Betreff 'Einladung zu Learny', Learny-Kopf, deutsch, responsiv", InvitationMailTemplate.Subject == "Einladung zu Learny" && inv.Contains(">Learny<") && inv.Contains("lang=\"de\"") && inv.Contains("max-width:520px"), null);
        check("Einladungsmail: persönliche Begrüßung (kodiert), sonst allgemeine Anrede", inv.Contains("Hallo Anna &lt;Admin&gt;,") && !inv.Contains("<Admin>") && anon.Contains("Hallo,"), null);
        check("Einladungsmail: Link (kodiert) als Schaltfläche und sichtbar, Gültigkeit, einmalig, Sicherheitshinweis", inv.Contains("href=\"https://poc.example.org/invite/ABC123?x=1&amp;y=2\"") && inv.Contains("01.12.2026 10:30 UTC") && inv.Contains("nur einmal") && inv.Contains("Sicherheitshinweis") && inv.Contains("Jetzt registrieren"), null);
        check("Einladungsmail: keine Bilder, Skripte oder Tracking-Pixel", !Regex.IsMatch(inv, "<img|<script|<iframe|<link", RegexOptions.IgnoreCase), null);
    }
}
