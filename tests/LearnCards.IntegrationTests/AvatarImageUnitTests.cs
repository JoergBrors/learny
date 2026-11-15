using System.Net;
using System.Text;
using System.Text.Json;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services;
using LearnCards.Web.Services.ImageStorage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>KI-Bildavatare (ADR 0056): Anfrageaufbau, Antwortprüfung, Fehler, Limits, Persistenz — ohne Netz mit Stub-Anbieter. Die Bildqualität wird hier NICHT geprüft.</summary>
public static class AvatarImageUnitTests
{
    private sealed class Stub : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Handler = (_, _) => throw new InvalidOperationException();
        public CancellationToken Ct; public int Calls; public string? LastPath, LastBody; public string? LastContentType;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Ct = ct;
            LastPath = request.RequestUri!.AbsolutePath;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastBody = request.Content is null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(ct));
            return await Handler(request, LastBody);
        }
    }
    private sealed class Factory : IHttpClientFactory { private readonly HttpMessageHandler _h; public Factory(HttpMessageHandler h) { _h = h; } public HttpClient CreateClient(string name) => new(_h, false); }

    private static byte[] Webp(int size = 600, byte fill = 1)
    {
        var b = new byte[size]; Array.Fill(b, fill);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); BitConverter.GetBytes((uint)(size - 8)).CopyTo(b, 4); Encoding.ASCII.GetBytes("WEBP").CopyTo(b, 8);
        return b;
    }
    private static byte[] Png()
    {
        var b = new byte[400]; new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        new byte[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' }.CopyTo(b, b.Length - 8); return b;
    }
    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Img(byte[] bytes) => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(bytes) } } }));

    private static (AvatarImageService Svc, Stub Stub) Make(Action<AvatarImageSettings>? tune = null, bool key = true)
    {
        var cfg = new AppConfig { OpenAiApiKey = key ? "test-key" : "", OpenAiBaseUrl = "http://stub.invalid/v1" };
        tune?.Invoke(cfg.AvatarImages);
        var stub = new Stub();
        return (new AvatarImageService(cfg, new Factory(stub), NullLogger<AvatarImageService>.Instance), stub);
    }

    private static async Task<string?> Fails(Func<Task> act) { try { await act(); return null; } catch (AvatarImageException ex) { return ex.Message; } }

    public static async Task RunAsync(Action<bool, string> check, string tmp)
    {
        // Einstellungen
        check(AvatarImageSettings.IsValidSize("1024x1024") && AvatarImageSettings.IsValidSize("816x816") && AvatarImageSettings.IsValidSize("1536x1024"), "Avatarbild: gültige Größen (Vielfache von 16, genug Pixel) werden akzeptiert");
        check(new[] { "512x512", "1000x1000", "4096x4096", "1024", "axb", "3072x512", "2048x2064" }.All(s => !AvatarImageSettings.IsValidSize(s)), "Avatarbild: unzulässige Größen (zu klein, kein Vielfaches von 16, zu groß, Seitenverhältnis) werden abgewiesen");
        var d = new AvatarImageSettings();
        check(d.Enabled && d.Model == "gpt-image-2.5-flare" && d.EditModel == "gpt-image-2.5-sunburst" && d.Quality == "medium" && d.DailyLimitPerUser == 10, "Avatarbild: Standardwerte (eigene Bildmodelle, mittlere Qualität, Tageslimit)");

        // Verfügbarkeit
        check(!Make(key: false).Svc.Available && !Make(s => s.Enabled = false).Svc.Available && Make().Svc.Available, "Avatarbild: ohne Schlüssel oder bei AVATAR_IMAGE_ENABLED=false nicht verfügbar");
        var off = Make(s => s.Enabled = false);
        check(await Fails(() => off.Svc.GenerateAsync("u1", "realistic", "Brille", null)) is not null && off.Stub.Calls == 0, "Avatarbild: abgeschaltet → kein Aufruf");

        // Prompt
        var pr = AvatarImageService.BuildPrompt("realistic", true, "graue Haare <script>x</script>");
        check(pr.Contains("only inspiration") && pr.Contains("Do not identify") && pr.Contains("<wish>graue Haare scriptx/script</wish>") && !pr.Contains("<script>"), "Avatarbild: Foto nur Inspiration, keine Identifizierung, Wunsch ohne Markup und nur als Beschreibung");
        check(!AvatarImageService.BuildPrompt("classic", false, "").Contains("attached photo") && AvatarImageService.BuildPrompt("classic", false, "").Contains("halftone"), "Avatarbild: Text-only-Auftrag ohne Fotoanweisung, Stil Klassischer Comic");
        check(new[] { "realistic", "cartoon3d", "classic" }.Select(s => AvatarImageService.BuildPrompt(s, false, "x")).Distinct().Count() == 3, "Avatarbild: drei Stile ergeben drei verschiedene Aufträge");

        // Ohne Foto: /images/generations, eigenes Modell, WebP, Größe/Qualität aus der Konfiguration
        var a = Make();
        a.Stub.Handler = (_, _) => Task.FromResult(Img(Webp()));
        var r = await a.Svc.GenerateAsync("u1", "cartoon3d", "Frau mit Zopf", null);
        check(a.Stub.LastPath!.EndsWith("/images/generations") && a.Stub.LastContentType == "application/json" && a.Stub.LastBody!.Contains("gpt-image-2.5-flare") && a.Stub.LastBody.Contains("\"output_format\":\"webp\"")
              && a.Stub.LastBody.Contains("1024x1024") && a.Stub.LastBody.Contains("\"quality\":\"medium\"") && a.Stub.LastBody.Contains("Frau mit Zopf") && r.Mime == "image/webp" && r.Model == "gpt-image-2.5-flare", "Avatarbild ohne Foto: Generation-Endpunkt, Bildmodell, Format und Größe aus der Konfiguration");
        check(!a.Stub.LastBody!.Contains("gpt-4o"), "Avatarbild: das Chat-Modell wird nicht für Bilder verwendet");

        // Mit Foto: /images/edits, multipart, Bearbeitungsmodell, Foto als image-Teil
        var photo = Png();
        a.Stub.Handler = (_, _) => Task.FromResult(Img(Png()));
        var e = await a.Svc.GenerateAsync("u2", "realistic", "mit Brille", photo);
        check(a.Stub.LastPath!.EndsWith("/images/edits") && a.Stub.LastContentType == "multipart/form-data" && a.Stub.LastBody!.Contains("gpt-image-2.5-sunburst") && a.Stub.LastBody.Contains("name=image") && a.Stub.LastBody.Contains("mit Brille")
              && e.Mime == "image/png" && e.Model == "gpt-image-2.5-sunburst", "Avatarbild mit Foto: Edit-Endpunkt, multipart, Foto als Bildteil, Zusatzwunsch im Auftrag");

        // Eingaben
        var calls = a.Stub.Calls;
        var bad1 = await Fails(() => a.Svc.GenerateAsync("u3", "realistic", "x", Encoding.UTF8.GetBytes("<html>" + new string(' ', 400))));
        var bad2 = await Fails(() => a.Svc.GenerateAsync("u3", "realistic", "", null));
        var bad3 = await Fails(() => a.Svc.GenerateAsync("u3", "unbekannt", "x", null));
        check(bad1 is not null && bad2 is not null && bad3 is not null && a.Stub.Calls == calls, "Avatarbild: ungültiges Foto, leere Eingabe und unbekannter Stil werden vor dem Aufruf abgewiesen");

        // Antworten des Anbieters
        var f = Make();
        async Task<string?> With(Func<HttpResponseMessage> resp) { f.Stub.Handler = (_, _) => Task.FromResult(resp()); return await Fails(() => f.Svc.GenerateAsync("uf" + Guid.NewGuid().ToString("N")[..6], "realistic", "x", null)); }
        var mod = await With(() => Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"moderation_blocked\",\"type\":\"image_generation_user_error\",\"message\":\"GEHEIMER-TEXT-DER-EINGABE\"}}"));
        check(mod is not null && mod.Contains("abgelehnt") && !mod.Contains("GEHEIMER"), "Avatarbild: Moderationsablehnung → verständliche Meldung ohne Anbieter-Text");
        check((await With(() => Json((HttpStatusCode)429, "{}")))!.Contains("ausgelastet"), "Avatarbild: 429 → Hinweis auf Auslastung/Kontingent");
        check((await With(() => Json(HttpStatusCode.Forbidden, "{}")))!.Contains("freigeschaltet"), "Avatarbild: 403 → Modell nicht freigeschaltet");
        check((await With(() => Json(HttpStatusCode.InternalServerError, "{}")))!.Contains("unverändert"), "Avatarbild: Serverfehler → Meldung, Avatar bleibt unverändert");
        check((await With(() => Json(HttpStatusCode.OK, "{\"data\":[{\"url\":\"http://169.254.169.254/x.png\"}]}")))!.Contains("kein Bild"), "Avatarbild: Antwort nur mit Adresse → verworfen (Adressen werden nie abgerufen)");
        check(await With(() => Json(HttpStatusCode.OK, "{\"data\":[{\"b64_json\":\"!!!kein-base64!!!\"}]}")) is not null, "Avatarbild: ungültiges Base64 wird abgewiesen");
        check(await With(() => Img(Encoding.UTF8.GetBytes("<svg onload=alert(1)>" + new string(' ', 400)))) is not null, "Avatarbild: Antwort, die kein JPG/PNG/WEBP ist (z. B. SVG), wird abgewiesen");
        check(await With(() => Img(Webp(1_600_000))) is not null, "Avatarbild: zu großes Ergebnis wird abgewiesen");
        check(await With(() => Json(HttpStatusCode.OK, "{}")) is not null && await With(() => Json(HttpStatusCode.OK, "kein json")) is not null, "Avatarbild: unerwartete Antwortstruktur → Fehlermeldung statt Absturz");

        // Zeitüberschreitung: die Frist (AVATAR_IMAGE_TIMEOUT_SECONDS) bricht eine hängende Anfrage ab
        var t = Make(s => s.TimeoutSeconds = 1);
        t.Stub.Handler = async (_, _) => { await Task.Delay(Timeout.Infinite, t.Stub.Ct); return Img(Webp()); };
        var timedOut = await Fails(() => t.Svc.GenerateAsync("ut", "realistic", "x", null));
        check(timedOut is not null && timedOut.Contains("zu lange") && t.Stub.Calls == 1, "Avatarbild: Zeitüberschreitung → verständliche Meldung, kein zweiter Versuch");

        // Limits: eine laufende Erzeugung je Benutzer, Tageslimit, keine Wiederholung
        var l = Make(s => s.DailyLimitPerUser = 2);
        var gate = new TaskCompletionSource(); var started = new TaskCompletionSource();
        l.Stub.Handler = async (_, _) => { started.TrySetResult(); await gate.Task; return Img(Webp()); };
        var first = l.Svc.GenerateAsync("ul", "realistic", "a", null);
        await started.Task;
        var second = await Fails(() => l.Svc.GenerateAsync("ul", "realistic", "b", null));
        check(second is not null && second.Contains("läuft bereits") && l.Stub.Calls == 1, "Avatarbild: zweiter Klick während einer laufenden Erzeugung löst keinen zweiten Aufruf aus");
        gate.SetResult(); await first;
        l.Stub.Handler = (_, _) => Task.FromResult(Img(Webp()));
        await l.Svc.GenerateAsync("ul", "realistic", "c", null);
        var third = await Fails(() => l.Svc.GenerateAsync("ul", "realistic", "d", null));
        check(third is not null && third.Contains("Tageslimit") && l.Svc.RemainingToday("ul") == 0 && l.Svc.RemainingToday("anderer") == 2, "Avatarbild: Tageslimit je Benutzer greift, andere Benutzer bleiben unberührt");
        var g = Make(s => s.MaxConcurrent = 1);
        var gate2 = new TaskCompletionSource(); var started2 = new TaskCompletionSource();
        g.Stub.Handler = async (_, _) => { started2.TrySetResult(); await gate2.Task; return Img(Webp()); };
        var run = g.Svc.GenerateAsync("ug1", "realistic", "a", null); await started2.Task;
        var busy = await Fails(() => g.Svc.GenerateAsync("ug2", "realistic", "b", null));
        check(busy is not null && busy.Contains("ausgelastet") && g.Stub.Calls == 1, "Avatarbild: globale Parallelitätsgrenze greift");
        gate2.SetResult(); await run;
        var err = Make(); err.Stub.Handler = (_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"));
        await Fails(() => err.Svc.GenerateAsync("ue", "realistic", "x", null));
        check(err.Stub.Calls == 1, "Avatarbild: genau ein Aufruf je Anfrage, keine automatische Wiederholung");

        // Persistenz und Kennung
        Directory.CreateDirectory(tmp);
        await using var db = new SqliteDatabase(Path.Combine(tmp, "avatar-img.db"));
        await db.InitAsync();
        var cfg = new AppConfig();
        var repo = new CardRepository(db, new CardImageService(db, new ImageStorageRegistry(cfg, db), new NullImageCache(), cfg, NullLogger<CardImageService>.Instance), new QuizPoolService(db));
        var bytes = Webp(5000, 9);
        var id = await repo.SaveAvatarImageAsync("alice", bytes, "gpt-image-2.5-flare", "realistic", 1_500_000);
        var pref = await repo.GetAvatarAsync("alice");
        var img = await repo.GetAvatarImageAsync("alice");
        check(Avatars.IsImageId(id) && pref.Preset == id && !pref.Hidden && img is not null && img.Value.Bytes.SequenceEqual(bytes) && img.Value.Mime == "image/webp" && Avatars.ImageVersionOf(id) == img.Value.Version,
            "Avatarbild: übernommenes Bild wird gespeichert, Kennung gesetzt, Bytes unverändert lesbar (nach erneutem Laden = nach Abmeldung/Anmeldung)");
        check(await repo.GetAvatarImageAsync("bob") is null && (await repo.GetAvatarAsync("bob")).Preset == "", "Avatarbild: Benutzer sehen nur ihr eigenes Bild");
        var rej = false; try { await repo.SaveAvatarImageAsync("alice", Encoding.UTF8.GetBytes("<svg>" + new string(' ', 400)), "m", "s", 1_500_000); } catch (ArgumentException) { rej = true; }
        var rej2 = false; try { await repo.SaveAvatarImageAsync("alice", Webp(5000), "m", "s", 1000); } catch (ArgumentException) { rej2 = true; }
        check(rej && rej2 && (await repo.GetAvatarImageAsync("alice"))!.Value.Version == Avatars.ImageVersionOf(id), "Avatarbild: ungültige oder zu große Bytes werden beim Speichern abgewiesen, das bisherige Bild bleibt");
        var id2 = await repo.SaveAvatarImageAsync("alice", Webp(5000, 3), "m", "classic", 1_500_000);
        check(id2 != id && (await repo.GetAvatarAsync("alice")).Preset == id2, "Avatarbild: Übernehmen ersetzt das frühere Bild");
        await repo.SaveAvatarAsync("alice", "luna", false); await repo.DeleteAvatarImageAsync("alice");
        check(await repo.GetAvatarImageAsync("alice") is null && (await repo.GetAvatarAsync("alice")).Preset == "luna", "Avatarbild: Wechsel auf einen Baukasten-Avatar entfernt das Bild nur auf Benutzeraktion; SVG-Avatare bleiben gültig");
        foreach (var bad in new[] { "img:", "img:XYZ", "img:0123456789abcdef0", "img:0123456789ABCDEF", "img:../../etc" }) check(!Avatars.IsValid(bad), "Avatarbild: ungültige Kennung abgewiesen (" + bad + ")");
        var rawPref = false; try { await repo.SaveAvatarAsync("alice", "img:zzzz", false); } catch (ArgumentException) { rawPref = true; }
        check(rawPref && Avatars.IsValid("img:0123456789abcdef") && Avatars.IsValid("luna") && Avatars.IsValid("custom:#F5D0B5|#C2410C|short|#7C3AED|none"), "Avatarbild: nur wohlgeformte Bildkennungen werden gespeichert; Altkennungen bleiben gültig");
    }
}
