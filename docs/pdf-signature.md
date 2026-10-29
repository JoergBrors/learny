# PDF-Signatur der Zertifikate

Zertifikats-PDFs (Modul- und Themenabschluss, [ADR 0033](adr/0033-signierte-zertifikats-pdfs.md)) werden digital signiert. Dafür braucht die Installation ein **eigenes X.509-Signaturzertifikat** in der `.env` — es ist **unabhängig** vom Badge-Schlüssel (ES256, ADR 0021), mit dem die Open Badges, das CLR und das Master-Badge signiert werden.

## Eigene Learny-PKI (ADR 0035)

Ohne `PDF_SIGNING_CERT_*` kann Learny das Signaturzertifikat aus der **eigenen PKI** beziehen ([docs/pki.md](pki.md)): Root CA → Issuing CA → Signaturzertifikat (EKU `id-kp-documentSigning`), die Kette ist im PDF eingebettet, Widerruf läuft über CRLs. Reihenfolge: ausdrücklich konfiguriertes `PDF_SIGNING_CERT_*` → betriebsbereite PKI → nur Development das Entwicklungszertifikat → sonst 503. Die PKI-Signatur ist **nur für Empfänger vertrauenswürdig, die die Learny-Root importieren**; Adobe & Co. erkennen sie nicht automatisch.

## Konfiguration

| Variable | Bedeutung |
|---|---|
| `PDF_SIGNING_CERT_FILE` | Pfad zu einer PKCS#12-Datei (`.pfx`/`.p12`) **oder** einer PEM-Datei mit dem Zertifikat (und optional der Kette) |
| `PDF_SIGNING_CERT_PASSWORD` | Passwort der PKCS#12-Datei (bzw. des verschlüsselten privaten PEM-Schlüssels) |
| `PDF_SIGNING_CERT_PFX_BASE64` | PKCS#12 als Base64-Text (für Secret-Verwaltungen; hat Vorrang vor `PDF_SIGNING_CERT_FILE`) |
| `PDF_SIGNING_CERT_PEM` / `PDF_SIGNING_KEY_PEM` | PEM-Text oder -Pfad für Zertifikat(-kette) und privaten Schlüssel |
| `PDF_SIGNING_NAME`, `_REASON`, `_LOCATION`, `_CONTACT` | Angaben, die in der Signatur stehen (optional) |
| `PDF_SIGNING_DEV_CERT` | `true`/`false`: Entwicklungszertifikat erzwingen/verbieten. Standard: nur in `Development` |

Der Schlüssel darf RSA oder ECDSA (z. B. P-256) sein. Das Zertifikat muss gültig und der private Schlüssel vorhanden sein; sonst bricht der **Start** mit einer verständlichen Meldung ab (kein stilles Abschalten). Passwörter, Pfade zu Secrets und Schlüsselinhalte erscheinen nie in Logs, Fehlermeldungen oder PDFs.

**Ohne Konfiguration** (und außerhalb von `Development`) liefert die Anwendung **keine** PDFs aus: `GET /api/certificates/{id}/certificate.pdf` antwortet `503` (`pdf_signing_unavailable`). Bereits erzeugte PDFs bleiben abrufbar.

In `Development` erzeugt die Anwendung ersatzweise ein selbstsigniertes Zertifikat (`data/pdf-signing-dev.pfx`). Es ist **nicht vertrauenswürdig** und nur für Entwicklung und Tests gedacht.

## Vertrauen — was die Signatur bedeutet

Technisch ist die Signatur eine CMS-Signatur (CAdES, `ETSI.CAdES.detached`, SHA-256) über das **gesamte** Dokument einschließlich aller eingebetteten Dateien. Sie belegt, dass das PDF seit der Signierung unverändert ist und vom Inhaber des Signaturzertifikats stammt.

- **Selbst ausgestelltes Zertifikat:** Die Signatur ist kryptografisch gültig und für den Aussteller selbst bestätigbar; PDF-Programme zeigen „Unterzeichner unbekannt“. Die Upload-Prüfung von LearnCards erkennt PDFs, die von **dieser Installation** signiert wurden (`signer_trust = installation`).
- **Zertifikat einer anerkannten Zertifizierungsstelle** (z. B. in der Adobe Approved Trust List oder ein qualifiziertes Zertifikat nach eIDAS): PDF-Programme vertrauen der Signatur ohne weitere Schritte. Das beschafft der Betreiber; es ist eine kostenpflichtige Leistung der Zertifizierungsstelle und **nicht** Teil von LearnCards.
- **Nicht umgesetzt:** Zeitstempel (RFC 3161), Langzeitvalidierung (PAdES-LTV/B-LT), sichtbare Signaturdarstellung. Die Bezeichnung „PAdES“ wird daher nicht beansprucht; das Profil entspricht dem Grundniveau CAdES-B (signing-certificate-v2 ist enthalten). **PDF/A-3 wird nicht beansprucht** (die Standardschriften sind nicht eingebettet, es gibt keine XMP-Metadaten und keine Validierung gegen PDF/A).

