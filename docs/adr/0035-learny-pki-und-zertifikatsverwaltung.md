# 0035 — Learny-PKI und Zertifikatsverwaltung

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ergänzt:** [0033](0033-signierte-zertifikats-pdfs.md), [0026](0026-betriebsmodus-fail-closed.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.md)

> **Architekturaussage.** Learny betreibt optional eine eigene, standardkonforme X.509-PKI. Sie erzeugt eine Root CA, eine Issuing CA und PDF-Signaturzertifikate, führt Zertifikats- und Widerrufsregister und veröffentlicht Sperrlisten (CRLs) nach RFC 5280. Die PKI dient der technischen Prüfbarkeit der von Learny ausgestellten Nachweise; sie begründet keine automatische öffentliche Anerkennung durch Adobe, Betriebssysteme oder Vertrauenslisten.

## Kontext

ADR 0033 verlangt ein eigenes PDF-Signaturzertifikat aus der `.env`. Ohne Zertifikat gibt es keine PDFs; in Development entsteht ein selbstsigniertes Zertifikat ohne Kette, ohne Widerruf und ohne Register. Betreiber ohne eigene Zertifizierungsstelle sollen eine nachvollziehbare, prüfbare Kette erhalten — mit kontrollierter Ersteinrichtung, Zertifikatsregister, Widerruf und Sperrliste, auf Windows/IIS und Linux/Docker gleich.

## Entscheidung

### Standards

RFC 5280 (X.509 v3-Zertifikate, v2-CRL, Pfadvalidierung), RFC 9336 (`id-kp-documentSigning`, OID 1.3.6.1.5.5.7.3.36), RFC 8017 und RFC 4055 (RSASSA-PKCS1-v1_5 mit SHA-2), NIST SP 800-57/800-131A (Schlüssellängen, SHA-2), ETSI EN 319 142 (CAdES/PAdES-Rahmen der bestehenden CMS-Struktur, ADR 0033). OCSP (RFC 6960, aktualisiert durch RFC 8954 und RFC 9654) wird **nicht** umgesetzt: Es gibt keinen Responder und keine OCSP-URL in den Zertifikaten; **die CRL ist der verbindliche Widerrufsmechanismus**. Kein proprietäres Zertifikats- oder Widerrufsformat.

### CA-Hierarchie

| Ebene | Profil |
|---|---|
| Root CA | selbstsigniert; `basicConstraints` CA:TRUE, pathLen 1 (kritisch); `keyUsage` keyCertSign + cRLSign (kritisch); SKI; AKI; RSA-4096/SHA-256 (oder ECDSA P-384/SHA-384); Standardgültigkeit 20 Jahre |
| Issuing CA | von der Root signiert; CA:TRUE, pathLen 0 (kritisch); keyCertSign + cRLSign (kritisch); SKI, AKI; CRL-Verteilungspunkt (Root-CRL); AIA `caIssuers`; RSA-3072 (oder P-384); 10 Jahre |
| Dokumentsignatur | von der Issuing CA signiert; CA:FALSE (kritisch); `keyUsage` digitalSignature + contentCommitment (kritisch); EKU **nur** `id-kp-documentSigning`; SKI, AKI; CRL-Verteilungspunkt (Issuing-CRL); AIA `caIssuers`; RSA-3072 (oder P-256); 730 Tage |

Seriennummern sind zufällig (128 Bit, positiv) und je CA eindeutig (Unique-Index); sie werden nie wiederverwendet. Es gibt keine Certificate Policies und keine erfundenen OIDs. Die CRL-URLs sind `{Basis-URL}/pki/crl/{ca-id}.crl`; die Basis-URL steht unveränderlich in den ausgestellten Zertifikaten.

### Ersteinrichtung

Die PKI wird **genau einmal** eingerichtet, nie durch einen späteren Neustart:

