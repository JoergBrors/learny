using LearnCards.Web.Domain;
using LearnCards.Web.Services;

/// <summary>Avatar-Baukasten und Generator (ADR 0051, 0055): nur geprüfte Parameter, Offline-Fallback, Rückweisung ungültiger Werte, Foto als Inspiration.</summary>
public static class AvatarUnitTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(Avatars.Find("luna") is not null && Avatars.Find("gibtsnicht") is null, "Avatar: Voreinstellungen werden aufgelöst, unbekannte nicht");
        check(Avatars.Custom("#F5D0B5", "#C2410C", "curly", "#7C3AED", "glasses") is { } c && Avatars.Find(c.Id)?.HairStyle == "curly", "Avatar: eigener Avatar ist über seine Kennung wieder auflösbar");
        foreach (var bad in new[] { "custom:red|#000000|short|#7C3AED|none", "custom:#F5D0B5|#C2410C|dreadlocks|#7C3AED|none", "custom:#F5D0B5|#C2410C|short|#7C3AED|<script>",
                                    "custom:#F5D0B5\" onload=\"x|#C2410C|short|#7C3AED|none", "custom:#F5D0B5|#C2410C|short|#7C3AED", "custom:",
                                    "custom:#F5D0B5|#C2410C|short|#7C3AED|none|triangle|none", "custom:#F5D0B5|#C2410C|short|#7C3AED|none|oval|goatee", "custom:#F5D0B5|#C2410C|short|#7C3AED|none|oval" })
            check(Avatars.Find(bad) is null, "Avatar: ungültige Parameter werden abgewiesen (" + bad[..Math.Min(24, bad.Length)] + ")");

        var r = AvatarGeneratorService.FromKeywords("freundliche Person mit roten Locken, Brille und grünem Pullover");
        var p = Avatars.Find(r.Id);
        check(p is not null && !r.UsedAi && p.HairStyle == "curly" && p.Accessory == "glasses" && p.Hair == "#C2410C" && p.Outfit == "#10B981",
            "Avatar-Generator (Offline): Locken, Brille, rote Haare, grüner Pullover werden erkannt (Haar- und Kleidungsfarbe getrennt)");
        var d = Avatars.Find(AvatarGeneratorService.FromKeywords("irgendwas").Id);
        check(d is not null && d.HairStyle == "short" && d.Accessory == "none", "Avatar-Generator (Offline): unbekannte Beschreibung ergibt einen gültigen Standardavatar");

        // Erweiterter Baukasten (ADR 0055): Altkennungen bleiben gültig, Standardform bleibt im 5-teiligen Format
        var plain = Avatars.Custom("#F5D0B5", "#C2410C", "short", "#7C3AED", "none")!;
        var rich = Avatars.Custom("#F5D0B5", "#9CA3AF", "bald", "#7C3AED", "glasses", "square", "full")!;
        check(plain.Id.Split('|').Length == 5 && plain.Face == "oval" && plain.Beard == "none" && Avatars.Find("custom:#F5D0B5|#C2410C|short|#7C3AED|none")?.Face == "oval", "Avatar: Altkennung (5 Teile) bleibt gültig und ergibt Standardform");
        check(Avatars.Find(rich.Id) is { Face: "square", Beard: "full", HairStyle: "bald" }, "Avatar: Gesichtsform und Bart sind über die Kennung wieder auflösbar");
        var kw = AvatarGeneratorService.FromKeywords("Mann mit Glatze, Vollbart und eckigem Gesicht");
        check(Avatars.Find(kw.Id) is { HairStyle: "bald", Beard: "full", Face: "square" }, "Avatar-Generator (Offline): Glatze, Vollbart, eckiges Gesicht werden erkannt");
        var distinct = new[] { "Frau mit langen schwarzen Haaren", "Mann mit Glatze und Bart", "Person mit rotem Afro und Brille", "Kind mit Stachelfrisur", "Person mit grauem Scheitel und rundem Gesicht" }
            .Select(t => AvatarGeneratorService.FromKeywords(t).Id).Distinct().Count();
        check(distinct == 5, "Avatar-Generator (Offline): deutlich verschiedene Beschreibungen ergeben verschiedene Figuren");

        // Foto-Prüfung nach Inhalt (ADR 0055)
        static byte[] Pad(byte[] head, byte[] tail) { var b = new byte[400]; Array.Copy(head, b, head.Length); Array.Copy(tail, 0, b, b.Length - tail.Length, tail.Length); return b; }
        var jpg = Pad(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, new byte[] { 0xFF, 0xD9 });
        var png = Pad(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }, new byte[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0 });
        var webp = Pad(System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP"), Array.Empty<byte>()); BitConverter.GetBytes((uint)(webp.Length - 8)).CopyTo(webp, 4);
        check(AvatarPhoto.DetectMime(jpg) == "image/jpeg" && AvatarPhoto.DetectMime(png) == "image/png" && AvatarPhoto.DetectMime(webp) == "image/webp", "Avatar-Foto: JPG, PNG und WEBP werden am Inhalt erkannt");
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>" + new string(' ', 400));
        var gif = Pad(System.Text.Encoding.ASCII.GetBytes("GIF89a"), Array.Empty<byte>());
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>" + new string(' ', 400));
        var cut = jpg[..300]; cut[^1] = 7;
        check(new[] { html, gif, svg, cut, Array.Empty<byte>(), new byte[10], new byte[AvatarPhoto.MaxBytes + 1] }.All(b => AvatarPhoto.DetectMime(b) is null), "Avatar-Foto: HTML, SVG, GIF, abgeschnittene, leere, zu kleine und zu große Dateien werden abgewiesen");

        // Generator mit Foto: Stub-KI ohne Netz; das Foto geht nur in den Anfrage-Inhalt
        string? lastBody = null; var calls = 0;
        AvatarGeneratorService Make(bool configured, Func<string, string> reply)
        {
            var cfg = new LearnCards.Web.Infrastructure.AppConfig { OpenAiApiKey = configured ? "test-key" : "", OpenAiBaseUrl = "http://stub.invalid/v1" };
            var factory = new StubFactory(new StubHandler(b => { lastBody = b; calls++; return reply(b); }));
            return new AvatarGeneratorService(new OpenAiClient(cfg, factory), Microsoft.Extensions.Logging.Abstractions.NullLogger<AvatarGeneratorService>.Instance);
        }
        static string Ok(string json) => System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = json } } } });
        var visionJson = "{\"skin\":\"#8D5A3C\",\"hair\":\"#9CA3AF\",\"hairStyle\":\"bald\",\"face\":\"round\",\"beard\":\"full\",\"outfit\":\"#2563EB\",\"accessory\":\"glasses\",\"summary\":\"Comicfigur mit Glatze, Vollbart und Brille\"}";
        var ai = Make(true, _ => Ok(visionJson));
        var withPhoto = await ai.GenerateAsync("mag Blau und Berge", png);
        check(withPhoto.UsedAi && withPhoto.PhotoUsed && Avatars.Find(withPhoto.Id) is { Beard: "full", Face: "round", HairStyle: "bald", Accessory: "glasses" }, "Avatar mit Foto: Merkmale aus der KI-Antwort (Frisur, Gesichtsform, Bart, Brille) landen im Avatar");
        check(lastBody!.Contains("image_url") && lastBody.Contains("data:image/png;base64,") && lastBody.Contains("mag Blau und Berge") && lastBody.Contains("niemals"), "Avatar mit Foto: Bild geht als Bildteil in die Anfrage, Zusatzwunsch und Nicht-Identifizieren-Regel sind enthalten");
        await ai.GenerateAsync("nur Text");
        check(!lastBody!.Contains("image_url") && lastBody.Contains("nur Text"), "Avatar nur Text: kein Bildteil in der Anfrage (bestehender Ablauf)");
        var callsBefore = calls; var badPhoto = false;
        try { await ai.GenerateAsync("x", html); } catch (ArgumentException) { badPhoto = true; }
        check(badPhoto && calls == callsBefore, "Avatar mit ungültigem Foto: abgelehnt, bevor irgendetwas an die KI geht");
        var invalidAi = Make(true, _ => Ok("{\"skin\":\"rot\",\"hairStyle\":\"x\"}"));
        var fb = await invalidAi.GenerateAsync("rote Locken", png);
        check(!fb.UsedAi && !fb.PhotoUsed && fb.Note is not null && Avatars.Find(fb.Id)?.HairStyle == "curly", "Avatar mit Foto: ungültige KI-Antwort → Fallback auf Text mit Hinweis, Foto nicht ausgewertet");
        var noAi = Make(false, _ => throw new InvalidOperationException("keine KI erwartet"));
        var onlyPhoto = false;
        try { await noAi.GenerateAsync("", png); } catch (ArgumentException) { onlyPhoto = true; }
        var textOnly = await noAi.GenerateAsync("rote Locken");
        var textAndPhoto = await noAi.GenerateAsync("rote Locken", png);
        check(onlyPhoto && !textOnly.UsedAi && textAndPhoto.Note is not null && !textAndPhoto.PhotoUsed, "Avatar ohne KI: nur Foto → verständliche Meldung, Text weiterhin möglich, Foto mit Text wird nicht ausgewertet (Hinweis)");
        var boom = Make(true, _ => throw new HttpRequestException("Netz weg"));
        check(!(await boom.GenerateAsync("Brille", png)).UsedAi, "Avatar bei KI-Ausfall: Offline-Fallback bleibt erhalten");
        foreach (var s in Avatars.States) check(Avatars.States.Contains(s), "Avatar: Stimmung " + s + " zulässig");
    }
}

sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<string, string> _reply;
    public StubHandler(Func<string, string> reply) { _reply = reply; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_reply(body), System.Text.Encoding.UTF8, "application/json") };
    }
}

sealed class StubFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _h;
    public StubFactory(HttpMessageHandler h) { _h = h; }
    public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
}