## Erzeugen eines Zertifikats (Beispiel für Test/Intern)

```powershell
# PowerShell, Windows: selbstsigniertes Zertifikat (nur für interne Tests)
$c = New-SelfSignedCertificate -Subject "CN=Learny PDF Signer, O=Meine Organisation" -KeyAlgorithm RSA -KeyLength 3072 -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)
Export-PfxCertificate -Cert $c -FilePath .\pdf-signing.pfx -Password (Read-Host -AsSecureString)
```

```bash
# OpenSSL
openssl req -x509 -newkey rsa:3072 -nodes -keyout key.pem -out cert.pem -days 1095 -subj "/CN=Learny PDF Signer/O=Meine Organisation" -addext keyUsage=digitalSignature
openssl pkcs12 -export -inkey key.pem -in cert.pem -out pdf-signing.pfx
```

## Betrieb

- **IIS:** Datei außerhalb des Webverzeichnisses ablegen (z. B. `<root>\keys\pdf-signing.pfx`, Leserecht nur für den Anwendungspool), `PDF_SIGNING_CERT_FILE` und `PDF_SIGNING_CERT_PASSWORD` in `<IIS_PHYSICAL_PATH>\.env` ergänzen (das Deployment-Template enthält sie bewusst nicht). „Benutzerprofil laden“ ist nicht nötig (flüchtiger Schlüsselspeicher).
- **Docker:** `PDF_SIGNING_CERT_PFX_BASE64` aus einem Secret oder `PDF_SIGNING_CERT_FILE` auf eine eingebundene Datei.
- **Rotation:** Ein neues Zertifikat gilt für neu erzeugte PDFs; bereits ausgestellte PDFs bleiben unverändert gültig. Die Upload-Prüfung erkennt frühere Unterzeichner dieser Installation anhand der gespeicherten Fingerabdrücke.
- **Ablauf:** Ein abgelaufenes Zertifikat wird beim Start abgelehnt.

## Prüfen

- Im Browser: `/verify` → „Zertifikat-PDF prüfen“ (Upload; getrennte Befunde für PDF-Signatur, Unversehrtheit, CLR, Master-Badge, Modulnachweise, Referenzen/Hashes, Vertrauen, Widerruf, Versionen).
- Per API: `POST /api/public/verify/pdf` (multipart-Feld `file` oder rohe PDF-Bytes, höchstens 25 MB).
- In PDF-Programmen: Signaturbereich bzw. Anhänge (Büroklammer-Ansicht): `clr-credential.json`, `master-badge.json`, `module-credentials.json`, `verification.json`, `public-key.jwk`, `competence-snapshot.json`.
- Unabhängig: Die Signaturen wurden während der Entwicklung mit pyHanko (Python) validiert (intakt, gültig, gesamte Datei abgedeckt).

## Nachtrag (ADR 0037/0038)

PDF-Pakete zu Credentials im neuen JWT-Profil tragen die JWS als `.jwt`-Dateien (die `.json`-Dateien sind das reine Credential, identisch mit dem Download); `PdfVerificationService` liest sie von dort, ältere PDFs weiter aus `proof.jwt`. Test-PDFs tragen „TESTNACHWEIS“ in Titel und Kopfzeile und werden mit der regulären PDF-Signatur signiert.

## Integritätsmanifest und OpenTimestamps (ADR 0039)

Neue PDFs enthalten zusätzlich zu den Credentials und `verification.json`:

| Datei | Inhalt |
|---|---|
| `integrity-manifest.json` | Dateiinventar (Name, Größe, Medientyp, SHA-256 der Originalbytes), Aussteller-Schlüssel (`kid`, JWK-Thumbprint nach RFC 7638), Regeln für Pflichtdateien (`required_files`) und erlaubte Zusatzdatei (`competence-snapshot.json`); UTF-8 ohne BOM, LF, sortiert. Enthält **nicht** sich selbst, die JWS, die OTS-Datei oder Blockchain-Status. |
| `integrity-manifest.jws` | Kompakte ES256-JWS über die **exakten Bytes** des Manifests. Header `alg=ES256`, `typ=learny-integrity-manifest+jws`, `cty=application/json`, `kid=<PUBLIC_BASE_URL>/badges/keys/<id>`; Nutzlast = Base64url der Manifestbytes; Signatur IEEE-P1363 (64 Byte) über `ASCII(b64(header).b64(payload))`. Derselbe Schlüssel wie die Credentials; kein VC-JWT-Profil. |
| `integrity-manifest.jws.ots` | optional: OpenTimestamps-Receipt über **SHA-256 der Datei `integrity-manifest.jws`** (nicht über das PDF, nicht über einen Hex-Text). |

