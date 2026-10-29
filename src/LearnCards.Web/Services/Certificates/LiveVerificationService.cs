using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Data;
using LearnCards.Web.Domain;
using LearnCards.Web.Services.Certificates.Ots;
using LearnCards.Web.Services.Pdf;

namespace LearnCards.Web.Services.Certificates;

/// <summary>Zustände eines Prüfschritts der Live-Anzeige. Nur <see cref="Running"/> wird animiert.</summary>
public static class StepState
{
    public const string Pending = "pending", Running = "running", Ok = "ok", Failed = "failed", Unverifiable = "unverifiable", Skipped = "skipped", Waiting = "waiting";
}

public sealed record LiveStep(string Key, string Label, string State, string? Detail);

/// <summary>Unveränderlicher Stand eines Prüfauftrags für die Oberfläche.</summary>
public sealed record LiveSnapshot(string CertificateId, string Phase, string Outcome, bool Done, IReadOnlyList<LiveStep> Steps, LiveStored? Stored, LiveOts? Ots, string? Notice, DateTime StartedAt, DateTime? FinishedAt);

/// <summary>Zuletzt <b>gespeicherter</b> Stand der Receipt (vor dem laufenden Auftrag) — getrennt vom Ergebnis der aktuellen Prüfung.</summary>
public sealed record LiveStored(int ProofVersion, string AnchorStatus, long? BitcoinHeight, DateTime? LastVerifiedAt, DateTime? LastCheckedAt);

/// <summary>Tatsächlich geprüfte Angaben zur Receipt (nur gefüllt, was nachgewiesen ist).</summary>
public sealed record LiveOts(int ProofVersion, string State, string Detail, long? Height, string? BlockHash, DateTime? BlockTime, long Confirmations, int RequiredConfirmations, string Source, bool Independent, DateTime? LastVerifiedAt, bool HasReceipt, bool HasManifestJws);

/// <summary>Ein laufender oder abgeschlossener Prüfauftrag; mehrere Besucher beobachten denselben Auftrag.</summary>
public sealed class LiveRun
{
    private readonly object _lock = new();
    private readonly List<LiveStep> _steps;
    private string _phase = "queued", _outcome = ""; private bool _done; private LiveStored? _stored; private LiveOts? _ots; private string? _notice; private DateTime? _finished;
    public string CertificateId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get { lock (_lock) return _finished; } }
    public bool Done { get { lock (_lock) return _done; } }
    /// <summary>Wird bei jeder echten Zustandsänderung ausgelöst (Schrittwechsel, Phase, Ergebnis). Kann von beliebigen Threads kommen.</summary>
    public event Action? Changed;

    public static readonly (string Key, string Label)[] Definition =
    {
        ("load", "Zertifikat geladen"),
        ("signature", "Credential-Signaturen geprüft"),
        ("issuer", "Ausstellerschlüssel überprüft"),
        ("manifest", "Integritätsmanifest und Manifest-JWS geprüft"),
        ("ots_read", "OpenTimestamps-Nachweis eingelesen"),
        ("ots_bind", "Bindung des Nachweises an die Manifest-JWS geprüft"),
        ("calendars", "Calendar-Server abgefragt"),
        ("bitcoin", "Bitcoin-Attestierung überprüft"),
        ("depth", "Bestätigungstiefe kontrolliert"),
        ("done", "Prüfergebnis abgeschlossen"),
    };

    public LiveRun(string certificateId) { CertificateId = certificateId; _steps = Definition.Select(d => new LiveStep(d.Key, d.Label, StepState.Pending, null)).ToList(); }

    private void Raise() { try { Changed?.Invoke(); } catch (Exception) { /* ein fehlerhafter Beobachter darf die Prüfung nicht stören */ } }

    public void Phase(string phase) { lock (_lock) _phase = phase; Raise(); }
    public void Step(string key, string state, string? detail = null)
    {
        lock (_lock) { var i = _steps.FindIndex(s => s.Key == key); _steps[i] = _steps[i] with { State = state, Detail = detail }; }
        Raise();
    }
    public void SetStored(LiveStored? s) { lock (_lock) _stored = s; }
    public void SetOts(LiveOts? o) { lock (_lock) _ots = o; Raise(); }
    public void Finish(string outcome, string? notice)
    {
        lock (_lock) { _outcome = outcome; _notice = notice; _phase = outcome == "notfound" ? "failed" : "completed"; _done = true; _finished = DateTime.UtcNow; }
        Raise();
    }

    public LiveSnapshot Snapshot() { lock (_lock) return new(CertificateId, _phase, _outcome, _done, _steps.ToList(), _stored, _ots, _notice, StartedAt, _finished); }
}