- **Erststart:** Ist `PKI_AUTO_INIT` nicht `false` und gibt es weder PKI-Zustand noch Zertifikate noch Schlüsseldateien, richtet der erste Start sie automatisch ein — sofern eine öffentliche Basis-URL feststeht (`PKI_PUBLIC_URL`, sonst `PUBLIC_BASE_URL`; in Development die lokale Adresse). Ohne Basis-URL wird **nichts** eingerichtet (kein Raten aus Request-Headern: die URL steht dauerhaft in den Zertifikaten).
- **Administration → PKI → „PKI initialisieren“** (nur Administrator, mit Bestätigung) und `dotnet LearnCards.Web.dll --pki-init [--root-out <Datei>]` (Einmal-Einrichtung für Installationsskripte; `--root-out` schreibt den verschlüsselten Root-Schlüssel an einen Zielort und entfernt ihn vom Server).
- Existiert bereits ein Zustand, ein Zertifikat oder eine Schlüsseldatei, wird **nie** eine neue PKI erzeugt (Fehler statt stiller Neuerzeugung). Fehlen Schlüsseldateien bei vorhandenem Register, ist der Zustand `key_unavailable`, die Signierung gesperrt und die Root wird **nie** neu erzeugt. Scheitert die Einrichtung, wird alles zurückgebaut, was sie selbst angelegt hat; ein abgebrochener Zustand (`initializing`, älter als 10 Minuten) ist nur über „Zurücksetzen“ (Bestätigung `RESET`) entfernbar.
- Zustände: `not_configured`, `initialization_required`, `initializing`, `initialized`, `operational`, `key_unavailable`, `maintenance_required`, `locked`.