Geschützter Umfang: Themenabschluss `clr-credential.json|.jwt`, `master-badge.json|.jwt`, `module-credentials.json`, `module-competence-snapshots.json`, `public-key.jwk`, `verification.json` (+ `competence-snapshot.json`); Modulzertifikat `module-credential.json|.jwt`, `public-key.jwk`, `verification.json` (+ `competence-snapshot.json`). Jede weitere Datei im PDF gilt als nicht geschützter (versteckter) Anhang und macht die Manifestprüfung ungültig; ebenso fehlende oder nicht lesbare Anhänge. Ältere PDFs ohne Manifest bleiben prüfbar („Manifest: nicht vorhanden“).

**Erzeugungsreihenfolge:** Credentials/Nutzdateien einschließlich `verification.json` → Manifest → Manifest-JWS → Rückprobe (Nutzlast = Manifestbytes, Signatur) → OTS-Stempel (optional) → Einbetten → PDF → CMS/CAdES zuletzt → unveränderliche Ablage. Eine fehlgeschlagene Rückprobe verhindert das PDF; ein OTS-Ausfall nicht (PDF ohne `.ots`, Zustand `none` mit Grund).

### Konfiguration (alle optional, Standard: aus)

| Variable | Bedeutung |
|---|---|
| `OTS_ENABLED` | `true` schaltet das Stempeln ein. **Seit v0.1.22 standardmäßig `true`** in `deploy/iis/web.env` und in den Docker-Compose-Dateien; ohne die Variable (z. B. eigene Starts) bleibt es aus. |
| *(entfällt)* `OTS_CLIENT_PATH` | Seit ADR 0040 gibt es keinen externen Client mehr; die Variable wird ignoriert. |
| `OTS_CALENDARS` | Kommagetrennte Einträge `https://calendar\|Betreiber`, mindestens zwei Calendars **unterschiedlicher, benannter Betreiber**. Keine Standardwerte; der Betreiber der Installation benennt den Betreiber (er wird nicht aus dem Hostnamen geraten). Einträge ohne Betreiber gelten als unbekannt und **nicht** als unabhängig; antworten nur Calendars eines Betreibers, entsteht keine Receipt. Belege: Abschnitt „Betreiberzuordnung“. |
| `OTS_TIMEOUT_SEC` | Zeitlimit je Clientaufruf (Standard 60). |
| `OTS_CONFIRMATIONS` | Bestätigungstiefe für „bestätigt“ (Standard 6). |
| `OTS_BITCOIN_EXPLORER_URL` | Optional: Esplora-Explorer (z. B. `https://mempool.space`) als Header-Quelle, wenn kein eigener Knoten vorhanden ist. Header-Echtheit (Doppel-SHA-256, Proof-of-Work) wird selbst geprüft; Kettenzugehörigkeit, Höhe und Tiefe sind eine **Zusatzannahme** über den Explorer-Betreiber und werden so ausgewiesen. |
| `OTS_BITCOIN_RPC_URL`, `OTS_BITCOIN_RPC_USER`, `OTS_BITCOIN_RPC_PASSWORD` | Eigener Bitcoin-Knoten für die unabhängige Header-Prüfung. Ohne Knoten bleibt jede Bitcoin-Behauptung „nicht überprüfbar“. |

Übertragen wird nur der 32-Byte-SHA-256-Digest der Manifest-JWS an die Calendars; keine Credential-Inhalte, Namen, JWTs, PDFs oder Schlüssel. Der Client wird ohne Shell, in leerem Temp-Verzeichnis, mit bereinigter Umgebung, Zeitlimit und begrenzter Ausgabe aufgerufen. Betriebsempfehlung: unprivilegiertes Dienstkonto.

### Zustände