/// <summary>
/// Live-Prüfung auf der Seite <c>/verify/{id}</c> (ADR 0039/0040): ein serverseitiger, geteilter Prüfauftrag je Zertifikat. Der Auftrag nutzt ausschließlich die vorhandenen Dienste
/// (<see cref="CertificateService.VerifyAsync"/>, <see cref="IntegrityManifest"/>, <see cref="OtsMaintenanceService"/>) — keine zweite Prüflogik — und meldet nur echte Zustandswechsel.
/// Mehrere Besucher beobachten denselben Auftrag; abgeschlossene Aufträge werden kurz zwischengespeichert, Calendar-Abfragen sind zusätzlich über <c>last_checked_at</c> gedrosselt.
/// Der Auftrag gehört dem Dienst (nicht dem Besucher) und wird nicht abgebrochen, wenn ein Besucher die Seite verlässt.
/// </summary>
public sealed class LiveVerificationService : IDisposable
{
    public static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CalendarMinInterval = TimeSpan.FromMinutes(5);
    private const int MaxParallel = 4, MaxCached = 500;

    private readonly IDatabase _db; private readonly CertificateService _certs; private readonly OtsMaintenanceService _ots;
    private readonly Dictionary<string, LiveRun> _runs = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(MaxParallel);
    private readonly CancellationTokenSource _stop = new();
    /// <summary>Anzahl tatsächlich gestarteter Prüfaufträge (für Tests: Mehrfachaufrufe dürfen keine doppelten Aufträge erzeugen).</summary>
    public int StartedRuns { get; private set; }

    public LiveVerificationService(IDatabase db, CertificateService certs, OtsMaintenanceService ots) { _db = db; _certs = certs; _ots = ots; }

