# 0040 — Nativer OpenTimestamps-Client (ohne CLI, Python und WSL)

- **Status:** Akzeptiert und umgesetzt (in Produktion seit v0.1.22)
- **Datum:** 2026-10-29
- **Ersetzt teilweise:** [0039](0039-opentimestamps-integritaetsmanifest-und-langzeitverifikation.md) — nur die Punkte 1 und 2 (offizieller `ots`-CLI als externes Betriebswerkzeug, Prozessaufruf) und die zugehörigen Konfigurationsvariablen `OTS_CLIENT_PATH`. Alle übrigen Entscheidungen von 0039 (Manifest, Manifest-JWS, Stempelgrundlage, Zustände, Persistenz, Upgrade-Prinzip, Bitcoin-Prüfung, Datenschutz) gelten unverändert.

> **Architekturaussage.** Learny stempelt, aktualisiert und liest OpenTimestamps-Nachweise mit eigenem C#-Code (`HttpClient`, `System.Security.Cryptography`) nach dem offiziellen Datei- und Calendar-Protokoll. Auf dem Windows-/IIS-Server werden weder Python noch WSL noch ein `ots`-Programm benötigt; es werden keine Prozesse gestartet. Die erzeugten `.ots`-Dateien sind Standarddateien (Version 1) und mit dem offiziellen Client austauschbar.

## Kontext

Der Produktivserver (Windows Server/IIS) bietet kein WSL; der Python-Client lässt sich dort nicht wie vorgesehen betreiben (u. a. lädt `python-bitcoinlib` OpenSSL unter Windows nicht). ADR 0002 verbietet NuGet-Pakete, das OTS-Format ist aber klein und vollständig dokumentiert (python-opentimestamps).

## Entscheidung