`none` (kein Nachweis erzeugt) · `pending` (Receipt gebunden, nur Calendar-Attestierung) · `anchored` (Bitcoin-Header unabhängig geprüft, Tiefe < Schwelle) · `confirmed` (Tiefe ≥ Schwelle) · `failed` (nachweislich ungültig: falsche Bindung, ungültige Operationen, abweichende Merkle-Root) · `unverifiable` (keine Datenquelle/Netzausfall). Bei einer Reorganisation fällt ein Nachweis auf `pending` zurück. Netzwerkausfälle sind nie kryptografische Ungültigkeit.

### Proof-Upgrade

`POST /api/admin/integrity/maintain` (Administrator) bzw. `OtsMaintenanceService.RunOnceAsync` lädt die jüngsten Proof-Versionen mit Receipt, führt `ots upgrade` auf einer Kopie aus, prüft den Bitcoin-Nachweis und legt bei Änderung eine **neue Proof-Version** in `integrity_proofs` an. Ursprüngliche Receipt und gespeichertes PDF bleiben unverändert. Öffentlicher Status ohne Personendaten: `GET /api/public/certificates/{id}/integrity`. *Bewusst nicht umgesetzt:* automatisches Einbetten eines aktualisierten Proofs in ein neues PDF-Artefakt; stattdessen ist die aktuelle Receipt separat abrufbar (siehe unten), das gespeicherte PDF bleibt unverändert.

### Grenzen

Der Zeitnachweis bestätigt die Existenz der gebundenen Daten spätestens zum Bitcoin-Blockzeitpunkt. Er bestätigt **nicht** die Identität des Ausstellers, die Richtigkeit des Lernnachweises, die Gültigkeit der Credential-Signatur, den aktuellen Widerrufsstatus oder eine 1EdTech-Zertifizierung. Ein eingebettetes `public-key.jwk` beweist die Identität nicht: Vertrauensstufen sind *mathematisch gültig* → *Aussteller bestätigt* (Schlüssel dieser Installation / live veröffentlichtes JWKS) → *unabhängig verankert* (vom Prüfer gesicherter Thumbprint, Learny-PKI-Kette des PDF-Signaturzertifikats). Bei Schlüsselrotation müssen alte öffentliche Schlüssel mit Thumbprint im JWKS bzw. unter `/badges/keys/<id>` verfügbar bleiben.

## Langzeitverifikation ohne Learny-Backend

1. **PDF-Signatur und Zertifikatskette** (nicht im Skript): `pdfsig zertifikat.pdf` (poppler), Adobe Acrobat oder die Learny-Prüfseite; ByteRange muss die ganze Datei abdecken (nachträgliche Incremental Updates machen die Signatur „verändert“). Die Kette gegen den archivierten Learny-Root prüfen (`docs/pki.md`); „öffentlich vertrauenswürdig“ ist immer nein.
2. **Anhänge extrahieren:** `pdfdetach -saveall -o anhaenge/ zertifikat.pdf` (oder Acrobat „Anlagen speichern“).
3. **Paket prüfen:** `python tools/integrity/verify_package.py anhaenge/ --jwks-url https://<aussteller>/.well-known/jwks.json` (oder `--trusted-thumbprint <archivierter RFC-7638-Thumbprint>`). Prüft getrennt: Manifest-JWS (eigene P-256-Arithmetik, nur Standardbibliothek), Nutzlast = Manifestbytes, Thumbprint, Dateihashes, Pflichtdateien, zusätzliche Anhänge, jedes Credential-JWT und die eingebetteten JWS des CLR einzeln, OTS-Bindung.
4. **OTS/Bitcoin:** `ots info anhaenge/integrity-manifest.jws.ots`; `ots verify -f anhaenge/integrity-manifest.jws anhaenge/integrity-manifest.jws.ots` gegen einen **eigenen Bitcoin-Knoten**, oder das Skript mit `--bitcoin-rpc http://knoten:8332 --rpc-user … --rpc-pass …`. Öffentliche Blockexplorer sind eine zusätzliche Vertrauensannahme (Betreiber des Explorers); ohne Knoten oder gesicherte Blockdaten gilt die Verankerung als *nicht überprüfbar*. `ots upgrade` auf einer Kopie holt einen ausstehenden Nachweis nach.
5. **Widerruf getrennt:** `GET <Aussteller>/api/public/revocation-list` (Feld `revokedCredential`) gegen die Credential-IDs; „nicht erreichbar“ heißt nicht überprüfbar, nicht gültig.
6. **Gesamtbild:** Dokumentintegrität, PDF-Signatur, Signaturzertifikat, Manifest, Manifest-JWS, Credential-JWT, Schemakonformität (1EdTech-Validator, siehe `credential-validation.md`), Ausstellervertrauen, Widerruf, OTS-Receipt, Bitcoin-Verankerung und Bestätigungstiefe sind **getrennte** Aussagen; ein gültiger Teil überdeckt keinen fehlgeschlagenen anderen.

