using System.Net;
using System.Security.Cryptography;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.ImageStorage;

namespace LearnCards.Web.Services.Certificates.Ots;

/// <summary>
/// Nativer OpenTimestamps-Client (ADR 0040), ersetzt den <c>ots</c>-CLI-Aufruf: kein Prozess, kein Python, kein WSL, kein PATH. Calendar-Protokoll nach python-opentimestamps
/// (<c>calendar.py</c>): <c>POST {calendar}/digest</c> mit dem Commitment als Rohbytes, <c>GET {calendar}/timestamp/{hex(commitment)}</c>, Header <c>Accept: application/vnd.opentimestamps.v1</c>,
/// Antwort = serialisierter Zeitstempelbaum (höchstens 10 000 Byte). Stempeln wie <c>ots stamp</c>: <c>sha256(datei) → append(16 Byte Zufall) → sha256 → Calendar-Commitment</c>.
/// Sicherheit: nur https, nur Calendars der Konfiguration (feste Allowlist, auch beim Upgrade), Betreiberzuordnung aus der Konfiguration (unbekannt = nicht unabhängig), keine Redirects, keine nicht öffentlichen Zieladressen (<see cref="SafeHttp"/>), Zeitlimit,
/// Größenlimit, begrenzte Parallelität. Gesendet wird ausschließlich das 32-Byte-Commitment.
/// </summary>
public sealed class NativeOtsClient : IOtsClient
{
    public const string Version = "learny-native-ots/1 (OpenTimestamps file v1, calendar API v1)";
    private const int MaxResponse = 10_000, MaxParallel = 4, MinCalendars = 2;
    private readonly OtsSettings _s; private readonly HttpClient _http;

    public NativeOtsClient(AppConfig cfg) : this(cfg, SafeHttp.CreateClient(allowPrivateNetwork: false, Math.Max(5, cfg.Ots.TimeoutSeconds))) { }
    public NativeOtsClient(AppConfig cfg, HttpClient http) { _s = cfg.Ots; _http = http; }

    public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>(Version);

    private IReadOnlyList<Uri> Allowed() => _s.Calendars.Select(c => Uri.TryCreate(c.TrimEnd('/') + "/", UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment) ? u : null)
        .Where(u => u is not null).Select(u => u!).DistinctBy(u => u.ToString(), StringComparer.OrdinalIgnoreCase).ToList();

    private static HttpRequestMessage Request(HttpMethod method, Uri url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.opentimestamps.v1");
        req.Headers.TryAddWithoutValidation("User-Agent", "learny-ots/1");
        return req;
    }

