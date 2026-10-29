using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using LearnCards.Web.Domain;
using LearnCards.Web.Infrastructure;
using LearnCards.Web.Services.Certificates;
using LearnCards.Web.Services.Certificates.Ots;
using LearnCards.Web.Services.Pdf;

/// <summary>Integritätsmanifest, Manifest-JWS und OpenTimestamps (ADR 0039): Manipulationen werden einzeln erkannt, kein erfolgreicher Teil überdeckt einen Fehler.</summary>
public static class IntegrityTests
{
    private static string S(JsonNode? n, string path)
    {
        var cur = n;
        foreach (var part in path.Split('.')) cur = cur is JsonArray a ? (int.TryParse(part, out var i) && i < a.Count ? a[i] : null) : (cur as JsonObject)?[part];
        return cur?.ToString() ?? "";
    }

    internal sealed class FakeHeaders : IBitcoinHeaderSource
    {
        public string Description => "Test-Datenquelle";
        public Func<long, BitcoinHeader>? Header; public bool Down; public bool Independent { get; set; }
        public Task<BitcoinHeader> GetHeaderAsync(long height, CancellationToken ct) => Down ? throw new HttpRequestException("down") : Task.FromResult(Header!(height));
    }

    // Baut eine gültige OTS-Datei: Digest → append(0x01) → sha256 → Blatt (pending bzw. Bitcoin-Attestierung) — das Format des offiziellen Clients.
    internal static (byte[] Ots, byte[] Leaf) Ots(byte[] jws, string kind, long height = 800000)
    {
        var digest = SHA256.HashData(jws);
        var leaf = SHA256.HashData(digest.Concat(new byte[] { 1 }).ToArray());
        var ms = new MemoryStream(); ms.Write(OtsFile.Header(digest));
        ms.WriteByte(0xF0); ms.WriteByte(1); ms.WriteByte(1); ms.WriteByte(0x08);   // append(0x01), sha256
        ms.WriteByte(0x00);                                                           // Attestierung
        if (kind == "bitcoin") { ms.Write(new byte[] { 0x05, 0x88, 0x96, 0x0D, 0x73, 0xD7, 0x19, 0x01 }); var p = VarInt(height); ms.WriteByte((byte)p.Length); ms.Write(p); }
        else { ms.Write(new byte[] { 0x83, 0xDF, 0xE3, 0x0D, 0x2E, 0xF9, 0x0C, 0x8E }); var uri = Encoding.UTF8.GetBytes("https://a.pool.opentimestamps.org"); ms.WriteByte((byte)(uri.Length + 1)); ms.WriteByte((byte)uri.Length); ms.Write(uri); }
        return (ms.ToArray(), leaf);
    }
    private static byte[] VarInt(long v) { var l = new List<byte>(); while (v >= 0x80) { l.Add((byte)((v & 0x7F) | 0x80)); v >>= 7; } l.Add((byte)v); return l.ToArray(); }

    public static async Task UnitAsync(Action<bool, string> check, string tmp)
    {
        Console.WriteLine("Integritätsmanifest und OpenTimestamps (ADR 0039)");
        var dir = Path.Combine(tmp, "integrity"); Directory.CreateDirectory(dir);
        var cfg = new AppConfig { Blockchain = new BlockchainSettings { BadgeKeyFile = Path.Combine(dir, "key.pem"), PublicBaseUrl = "https://learny.test" } };
        using var signer = new BadgeSigner(cfg);
        using var foreign = new BadgeSigner(new AppConfig { Blockchain = new BlockchainSettings { BadgeKeyFile = Path.Combine(dir, "other.pem"), PublicBaseUrl = "https://learny.test" } });
        var jwk = signer.PublicJwk(); var kid = "https://learny.test/badges/keys/" + signer.KeyId;
        var cert = new Certificate { Number = "LC-2026-TEST", Kind = "topic", IssuedAt = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc) };

        PdfEmbeddedFile F(string n, string c, string mime = "application/json") => new(n, Encoding.UTF8.GetBytes(c), mime, "Data", n);
        var payload = IntegrityManifest.RequiredFiles("topic").Select(n => F(n, "{\"datei\":\"" + n + "\"}")).Append(F("competence-snapshot.json", "{\"snapshot\":1}")).ToList();
        var manifest = IntegrityManifest.Build(cert, "https://learny.test/verify/" + cert.Id, "https://learny.test/badges/issuer", cert.IssuedAt, jwk, kid, "https://learny.test/.well-known/jwks.json", payload);
        var jws = signer.SignBytes(manifest, IntegrityManifest.JwsTyp, IntegrityManifest.JwsCty, kid);
        var jwsBytes = Encoding.ASCII.GetBytes(jws);

        Dictionary<string, byte[]> Pkg() { var d = payload.ToDictionary(f => f.Name, f => f.Data); d[IntegrityManifest.ManifestName] = manifest; d[IntegrityManifest.JwsName] = jwsBytes; return d; }
        JsonObject Check(Dictionary<string, byte[]> d, JsonNode? embedded = null) => IntegrityManifest.Verify(d, embedded ?? jwk, jwk);