Praktisch erprobt: `verify_package.py` gegen die Anhänge eines im Integrationstest erzeugten Themenabschluss-PDFs (alle Prüfungen gültig; manipulierte Datei und zusätzlicher Anhang → Exit-Code 1). Die Bitcoin-Schritte wurden mangels Knoten nicht gegen echte Blöcke ausgeführt.

## Reale OpenTimestamps-Ausführung (2026-10-29)

> Historisches Prüfprotokoll mit dem damaligen CLI-Client unter WSL (v0.7.2). Seit ADR 0040 stempelt die Anwendung **nativ**; der offizielle Client dient nur noch als Gegenprobe auf Entwicklungsrechnern.

Offizieller Client `opentimestamps-client` **v0.7.2** (`ots --version`), in einer isolierten virtuellen Umgebung unter WSL/Debian installiert (unter Windows lädt `python-bitcoinlib` OpenSSL nicht). Die Anwendung ruft ihn über `OTS_CLIENT_PATH` auf (Wrapper `wsl.exe … ots %*`; `CliOtsClient` übergibt Dateien relativ zum leeren Arbeitsverzeichnis). Calendars: `alice.btc.calendar.opentimestamps.org`, `finney.calendar.eternitywall.com`, `btc.calendar.catallaxy.com` (alice: Peter Todd, finney: Eternity Wall, catallaxy: Catallaxy — drei Betreiber).

| Ergebnis | Befund |
|---|---|
| Stempeln | `ots stamp -c … -c … -c … -m 2 integrity-manifest.jws` → echte `.ots` (739 Byte, drei Calendar-Attestierungen `PendingAttestation`); über die Anwendung erzeugtes PDF mit eingebettetem `integrity-manifest.jws.ots` (Proof-Version 1, `calendar_status=submitted`, `anchor_status=pending`, `ots_client_version=v0.7.2`) |
| Bindung | Digest in der Receipt = SHA-256 der exakten Bytes von `integrity-manifest.jws` (z. B. `f31b16ae…08b1` bzw. `19b2c60c…6298`), unabhängig durch `ots info`, `verify_package.py` und den C#-Leser bestätigt |
| `ots upgrade` | Antwort aller drei Calendars „Pending confirmation in Bitcoin blockchain“, Datei unverändert → Zustand **pending**; der Wartungslauf kann später wiederholt werden, ein Bitcoin-Nachweis wird **nicht** behauptet |
| Calendar-Ausfall | `ots stamp` gegen unerreichbare Calendars: „need at least 2 attestations but received 0 within timeout“, keine `.ots`; die Anwendung bettet dann nichts ein (`calendar_status=none`, Grund gespeichert) — real beobachtet, auch durch einen fehlenden Client |
| `ots verify` | benötigt einen lokalen Bitcoin-Core-Knoten; keiner vorhanden → nicht ausgeführt |

**Bitcoin-Verifikation (A: neuer Learny-Teststempel, B: bekannter bestätigter Nachweis):**
- **A** ist *pending*; er wird erst nach der Bitcoin-Bestätigung (typisch Stunden) und einem späteren `ots upgrade` als verankert/bestätigt bewertet. **Kein** bestätigter Status behauptet.
- **B** (offizielle Beispieldatei `examples/hello-world.txt` + `.ots` des opentimestamps-client): `verify_package.py ots …` rechnet den Operationsbaum nach (SHA-256, RIPEMD-160, Append/Prepend), findet die Bitcoin-Attestierung für **Block 358391**, holt den Header über die Esplora-API von mempool.space, prüft Doppel-SHA-256 = Blockhash `000000000000000003e892881a8cdcdc117c06d444057c98b6f04a9ee75a2319` und das Proof-of-Work-Ziel, vergleicht die Merkle-Root: **gültig**; Blockzeit 2015-05-28T15:41:18 UTC; Bestätigungstiefe 615 341 (ausreichend).
- **Vertrauensgrundlage von B:** Header-Echtheit (PoW) selbst geprüft; Zugehörigkeit zur besten Kette, Höhe und Tiefe stammen vom Explorer (zusätzliche Vertrauensannahme) — **kein** eigener Bitcoin-Knoten. Für eine Prüfung ohne Explorer: `ots verify` bzw. `--bitcoin-rpc` gegen einen eigenen Knoten.

Nicht erledigt: Verifikation von A gegen die Blockchain (noch pending), Prüfung mit eigenem Bitcoin-Knoten, `ots verify`.