1. **Nativer Client** `NativeOtsClient : IOtsClient` ersetzt `CliOtsClient` (gelöscht). Die fachliche Schnittstelle `IOtsClient`, `OtsMaintenanceService`, die Zustände und die Tabelle `integrity_proofs` bleiben unverändert; es gibt keine Migration. Bestehende Receipts (auch die vom offiziellen Client erzeugten) bleiben les- und prüfbar.
2. **Calendar-Protokoll** nach `opentimestamps/calendar.py`: `POST {calendar}/digest` (Rohbytes des 32-Byte-Commitments, Antwort ein serialisierter Zeitstempelbaum), `GET {calendar}/timestamp/{hex(commitment)}` (404 = noch nicht bestätigt), Header `Accept: application/vnd.opentimestamps.v1`, Antwort höchstens 10 000 Byte. Es werden keine eigenen Endpunkte verwendet.
3. **Stempeln wie `ots stamp`:** `digest = SHA-256(exakte Bytes von integrity-manifest.jws)`; Baum `digest → append(16 Byte Zufall) → sha256 → Commitment`; die Antworten aller Calendars werden in den Commitment-Knoten vereinigt; Ausgabe `DetachedTimestampFile` Version 1 (Magic, Version 1, Digest-Operation SHA-256, Baum). Mindestens **zwei Calendars unterschiedlicher Hosts** müssen antworten, sonst Fehler ohne Receipt.
4. **Format:** `OtsFile` liest und schreibt den Baum (Operationen SHA-1, RIPEMD-160, SHA-256, Append, Prepend, Reverse, Hexlify; Attestierungen Bitcoin und Pending, andere werden erhalten). Serialisierung wie die Referenz (sortierte Attestierungen/Operationen): gelesene Standardbeweise werden bytegleich zurückgeschrieben. RIPEMD-160 ist in .NET nicht verfügbar und wird zum Nachrechnen älterer Beweise selbst implementiert (Referenzvektoren getestet); SHA-1/RIPEMD-160 dienen ausschließlich dem Format älterer Beweise, nie neuen Signaturen. Nicht berechenbare Operationen (Keccak-256) ergeben „nicht überprüfbar“, nie „ungültig“; Unbekanntes und Manipuliertes ergibt „ungültig“. Grenzen: 20 000 Knoten, Tiefe 256, Operand 4096 Byte, Nachricht 4096 Byte, Datei 1 MB.
5. **Sicherheit:** nur `https`, nur Calendars aus `OTS_CALENDARS` (feste Allowlist — auch beim Upgrade werden URIs aus einer Receipt **nie** abgerufen, wenn sie nicht konfiguriert sind), `SafeHttp` (keine Redirects, keine nicht öffentlichen Zieladressen, Größenlimit), Zeitlimit `OTS_TIMEOUT_SEC`, höchstens 4 parallele Anfragen, `CancellationToken`. Gesendet wird ausschließlich das 32-Byte-Commitment (Nonce-gehärtet), nie Dateiinhalte.
6. **Upgrade:** für jedes Pending-Blatt einer konfigurierten Calendar-URI `GET …/timestamp/{hex}`; 200 → Baum validieren und in das Blatt vereinigen (Pending bleibt erhalten); 404/Ausfall → unverändert, kein Fehler. Idempotent: ohne Neues bleibt die Receipt bytegleich. Eine aktualisierte Receipt wird nie automatisch „bestätigt“; das entscheidet allein die Header-Prüfung. Die Persistenz (neue Proof-Version, Original erhalten) liegt unverändert im `OtsMaintenanceService`; es bleibt bei Wartungsläufen, keine offene HTTP-Anfrage wartet auf Bitcoin.
7. **Bitcoin-Prüfung** bleibt getrennt in sechs Aussagen: (A) Receipt an Datei gebunden, (B) Calendar-Einreichung erfolgreich, (C) Bitcoin-Attestierung vorhanden, (D) Block-Header kryptografisch korrekt (Doppel-SHA-256, Proof-of-Work, Merkle-Root), (E) Block gehört zur akzeptierten Kette, (F) Bestätigungstiefe. Quellen: eigener Bitcoin-Core-RPC (`OTS_BITCOIN_RPC_URL`, D–F aus eigener Hand) **oder** ein Esplora-Explorer (`OTS_BITCOIN_EXPLORER_URL`, neu: D selbst geprüft, E und F sind eine ausgewiesene Zusatzannahme über den Betreiber des Explorers, die Oberfläche nennt sie). Ohne Quelle: „nicht überprüfbar“. Keine eigene Node-Implementierung; ein Knoten ist keine Voraussetzung für das Stempeln.
8. **Betrieb:** keine `OTS_CLIENT_PATH`-Variable mehr (wird ignoriert). Ausgehende HTTPS-Verbindungen zu den freigegebenen Calendars (und optional Explorer/RPC) genügen; keine Shell, kein Benutzerprofil, kein PATH.
9. **Interoperabilität** wird mit dem offiziellen Client auf einem Entwicklungsrechner geprüft (nicht auf dem Produktivserver): nativ erzeugte `.ots` lesen (`ots info`) und von den Calendars bestätigt (`ots upgrade`), offizielle Receipts nativ lesen/zurückschreiben, offizieller bestätigter Beispielbeweis gegen echte Bitcoin-Daten.

## Nachtrag 2026-10-29 (Restabnahme)