    /// <summary>Liefert den laufenden (oder frisch abgeschlossenen) Auftrag oder startet genau einen neuen.</summary>
    public LiveRun GetOrStart(string certificateId)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(certificateId, out var existing) && (!existing.Done || DateTime.UtcNow - existing.FinishedAt < ResultTtl)) return existing;
            if (_runs.Count >= MaxCached) foreach (var k in _runs.Where(r => r.Value.Done && DateTime.UtcNow - r.Value.FinishedAt > ResultTtl).Select(r => r.Key).ToList()) _runs.Remove(k);
            var run = new LiveRun(certificateId);
            _runs[certificateId] = run; StartedRuns++;
            _ = Task.Run(() => ExecuteAsync(run, _stop.Token));   // gehört dem Dienst, nicht dem Besucher
            return run;
        }
    }

    private async Task ExecuteAsync(LiveRun run, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct);
            try { await RunAsync(run, ct); } finally { _slots.Release(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            run.Finish("unavailable", "Die Prüfung konnte nicht abgeschlossen werden (interner Fehler). Bitte später erneut versuchen.");
        }
    }

    private async Task RunAsync(LiveRun run, CancellationToken ct)
    {
        var id = run.CertificateId;
        // A: Zertifikat laden
        run.Phase("loading"); run.Step("load", StepState.Running);
        var cert = UserService.IsGuid(id) ? await _certs.GetRawAsync(id) : null;
        if (cert is null) { run.Step("load", StepState.Failed, "Kein Zertifikat mit dieser Kennung."); run.Step("done", StepState.Failed); run.Finish("notfound", null); return; }
        var proof = await _ots.GetLatestAsync(id);
        run.SetStored(proof is null ? null : new LiveStored(proof.ProofVersion, proof.AnchorStatus, proof.BitcoinHeight, proof.LastVerifiedAt, proof.LastCheckedAt));
        run.Step("load", StepState.Ok, $"Nr. {cert.Number}");

        // B/C: Credential-Signaturen und Ausstellerschlüssel (vorhandene Prüfung)
        run.Phase("verifying_signature"); run.Step("signature", StepState.Running);
        var results = new List<BadgeVerification> { await _certs.VerifyAsync(cert.BadgeJws) };
        if (cert.IsTopic && !string.IsNullOrEmpty(cert.ClrJws)) results.Add(await _certs.VerifyAsync(cert.ClrJws));
        var sigOk = results.All(r => r.SignatureValid && r.EmbeddedSignaturesValid != false && r.ContentMatches != false);
        var issuerOk = results.All(r => r.IssuerValid);
        var revoked = results.Any(r => r.Revoked) || cert.IsRevoked;
        run.Step("signature", sigOk ? StepState.Ok : StepState.Failed, (sigOk ? cert.IsTopic ? "Master-Badge und CLR (inkl. eingebetteter Credentials)" : "Open Badge" : results.Select(r => r.SignatureError).FirstOrDefault(e => e is not null) ?? "Signatur ungültig") + (revoked ? " — Nachweis ist widerrufen" : ""));
        run.Step("issuer", issuerOk ? StepState.Ok : StepState.Failed, issuerOk ? "Schlüssel dieser Installation" : "Schlüssel gehört nicht zu diesem Aussteller");

        // D: Integritätsmanifest (aus dem gespeicherten PDF; nichts wird neu erzeugt)
        run.Phase("verifying_manifest"); run.Step("manifest", StepState.Running);
        var manifestFailed = false;
        var pdfRows = await _db.QueryAsync("SELECT pdf_base64 FROM certificate_pdfs WHERE certificate_id = @id", new[] { ("id", (object?)id) });
        if (pdfRows.Count == 0) run.Step("manifest", StepState.Skipped, "Zu diesem Zertifikat wurde noch kein PDF erzeugt.");
        else
        {
            var files = PdfInspector.Inspect(Convert.FromBase64String(pdfRows[0]["pdf_base64"] ?? "")).Attachments.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Data);
            JsonNode? jwk = null; try { jwk = files.TryGetValue("public-key.jwk", out var kb) ? JsonNode.Parse(kb) : null; } catch (System.Text.Json.JsonException) { }
            var m = IntegrityManifest.Verify(files, jwk, _certs.Signer.PublicJwk());
            var status = m["status"]?.GetValue<string>();
            manifestFailed = status == "invalid";
            run.Step("manifest", status switch { "valid" => StepState.Ok, "absent" => StepState.Skipped, _ => StepState.Failed },
                status switch { "valid" => $"{(m["files"]?["listed"] as JsonArray)?.Count ?? 0} Dateien durch die Manifest-JWS geschützt", "absent" => "Älteres Paket ohne Integritätsmanifest.", _ => (m["problems"] as JsonArray)?.FirstOrDefault()?.ToString() ?? "Manifest ungültig" });
        }

        // E/F/G/H/I: OpenTimestamps
        run.Phase("verifying_ots");
        var hasManifestJws = proof is not null;
        if (proof?.Ots is null)
        {
            foreach (var k in new[] { "ots_read", "ots_bind", "calendars", "bitcoin", "depth" }) run.Step(k, StepState.Skipped, k == "ots_read" ? "Kein Zeitnachweis eingebettet." : null);
            run.SetOts(proof is null ? null : new LiveOts(proof.ProofVersion, proof.AnchorStatus, "none", null, null, null, 0, _ots.Settings.Confirmations, "", false, proof.LastVerifiedAt, false, true));
            Complete(run, sigOk, issuerOk, manifestFailed, null, false);
            return;
        }
        run.Step("ots_read", StepState.Running);
        var parsed = OtsFile.Parse(proof.Ots);
        run.Step("ots_read", parsed.Valid ? StepState.Ok : parsed.Unsupported ? StepState.Unverifiable : StepState.Failed, parsed.Valid ? $"Receipt Version {proof.ProofVersion}" : parsed.Error);
        if (!parsed.Valid)
        {
            foreach (var k in new[] { "ots_bind", "calendars", "bitcoin", "depth" }) run.Step(k, StepState.Skipped);
            run.SetOts(new LiveOts(proof.ProofVersion, proof.AnchorStatus, parsed.Unsupported ? "not_verifiable" : "failed", null, null, null, 0, _ots.Settings.Confirmations, "", false, proof.LastVerifiedAt, true, true));
            Complete(run, sigOk, issuerOk, manifestFailed, parsed.Unsupported ? "unavailable" : "failed", false);
            return;
        }
        run.Step("ots_bind", StepState.Running);
        var bound = parsed.DigestAlgorithm == "SHA256" && CryptographicOperations.FixedTimeEquals(parsed.FileDigest, SHA256.HashData(Encoding.ASCII.GetBytes(proof.Jws)));
        run.Step("ots_bind", bound ? StepState.Ok : StepState.Failed, bound ? "SHA-256 der exakten Manifest-JWS-Bytes stimmt überein" : "Der Nachweis gehört nicht zu dieser Manifest-JWS");
        if (!bound)
        {
            foreach (var k in new[] { "calendars", "bitcoin", "depth" }) run.Step(k, StepState.Skipped);
            run.SetOts(new LiveOts(proof.ProofVersion, OtsState.Failed, "failed", null, null, null, 0, _ots.Settings.Confirmations, "", false, proof.LastVerifiedAt, true, true));
            Complete(run, sigOk, issuerOk, manifestFailed, "failed", false);
            return;
        }

        // G: Calendar-Abfrage (gedrosselt, nur für noch nicht bestätigte Nachweise)
        run.Phase("contacting_calendars"); run.Step("calendars", StepState.Running);
        var check = await _ots.CheckLatestAsync(id, CalendarMinInterval, ct);
        var calendarUnavailable = false;
        switch (check.Kind)
        {
            case OtsMaintenanceService.CheckKind.NotNeeded: run.Step("calendars", StepState.Skipped, "Bereits bestätigt — keine Abfrage nötig."); break;
            case OtsMaintenanceService.CheckKind.NotConfigured: run.Step("calendars", StepState.Skipped, "OpenTimestamps ist auf diesem Server nicht aktiv."); break;
            case OtsMaintenanceService.CheckKind.Throttled:
                run.Step("calendars", StepState.Skipped, $"Zuletzt vor {Math.Max(1, (int)(DateTime.UtcNow - (check.Row?.LastCheckedAt ?? DateTime.UtcNow)).TotalMinutes)} Min. abgefragt — nächste Abfrage frühestens nach {(int)CalendarMinInterval.TotalMinutes} Min."); break;
            default:
                calendarUnavailable = check.Result?.UpgradeError is { Length: > 0 };
                run.Step("calendars", calendarUnavailable ? StepState.Unverifiable : StepState.Ok, calendarUnavailable ? "Calendar-Server nicht erreichbar." : check.Result!.Changed ? "Aktualisierte Receipt erhalten (neue Proof-Version)." : "Calendars geantwortet — keine neuere Bestätigung."); break;
        }

        // H/I: Bitcoin
        run.Phase("verifying_bitcoin"); run.Step("bitcoin", StepState.Running);
        var (row, eval) = await _ots.EvaluateLatestAsync(id, ct);
        row ??= proof;
        eval ??= await OtsEvaluator.EvaluateAsync(Encoding.ASCII.GetBytes(row.Jws), row.Ots!, null, _ots.Settings.Confirmations, null, ct);
        string? outcomeOverride = null;
        switch (eval.State)
        {
            case OtsState.Confirmed or OtsState.Anchored:
                run.Step("bitcoin", StepState.Ok, $"Block {eval.Height}");
                run.Step("depth", eval.State == OtsState.Confirmed ? StepState.Ok : StepState.Waiting, eval.State == OtsState.Confirmed ? $"{eval.Confirmations} Bestätigungen (erforderlich: {_ots.Settings.Confirmations})" : $"{eval.Confirmations} von {_ots.Settings.Confirmations} Bestätigungen");
                break;
            case OtsState.Pending:
                run.Step("bitcoin", StepState.Waiting, "Bitcoin-Bestätigung ausstehend"); run.Step("depth", StepState.Skipped); break;
            case OtsState.Failed:
                run.Step("bitcoin", StepState.Failed, eval.Error); run.Step("depth", StepState.Skipped); outcomeOverride = "failed"; break;
            default:
                run.Step("bitcoin", StepState.Unverifiable, eval.Height is null ? "Keine Bitcoin-Datenquelle erreichbar oder konfiguriert." : $"Block {eval.Height} behauptet, aber nicht unabhängig geprüft."); run.Step("depth", StepState.Skipped); break;
        }
        run.SetOts(new LiveOts(row.ProofVersion, eval.State, eval.Detail, eval.Height ?? row.BitcoinHeight, eval.BlockHash ?? row.BlockHash, eval.BlockTime ?? row.BlockTime, eval.Confirmations, _ots.Settings.Confirmations, eval.Source, eval.Independent, row.LastVerifiedAt, true, true));
        Complete(run, sigOk, issuerOk, manifestFailed, outcomeOverride, calendarUnavailable || eval.State == OtsState.Unverifiable);
    }

    /// <summary>Gesamtergebnis: ein fehlgeschlagener Schritt wird nie durch erfolgreiche andere überdeckt; Pending ist ein Endzustand der Anzeige.</summary>
    private static void Complete(LiveRun run, bool sigOk, bool issuerOk, bool manifestFailed, string? otsOutcome, bool unavailable)
    {
        var steps = run.Snapshot().Steps;
        var failed = !sigOk || !issuerOk || manifestFailed || otsOutcome == "failed" || steps.Any(s => s.State == StepState.Failed);
        var anyUnverifiable = unavailable || otsOutcome == "unavailable" || steps.Any(s => s.State == StepState.Unverifiable);
        var waiting = steps.Any(s => s.State == StepState.Waiting);
        var outcome = failed ? "failed" : anyUnverifiable ? "unavailable" : waiting ? "pending" : "valid";
        var notice = outcome switch
        {
            "pending" => "Der kryptografische Nachweis ist vorhanden. Die Verankerung in der Bitcoin-Blockchain steht noch aus. Learny prüft den Status bei späteren Wartungsläufen erneut.",
            "unavailable" => "Die aktuelle Blockchain-Prüfung konnte nicht abgeschlossen werden. Der zuletzt gespeicherte Stand wird getrennt angezeigt; es wird kein Erfolg angenommen.",
            "failed" => "Mindestens ein Prüfschritt ist fehlgeschlagen. Der betroffene Schritt ist markiert.",
            _ => null,
        };
        run.Step("done", failed ? StepState.Failed : anyUnverifiable ? StepState.Unverifiable : StepState.Ok, outcome switch { "valid" => "Alle durchgeführten Prüfungen bestanden", "pending" => "Bestanden, Bitcoin-Bestätigung ausstehend", "unavailable" => "Nicht vollständig überprüfbar", _ => "Fehlgeschlagen" });
        run.Finish(outcome, notice);
    }

    public void Dispose() { _stop.Cancel(); _stop.Dispose(); _slots.Dispose(); }
}