## Nativer OpenTimestamps-Client (ADR 0040)

Learny stempelt, aktualisiert und liest `.ots`-Dateien selbst (`NativeOtsClient`, `OtsFile`); es wird **kein** Prozess gestartet — kein `ots`, kein Python, kein WSL, kein PATH, kein Benutzerprofil. Der IIS-Anwendungspool braucht nur ausgehendes HTTPS zu den Calendars (und optional zu Explorer/RPC).

- **Protokoll:** `POST {calendar}/digest` (Commitment als Rohbytes), `GET {calendar}/timestamp/{hex}` (404 = noch nicht bestätigt), `Accept: application/vnd.opentimestamps.v1`, Antworten ≤ 10 000 Byte; Datei: DetachedTimestampFile **Version 1**; Operationen SHA-1, RIPEMD-160, SHA-256, Append, Prepend, Reverse, Hexlify; Attestierungen Bitcoin und Pending. Keccak-256 und fremde Attestierungen: „nicht überprüfbar“.
- **Stempeln:** `sha256(manifest-jws-bytes) → append(16 Byte Zufall) → sha256 → Commitment`; mindestens zwei Calendars **unterschiedlicher, benannter Betreiber** müssen antworten (sonst Fehler, kein PDF-Anhang; siehe „Betreiberzuordnung“). Allowlist = `OTS_CALENDARS`; https; keine Redirects; keine privaten Zieladressen; 4 parallele Anfragen; Zeitlimit.
- **Upgrade:** `POST /api/admin/integrity/maintain` bzw. der Wartungslauf ruft für Pending-Blätter **konfigurierter** Calendars die aktualisierten Zeitstempel ab und vereinigt sie; „noch ausstehend“ ist kein Fehler; Pending wird nie automatisch bestätigt.
- **Bitcoin-Aussagen getrennt:** (A) an Datei gebunden · (B) Calendar-Einreichung erfolgreich · (C) Bitcoin-Attestierung vorhanden · (D) Block-Header kryptografisch korrekt · (E) Block gehört zur akzeptierten Kette · (F) Bestätigungstiefe. Eigener Knoten (RPC) deckt D–F selbst ab; mit Explorer sind E und F Zusatzannahme; ohne Quelle „nicht überprüfbar“.
- **Interoperabilität (2026-10-29, Entwicklungsrechner):** nativ erzeugte `.ots` (drei echte Calendars alice/finney/catallaxy) wurden vom offiziellen `ots` 0.7.2 gelesen (`ots info`: identischer Datei-Hash und Baum) und von allen drei Calendars für die nativen Commitments als „Pending confirmation“ erkannt (`ots upgrade`); eine vom offiziellen Client erzeugte Receipt wird nativ gelesen und bytegleich zurückgeschrieben; der offizielle bestätigte Beispielbeweis (Block 358391) wird nativ gelesen und mit dem Explorer-Modus gegen echte Bitcoin-Daten als bestätigt geprüft. *Noch nicht gezeigt:* ein vom nativen Client aktualisierter Beweis mit Bitcoin-Attestierung, der vom offiziellen Client gelesen wird (der neue Stempel ist noch ausstehend; die Zusammenführung ist durch Calendar-Doubles getestet).


### Betreiberzuordnung der Calendars (ADR 0040)

Unabhängigkeit wird über **benannte Betreiber** entschieden, nicht über Hostnamen. Belegt (Stand 2026-10-29) ist, was die offiziellen Quellen hergeben:

| Calendar | Zuordnung (`OTS_CALENDARS`) | Beleg |
|---|---|---|
| `alice.btc.calendar.opentimestamps.org` (ebenso `bob…`) | `OpenTimestamps` | Aufgeführt als Standard-Calendar auf https://opentimestamps.org/#calendars; Domain des Projekts |
| `finney.calendar.eternitywall.com` | `Eternity Wall` | Aufgeführt auf derselben Seite unter „Eternity wall“; Standardliste des Clients (`otsclient/args.py`: `a.pool.eternitywall.com`); Domain `eternitywall.com` |
| `btc.calendar.catallaxy.com` (die Projektseite nennt `ots.btc.catallaxy.com`) | `Catallaxy` | Aufgeführt als „Catallaxy“; Standardliste des Clients (`ots.btc.catallaxy.com`); Domain `catallaxy.com` |

