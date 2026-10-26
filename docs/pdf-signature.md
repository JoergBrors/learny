# PDF-Signatur der Zertifikate

Zertifikats-PDFs (Modul- und Themenabschluss, [ADR 0033](adr/0033-signierte-zertifikats-pdfs.md)) werden digital signiert. Dafür braucht die Installation ein **eigenes X.509-Signaturzertifikat** in der `.env` — es ist **unabhängig** vom Badge-Schlüssel (ES256, ADR 0021), mit dem die Open Badges, das CLR und das Master-Badge signiert werden.

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