- **Betreiberidentität statt Hostanzahl:** `OTS_CALENDARS` nimmt Einträge `https://calendar|Betreiber`. Unabhängigkeit = mindestens zwei **verschiedene benannte** Betreiber, sowohl in der Konfiguration (`OtsSettings.Problem()`) als auch unter den tatsächlich antwortenden Calendars (`NativeOtsClient`). Fehlt der Betreiber, gilt der Calendar als nicht unabhängig. Dies ersetzt die frühere Regel „zwei unterschiedliche Hosts“ (Punkt 3 oben). Die Zuordnung der drei verwendeten Calendars und ihre Belege/Grenzen stehen in `docs/pdf-signature.md`; es werden keine Betreiber aus Hostnamen geraten.
- **Prüfvertrauensmodell:** `IBitcoinHeaderSource.Independent` ist nur beim eigenen Bitcoin-Core-RPC wahr; Explorer-Prüfung bleibt „explorer_verified“ und wird nie als unabhängig ausgegeben. Der öffentliche Zustand (`State`) bleibt unverändert, `OtsEvaluation.Detail` ergänzt intern `receipt_valid · pending · bitcoin_attestation_present · header_verified · explorer_verified · independently_confirmed · not_verifiable · failed`.
- **Aktualisierte Receipts:** werden nie in ein gespeichertes PDF geschrieben; sie sind als Proof-Version separat abrufbar (`GET /api/public/certificates/{id}/integrity.ots[?version=N]`). Ein neues, neu CMS-signiertes PDF-Artefakt bleibt eine eigene, noch nicht getroffene Architekturentscheidung.
- **Legacy-Hashes:** SHA-1 und RIPEMD-160 werden nur zum Nachrechnen von OTS-Operationen verwendet; die DevSkim-Meldungen sind zeilenweise mit Begründung unterdrückt (keine globale Deaktivierung).

## Abnahme (2026-10-29)

Erfüllt und belegt: nativer Client ohne Prozesse (im Quellcode keine Prozessaufrufe; Windows-Lauf mit Minimal-PATH ohne WSL/Python); drei echte Calendars antworten, Receipts sind standardkonform (`ots info` des offiziellen Clients liest sie, die Calendars erkennen die nativen Commitments); offizielle Receipts werden nativ gelesen und bytegleich zurückgeschrieben; Parser-Grenzen und Manipulationsfälle getestet; Betreiberzuordnung mit dokumentierten Belegen; Detailstatus und Vertrauensmodelle (Explorer ≠ unabhängig). **Nicht erreicht, bewusst offen:** ein reales Upgrade zu einer Bitcoin-Attestierung (alle gestempelten Receipts waren zum Prüfzeitpunkt noch ausstehend), die Prüfung gegen einen echten eigenen Bitcoin-Core-Knoten und ein Windows-Test auf einem Server ohne installiertes WSL. Diese Punkte ändern die Entscheidung nicht; sie sind Betriebsprüfungen, die nachzuholen sind, sobald die Bestätigung vorliegt bzw. ein Knoten bereitsteht.

## Konsequenzen

- Kein externes Laufzeitwerkzeug; weniger Angriffsfläche (keine Prozesse), aber eigener Code für ein Fremdformat → Interoperabilitätstests sind Pflicht bei Änderungen (`IntegrityTests.NativeAsync`, mit `OTS_LIVE_TESTS=1` gegen echte Calendars).
- Das Protokoll kann sich ändern; Antworten unbekannten Formats gelten als Fehler, nicht als Nachweis.
- Verbleibend: automatisches Einbetten eines aktualisierten Proofs in ein neues PDF-Artefakt (nicht umgesetzt), Prüfung über eigenen Knoten (nur dokumentiert/RPC-Quelle).

## Leitplanken für Änderungen

- Format- und Protokollcode nur in `Services/Certificates/Ots/*`; keine Prozessaufrufe, keine NuGet-Pakete.
- Calendar-URLs ausschließlich aus der Konfiguration; Receipt-Inhalte (URIs, Operanden) sind nicht vertrauenswürdig.
- Nach Änderungen: Rundlauftests mit echten Receipts, Live-Test (`OTS_LIVE_TESTS=1`) und, wenn möglich, `ots info`/`ots upgrade` auf einer nativ erzeugten Datei.
- Zustände nie ohne unabhängig geprüftes Ergebnis anheben (ADR 0039 Punkt 13).

## Betroffener Code

- `Services/Certificates/Ots/NativeOtsClient.cs`, `OtsFile.cs`, `OtsService.cs` (`EsploraHeaderSource`, `BitcoinRpcHeaderSource`, `OtsEvaluator`), `OtsMaintenanceService.cs`, `Infrastructure/OtsSettings.cs`, `Program.cs`, `tests/LearnCards.IntegrationTests/IntegrityTests.cs`, `tools/integrity/verify_package.py`