Grenzen dieses Belegs: Die Quellen dokumentieren die Auflistung und die Domains, **nicht** die Rechtsträger oder gemeinsame Infrastruktur (z. B. ein gemeinsamer Bitcoin-Zugang). Die Zuordnung ist daher eine ausdrückliche Betreiberentscheidung; zwei benannte Betreiber verringern das Risiko eines einzelnen Calendar-Ausfalls oder -Betrugs, beweisen aber keine vollständige Unabhängigkeit. `alice` und `bob` zählen als **ein** Betreiber. Die drei Calendars zeigen auf ihren Statusseiten dieselbe Bitcoin-Blockhöhe (973738 am 2026-10-29), laufen aber unter unterschiedlichen Domains und Software-Ständen (v0.7.1 bzw. v0.6.0).

### Windows-/IIS-Betrieb ohne CLI (nachgewiesen)

Test 2026-10-29 unter Windows: Anwendung mit **ausschließlich** `C:\Program Files\dotnet` im `PATH` (kein System32, kein WSL, kein Python), leerer Umgebung, `OTS_CLIENT_PATH` auf eine nicht vorhandene Datei gesetzt (wird ignoriert), drei reale Calendars → PDF mit nativ erzeugter Receipt, PDF-Signatur gültig, Datei unverändert, Manifest gültig, Zeitnachweis „ausstehend“ und gebunden; Wartungslauf ohne Fehler; die Anwendung hatte danach keine Kindprozesse; im Quellcode gibt es keine Prozessaufrufe für OTS. Einschränkung: Kindprozesse wurden nach dem Lauf stichprobenartig geprüft, nicht während der Stempelung überwacht, und der Test lief auf einem Entwicklungsrechner mit installiertem WSL, nur für den Testprozess unerreichbar gemacht — nicht auf einem WSL-freien Server.

### Receipts, Upgrade und PDFs

- Das gespeicherte PDF enthält die **ursprüngliche** (meist ausstehende) Receipt und wird nie geändert. Aktualisierte Receipts entstehen als neue Proof-Version in `integrity_proofs` und sind separat abrufbar: `GET /api/public/certificates/{id}/integrity.ots` (aktuelle) bzw. `?version=N`. Sie gehört zur im PDF eingebetteten Manifest-JWS (gleicher Digest); die Prüfung verbindet die aktuelle Receipt mit dieser JWS. Ein neues PDF-Artefakt (neu CMS-signiert) wird **nicht** automatisch erzeugt — dafür wäre eine ausdrückliche Architekturentscheidung nötig.
- Calendars antworten für ein bereits gestempeltes, noch unbestätigtes Commitment mit HTTP 404 und einer Meldung wie „Pending confirmation in Bitcoin blockchain“ bzw. „Timestamped by transaction …“ (Transaktion gesendet, noch nicht in einem Block): beides bleibt **pending**. Erst eine Antwort 200 mit Bitcoin-Attestierung ändert die Receipt.
- Größenlimit: reale Calendar-Antworten sind 185–207 Byte, komplette Receipts 0,6–1 KB; das Limit von 10 000 Byte (wie der Referenzclient) wird beibehalten, größere Antworten werden abgelehnt.

### Bitcoin-Vertrauensmodelle und Detailstatus

| Quelle | Header selbst geprüft (Doppel-SHA-256, PoW) | Kettenzugehörigkeit, Höhe, Tiefe | Bezeichnung |
|---|---|---|---|
| keine | – | – | `not_verifiable` / `bitcoin_attestation_present` |
| Explorer (`OTS_BITCOIN_EXPLORER_URL`) | ja | **Zusatzannahme**: Betreiber des Explorers | `explorer_verified`, nicht „unabhängig“ |
| eigener Bitcoin-Core (`OTS_BITCOIN_RPC_URL`, optional extern erreichbar, kein Knoten auf dem IIS-Server nötig) | über den Knoten | eigene Hand | `header_verified` bzw. `independently_confirmed` (Tiefe ≥ `OTS_CONFIRMATIONS`) |

Öffentlicher Statusvertrag unverändert: `none | pending | anchored | confirmed | failed | unverifiable`; zusätzlich liefert die Prüfung intern `detail` (`receipt_valid`, `pending`, `bitcoin_attestation_present`, `header_verified`, `explorer_verified`, `independently_confirmed`, `not_verifiable`, `failed`) und `independent`. Der RPC-Pfad ist gegen einen RPC-Double getestet, **nicht** gegen einen echten Knoten.

## Live-Verifikation auf `/verify/{id}`