    private async Task<byte[]?> SendAsync(HttpRequestMessage req, bool notFoundIsNull, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(_s.TimeoutSeconds));
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (notFoundIsNull && res.StatusCode == HttpStatusCode.NotFound) return null;   // Commitment dem Calendar noch nicht bestätigt
            if (res.StatusCode != HttpStatusCode.OK) throw new OtsClientException($"Calendar antwortete mit HTTP {(int)res.StatusCode}.");   // auch 3xx: keine Weiterleitungen
            var body = await SafeHttp.ReadCappedAsync(res, "ots-calendar", MaxResponse + 1, timeout.Token);
            if (body.Length > MaxResponse) throw new OtsClientException("Calendar-Antwort überschreitet das Größenlimit.");
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OtsClientException("Zeitüberschreitung beim Calendar."); }
        catch (HttpRequestException e) { throw new OtsClientException("Calendar nicht erreichbar: " + e.GetType().Name); }
        catch (Exception e) when (e is ImageStorageException) { throw new OtsClientException("Calendar-Antwort abgelehnt (Größenlimit)."); }
    }

    private async Task<OtsNode> SubmitAsync(Uri calendar, byte[] commitment, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, new Uri(calendar, "digest"));
        req.Content = new ByteArrayContent(commitment); req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");   // wie urllib im Referenzclient
        var body = await SendAsync(req, false, ct) ?? throw new OtsClientException("Leere Calendar-Antwort.");
        try { return OtsFile.ParseTimestamp(body, commitment); }
        catch (Exception e) when (e is FormatException or NotSupportedException) { throw new OtsClientException("Calendar-Antwort ist kein gültiger Zeitstempel: " + e.Message); }
    }

    public async Task<byte[]> StampAsync(byte[] fileBytes, IReadOnlyList<string> calendars, CancellationToken ct)
    {
        var allowed = Allowed();
        var wanted = calendars.Select(c => c.TrimEnd('/') + "/").Distinct(StringComparer.OrdinalIgnoreCase).Where(c => allowed.Any(a => a.ToString().Equals(c, StringComparison.OrdinalIgnoreCase))).Select(c => new Uri(c)).ToList();
        string? OperatorOf(Uri u) => _s.OperatorOf(u.ToString());
        if (wanted.Select(OperatorOf).Where(o => o is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() < MinCalendars)
            throw new OtsClientException("Mindestens zwei Calendars unterschiedlicher, benannter Betreiber (OTS_CALENDARS: url|Betreiber) sind erforderlich; unbekannte Betreiber gelten nicht als unabhängig.");

        var digest = SHA256.HashData(fileBytes);
        var root = new OtsNode(digest);
        var tip = root.AddOp(OtsFile.OpAppend, RandomNumberGenerator.GetBytes(16)).AddOp(OtsFile.OpSha256, Array.Empty<byte>());   // Nonce schützt die Dateihashes vor Offenlegung gegenüber Calendars

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = wanted.Select(async cal =>
        {
            await gate.WaitAsync(ct);
            try { return (Cal: cal, Result: await SubmitAsync(cal, tip.Msg, ct), Error: (string?)null); }
            catch (OtsClientException e) { return (Cal: cal, Result: (OtsNode?)null, Error: e.Message); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        var ok = results.Where(r => r.Result is not null).ToList();
        var operators = ok.Select(r => OperatorOf(r.Cal)).Where(o => o is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (operators < MinCalendars)
            throw new OtsClientException($"Nur {ok.Count} von {wanted.Count} Calendars haben geantwortet, davon {operators} unabhängige Betreiber (mindestens {MinCalendars} erforderlich).");
        foreach (var r in ok) tip.Merge(r.Result!);
        return OtsFile.Serialize(OtsFile.OpSha256, digest, root);
    }

    public async Task<byte[]> UpgradeAsync(byte[] ots, CancellationToken ct)
    {
        var parsed = OtsFile.Parse(ots);
        if (!parsed.Valid || parsed.Root is null) throw new OtsClientException("Die Receipt ist ungültig oder nicht unterstützt: " + parsed.Error);
        var allowed = Allowed();
        var pending = OtsFile.PendingLeaves(parsed.Root)
            .Where(p => Uri.TryCreate(p.Uri.TrimEnd('/') + "/", UriKind.Absolute, out var u) && allowed.Any(a => a.ToString().Equals(u.ToString(), StringComparison.OrdinalIgnoreCase)))   // nie beliebige URIs aus der Receipt abrufen
            .ToList();
        var changed = false;
        using var gate = new SemaphoreSlim(MaxParallel);
        var fetches = pending.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var url = new Uri(new Uri(p.Uri.TrimEnd('/') + "/"), "timestamp/" + Convert.ToHexString(p.Node.Msg).ToLowerInvariant());
                using var req = Request(HttpMethod.Get, url);
                var body = await SendAsync(req, true, ct);   // null = 404 (noch nicht bestätigt) — der Calendar hat aber geantwortet
                return (p.Node, Upgraded: body is null ? null : OtsFile.ParseTimestamp(body, p.Node.Msg), Answered: true);
            }
            catch (Exception e) when (e is OtsClientException or FormatException or NotSupportedException) { return (p.Node, Upgraded: (OtsNode?)null, Answered: false); }   // Ausfall eines Calendars ist kein Fehler; Totalausfall wird unten gemeldet
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(fetches);
        if (pending.Count > 0 && results.All(r => !r.Answered)) throw new OtsClientException("Kein Calendar-Server hat geantwortet (Netzwerk oder Calendar nicht erreichbar).");
        foreach (var f in results)
        {
            if (f.Upgraded is null) continue;
            var before = OtsFile.Serialize(parsed.DigestTag, parsed.FileDigest, parsed.Root);
            f.Node.Merge(f.Upgraded);
            changed |= !OtsFile.Serialize(parsed.DigestTag, parsed.FileDigest, parsed.Root).AsSpan().SequenceEqual(before);
        }
        return changed ? OtsFile.Serialize(parsed.DigestTag, parsed.FileDigest, parsed.Root) : ots;   // unverändert, wenn nichts Neues vorliegt
    }
}