**Abweichung vom Zielbild „Root-Schlüssel nie im Webprozess“ (bewusste Übergangslösung).** Die automatische Ersteinrichtung legt den Root-Schlüssel verschlüsselt (PKCS#12, AES-256/PBKDF2) in `root-offline/` ab, damit der Betreiber ihn sichern kann. Der laufende Webprozess **lädt ihn nie von allein**: Er wird nur für ausdrückliche Root-Aktionen gebraucht (Widerruf der Issuing CA, Root-CRL) und kann in Administration → PKI (Bestätigung `ROOT-SCHLUESSEL ENTFERNEN`) bzw. mit `--root-out` vom Server entfernt werden; danach verlangt jede Root-Aktion den Upload der offline gesicherten Datei samt Passphrase. Die Verwaltung warnt, solange der Root-Schlüssel auf dem Server liegt. Das Restrisiko steht unter „Grenzen“.

### Schlüsselverwaltung

- Private Schlüssel liegen **nie** in der Datenbank, im Webroot, im Image, in Git oder in Logs — nur als AES-256-verschlüsselte PKCS#12-Dateien (PBES2, PBKDF2-HMAC-SHA-256, 400 000 Iterationen) in `PKI_KEY_DIR` (Standard `data/pki`; im Container ein persistentes Volume). Dateien werden atomar geschrieben und nie überschrieben.
- Passwort: `PKI_KEY_PASSWORD` (≥ 16 Zeichen, Secret-Verwaltung/`.env`). Fehlt es beim Erststart, wird ein zufälliges erzeugt und mit restriktiven Rechten neben den Schlüsseln abgelegt (analog Badge-Schlüssel, ADR 0023); die Verwaltung warnt, bis es in die Secret-Verwaltung übernommen ist. Der Webprozess braucht das Passwort für Issuing- und Signaturschlüssel.
- Dateirechte: Linux `0600`/`0700`; Windows ACL mit Vererbung deaktiviert, nur Dienstkonto, SYSTEM und Administratoren. Auf gemounteten Volumes, deren Rechte sich nicht setzen lassen, ist der Betreiber verantwortlich (`docs/pki.md`).
- Die Datenbank hält nur öffentliche Zertifikate und Metadaten (`key_reference` = Dateiname).

### Ausstellung

Ausschließlich der `PkiService`: kein Endpunkt nimmt CSR-Inhalte, Namen, Rechte oder zusätzliche Roots vom Client entgegen. Ein Signaturzertifikat entsteht bei Einrichtung, bei „Erneuern“ (Administrator) und durch die Wartung kurz vor Ablauf; Zertifikat und Registereintrag entstehen zusammen, der Vorgänger wird `superseded` (nicht widerrufen). Die Ausstellung ist gesperrt, solange die Issuing CA widerrufen oder abgelaufen ist.

### Widerruf und Register

- `pki_certificates`: vollständiges öffentliches Zertifikat (PEM), Seriennummer (unique je CA), Status `active|revoked|expired|superseded`; die Statushistorie steht getrennt in `pki_certificate_events`.
- `pki_revocations`: nur RFC-5280-Gründe (0–6, 9, 10; nicht 7/8), nie gelöscht; ein aktiver Eintrag je Zertifikat (partieller Unique-Index). `certificateHold` (6) ist als einziger Grund aufhebbar (`released_at`; der Eintrag bleibt als Historie, fehlt aber in neuen CRLs); alle anderen sind endgültig.
- Widerruf nur durch Administratoren, mit Bestätigung durch Eingabe der Seriennummer; die Root CA wird nicht widerrufen (Selbstsignatur — bei Kompromittierung neue PKI und Anker entfernen). Der Widerruf der Issuing CA verlangt den Root-Schlüssel (Server oder Upload), sperrt die PKI und wird in der Root-CRL geführt.

### CRL

X.509 v2, DER, aus der Datenbank gebaut und mit der CA signiert (RSA-PKCS#1-v1.5/SHA-256 bzw. ECDSA/SHA-384): Aussteller, thisUpdate, nextUpdate, `cRLNumber` (je Aussteller lückenlos monoton, Unique-Index mit Wiederholung bei Kollision), AKI, Einträge mit Grund. Jede CRL wird vor dem Speichern gegen das Ausstellerzertifikat geprüft und in `pki_crls` abgelegt. Die Issuing-CRL wird nach jedem Widerruf sofort, sonst vor `nextUpdate` (`PKI_CRL_RENEW_BEFORE_HOURS`, Standard 48 h; Standardgültigkeit 7 Tage) vom `PkiMaintenanceService` erneuert; die Root-CRL (365 Tage) nur mit Root-Schlüssel. Eine abgelaufene CRL gilt nicht als aktuell. Öffentlich (ohne Anmeldung, nur öffentliche Daten, ratenbegrenzt): `GET /pki/crl/{ca-id}.crl` (`application/pkix-crl`), `/pki/certificates/root|issuing|{id}.cer` (`application/pkix-cert`), `/pki/chain.pem`.

### Prüfung

Die Upload-Prüfung (`/verify`, ADR 0033) weist getrennt aus: **kryptografisch gültig**, **Kette/Profil gültig**, **Aussteller durch die Learny-PKI bestätigt** (Kette endet an einem Root aus dem **Zertifikatsregister**, optional per `PKI_TRUSTED_ROOT_SHA256` gepinnt, und das Zertifikat steht im Register), **Widerrufsprüfung** (`good`/`revoked`/`unknown` aus Register und signaturgeprüfter, aktueller CRL) und **öffentlich vertrauenswürdige CA: nein**. Ein im PDF eingebettetes Root ist nie Vertrauensanker. Ein widerrufenes Signaturzertifikat macht die Prüfung ungültig. Neuer Signer-Trust-Wert `learny_pki` neben `installation`.

### Rollen, Audit

Lesen: Administrator und Auditor. Schreiben: ausschließlich Administratoren; jede Operation prüft die Rolle im Dienst gegen die Datenbank (`RequireRoleAsync`). Nie zugänglich für normale Benutzer, `themen_admin`, MCP-Tokens oder den MCP-Dienstschlüssel (`/api/pki` steht nicht in `TokenAllowedPrefixes`). Auditereignisse: `pki.initialize`, `pki.certificate.issue`, `pki.certificate.renew`, `pki.certificate.revoke`, `pki.crl.issue`, `pki.key.rotate` (Root-Schlüssel entfernt), `pki.configuration.change` (Zurücksetzen) — ohne Geheimnisse; Verifikationsfehler gehen nicht ins Audit (öffentlicher, anonymer Pfad).

### Sicherung und Wiederherstellung

Gesichert werden: Datenbank (Register, CRLs), das Schlüsselverzeichnis (`issuing-ca.p12`, `signing-*.p12`), das Schlüsselpasswort und — getrennt und offline — der Root-Schlüssel samt Passphrase. Nach Wiederherstellung zeigt die Verwaltung den Zustand; ist eine Schlüsseldatei nicht vorhanden oder passt sie nicht zum Register, ist die PKI `key_unavailable`/gesperrt und es wird nichts neu erzeugt.

### Plattformen

Gleiche Logik auf Windows/IIS und Linux/Docker: derselbe Code, Schlüssel in `PKI_KEY_DIR` außerhalb des Webroots (IIS: `<root>\data\pki`; Docker: Volume `web_data` → `/app/data/pki`), SQLite und PostgreSQL mit denselben idempotenten Migrationen (`Schema.cs`). DPAPI wurde erwogen und verworfen, weil sie Windows-spezifisch und an den Rechner gebunden ist (nicht portierbar, bricht Docker/Wiederherstellung); das passwortbasierte PKCS#12 ist plattformgleich.

### Bestehende Installationen

`PDF_SIGNING_CERT_FILE/_PFX_BASE64/_PASSWORD` bleiben unverändert wirksam und haben **Vorrang**. Reihenfolge: ausdrücklich konfiguriertes Zertifikat → betriebsbereites PKI-Signaturzertifikat (Kette Issuing + Root im PDF) → nur in Development das Entwicklungszertifikat → sonst 503. Bereits gespeicherte PDFs und alte Zertifikate werden nie verändert. Open-Badge/CLR bleiben unabhängig (VC-JWT/ES256, ADR 0021/0032).

## Konsequenzen

- Neue Tabellen `pki_state`, `pki_certificates`, `pki_certificate_events`, `pki_revocations`, `pki_crls`; neue Dienste `PkiService`, `PkiKeyStore`, `PkiVerifier`, `PkiMaintenanceService`; Verwaltung `/admin/pki`, API `/api/pki/*`, öffentliche Endpunkte `/pki/*`.
- Unabhängige Prüfung mit OpenSSL und pyHanko (`tools/pki/validate_pki.py`, `docs/testing.md`). OCSP wurde nicht umgesetzt und wird nicht behauptet.

## Grenzen und offene Risiken

- **Keine öffentliche Anerkennung:** Adobe Acrobat, Windows, macOS, Browser und EUTL erkennen die Learny-Root nicht; Empfänger müssen sie ausdrücklich als Vertrauensanker importieren. Es wird nie versucht, die Root in fremde Vertrauensspeicher zu installieren.
- Solange der Root-Schlüssel auf dem Server liegt (Erststart), schützt ihn nur Passphrase und Dateirecht; die Kompromittierung des Servers samt Passphrase kompromittiert die Root.
- Das Schlüsselpasswort liegt dem Webprozess vor (nötig zum Signieren); wer die Konfiguration/den Prozess kontrolliert, kann Zertifikate ausstellen. Die Issuing CA ist online verfügbar — Widerruf der Issuing CA ist das Gegenmittel.
- Sperrprüfung nur per CRL (kein OCSP, kein Stapling, keine LTV-/PAdES-B-LT-Einbettung); Prüfer müssen die CRL abrufen können. Verifikation und CRL-Auslieferung hängen an der Erreichbarkeit der Basis-URL.
- Eine Änderung der öffentlichen Basis-URL macht die CRL-URLs bereits ausgestellter Zertifikate ungültig (Neuausstellung der Kette nötig).
- Rotation der Issuing/Root CA ist nicht automatisiert; sie erfordert eine kontrollierte neue Hierarchie (siehe `docs/pki.md`).

## Leitplanken für Änderungen

- Keine zweite Ausstellungs-, Widerrufs- oder CRL-Logik; alles über `PkiService`. Keine Endpunkte für CSR-Inhalte, zusätzliche Roots oder Schlüsselimport/-export.
- Private Schlüssel nie in Datenbank, Logs, Audit, Antworten oder Repository; Schlüsseldateien nie überschreiben; Root nie stillschweigend neu erzeugen.
- Vertrauensanker nur aus dem Register (bzw. Pin), nie aus dem geprüften Dokument; Ergebnis „öffentlich vertrauenswürdig“ ist immer `false`.
- Neue OIDs, OCSP, Certificate Policies oder öffentliche Anerkennung nur mit neuer ADR.
- Neue Konfiguration: `PkiSettings`, `.env.example`, `deploy/docker/*`, `deploy/iis/web.env`, `docs/pki.md`.