Beim Öffnen eines Zertifikats zeigt die Seite unter den Zertifikatsangaben den Bereich „Verifikation des digitalen Nachweises“ (Komponente `LiveVerification`). Er stellt den **tatsächlichen** Ablauf eines serverseitigen Prüfauftrags dar (`LiveVerificationService`); die Prüfung selbst nutzt die vorhandenen Dienste (`CertificateService.VerifyAsync`, `IntegrityManifest.Verify`, `OtsMaintenanceService`, `OtsEvaluator`) — es gibt keine zweite Verifikationslogik.

**Schritte** (in dieser Reihenfolge): Zertifikat geladen · Credential-Signaturen geprüft · Ausstellerschlüssel überprüft · Integritätsmanifest und Manifest-JWS geprüft (aus dem gespeicherten PDF; nichts wird neu erzeugt) · OpenTimestamps-Nachweis eingelesen · Bindung an die Manifest-JWS geprüft · Calendar-Server abgefragt · Bitcoin-Attestierung überprüft · Bestätigungstiefe kontrolliert · Prüfergebnis abgeschlossen. Nicht zutreffende Schritte (kein PDF, älteres Paket ohne Manifest, kein Zeitnachweis, bereits bestätigt, OTS nicht aktiv, Abfrage gedrosselt) erscheinen als **übersprungen**, nie als ausgeführt.

**Schrittzustände und Darstellung:** wartet (neutraler Kreis) · wird geprüft (drehendes Symbol, nur dieser eine Schritt) · erfolgreich (✓) · fehlgeschlagen (✕) · ausstehend (◷, z. B. Bitcoin-Bestätigung) · nicht überprüfbar (⚠) · übersprungen (–). Jeder Zustand hat zusätzlich einen Text (nicht nur Farbe/Symbol); keine Prozentangaben, keine künstlichen Wartezeiten, keine Wiederholung nach Abschluss. Bei `prefers-reduced-motion: reduce` entfällt die Drehung, die Statuswechsel bleiben sichtbar. Ein Screenreader-Status (`role="status"`, `aria-live="polite"`) meldet die Phase.

**Phasen des Auftrags:** `queued → loading → verifying_signature → verifying_manifest → verifying_ots → contacting_calendars → verifying_bitcoin → completed`; Ergebnis: `valid` · `pending` · `unavailable` · `failed` · `notfound`. Ein fehlgeschlagener Schritt wird nie durch erfolgreiche andere überdeckt.

**Pending:** Ist die Receipt gebunden und gültig, aber nicht auf Bitcoin bestätigt, endet die Animation mit „Bitcoin-Bestätigung ausstehend“ und dem Hinweis: „Der kryptografische Nachweis ist vorhanden. Die Verankerung in der Bitcoin-Blockchain steht noch aus. Learny prüft den Status bei späteren Wartungsläufen erneut.“ Pending ist ein Endzustand der Anzeige, kein laufender Vorgang.

**Bestätigt:** zeigt nur Nachgewiesenes — Blockhöhe, Blockzeit, Bestätigungen (mit der erforderlichen Zahl), Proof-Version, Zeitpunkt der letzten Verifikation und das **Vertrauensmodell** (Explorer-basiert wird ausdrücklich als Zusatzannahme gekennzeichnet; „unabhängig“ nur beim eigenen Bitcoin-Knoten).

**Nicht überprüfbar:** Bei Calendar-Ausfall oder fehlender Bitcoin-Datenquelle steht „Die aktuelle Blockchain-Prüfung konnte nicht abgeschlossen werden.“ Der zuletzt **gespeicherte** Stand (Proof-Version, Status, letzte Bestätigung) wird getrennt als „nicht Teil der aktuellen Prüfung“ angezeigt.

**Mehrere Besucher, keine redundanten Abfragen:** Es gibt je Zertifikat höchstens einen laufenden Auftrag; weitere Besucher beobachten ihn. Abgeschlossene Aufträge werden 60 s wiederverwendet. Calendar-Abfragen sind zusätzlich über `last_checked_at` auf höchstens eine alle 5 Minuten gedrosselt (bereits bestätigte Nachweise werden gar nicht abgefragt). Der Auftrag gehört dem Dienst: Verlässt ein Besucher die Seite, endet nur die Beobachtung (Abmeldung vom Ereignis), nicht die Prüfung. Aktualisierung über die bestehende Blazor-Server-Verbindung (Ereignis `Changed` → `StateHasChanged`), kein Polling.

**Downloads:** `GET /api/public/certificates/{id}/integrity.ots` (aktuelle Proof-Version, `?version=N` für ältere) und `GET /api/public/certificates/{id}/integrity-manifest.jws` liefern nur bereits gespeicherte Artefakte; ein Download löst weder Stempelung noch Prüfung aus.
