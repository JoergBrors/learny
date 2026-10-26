# 0033 — Signierte Zertifikats-PDFs mit eingebetteten Credentials

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ergänzt:** [0021](0021-zertifikate-und-open-badges.md), [0031](0031-kompetenzmodell-und-pruefungssnapshots.md), [0032](0032-themenabschluss-clr-und-master-badge.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.md) (Schlüsselablage), [0026](0026-betriebsmodus-fail-closed.md) (fail-closed)

## Kontext

Das bisherige „PDF“ war eine druckbare HTML-Seite ohne kryptografischen Wert. Zertifikate sollen als PDF menschenlesbar sein, die signierten Credentials (Open Badges, CLR) maschinenlesbar mitführen und selbst signiert sein — ohne Fremdpakete (ADR 0002), ohne kostenpflichtige Dienste.

## Entscheidung

1. **Ein Konzept für Module und Themen.** `CertificatePdfService` erzeugt das PDF ausschließlich aus den unveränderlichen Credentials und Snapshots (keine abweichende Wahrheitsquelle). Modul-PDF: Siegel, Lernende/r, Modul und Version, Nummer, Ausstellungs- und Abschlussdatum, Ergebnis, **nachgewiesene** Kompetenzen (nicht oder nicht ausreichend geprüfte getrennt und eindeutig gekennzeichnet), Bewertungskriterien, Nachweisreferenzen, QR-Code, Verifikationsadresse. Themen-PDF (mehrseitig): Gesamtabschluss, Modulübersicht, Kompetenznachweise je Modul, Themenabschlussprüfung, Verifikation (IDs, Algorithmen, Hinweise). Automatischer Umbruch mit wiederholten Tabellenköpfen; nichts wird abgeschnitten.
2. **Eigene PDF-/QR-Erzeugung** ohne Bibliothek (`Services/Pdf/`): PDF 1.7 mit Standardschriften (WinAnsi), Vektorgrafik, Flate-Kompression, Link-Annotationen, QR-Encoder (ISO 18004, Byte-Modus; gegen einen unabhängigen Decoder geprüft).
3. **Eingebettete Dateien** (`/EmbeddedFiles` und `/AF`): `clr-credential.json` (+ `.jwt`), `master-badge.json` (+ `.jwt`), `module-credentials.json` (identisch mit den im CLR eingebetteten Credentials), `competence-snapshot.json` (byte-genau der gespeicherte Snapshot), `module-competence-snapshots.json`, `verification.json`, `public-key.jwk` (Modul-PDF: `module-credential.json`/`.jwt`). Das CLR liegt vollständig und unverändert im PDF, nicht nur als Link. `verification.json` nennt Kennungen, Algorithmus (ES256), Schlüssel-ID, Standards, Proof-Format, Verifikations-/JWKS-Adressen und SHA-256-Prüfsummen aller übrigen Anhänge und der referenzierten Credentials; es enthält keine Geheimnisse.
4. **PDF-Signatur.** CMS/CAdES (`ETSI.CAdES.detached`, SHA-256, signed attributes contentType/messageDigest/signingTime/signing-certificate-v2; RSA oder ECDSA), gebaut mit `System.Formats.Asn1` und den .NET-Kryptoklassen. Signiert wird zuletzt, nachdem Inhalte, Anhänge und Metadaten fertig sind; der ByteRange deckt die gesamte Datei außer dem Signaturwert ab (keine ungeschützten Nachträge). **Eigenes Signaturzertifikat** aus der `.env` (`PDF_SIGNING_*`), unabhängig vom Badge-Schlüssel. Ohne Zertifikat: keine PDFs (503, fail-closed); in Development ein selbstsigniertes Entwicklungszertifikat. Keine Zeitstempel/LTV, sichtbare Signatur nicht umgesetzt; die Bezeichnung PAdES und **PDF/A-3** werden nicht beansprucht (siehe `docs/pdf-signature.md`).
5. **Ablage.** Das erzeugte PDF wird einmal in `certificate_pdfs` gespeichert (Base64, datenbankunabhängig) und danach unverändert ausgeliefert (`GET /api/certificates/{id}/certificate.pdf`, nur der Besitzer). Bereits ausgestellte Zertifikate/Badges bleiben unverändert.
6. **Verifikation** (`PdfVerificationService`, `POST /api/public/verify/pdf`, UI `/verify`): getrennte Befunde für (1) PDF-Signatur, (2) Datei seit Signierung unverändert (ByteRange + Digest, angehängte Daten erkannt), (3) CLR, (4) Master-Badge, (5) Modul-Credentials, (6) Referenzen/Hashes/Anhänge, (7) Aussteller- und Unterzeichner-Vertrauen (`installation` | `trusted_ca` | `untrusted`), (8) Widerruf (online), (9) Versionen. Eine gültige CLR-Signatur legitimiert keine veränderte Datei, eine gültige PDF-Signatur kein ungültiges CLR. Offline lassen sich Signaturen mit dem mitgelieferten Schlüssel prüfen (`BadgeSigner.VerifyWithJwk`; die Echtheit des Schlüssels bestätigt nur der Aussteller). Der Upload ist auf 25 MB und Anfragen je Minute begrenzt und wird nie gespeichert.
7. **Prüfsiegel.** „Learny Verified – Digitally Signed Credential“ als Vektor im PDF; es bestätigt die Learny-Prüfung, keine Zertifizierung durch 1EdTech oder LinkedIn.

## Konsequenzen

- Vertrauenswürdig für Dritte ist die PDF-Signatur nur mit Zertifikat einer anerkannten Zertifizierungsstelle (Beschaffung durch den Betreiber).
- Eigene PDF-Erzeugung deckt nur das nötige Teilset ab (Standardschriften, keine Fremdschrift-Einbettung); fremde oder inkrementell geänderte PDFs werden von der Inspektion nicht bestätigt.
- Zeitstempel und Langzeitvalidierung fehlen; die Signatur ist nur so lange belastbar wie das Zertifikat gültig ist bzw. der Schlüssel nicht kompromittiert wurde.
- Die HTML-Druckansicht (`certificate.html`) bleibt für ältere Zertifikate und ohne Signaturzertifikat erhalten.

## Leitplanken für Änderungen

- PDF nur aus den gespeicherten Credentials/Snapshots ableiten; nie eigene Ergebnisse berechnen.
- Erst alle Inhalte, Anhänge und Metadaten, dann die Signatur; kein Schreiben nach der Signierung.
- Keine privaten Schlüssel, Passwörter oder Secrets im PDF, in Anhängen, Logs oder Fehlermeldungen.
- Keine neuen NuGet-Pakete; PDF/A, PAdES-LTV oder 1EdTech-Konformität nur bei tatsächlicher Validierung behaupten.
- Tests: `PdfTests.cs`, `TopicCredentialTests.cs`.

## Betroffener Code

- `Services/Pdf/*` (`PdfBuilder`, `PdfFlow`, `PdfCms`, `PdfInspector`, `QrCode`), `Services/Certificates/CertificatePdfService.cs`, `PdfVerificationService.cs`
- `Infrastructure/PdfSigningSettings.cs` (`PdfSigningProvider`), `Api/CertificateEndpoints.cs`, `Components/Pages/Verify.razor`, `Profile.razor`