        // ─── Manifest und JWS ───
        var good = Check(Pkg());
        var mText = Encoding.UTF8.GetString(manifest);
        check(S(good, "status") == "valid" && S(good, "jws.signature_valid") == "true" && S(good, "jws.payload_matches_manifest") == "true" && S(good, "issuer_key.trust") == "installation" && S(good, "files.hashes_match") == "true",
            "Manifest: gültiges Paket (Signatur, Nutzlast = Manifestbytes, Dateihashes, Schlüssel dieser Installation)");
        var mj = JsonNode.Parse(manifest)!;
        var names = mj["files"]!.AsArray().Select(f => S(f, "name")).ToList();
        check(names.Count == payload.Count && names.Contains("competence-snapshot.json") && names.Contains("verification.json") && names.Contains("public-key.jwk") && !names.Any(n => IntegrityManifest.ArtifactNames.Contains(n))
              && S(mj, "hash_algorithm") == "SHA-256" && S(mj, "issuer_key.jwk_thumbprint") == BadgeSigner.JwkThumbprint(jwk) && manifest[0] != 0xEF && !mText.Contains('\r'),
            "Manifest: alle Nutzdateien (auch competence-snapshot.json), keine Manifest-Artefakte als Dateien, SHA-256, Schlüssel-Thumbprint, UTF-8 ohne BOM und CR");
        var header = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(jws.Split('.')[0].Replace('-', '+').Replace('_', '/').PadRight(((jws.Split('.')[0].Length + 3) / 4) * 4, '='))))!;
        check(S(header, "alg") == "ES256" && S(header, "typ") == IntegrityManifest.JwsTyp && S(header, "kid") == kid && S(header, "cty") == "application/json" && jws.Split('.').Length == 3 && !jws.EndsWith('='),
            "Manifest-JWS: ES256, eigener typ (kein VC-JWT), kid-URL, cty, kompakte Serialisierung");

        foreach (var name in payload.Select(f => f.Name))
        {
            var d = Pkg(); d[name] = Encoding.UTF8.GetBytes("{\"manipuliert\":true}");
            check(S(Check(d), "status") == "invalid" && S(Check(d), "files.hashes_match") == "false", $"Manipulation von {name} wird erkannt");
        }
        var dTampManifest = Pkg(); dTampManifest[IntegrityManifest.ManifestName] = Encoding.UTF8.GetBytes(mText.Replace("LC-2026-TEST", "LC-2026-FAKE"));
        check(S(Check(dTampManifest), "status") == "invalid" && S(Check(dTampManifest), "jws.payload_matches_manifest") == "false", "verändertes Manifest: Nutzlast der JWS ≠ Manifestbytes");
        var parts = jws.Split('.'); var sigBytes = parts[2].ToCharArray(); sigBytes[5] = sigBytes[5] == 'A' ? 'B' : 'A';
        var dJws = Pkg(); dJws[IntegrityManifest.JwsName] = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}.{new string(sigBytes)}");
        check(S(Check(dJws), "status") == "invalid" && S(Check(dJws), "jws.signature_valid") == "false", "manipulierte Manifest-JWS (Signatur)");
        var dJwsPayload = Pkg(); dJwsPayload[IntegrityManifest.JwsName] = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1][..^2]}AA.{parts[2]}");
        check(S(Check(dJwsPayload), "status") == "invalid", "manipulierte Manifest-JWS (Nutzlast)");
        var otherJws = foreign.SignBytes(manifest, IntegrityManifest.JwsTyp, IntegrityManifest.JwsCty, kid);
        var dForeign = Pkg(); dForeign[IntegrityManifest.JwsName] = Encoding.ASCII.GetBytes(otherJws);
        check(S(Check(dForeign), "status") == "invalid" && S(Check(dForeign), "jws.signature_valid") == "false", "falsche Manifest-JWS-Zuordnung: Signatur eines fremden Schlüssels über dasselbe Manifest");
        // Fremder Aussteller: Angreifer erzeugt Manifest + JWS + JWK konsistent mit eigenem Schlüssel
        var forgedManifest = IntegrityManifest.Build(cert, "https://learny.test/verify/" + cert.Id, "https://learny.test/badges/issuer", cert.IssuedAt, foreign.PublicJwk(), kid, "https://learny.test/.well-known/jwks.json", payload);
        var forged = Pkg(); forged[IntegrityManifest.ManifestName] = forgedManifest; forged[IntegrityManifest.JwsName] = Encoding.ASCII.GetBytes(foreign.SignBytes(forgedManifest, IntegrityManifest.JwsTyp, IntegrityManifest.JwsCty, kid)); forged["public-key.jwk"] = Encoding.UTF8.GetBytes(foreign.PublicJwk().ToJsonString());
        var fm = IntegrityManifest.Verify(forged, foreign.PublicJwk(), jwk);
        check(S(fm, "jws.signature_valid") == "true" && S(fm, "issuer_key.trust") == "unconfirmed" && S(fm, "issuer_key.trust") != "installation",
            "fremder Ausstellerschlüssel: mathematisch gültig, aber NICHT als Aussteller bestätigt (Vertrauensstufe „unconfirmed“)");
        var dSwapKey = Pkg();
        check(S(Check(dSwapKey, foreign.PublicJwk()), "status") == "invalid" && S(Check(dSwapKey, foreign.PublicJwk()), "issuer_key.matches_manifest") == "false", "ausgetauschtes public-key.jwk passt weder zur Signatur noch zum Manifest");
        var dExtra = Pkg(); dExtra["versteckt.json"] = Encoding.UTF8.GetBytes("{}");
        check(S(Check(dExtra), "status") == "invalid" && S(Check(dExtra), "files.unlisted_attachments.0") == "versteckt.json", "zusätzlicher/versteckter Anhang wird gemeldet");
        var dMissing = Pkg(); dMissing.Remove("clr-credential.jwt");
        check(S(Check(dMissing), "status") == "invalid" && S(Check(dMissing), "problems").Contains("fehlt im PDF"), "fehlende Pflichtdatei wird erkannt");
        var dNoManifest = Pkg(); dNoManifest.Remove(IntegrityManifest.ManifestName); dNoManifest.Remove(IntegrityManifest.JwsName);
        check(S(Check(dNoManifest), "status") == "absent", "älteres Paket ohne Manifest: „nicht vorhanden“, nicht „ungültig“");
        var dHalf = Pkg(); dHalf.Remove(IntegrityManifest.JwsName);
        check(S(Check(dHalf), "status") == "invalid", "Manifest ohne JWS ist ungültig (unvollständig)");
        check(S(IntegrityManifest.Verify(Pkg(), null, jwk), "status") == "invalid", "ohne public-key.jwk keine gültige Manifestprüfung");

        await RealArtifactsAsync(check);
        await NativeAsync(check, tmp);
        await LiveAsync(check, tmp);

        // ─── OpenTimestamps ───
        var (pendingOts, leaf) = Ots(jwsBytes, "pending");
        var pend = await OtsEvaluator.EvaluateAsync(jwsBytes, pendingOts, null, 6, null, default);
        check(pend.State == OtsState.Pending && pend.BindingOk && pend.State != OtsState.Anchored && pend.State != OtsState.Confirmed, "OTS pending: Receipt vorhanden und gebunden, kein Bitcoin-Nachweis — wird nicht als verankert ausgegeben");
        var (otherOts, _) = Ots(Encoding.ASCII.GetBytes(otherJws), "pending");
        var swapped = await OtsEvaluator.EvaluateAsync(jwsBytes, otherOts, null, 6, null, default);
        check(swapped.State == OtsState.Failed && !swapped.BindingOk, "ausgetauschte OTS-Datei (andere Manifest-JWS) wird erkannt");
        var hexAscii = SHA256.HashData(Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(jwsBytes)).ToLowerInvariant()));
        var wrongBase = OtsFile.Header(hexAscii).Concat(pendingOts.Skip(OtsFile.Header(hexAscii).Length)).ToArray();
        check((await OtsEvaluator.EvaluateAsync(jwsBytes, wrongBase, null, 6, null, default)).State == OtsState.Failed, "falscher Manifest-Digest (z. B. ASCII-Hex statt Dateibytes gestempelt) wird erkannt");
        var garbage = pendingOts.Take(OtsFile.Header(new byte[32]).Length + 1).Concat(new byte[] { 0xEE, 1, 2 }).ToArray();
        check((await OtsEvaluator.EvaluateAsync(jwsBytes, garbage, null, 6, null, default)).State == OtsState.Failed && !OtsFile.Parse(new byte[] { 1, 2, 3 }).Valid && !OtsFile.Parse(pendingOts.Take(40).ToArray()).Valid, "ungültige OTS-Operationen und abgeschnittene Dateien sind ungültig");

        // Bitcoin: Merkle-Root des Blocks = berechneter Wert (interne Byte-Reihenfolge ↔ Anzeige)
        var (btcOts, btcLeaf) = Ots(jwsBytes, "bitcoin", 800000);
        string RootHex(byte[] internalOrder) => Convert.ToHexString(internalOrder.Reverse().ToArray()).ToLowerInvariant();
        var good800 = new FakeHeaders { Header = h => new BitcoinHeader("00000000000000000001abc", RootHex(btcLeaf), new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), 800009) };
        var anchoredOk = await OtsEvaluator.EvaluateAsync(jwsBytes, btcOts, good800, 6, null, default);
        check(anchoredOk.State == OtsState.Confirmed && anchoredOk.Height == 800000 && anchoredOk.Confirmations == 10 && anchoredOk.BlockTime is not null, "Bitcoin: Merkle-Root stimmt, Tiefe 10 ≥ 6 → bestätigt (Quelle: Test-Datenquelle, Höhe/Zeit aus dem Header)");
        var shallow = new FakeHeaders { Header = h => new BitcoinHeader("00000000000000000001abc", RootHex(btcLeaf), DateTime.UtcNow, 800002) };
        check((await OtsEvaluator.EvaluateAsync(jwsBytes, btcOts, shallow, 6, null, default)).State == OtsState.Anchored, "unzureichende Bestätigungstiefe (3 < 6) → nur verankert, nicht bestätigt");
        var wrongBlock = new FakeHeaders { Header = h => new BitcoinHeader("00000000000000000009fff", RootHex(SHA256.HashData(new byte[] { 9 })), DateTime.UtcNow, 800009) };
        check((await OtsEvaluator.EvaluateAsync(jwsBytes, btcOts, wrongBlock, 6, null, default)).State == OtsState.Failed, "falscher Bitcoin-Block (Merkle-Root weicht ab) → ungültig");
        var reorg = await OtsEvaluator.EvaluateAsync(jwsBytes, btcOts, good800, 6, "00000000000000000000old", default);
        check(reorg.State == OtsState.Pending && (reorg.Error ?? "").Contains("Reorganisation"), "Bitcoin-Reorganisation: zuvor nachgewiesener Block ist nicht mehr in der besten Kette → zurück auf ausstehend");
        var down = await OtsEvaluator.EvaluateAsync(jwsBytes, btcOts, new FakeHeaders { Down = true }, 6, null, default);
        check(down.State == OtsState.Unverifiable, "Datenquelle nicht erreichbar → „nicht überprüfbar“, nicht „ungültig“ und nicht „bestätigt“");
        var noSource = await OtsEvaluator.EvaluateAsync(jwsBytes, btcOts, null, 6, null, default);
        check(noSource.State == OtsState.Unverifiable && noSource.State != OtsState.Anchored, "Bitcoin-Behauptung ohne unabhängige Datenquelle bleibt „nicht überprüfbar“ (keine Verankerung behauptet)");

        // ─── Wartung: Calendar-Ausfall, Upgrade, Proof-Versionen ───
        await using var db = new LearnCards.Web.Data.SqliteDatabase(Path.Combine(dir, "i.db")); await db.InitAsync();
        var certId = Guid.NewGuid().ToString();
        var upgraded = btcOts;
        var client = new FakeClient { Stamp = pendingOts, Upgrade = _ => upgraded };
        var headers = new FakeHeaders { Header = h => new BitcoinHeader("00000000000000000001abc", RootHex(btcLeaf), new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), 800009) };
        var ocfg = new AppConfig { Ots = new OtsSettings { Enabled = true, Calendars = { "https://cal-a.example", "https://cal-b.example" }, Confirmations = 6 } };
        ocfg.Ots.Operators["https://cal-a.example"] = "Alpha"; ocfg.Ots.Operators["https://cal-b.example"] = "Beta";
        var svc = new OtsMaintenanceService(db, ocfg, client, headers);
        var stamp = await svc.TryStampAsync(jwsBytes, default);
        check(stamp.Ots is not null && client.StampCalls == 1 && client.LastCalendars!.Count == 2, "OTS-Stempel: zwei unabhängig konfigurierte Calendars werden übergeben, Receipt ist an die Manifest-JWS gebunden");
        check(client.LastBytes!.SequenceEqual(jwsBytes), "OTS-Stempelgrundlage sind die exakten Bytes von integrity-manifest.jws");
        await svc.RecordInitialAsync(certId, manifest, jws, stamp, "pdfsha1");
        var v1 = await svc.GetLatestAsync(certId);
        check(v1 is { ProofVersion: 1, AnchorStatus: OtsState.Pending } && v1.Ots!.SequenceEqual(pendingOts), "Proof-Version 1 gespeichert (pending, ursprüngliche Receipt)");
        await svc.RecordInitialAsync(certId, manifest, jws, stamp, "pdfsha2");
        check((await svc.GetVersionsAsync(certId)).Count == 1, "Ablage ist idempotent");
        client.UpgradeThrows = true;
        var chOut = await svc.RunOnceAsync(default);
        check(chOut == 0 && (await svc.GetLatestAsync(certId))!.AnchorStatus == OtsState.Pending, "Calendar-/Clientausfall beim Upgrade: Zustand bleibt ausstehend, keine Zustandsänderung ohne Prüfergebnis");
        client.UpgradeThrows = false;
        var ch = await svc.RunOnceAsync(default); var versions = await svc.GetVersionsAsync(certId);
        check(ch == 1 && versions.Count == 2 && versions[1].AnchorStatus == OtsState.Confirmed && versions[1].BitcoinHeight == 800000 && versions[1].BlockHash == "00000000000000000001abc" && versions[1].Confirmations == 10,
            "Upgrade: neue Proof-Version „bestätigt“ mit Blockhöhe, Blockhash und Tiefe");
        check(versions[0].Ots!.SequenceEqual(pendingOts) && versions[0].AnchorStatus == OtsState.Pending && versions[1].PdfSha256 == "pdfsha1", "ursprüngliche Receipt und alte Proof-Version bleiben unverändert erhalten; Bezug zum PDF bleibt");
        check(await svc.RunOnceAsync(default) == 0 && (await svc.GetVersionsAsync(certId)).Count == 2, "Wartungslauf ist idempotent (bestätigte Nachweise werden nicht erneut verändert)");
        var offCfg = new OtsMaintenanceService(db, new AppConfig(), client, null);
        var off = await offCfg.TryStampAsync(jwsBytes, default);
        var oneCal = new OtsMaintenanceService(db, new AppConfig { Ots = new OtsSettings { Enabled = true, Calendars = { "https://cal-a.example" } } }, client, null);
        check(off.Ots is null && off.CalendarStatus == OtsState.None && (await oneCal.TryStampAsync(jwsBytes, default)).Ots is null, "OTS aus oder weniger als zwei Calendars: kein Nachweis, kein stiller Betrieb mit einem Einzelserver");
        var badClient = new FakeClient { Stamp = garbage };
        var badStamp = await new OtsMaintenanceService(db, ocfg, badClient, null).TryStampAsync(jwsBytes, default);
        check(badStamp.Ots is null && badStamp.CalendarStatus == OtsState.Failed, "fehlerhafte Receipt des Clients wird nicht eingebettet (Rückprobe der Bindung)");
        client.StampThrows = true;
        var outage = await svc.TryStampAsync(jwsBytes, default);
        check(outage.Ots is null && outage.CalendarStatus == OtsState.None && outage.Error.Length > 0, "Calendar-Ausfall beim Stempeln: PDF ohne Nachweis, Zustand „none“ mit Grund");
    }

    /// <summary>
    /// Echte Artefakte (kein Test-Double): <c>fixtures/ots/integrity-manifest.jws.ots</c> wurde am 2026-10-29 mit dem offiziellen ots-Client 0.7.2 bei drei realen Calendars
    /// (alice.btc.calendar.opentimestamps.org, finney.calendar.eternitywall.com, btc.calendar.catallaxy.com) für die danebenliegende Manifest-JWS (Testnachweis, keine Personendaten) erzeugt.
    /// </summary>
    public static async Task RealArtifactsAsync(Action<bool, string> check)
    {
        var dir = AppContext.BaseDirectory; string? fx = null;
        for (var d = new DirectoryInfo(dir); d is not null && fx is null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "fixtures", "ots"))) fx = Path.Combine(d.FullName, "fixtures", "ots");
        if (fx is null) { check(false, "Reale OTS-Artefakte: Fixture-Verzeichnis nicht gefunden"); return; }
        var jws = File.ReadAllBytes(Path.Combine(fx, "integrity-manifest.jws")); var ots = File.ReadAllBytes(Path.Combine(fx, "integrity-manifest.jws.ots"));
        var parsed = OtsFile.Parse(ots);
        check(parsed.Valid && parsed.DigestAlgorithm == "SHA256" && parsed.Attestations.Count(a => a.Kind == "pending") == 3 && parsed.Attestations.Select(a => a.CalendarUri).Distinct().Count() == 3,
            "Echte Receipt (ots 0.7.2, 3 reale Calendars): vom Leser gelesen, drei Calendar-Attestierungen");
        var ev = await OtsEvaluator.EvaluateAsync(jws, ots, null, 6, null, default);
        check(ev.State == OtsState.Pending && ev.BindingOk, "Echte Receipt: an die exakten Bytes der Manifest-JWS gebunden, Zustand „ausstehend“ (kein Bitcoin-Nachweis behauptet)");
        var changed = (byte[])jws.Clone(); changed[10] ^= 1;
        check((await OtsEvaluator.EvaluateAsync(changed, ots, null, 6, null, default)).State == OtsState.Failed, "Echte Receipt gehört nicht zu einer veränderten Manifest-JWS");
        var sig = Encoding.ASCII.GetString(jws).Split('.');
        check(sig.Length == 3 && BadgeSigner.VerifyBytesWithJwk(Encoding.ASCII.GetString(jws), JsonNode.Parse("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"gQsC4JTPYY1BoGVWYKEzDNx6qBMAuBPx3TkRWp5i3gk\",\"y\":\"G2ijwhjLZXIZvFxE2Har5RNTWLhrQAmUankGO6emADc\"}")).SignatureValid == false,
            "Echte Manifest-JWS ist mit einem fremden Schlüssel nicht gültig (JWK-Zuordnung)");
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }
    }

    private sealed class FakeCalendar : HttpMessageHandler
    {
        public List<string> Requests = new(); public Func<HttpRequestMessage, HttpResponseMessage>? Handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request.Method + " " + request.RequestUri); return Task.FromResult(Handler!(request)); }
    }

    private static byte[] CalendarReply(byte[] commitment, string uri, long? bitcoinHeight = null)
    {
        var root = new OtsNode(commitment); var n = root.AddOp(OtsFile.OpAppend, Encoding.ASCII.GetBytes("salt-" + uri)).AddOp(OtsFile.OpSha256, Array.Empty<byte>());
        if (bitcoinHeight is { } h) n.Attestations.Add(new OtsRawAttestation(OtsFile.BitcoinTag, VarInt(h)));
        else { var u = Encoding.UTF8.GetBytes(uri); n.Attestations.Add(new OtsRawAttestation(OtsFile.PendingTag, new byte[] { (byte)u.Length }.Concat(u).ToArray())); }
        return OtsFile.SerializeTimestamp(root);
    }

    /// <summary>Nativer OpenTimestamps-Client (ADR 0040): Protokoll gegen Calendar-Doubles, Format, Rundlauf, Robustheit; mit <c>OTS_LIVE_TESTS=1</c> zusätzlich gegen echte Calendars und Bitcoin-Daten.</summary>
    public static async Task NativeAsync(Action<bool, string> check, string tmp)
    {
        // RIPEMD-160 (Referenzvektoren)
        check(Convert.ToHexString(Ripemd160.Hash(Encoding.ASCII.GetBytes("abc"))).ToLowerInvariant() == "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc" && Convert.ToHexString(Ripemd160.Hash(Encoding.ASCII.GetBytes("a"))).ToLowerInvariant() == "0bdc9d2d256b3ee9daae347be6f4dc835a467ffe"
              && Convert.ToHexString(Ripemd160.Hash(Array.Empty<byte>())).ToLowerInvariant() == "9c1185a5c5e9fc54612808977ee8f548b2258d31", "Nativ: RIPEMD-160 entspricht den Referenzvektoren");

        // Rundlauf echter Receipts: Lesen + Serialisieren ist bytegleich (Sortierung wie im Referenzclient)
        var real = File.ReadAllBytes(Path.Combine(FixtureDir(), "integrity-manifest.jws.ots")); var rp = OtsFile.Parse(real);
        check(rp.Valid && OtsFile.Serialize(rp.DigestTag, rp.FileDigest, rp.Root!).AsSpan().SequenceEqual(real), "Nativ: echte ots-0.7.2-Receipt (3 Calendars) wird gelesen und bytegleich wieder geschrieben");
        var offFile = File.ReadAllBytes(Path.Combine(FixtureDir(), "official", "hello-world.txt"));
        var off = File.ReadAllBytes(Path.Combine(FixtureDir(), "official", "hello-world.txt.ots")); var op = OtsFile.Parse(off);
        check(op.Valid && op.Bitcoin.Count() == 1 && op.Bitcoin.First().BitcoinHeight == 358391 && OtsFile.Serialize(op.DigestTag, op.FileDigest, op.Root!).AsSpan().SequenceEqual(off) && op.FileDigest.SequenceEqual(SHA256.HashData(offFile)),
            "Nativ: offizielle Beispiel-Receipt (RIPEMD-160, Bitcoin-Block 358391) wird gelesen, an die Datei gebunden und bytegleich geschrieben");
        check(Convert.ToHexString(op.Bitcoin.First().Message).ToLowerInvariant() == "007ee445d23ad061af4a36b809501fab1ac4f2d7e7a739817dd0cbb7ec661b8a", "Nativ: der berechnete Merkle-Wert der offiziellen Receipt entspricht dem unabhängig mit Python ermittelten Wert");

        // Stempeln gegen Calendar-Doubles
        var calendars = new[] { "https://cal-a.example", "https://cal-b.example", "https://cal-c.example" };
        var cfg = new AppConfig { Ots = new OtsSettings { Enabled = true, Calendars = calendars.ToList(), TimeoutSeconds = 5 } };
        cfg.Ots.Operators["https://cal-a.example"] = "Alpha"; cfg.Ots.Operators["https://cal-b.example"] = "Beta"; cfg.Ots.Operators["https://cal-c.example"] = "Gamma";
        var cal = new FakeCalendar { Handler = r => r.RequestUri!.AbsolutePath == "/digest" && r.Method == HttpMethod.Post && r.Headers.Accept.ToString() == "application/vnd.opentimestamps.v1" && r.RequestUri.Host != "cal-c.example"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(CalendarReply(r.Content!.ReadAsByteArrayAsync().Result, "https://" + r.RequestUri.Host)) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) };
        var native = new NativeOtsClient(cfg, new HttpClient(cal));
        var jws = Encoding.ASCII.GetBytes("aaa.bbb.ccc-test");
        var stamped = await native.StampAsync(jws, calendars, default);
        var sp = OtsFile.Parse(stamped);
        check(sp.Valid && sp.DigestAlgorithm == "SHA256" && sp.FileDigest.SequenceEqual(SHA256.HashData(jws)) && sp.Attestations.Count(a => a.Kind == "pending") == 2 && stamped.AsSpan(0, 31).SequenceEqual(OtsFile.Header(new byte[32]).AsSpan(0, 31)),
            "Nativ: Stempel liefert gültige .ots (Kopf, SHA-256 der exakten Bytes, zwei Calendar-Attestierungen); ein ausgefallener dritter Calendar ist tolerierbar");
        check(cal.Requests.Count == 3 && cal.Requests.All(x => x.StartsWith("POST https://cal-") && x.EndsWith("/digest")), "Nativ: POST {calendar}/digest an alle konfigurierten Calendars");
        var committed = sp.Root!.Ops[0];
        check(committed.Tag == OtsFile.OpAppend && committed.Arg.Length == 16 && committed.Child.Ops[0].Tag == OtsFile.OpSha256, "Nativ: Baum folgt dem Referenzclient (append 16 Byte Zufall → sha256 → Calendar-Zweige)");
        var oneOk = new FakeCalendar { Handler = r => r.RequestUri!.Host == "cal-a.example" ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(CalendarReply(r.Content!.ReadAsByteArrayAsync().Result, "https://cal-a.example")) } : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) };
        var failed = false; try { await new NativeOtsClient(cfg, new HttpClient(oneOk)).StampAsync(jws, calendars, default); } catch (OtsClientException) { failed = true; }
        check(failed, "Nativ: weniger als zwei antwortende Calendars → Fehler, keine Receipt (fail-closed)");
        var redirect = new FakeCalendar { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/") } } };
        var rfail = false; try { await new NativeOtsClient(cfg, new HttpClient(redirect)).StampAsync(jws, calendars, default); } catch (OtsClientException) { rfail = true; }
        check(rfail && redirect.Requests.All(x => x.Contains("cal-")), "Nativ: Weiterleitungen werden nicht gefolgt");
        var big = new FakeCalendar { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[20000]) } };
        var bfail = false; try { await new NativeOtsClient(cfg, new HttpClient(big)).StampAsync(jws, calendars, default); } catch (OtsClientException) { bfail = true; }
        check(bfail, "Nativ: übergroße Calendar-Antworten (> 10 000 Byte) werden abgelehnt");
        var cfgHttp = new AppConfig { Ots = new OtsSettings { Calendars = { "http://cal-a.example", "https://cal-b.example" } } };
        cfgHttp.Ots.Operators["http://cal-a.example"] = "Alpha"; cfgHttp.Ots.Operators["https://cal-b.example"] = "Beta";
        var hfail = false; try { await new NativeOtsClient(cfgHttp, new HttpClient(cal)).StampAsync(jws, cfgHttp.Ots.Calendars, default); } catch (OtsClientException) { hfail = true; }
        check(hfail, "Nativ: nur https-Calendars aus der Konfiguration (http wird nicht verwendet)");

        // Upgrade: ausstehend → unverändert; bestätigt → vereinigt, Original erhalten; fremde URI wird nie abgerufen
        var pendingCal = new FakeCalendar { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("Pending confirmation in Bitcoin blockchain") } };
        var same = await new NativeOtsClient(cfg, new HttpClient(pendingCal)).UpgradeAsync(stamped, default);
        check(same.SequenceEqual(stamped) && pendingCal.Requests.Count == 2 && pendingCal.Requests.All(x => x.StartsWith("GET https://cal-") && x.Contains("/timestamp/")), "Nativ: Upgrade mit „noch ausstehend“ (404) lässt die Receipt unverändert (kein Fehler, GET {calendar}/timestamp/{hex})");
        var confirmedCal = new FakeCalendar { Handler = r => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(CalendarReply(Convert.FromHexString(r.RequestUri!.Segments[^1]), r.RequestUri.Host, 800123)) } };
        var upgraded = await new NativeOtsClient(cfg, new HttpClient(confirmedCal)).UpgradeAsync(stamped, default);
        var up = OtsFile.Parse(upgraded);
        check(up.Valid && up.Bitcoin.Count() == 2 && up.Attestations.Count(a => a.Kind == "pending") == 2 && up.FileDigest.SequenceEqual(sp.FileDigest), "Nativ: Upgrade vereinigt Bitcoin-Attestierungen in die Receipt (Pending bleibt erhalten, Datei-Digest unverändert)");
        var ev = await OtsEvaluator.EvaluateAsync(jws, upgraded, null, 6, null, default);
        check(ev.State == OtsState.Unverifiable && ev.State != OtsState.Confirmed, "Nativ: eine aktualisierte Receipt wird ohne unabhängige Header-Prüfung nie als bestätigt ausgegeben");
        var foreignUri = OtsFile.Parse(stamped); var leaf = foreignUri.Root!.Ops[0].Child.Ops[0].Child; leaf.Attestations.Clear();
        var evil = Encoding.UTF8.GetBytes("https://evil.example"); leaf.Attestations.Add(new OtsRawAttestation(OtsFile.PendingTag, new byte[] { (byte)evil.Length }.Concat(evil).ToArray()));
        var evilCal = new FakeCalendar { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) };
        await new NativeOtsClient(cfg, new HttpClient(evilCal)).UpgradeAsync(OtsFile.Serialize(foreignUri.DigestTag, foreignUri.FileDigest, foreignUri.Root), default);
        check(!evilCal.Requests.Any(x => x.Contains("evil.example")), "Nativ: Upgrade ruft nur konfigurierte Calendars ab (URIs aus der Receipt sind nicht vertrauenswürdig, kein SSRF)");

        // Manipulierte und fremde Eingaben: nie Ausnahme außerhalb der kontrollierten Klassen
        var rnd = new Random(7); var bad = 0;
        for (var i = 0; i < 3000; i++)
        {
            var m = (byte[])real.Clone();
            switch (i % 3) { case 0: m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8)); break; case 1: m = m.Take(rnd.Next(m.Length)).ToArray(); break; default: m = m.Concat(new byte[] { (byte)rnd.Next(256) }).ToArray(); break; }
            try { OtsFile.Parse(m); } catch (Exception) { bad++; }
        }
        check(bad == 0, "Nativ: 3000 manipulierte/abgeschnittene Receipts → nie eine unkontrollierte Ausnahme");
        var deep = OtsFile.Header(new byte[32]).Concat(Enumerable.Repeat((byte)0xF2, 5000)).Append((byte)0x00).ToArray();
        var big2 = OtsFile.Header(new byte[32]).Concat(new byte[] { 0xF0, 0xFF, 0xFF, 0x7F }).ToArray();
        check(!OtsFile.Parse(deep).Valid && !OtsFile.Parse(big2).Valid, "Nativ: zu tiefe Bäume und überlange Operanden werden begrenzt abgelehnt");
        var keccak = OtsFile.Header(new byte[32]).Concat(new byte[] { 0x67, 0x00 }).ToArray();
        var kp = OtsFile.Parse(keccak);
        check(!kp.Valid && kp.Unsupported && (await OtsEvaluator.EvaluateAsync(jws, keccak, null, 6, null, default)).State == OtsState.Unverifiable, "Nativ: nicht berechenbare Operation (Keccak-256) → „nicht überprüfbar“, nicht „ungültig“");
        var foreignFile = await OtsEvaluator.EvaluateAsync(Encoding.ASCII.GetBytes("andere Datei"), real, null, 6, null, default);
        check(foreignFile.State == OtsState.Failed && !foreignFile.BindingOk, "Nativ: Receipt einer anderen Datei wird als falsch zugeordnet abgelehnt");


        // ─── Betreiberunabhängigkeit (ADR 0040) ───
        var parsedCfg = OtsSettings.ParseCalendars("https://a.example|Alpha, https://b.example/|Beta;https://c.example", out var parsedOps);
        check(parsedCfg.SequenceEqual(new[] { "https://a.example", "https://b.example", "https://c.example" }) && parsedOps["https://a.example"] == "Alpha" && parsedOps["https://b.example"] == "Beta" && !parsedOps.ContainsKey("https://c.example"),
            "Betreiber: OTS_CALENDARS „url|Betreiber“ wird gelesen; ein Eintrag ohne Betreiber bleibt unbekannt");
        AppConfig Cfg(params (string Url, string? Op)[] cals) { var c = new AppConfig { Ots = new OtsSettings { Enabled = true, TimeoutSeconds = 5 } }; foreach (var (u, o) in cals) { c.Ots.Calendars.Add(u); if (o is not null) c.Ots.Operators[u] = o; } return c; }
        var sameOp = Cfg(("https://cal-a.example", "X"), ("https://cal-b.example", "X"), ("https://cal-c.example", "X"));
        var sameFail = false; try { await new NativeOtsClient(sameOp, new HttpClient(cal)).StampAsync(jws, sameOp.Ots.Calendars, default); } catch (OtsClientException) { sameFail = true; }
        check(sameFail && sameOp.Ots.Problem() is not null, "Betreiber: zwei Hosts desselben Betreibers reichen nicht aus (Stempeln scheitert, Konfiguration wird beanstandet)");
        var unknownOp = Cfg(("https://cal-a.example", null), ("https://cal-b.example", null));
        var unkFail = false; try { await new NativeOtsClient(unknownOp, new HttpClient(cal)).StampAsync(jws, unknownOp.Ots.Calendars, default); } catch (OtsClientException) { unkFail = true; }
        check(unkFail && unknownOp.Ots.Problem() is not null, "Betreiber: unbekannte Betreiberzuordnung gilt nicht als unabhängig");
        var twoOps = Cfg(("https://cal-a.example", "Alpha"), ("https://cal-b.example", "Beta"));
        check(twoOps.Ots.Problem() is null && OtsFile.Parse(await new NativeOtsClient(twoOps, new HttpClient(cal)).StampAsync(jws, twoOps.Ots.Calendars, default)).Valid, "Betreiber: zwei benannte, verschiedene Betreiber werden akzeptiert");
        var badHttp = Cfg(("http://cal-a.example", "Alpha"), ("https://cal-b.example", "Beta"));
        check(badHttp.Ots.Problem() is not null, "Betreiber: ungültige Konfiguration (http-Calendar) wird abgelehnt");
        var halfOps = Cfg(("https://cal-a.example", "A"), ("https://cal-b.example", "A"), ("https://cal-c.example", "B"));   // c fällt aus → nur Betreiber A hat geantwortet
        var halfFail = false; try { await new NativeOtsClient(halfOps, new HttpClient(cal)).StampAsync(jws, halfOps.Ots.Calendars, default); } catch (OtsClientException e) { halfFail = e.Message.Contains("Betreiber"); }
        check(halfFail, "Betreiber: antworten nur Calendars eines Betreibers, entsteht keine Receipt");

        // Zeitlimit
        var slowCfg = Cfg(("https://cal-a.example", "Alpha"), ("https://cal-b.example", "Beta")); slowCfg.Ots.TimeoutSeconds = 1;
        var timedOut = false; var t0 = DateTime.UtcNow;
        try { await new NativeOtsClient(slowCfg, new HttpClient(new HangHandler())).StampAsync(jws, slowCfg.Ots.Calendars, default); } catch (OtsClientException) { timedOut = true; }
        check(timedOut && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(8), "Calendar-Timeout: hängende Calendars brechen nach dem Zeitlimit kontrolliert ab (keine endlose Anfrage)");

        // Grenzen: viele Operationen
        var wide = new List<byte>(OtsFile.Header(new byte[32]));
        for (var i = 0; i < 25000; i++) { wide.AddRange(new byte[] { 0xFF, 0xF0, 2, (byte)(i >> 8), (byte)i, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 0 }); }
        wide.AddRange(new byte[] { 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 0 });
        check(!OtsFile.Parse(wide.ToArray()).Valid, "Grenzen: mehr als 20 000 Knoten werden abgelehnt (keine unbegrenzte Speicherbelegung)");
        check(OtsFile.Parse(stamped).Valid && stamped.Length < 2000 && real.Length < 2000, "Grenzen: erwartete Beweise (≈ 0,7–1 KB je Receipt, Calendar-Antworten ≈ 0,2–0,3 KB) liegen weit unter dem 10 000-Byte-Limit");

        // Upgrade: bytegenaue Bindung und Erhalt des Originals
        var upBind = await OtsEvaluator.EvaluateAsync(jws, upgraded, null, 6, null, default);
        check(upBind.BindingOk && sp.Attestations.All(a => up.Attestations.Any(b => b.Kind == a.Kind && b.CalendarUri == a.CalendarUri && b.Message.SequenceEqual(a.Message))) && OtsFile.Parse(stamped).Valid,
            "Upgrade: Bindung an die Manifest-JWS bleibt bytegenau, alle ursprünglichen Attestierungen bleiben erhalten (Original separat unverändert)");

        // Bitcoin-Detailstatus: Explorer ≠ unabhängig
        var (btcOts2, btcLeaf2) = Ots(jws, "bitcoin", 800000);
        string Hex(byte[] internalOrder) => Convert.ToHexString(internalOrder.Reverse().ToArray()).ToLowerInvariant();
        var explorerLike = new FakeHeaders { Header = h => new BitcoinHeader("00000000000000000001abc", Hex(btcLeaf2), DateTime.UtcNow, 800009) };
        var ownNode = new FakeHeaders { Header = explorerLike.Header, Independent = true };
        var viaExplorer = await OtsEvaluator.EvaluateAsync(jws, btcOts2, explorerLike, 6, null, default);
        var viaNode = await OtsEvaluator.EvaluateAsync(jws, btcOts2, ownNode, 6, null, default);
        check(viaExplorer.State == OtsState.Confirmed && viaExplorer.Detail == "explorer_verified" && !viaExplorer.Independent && viaNode.Detail == "independently_confirmed" && viaNode.Independent,
            "Bitcoin: Explorer-Prüfung heißt „explorer_verified“ und gilt nicht als unabhängig; nur der eigene Knoten ergibt „independently_confirmed“");
        var shallow2 = await OtsEvaluator.EvaluateAsync(jws, btcOts2, new FakeHeaders { Header = h => new BitcoinHeader("00000000000000000001abc", Hex(btcLeaf2), DateTime.UtcNow, 800001), Independent = true }, 6, null, default);
        var pend2 = await OtsEvaluator.EvaluateAsync(jws, pendingOts2(), null, 6, null, default);
        var none2 = await OtsEvaluator.EvaluateAsync(jws, btcOts2, null, 6, null, default);
        var bad2 = await OtsEvaluator.EvaluateAsync(jws, btcOts2, new FakeHeaders { Header = h => new BitcoinHeader("0000bad", Hex(SHA256.HashData(new byte[] { 3 })), DateTime.UtcNow, 800009), Independent = true }, 6, null, default);
        check(shallow2.Detail == "header_verified" && pend2.Detail == "pending" && none2.Detail == "bitcoin_attestation_present" && bad2.Detail == "failed" && bad2.State == OtsState.Failed,
            "Bitcoin: Detailstatus header_verified / pending / bitcoin_attestation_present / failed; ungültige Bitcoin-Attestierung wird abgelehnt");
        byte[] pendingOts2() => Ots(jws, "pending").Ots;

        // Bitcoin-Core-RPC-Pfad (JSON-RPC 1.0 wie dokumentiert: getblockcount, getblockhash, getblockheader) gegen einen RPC-Double; ein echter Knoten stand nicht zur Verfügung
        var rpcLeaf = SHA256.HashData(new byte[] { 42 });
        var rpcOts = Ots(jws, "bitcoin", 700000).Ots;   // Blatt-Digest wird unten passend ersetzt
        var rpcParsedLeaf = OtsFile.Parse(rpcOts).Bitcoin.First().Message;
        var rpcCalls = new List<string>();
        var rpcHandler = new FakeCalendar { Handler = r =>
        {
            var body = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!; var m = body["method"]!.GetValue<string>(); rpcCalls.Add(m);
            object? result = m switch { "getblockcount" => 700010L, "getblockhash" => "00000000000000000002aaaa", "getblockheader" => new JsonObject { ["hash"] = "00000000000000000002aaaa", ["merkleroot"] = Convert.ToHexString(rpcParsedLeaf.Reverse().ToArray()).ToLowerInvariant(), ["time"] = 1700000000L }, _ => null };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["result"] = result is JsonObject jo ? jo : JsonValue.Create(result), ["error"] = null, ["id"] = "learny" }.ToJsonString()) };
        } };
        var rpcSource = new BitcoinRpcHeaderSource(new OtsSettings { BitcoinRpcUrl = "http://127.0.0.1:8332" }, new HttpClient(rpcHandler));
        var viaRpc = await OtsEvaluator.EvaluateAsync(jws, rpcOts, rpcSource, 6, null, default);
        check(viaRpc.State == OtsState.Confirmed && viaRpc.Detail == "independently_confirmed" && viaRpc.Independent && viaRpc.Height == 700000 && viaRpc.Confirmations == 11 && rpcCalls.SequenceEqual(new[] { "getblockcount", "getblockhash", "getblockheader" }),
            "Bitcoin-Core-RPC (Double): Header, Höhe und Tiefe aus dem eigenen Knoten → „independently_confirmed“ (echter Knoten nicht getestet)");

        // Echte Calendars und Bitcoin-Daten (nur mit OTS_LIVE_TESTS=1; benötigt Internet)
        if (Environment.GetEnvironmentVariable("OTS_LIVE_TESTS") == "1")
        {
            var live = new AppConfig { Ots = new OtsSettings { Enabled = true, TimeoutSeconds = 40, Calendars = { "https://alice.btc.calendar.opentimestamps.org", "https://finney.calendar.eternitywall.com", "https://btc.calendar.catallaxy.com" } } };
            live.Ots.Operators["https://alice.btc.calendar.opentimestamps.org"] = "OpenTimestamps"; live.Ots.Operators["https://finney.calendar.eternitywall.com"] = "Eternity Wall"; live.Ots.Operators["https://btc.calendar.catallaxy.com"] = "Catallaxy";
            var lc = new NativeOtsClient(live);
            var file = Encoding.ASCII.GetBytes("learny-live-test " + Guid.NewGuid());
            var lots = await lc.StampAsync(file, live.Ots.Calendars, default);
            var lp = OtsFile.Parse(lots);
            check(lp.Valid && lp.FileDigest.SequenceEqual(SHA256.HashData(file)) && lp.Attestations.Count(a => a.Kind == "pending") >= 2 && lp.Attestations.Select(a => a.CalendarUri).Distinct().Count() >= 2, $"LIVE: native Stempelung bei echten Calendars ({string.Join(", ", lp.Attestations.Select(a => new Uri(a.CalendarUri!).Host))})");
            var lup = await lc.UpgradeAsync(lots, default);
            check((await OtsEvaluator.EvaluateAsync(file, lup, null, 6, null, default)).State is OtsState.Pending, "LIVE: Upgrade gegen echte Calendars — ausstehend bleibt ausstehend");
            if (Environment.GetEnvironmentVariable("TEST_DUMP_DIR") is { Length: > 0 } dump) { Directory.CreateDirectory(dump); File.WriteAllBytes(Path.Combine(dump, "native-live.bin"), file); File.WriteAllBytes(Path.Combine(dump, "native-live.bin.ots"), lots); }
            // Optional: reales Upgrade einer vorhandenen Receipt (OTS_UPGRADE_FILE=<.ots>, OTS_UPGRADE_SUBJECT=<Datei, auf die sie sich bezieht>, Ergebnis nach <.ots>.upgraded)
            if (Environment.GetEnvironmentVariable("OTS_UPGRADE_FILE") is { Length: > 0 } upFile && Environment.GetEnvironmentVariable("OTS_UPGRADE_SUBJECT") is { Length: > 0 } upSubject)
            {
                var before = File.ReadAllBytes(upFile); var subject = File.ReadAllBytes(upSubject);
                var after = await lc.UpgradeAsync(before, default);
                File.WriteAllBytes(upFile + ".upgraded", after);
                var parsedAfter = OtsFile.Parse(after);
                using var http2 = LearnCards.Web.Services.ImageStorage.SafeHttp.CreateClient(false, 30);
                var evUp = await OtsEvaluator.EvaluateAsync(subject, after, new EsploraHeaderSource("https://mempool.space", http2), 6, null, default);
                check(parsedAfter.Valid && evUp.BindingOk, $"LIVE-UPGRADE: Receipt {Path.GetFileName(upFile)} — {before.Length} → {after.Length} Byte, Bitcoin-Attestierungen: {parsedAfter.Bitcoin.Count()}, Zustand {evUp.State}/{evUp.Detail}" + (evUp.Height is { } hh ? $", Block {hh} {evUp.BlockHash}, Zeit {evUp.BlockTime:O}, Tiefe {evUp.Confirmations}, Quelle {evUp.Source}" : ""));
            }
            using var http = LearnCards.Web.Services.ImageStorage.SafeHttp.CreateClient(false, 30);
            var ex = new EsploraHeaderSource("https://mempool.space", http);
            var live2 = await OtsEvaluator.EvaluateAsync(offFile, off, ex, 6, null, default);
            check(live2.State == OtsState.Confirmed && live2.Height == 358391 && live2.BlockHash == "000000000000000003e892881a8cdcdc117c06d444057c98b6f04a9ee75a2319" && live2.BlockTime == new DateTime(2015, 5, 28, 15, 41, 18, DateTimeKind.Utc),
                $"LIVE: offizieller bestätigter Beweis gegen echte Bitcoin-Daten (Block {live2.Height}, {live2.BlockHash}, Tiefe {live2.Confirmations}; Quelle: {live2.Source})");
        }
    }

    private static string FixtureDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "fixtures", "ots"))) return Path.Combine(d.FullName, "fixtures", "ots");
        throw new DirectoryNotFoundException("fixtures/ots");
    }

    /// <summary>
    /// Live-Verifikation (/verify/{id}): echte Zustandswechsel, geteilter Auftrag, Pending/Bestätigt/Ausfall/Fehler, Drosselung. Die Dienste sind die produktiven; Calendar-Client und
    /// Bitcoin-Quelle sind Test-Doubles (echte Calendars: siehe Live-Tests der nativen OTS-Schicht).
    /// </summary>
    public static async Task LiveAsync(Action<bool, string> check, string tmp)
    {
        var dir = Path.Combine(tmp, "livever"); Directory.CreateDirectory(dir);
        await using var db = new LearnCards.Web.Data.SqliteDatabase(Path.Combine(dir, "l.db")); await db.InitAsync();
        var cfg = new AppConfig { Blockchain = new BlockchainSettings { BadgeKeyFile = Path.Combine(dir, "key.pem"), PublicBaseUrl = "https://learny.test" }, Ots = new OtsSettings { Enabled = true, Confirmations = 6, Calendars = { "https://cal-a.example", "https://cal-b.example" } } };
        cfg.Ots.Operators["https://cal-a.example"] = "Alpha"; cfg.Ots.Operators["https://cal-b.example"] = "Beta";
        var users = new LearnCards.Web.Services.UserService(db, cfg); var chain = new ChainSettingsStore(db, cfg);
        using var signer = new BadgeSigner(cfg); var certs = new CertificateService(db, signer, chain, users, cfg);
        var jwk = signer.PublicJwk(); var kid = certs.JwtKeyId;

        async Task<(Certificate Cert, byte[] Jws)> MakeAsync(string ownOtsFor, bool withPdf = true)
        {
            var uid = Guid.NewGuid().ToString();
            await db.ExecuteAsync("INSERT INTO users (id, display_name, email, status, legacy_migrated, created_at, last_login_at) VALUES (@id, @n, '', 'active', 1, @t, @t)", new (string, object?)[] { ("id", uid), ("n", "Lernende"), ("t", DateTime.UtcNow) });
            await db.ExecuteAsync("INSERT INTO user_roles (user_id, role, granted_by, granted_at) VALUES (@id, 'user', NULL, @t)", new (string, object?)[] { ("id", uid), ("t", DateTime.UtcNow) });
            var c = (await certs.TryIssueAsync(new QuizResultRecord { UserSub = uid, ModuleId = Guid.NewGuid().ToString(), Score = 90, MaxScore = 100, PassPercent = 60, Passed = true, SessionId = Guid.NewGuid().ToString() }, "Livemodul", "global", 1))!;
            var files = new List<PdfEmbeddedFile>
            {
                new("module-credential.json", Encoding.UTF8.GetBytes("{}"), "application/json", "Data", "x"), new("module-credential.jwt", Encoding.ASCII.GetBytes(c.BadgeJws), "application/jwt", "Data", "x"),
                new("public-key.jwk", Encoding.UTF8.GetBytes(jwk.ToJsonString()), "application/jwk+json", "Supplement", "x"), new("verification.json", Encoding.UTF8.GetBytes("{}"), "application/json", "Supplement", "x"),
            };
            var manifest = IntegrityManifest.Build(c, "https://learny.test/verify/" + c.Id, certs.IssuerId, c.IssuedAt, jwk, kid, "https://learny.test/.well-known/jwks.json", files);
            var jws = signer.SignBytes(manifest, IntegrityManifest.JwsTyp, IntegrityManifest.JwsCty, kid);
            files.Add(new(IntegrityManifest.ManifestName, manifest, "application/json", "Supplement", "x")); files.Add(new(IntegrityManifest.JwsName, Encoding.ASCII.GetBytes(jws), "application/jose", "Supplement", "x"));
            if (withPdf)
            {
                var pdf = new PdfBuilder { Title = "t" }; pdf.AddPage().Text(50, 780, "x", 12); foreach (var f in files) pdf.Files.Add(f);
                await db.ExecuteAsync("INSERT INTO certificate_pdfs (certificate_id, pdf_base64, pdf_sha256, size_bytes, signed, signer_thumbprint, signer_subject, signed_at, created_at) VALUES (@id, @b, 'h', 1, 1, '', '', @t, @t)",
                    new (string, object?)[] { ("id", c.Id), ("b", Convert.ToBase64String(pdf.Build())), ("t", DateTime.UtcNow) });
            }
            return (c, Encoding.ASCII.GetBytes(jws));
        }
        var skew = TimeSpan.Zero;
        OtsMaintenanceService NewOts(FakeClient client, FakeHeaders? headers) { var o = new OtsMaintenanceService(db, cfg, client, headers); o.UseClock(() => DateTime.UtcNow + skew); return o; }
        // Ablage zur „Ausstellungszeit“, danach „eine Stunde später“: Die Drosselung der Calendar-Abfrage ist dann abgelaufen
        async Task Seed(OtsMaintenanceService o, string certId, byte[] manifest, string jws, StampResult stamp, string pdf) { skew = TimeSpan.Zero; await o.RecordInitialAsync(certId, manifest, jws, stamp, pdf); skew = TimeSpan.FromHours(1); }
        async Task<LiveSnapshot> Run(LiveVerificationService svc, string id, List<string>? log = null)
        {
            var run = svc.GetOrStart(id); var running = 0;
            if (log is not null) { run.Changed += () => { var s = run.Snapshot(); running = Math.Max(running, s.Steps.Count(x => x.State == StepState.Running)); log.Add(string.Join(",", s.Steps.Select(x => x.State[0]))); }; }
            for (var i = 0; i < 400 && !run.Done; i++) await Task.Delay(25);
            if (log is not null) log.Add("maxRunning=" + running);
            return run.Snapshot();
        }

        // 1) Pending: Receipt gebunden, Calendars antworten ohne Bestätigung → Animation endet, Bitcoin „ausstehend“
        var (c1, jws1) = await MakeAsync("pending");
        var client1 = new FakeClient { Stamp = Ots(jws1, "pending").Ots, Upgrade = o => o };
        var ots1 = NewOts(client1, null);
        await Seed(ots1, c1.Id, Encoding.UTF8.GetBytes("m"), Encoding.ASCII.GetString(jws1), await ots1.TryStampAsync(jws1, default), "pdf");
        using var live1 = new LiveVerificationService(db, certs, ots1);
        var log1 = new List<string>(); var s1 = await Run(live1, c1.Id, log1);
        var order = s1.Steps.Select(x => x.Key).SequenceEqual(LiveRun.Definition.Select(d => d.Key));
        check(s1.Done && s1.Outcome == "pending" && order && s1.Steps.All(x => x.State != StepState.Running && x.State != StepState.Pending),
            "Live: Pending → Prüfung abgeschlossen (Endzustand), Reihenfolge der Schritte unverändert, kein Schritt bleibt laufend");
        string St(string k) => s1.Steps.First(x => x.Key == k).State;
        check(St("load") == StepState.Ok && St("signature") == StepState.Ok && St("issuer") == StepState.Ok && St("manifest") == StepState.Ok && St("ots_read") == StepState.Ok && St("ots_bind") == StepState.Ok && St("calendars") == StepState.Ok && St("bitcoin") == StepState.Waiting && St("depth") == StepState.Skipped && St("done") == StepState.Ok,
            "Live: Pending zeigt Signatur/Manifest/OTS/Calendar als erfolgreich, Bitcoin als ausstehend (nicht bestätigt), Tiefe übersprungen");
        check((s1.Notice ?? "").Contains("Verankerung in der Bitcoin-Blockchain steht noch aus") && s1.Ots is { State: "pending" } && s1.Ots.Height is null && s1.Ots.BlockHash is null, "Live: Pending-Hinweis wörtlich, keine erfundenen Blockangaben");
        check(log1.Count > 8 && log1.Last().StartsWith("maxRunning=1") || log1.Last() == "maxRunning=1", "Live: höchstens ein Schritt gleichzeitig „in Bearbeitung“ (Animation wandert weiter)");
        check(log1.Where(l => !l.StartsWith("maxRunning")).Select(l => l.Count(ch => ch == 'p')).Zip(log1.Where(l => !l.StartsWith("maxRunning")).Select(l => l.Count(ch => ch == 'p')).Skip(1)).All(t => t.Second <= t.First || t.Second - t.First <= 0), "Live: es werden nur echte Zustandswechsel gemeldet (Anzahl wartender Schritte steigt nie)");

        // 2) Mehrfachaufrufe: ein Auftrag, genau eine Calendar-Abfrage
        var (c2, jws2) = await MakeAsync("pending");
        var client2 = new FakeClient { Stamp = Ots(jws2, "pending").Ots, Upgrade = o => o };
        var ots2 = NewOts(client2, null); await Seed(ots2, c2.Id, Encoding.UTF8.GetBytes("m"), Encoding.ASCII.GetString(jws2), await ots2.TryStampAsync(jws2, default), "pdf");
        using var live2 = new LiveVerificationService(db, certs, ots2);
        var runs = Enumerable.Range(0, 12).AsParallel().Select(_ => live2.GetOrStart(c2.Id)).ToList();
        for (var i = 0; i < 400 && !runs[0].Done; i++) await Task.Delay(25);
        check(runs.Distinct().Count() == 1 && live2.StartedRuns == 1 && client2.UpgradeCalls == 1, "Live: zwölf gleichzeitige Besucher teilen einen Auftrag — genau eine Calendar-Abfrage");
        var again = live2.GetOrStart(c2.Id);
        check(ReferenceEquals(again, runs[0]) && live2.StartedRuns == 1 && client2.UpgradeCalls == 1, "Live: ein weiterer Besucher kurz danach erzwingt keine zweite Prüfung");
        var throttled = await ots2.CheckLatestAsync(c2.Id, TimeSpan.FromHours(3), default);
        check(throttled.Kind == OtsMaintenanceService.CheckKind.Throttled && client2.UpgradeCalls == 1, "Live: Calendar-Abfragen sind gedrosselt (last_checked_at), Besucher können sie nicht erzwingen");

        // 3) Calendar-Ausfall: nicht überprüfbar, gespeicherter Stand getrennt, kein Erfolg simuliert
        var (c3, jws3) = await MakeAsync("pending");
        var client3 = new FakeClient { Stamp = Ots(jws3, "pending").Ots, Upgrade = o => o, UpgradeThrows = true };
        var ots3 = NewOts(client3, null); await Seed(ots3, c3.Id, Encoding.UTF8.GetBytes("m"), Encoding.ASCII.GetString(jws3), await ots3.TryStampAsync(jws3, default), "pdf");
        using var live3 = new LiveVerificationService(db, certs, ots3);
        var s3 = await Run(live3, c3.Id);
        check(s3.Done && s3.Outcome == "unavailable" && s3.Steps.First(x => x.Key == "calendars").State == StepState.Unverifiable && s3.Stored is { AnchorStatus: "pending", ProofVersion: 1 } && (s3.Notice ?? "").Contains("konnte nicht abgeschlossen werden") && s3.Steps.First(x => x.Key == "done").State == StepState.Unverifiable,
            "Live: Calendar-Ausfall → „nicht überprüfbar“, letzter gespeicherter Stand getrennt, kein simulierter Erfolg");

        // 4) Bestätigt: Upgrade liefert Bitcoin-Attestierung, Header stimmen → Blockangaben, Vertrauensmodell, neue Proof-Version, Download = aktuelle Version
        string Hex(byte[] internalOrder) => Convert.ToHexString(internalOrder.Reverse().ToArray()).ToLowerInvariant();
        var (c4, jws4) = await MakeAsync("pending");
        var (btc4, leaf4) = Ots(jws4, "bitcoin", 800000);
        var client4 = new FakeClient { Stamp = Ots(jws4, "pending").Ots, Upgrade = _ => btc4 };
        FakeHeaders Hdr(bool independent) => new() { Independent = independent, Header = h => new BitcoinHeader("00000000000000000001abc", Hex(leaf4), new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), 800009) };
        var ots4 = NewOts(client4, Hdr(false)); await Seed(ots4, c4.Id, Encoding.UTF8.GetBytes("m"), Encoding.ASCII.GetString(jws4), await ots4.TryStampAsync(jws4, default), "pdf");
        using var live4 = new LiveVerificationService(db, certs, ots4);
        var s4 = await Run(live4, c4.Id);
        var latest4 = await ots4.GetLatestAsync(c4.Id); var versions4 = await ots4.GetVersionsAsync(c4.Id);
        check(s4.Outcome == "valid" && s4.Steps.First(x => x.Key == "bitcoin").State == StepState.Ok && s4.Steps.First(x => x.Key == "depth").State == StepState.Ok && s4.Ots is { State: "confirmed", Height: 800000, BlockHash: "00000000000000000001abc", Confirmations: 10, Independent: false } && s4.Ots.Detail == "explorer_verified",
            "Live: bestätigter Nachweis zeigt Blockhöhe, Blockhash, Tiefe; Explorer-Prüfung ist als solche gekennzeichnet (nicht unabhängig)");
        check(versions4.Count == 2 && latest4!.Ots!.SequenceEqual(btc4) && versions4[0].Ots!.SequenceEqual(client4.Stamp) && s4.Ots.ProofVersion == 2, "Live: Upgrade legt Proof-Version 2 an, das Original (Version 1) bleibt erhalten, der Download-Stand ist die aktuelle Version");
        var ots4n = NewOts(client4, Hdr(true)); using var live4n = new LiveVerificationService(db, certs, ots4n);
        var s4n = await Run(live4n, c4.Id);
        check(s4n.Ots is { Independent: true, Detail: "independently_confirmed" } && s4n.Steps.First(x => x.Key == "calendars").State == StepState.Skipped, "Live: bereits bestätigter Nachweis wird nicht erneut bei Calendars abgefragt; eigener Knoten = unabhängig");

        // 5) Fehlgeschlagen: Receipt gehört zu einer anderen Datei
        var (c5, jws5) = await MakeAsync("pending");
        var wrong = Ots(Encoding.ASCII.GetBytes("andere"), "pending").Ots;
        var client5 = new FakeClient { Stamp = wrong, Upgrade = o => o };
        var ots5 = NewOts(client5, null); await Seed(ots5, c5.Id, Encoding.UTF8.GetBytes("m"), Encoding.ASCII.GetString(jws5), new StampResult(wrong, "submitted", "", "t"), "pdf");
        using var live5 = new LiveVerificationService(db, certs, ots5);
        var s5 = await Run(live5, c5.Id);
        check(s5.Outcome == "failed" && s5.Steps.First(x => x.Key == "ots_bind").State == StepState.Failed && s5.Steps.First(x => x.Key == "calendars").State == StepState.Skipped && s5.Steps.First(x => x.Key == "signature").State == StepState.Ok && client5.UpgradeCalls == 0,
            "Live: falsch zugeordnete Receipt → genau dieser Schritt ist fehlgeschlagen, spätere Schritte übersprungen, erfolgreiche Schritte überdecken ihn nicht");

        // 6) Ohne Zeitnachweis / unbekannt
        var (c6, _) = await MakeAsync("none");
        var ots6 = NewOts(new FakeClient(), null); await Seed(ots6, c6.Id, Encoding.UTF8.GetBytes("m"), "x.y.z", new StampResult(null, "none", "OTS aus", ""), "pdf");
        using var live6 = new LiveVerificationService(db, certs, ots6);
        var s6 = await Run(live6, c6.Id);
        check(s6.Steps.Where(x => x.Key is "ots_read" or "ots_bind" or "calendars" or "bitcoin" or "depth").All(x => x.State == StepState.Skipped) && s6.Ots is { HasReceipt: false },
            "Live: ohne Zeitnachweis werden die OTS-Schritte als übersprungen dargestellt, nicht als ausgeführt");
        var sNf = await Run(live6, Guid.NewGuid().ToString());
        var sBad = await Run(live6, "kein-guid");
        check(sNf.Outcome == "notfound" && sBad.Outcome == "notfound" && sNf.Steps.First(x => x.Key == "load").State == StepState.Failed, "Live: unbekannte Kennung → „nicht gefunden“, kein Prüfergebnis");

        // 7) Oberfläche: keine Pakete, reduzierte Bewegung, nur laufender Schritt animiert, keine Prozentangaben
        var root = FixtureDir(); for (var i = 0; i < 4; i++) root = Path.GetDirectoryName(root)!;
        var razor = File.ReadAllText(Path.Combine(root, "src", "LearnCards.Web", "Components", "Shared", "LiveVerification.razor")); var css = File.ReadAllText(Path.Combine(root, "src", "LearnCards.Web", "wwwroot", "app.css"));
        check(razor.Contains("st.State == StepState.Running ? \"lv-spin\"") && css.Contains("@media (prefers-reduced-motion: reduce) { .lv-spin { animation: none; }") && razor.Contains("aria-live=\"polite\"") && razor.Contains("lv-sr") && !razor.Contains("%\"") && !razor.Contains("Task.Delay") && !css.Contains("@import"),
            "Live-UI: nur der laufende Schritt animiert, reduzierte Bewegung deaktiviert die Animation, Screenreader-Statusmeldung, keine Prozentwerte/künstlichen Verzögerungen/externen Ressourcen");
    }

    internal sealed class FakeClient : IOtsClient
    {
        public byte[] Stamp = Array.Empty<byte>(); public Func<byte[], byte[]>? Upgrade; public bool UpgradeThrows, StampThrows; public int StampCalls, UpgradeCalls; public IReadOnlyList<string>? LastCalendars; public byte[]? LastBytes;
        public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>("ots-fake 0.0");
        public Task<byte[]> StampAsync(byte[] fileBytes, IReadOnlyList<string> calendars, CancellationToken ct) { StampCalls++; LastCalendars = calendars; LastBytes = fileBytes; return StampThrows ? throw new OtsClientException("Calendars nicht erreichbar") : Task.FromResult(Stamp); }
        public Task<byte[]> UpgradeAsync(byte[] ots, CancellationToken ct) { Interlocked.Increment(ref UpgradeCalls); return UpgradeThrows ? throw new OtsClientException("Calendar nicht erreichbar") : Task.FromResult(Upgrade!(ots)); }
    }

    /// <summary>PDF-Ebene: das echte, ausgestellte Themen-PDF enthält Manifest und JWS; ein fremd signiertes Paket mit manipulierten/zusätzlichen Anhängen wird auf Manifest-Ebene abgewiesen.</summary>
    public static async Task E2EAsync(IReadOnlyList<PdfAttachment> attachments, Func<byte[], Task<JsonNode?>> upload, X509Certificate2 foreignCert, Action<bool, string> check, byte[] originalPdf)
    {
        var names = attachments.Select(a => a.Name).ToHashSet();
        check(names.Contains(IntegrityManifest.ManifestName) && names.Contains(IntegrityManifest.JwsName) && IntegrityManifest.RequiredFiles("topic").All(names.Contains) && names.Contains("competence-snapshot.json"),
            "Integrität E2E: Themen-PDF enthält integrity-manifest.json, integrity-manifest.jws und alle ursprünglichen Nutzdateien (auch competence-snapshot.json)");
        if (Environment.GetEnvironmentVariable("TEST_DUMP_DIR") is { Length: > 0 } dump)   // nur für die manuelle/skriptgestützte Langzeitprüfung (tools/integrity/verify_package.py)
        {
            var ad = Path.Combine(dump, "attachments"); Directory.CreateDirectory(ad);
            foreach (var at in attachments) File.WriteAllBytes(Path.Combine(ad, at.Name), at.Data);
        }
        var v = await upload(originalPdf);
        check(S(v, "integrity_manifest.status") == "valid" && S(v, "integrity_manifest.jws.signature_valid") == "true" && S(v, "integrity_manifest.issuer_key.trust") == "installation" && S(v, "overall.manifest_status") == "valid" && S(v, "attachments.hashes_match") == "true",
            "Integrität E2E: Prüfung des echten PDFs — Manifest gültig, JWS gültig, Schlüssel dieser Installation, Anhänge ohne verification.json-Fehlalarm für die Manifest-Artefakte");
        check(S(v, "timestamp.present") == "false" && S(v, "timestamp.state") == "none" && S(v, "overall.timestamp_state") == "none", "Integrität E2E: ohne OTS-Konfiguration kein Zeitnachweis eingebettet, Zustand „none“ — kein Anschein einer Verankerung");

        PdfEmbeddedFile Mk(PdfAttachment a, byte[]? data = null) => new(a.Name, data ?? a.Data, "application/json", "Data", a.Name);
        byte[] Build(Func<PdfAttachment, byte[]?> mutate, IEnumerable<PdfEmbeddedFile>? extra = null)
        {
            var d = new PdfBuilder { Title = "Fälschung" }; d.AddPage().Text(50, 780, "Fälschung", 12);
            foreach (var a in attachments) { var m = mutate(a); if (m is not null) d.Files.Add(Mk(a, m)); }
            foreach (var e in extra ?? Array.Empty<PdfEmbeddedFile>()) d.Files.Add(e);
            return d.Build(new PdfSignOptions { Certificate = foreignCert });
        }
        var vExtra = await upload(Build(a => a.Data, new[] { new PdfEmbeddedFile("versteckt.bin", new byte[] { 1, 2, 3 }, "application/octet-stream", "Data", "x") }));
        check(S(vExtra, "integrity_manifest.status") == "invalid" && S(vExtra, "integrity_manifest.files.unlisted_attachments.0") == "versteckt.bin", "Integrität E2E: zusätzlicher Anhang wird gemeldet");
        var foreignJwk = JsonNode.Parse(attachments.First(a => a.Name == "public-key.jwk").Data)!;   // ungültiger Kurvenpunkt im JWK darf keinen 500 auslösen
        var vPk = await upload(Build(a => a.Name == "public-key.jwk" ? Encoding.UTF8.GetBytes(foreignJwk.ToJsonString().Replace(S(foreignJwk, "x"), S(foreignJwk, "y"))) : a.Data, new[] { new PdfEmbeddedFile("versteckt.bin", new byte[] { 1, 2, 3 }, "application/octet-stream", "Data", "x") }));
        check(S(vPk, "integrity_manifest.status") == "invalid" && S(vPk, "overall.valid") == "false", "Integrität E2E: ausgetauschtes public-key.jwk (ungültiger Kurvenpunkt) wird ohne Serverfehler als ungültig erkannt");
        var vOts = await upload(Build(a => a.Data, new[] { new PdfEmbeddedFile(IntegrityManifest.OtsName, OtsFile.Header(SHA256.HashData(new byte[] { 1 })), "application/vnd.opentimestamps.ots", "Data", "x") }));
        check(S(vOts, "timestamp.state") == "failed" && S(vOts, "overall.valid") == "false", "Integrität E2E: eingeschleuste/ausgetauschte OTS-Datei (falsche Bindung) → Zeitnachweis ungültig");
    }
}
