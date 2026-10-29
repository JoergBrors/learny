# Learny-PKI — Betrieb und Verwaltung

Entscheidung und Sicherheitsgrenzen: [ADR 0035](adr/0035-learny-pki-und-zertifikatsverwaltung.md).

> Learny betreibt optional eine eigene, standardkonforme X.509-PKI. Sie begründet **keine** automatische öffentliche Anerkennung durch Adobe, Betriebssysteme oder Vertrauenslisten. Empfänger müssen die Learny-Root ausdrücklich als Vertrauensanker importieren, wenn sie die Signatur in ihrer Software als „vertrauenswürdig“ sehen wollen.

## Überblick

```
Learny Root CA   (offline gesichert, CA:TRUE pathLen 1)
 └─ Learny Issuing CA   (online, CA:TRUE pathLen 0, signiert Zertifikate und CRLs)
     └─ Learny Document Signing   (CA:FALSE, EKU id-kp-documentSigning, signiert die Zertifikats-PDFs)
```

PDF-Signaturreihenfolge: ausdrücklich konfiguriertes `PDF_SIGNING_CERT_*` → betriebsbereites PKI-Signaturzertifikat (Kette Issuing + Root im PDF) → nur Development: selbstsigniertes Entwicklungszertifikat → sonst keine PDFs (503). Bestehende Installationen mit `PDF_SIGNING_CERT_*` ändern sich nicht.

## Ersteinrichtung

1. **Automatisch beim ersten Start** (Standard, `PKI_AUTO_INIT=true`): Sobald die öffentliche Basis-URL feststeht (`PKI_PUBLIC_URL`, sonst `PUBLIC_BASE_URL`; in Development die lokale Adresse), richtet der erste Start die PKI einmalig ein. Spätere Starts richten **nie** etwas ein; bei vorhandenem Zustand, Zertifikaten oder Schlüsseldateien gibt es einen Fehler statt einer neuen PKI.
2. **Ausdrücklich:** Administration → PKI → „PKI initialisieren“ (Basis-URL prüfen, bestätigen).
3. **Einmal-Befehl:** `dotnet LearnCards.Web.dll --pki-init [--root-out <Pfad>]` (Umgebung wie beim Dienst: DB, `PKI_*`). Mit `--root-out` wird der verschlüsselte Root-Schlüssel an den Zielort geschrieben (Passphrase siehe `PKI_ROOT_PASSPHRASE` bzw. `<Pfad>.passphrase`) und vom Server entfernt.

Die Basis-URL steht **dauerhaft** in den CRL-Verteilungspunkten; sie muss die spätere öffentliche Adresse sein (https im Betrieb). Ohne Basis-URL wird nichts automatisch eingerichtet; der Status zeigt „Initialisierung erforderlich“.

**Nach der Einrichtung (Pflicht für Produktion):**

1. `root-offline/root-ca.p12` und die Passphrase (`PKI_ROOT_PASSPHRASE` oder `root-offline/ROOT-PASSPHRASE.txt`) auf ein Offline-Medium kopieren (zwei Orte).
2. Administration → PKI → „Root-Schlüssel vom Server entfernen“ (Bestätigung `ROOT-SCHLUESSEL ENTFERNEN`).
3. Falls das Schlüsselpasswort erzeugt wurde (`pki-key.pass`): als `PKI_KEY_PASSWORD` in die Secret-Verwaltung übernehmen, danach die Datei entfernen.
4. Root-Zertifikat (`/pki/certificates/root`) und Kette (`/pki/chain.pem`) an Empfänger verteilen; optional den SHA-256-Fingerabdruck als `PKI_TRUSTED_ROOT_SHA256` festlegen.

## Schlüsselablage

| Datei | Inhalt | Wo |
|---|---|---|
| `issuing-ca.p12` | Issuing-CA-Schlüssel + Zertifikat (AES-256-verschlüsselt) | `PKI_KEY_DIR` |
| `signing-<id>.p12` | Signaturschlüssel je Signaturzertifikat | `PKI_KEY_DIR` |
| `root-offline/root-ca.p12` | Root-Schlüssel, nur bis zur Auslagerung | `PKI_KEY_DIR/root-offline` |
| `pki-key.pass` | nur wenn `PKI_KEY_PASSWORD` fehlte (erzeugt) | `PKI_KEY_DIR` |

- **Windows/IIS:** Standard `<IIS_PHYSICAL_PATH>\data\pki` (außerhalb des Webroots, kein statisch ausgelieferter Pfad). Die Anwendung setzt eine ACL nur für das Dienstkonto (Anwendungspool-Identität), SYSTEM und Administratoren. `PKI_KEY_PASSWORD`/`PKI_ROOT_PASSPHRASE` gehören in die `.env` bzw. Secret-Verwaltung. DPAPI wird bewusst nicht verwendet (maschinengebunden, nicht portabel).
- **Linux/Docker:** Standard `/app/data/pki` im Volume `web_data` (Modus 0600/0700). Nie in das Image backen, nie in Git, nie im Compose-File: Passwörter nur über `.env`/Docker-Secrets.
- Private Schlüssel stehen nie in der Datenbank, in Logs oder im Audit. Die Datenbank enthält nur öffentliche Zertifikate, Register und CRLs.

## Konfiguration

Siehe `.env.example` („Eigene Learny-PKI“): `PKI_PUBLIC_URL`, `PKI_AUTO_INIT`, `PKI_KEY_DIR`, `PKI_KEY_PASSWORD`, `PKI_ROOT_PASSPHRASE`, `PKI_KEY_ALGORITHM` (`rsa` Standard | `ecdsa`), `PKI_ORGANIZATION`, `PKI_ROOT_VALIDITY_YEARS` (20), `PKI_ISSUING_VALIDITY_YEARS` (10), `PKI_SIGNING_VALIDITY_DAYS` (730), `PKI_CRL_VALIDITY_DAYS` (7), `PKI_ROOT_CRL_VALIDITY_DAYS` (365), `PKI_CRL_RENEW_BEFORE_HOURS` (48), `PKI_EXPIRY_WARNING_DAYS` (60), `PKI_TRUSTED_ROOT_SHA256`. Passwörter müssen mindestens 16 Zeichen haben; ein zu kurzes gesetztes Passwort ist ein Startfehler.

## Verwaltung (Administration → PKI, `/admin/pki`)

Status und Zustand, Zertifikatsregister mit Ablaufwarnung, Kette/Zertifikate herunterladen, Signaturzertifikat erneuern, Widerruf (Grund nach RFC 5280, Bestätigung durch Eingabe der Seriennummer; Issuing CA: Root-Schlüssel), Sperre aufheben (nur `certificateHold`), Widerrufsregister, CRLs mit Gültigkeit und Download, Selbsttest (Profile, Kette, CRLs, Probesignatur). Auditoren sehen alles lesend; Änderungen sind Administratoren vorbehalten. Entsprechende API: `/api/pki/*` (nur Browser-Anmeldung).

Zustände: `not_configured`, `initialization_required`, `initializing`, `initialized`, `operational`, `key_unavailable` (Schlüsseldatei fehlt/beschädigt/passt nicht — Signierung gesperrt, nie Neuerzeugung), `maintenance_required` (z. B. kein gültiges Signaturzertifikat, CRL abgelaufen, abgebrochene Einrichtung), `locked` (Issuing CA widerrufen).

## Sperrlisten

`GET /pki/crl/{ca-id}.crl` (`application/pkix-crl`, öffentlich). Die Issuing-CRL wird nach jedem Widerruf sofort und sonst vor `nextUpdate` erneuert (Hintergrunddienst alle 15 Minuten; zusätzlich beim Abruf einer abgelaufenen CRL). Die Root-CRL gilt 365 Tage und wird nur mit dem Root-Schlüssel erneuert (API `POST /api/pki/crl/root` mit Upload der Root-Datei) — die Verwaltung warnt vor Ablauf. Eine abgelaufene CRL wird nicht als aktuell ausgeliefert (503) und nicht als aktuell bewertet.

## Sicherung und Wiederherstellung

Sichern: Datenbank, `PKI_KEY_DIR` (ohne `root-offline`), Schlüsselpasswort (Secret-Verwaltung), **getrennt offline** den Root-Schlüssel samt Passphrase. Wiederherstellung: Datenbank und Schlüsselverzeichnis gemeinsam einspielen; die Verwaltung zeigt danach den Zustand. Passt eine Schlüsseldatei nicht zum Register oder fehlt sie, bleibt die PKI `key_unavailable` (keine Signatur, keine Neuerzeugung). Nach einem Restore: Selbsttest ausführen und eine neue CRL ausstellen.

## Schlüssel- und Zertifikatswechsel

- **Signaturzertifikat:** läuft nach 730 Tagen ab; die Wartung erneuert es 14 Tage vorher automatisch, „Erneuern“ löst es sofort aus (Vorgänger = „ersetzt“; bestehende PDFs bleiben gültig, solange nicht widerrufen).
- **Issuing CA:** bei Kompromittierung widerrufen (Root-Schlüssel nötig) — die PKI ist danach `locked`. Eine neue Hierarchie (neue Basis-URL oder neue Datenbank/Schlüsselablage) ist ein bewusster Neuanfang; ältere PDFs sind dann an die alte Root gebunden. Eine automatisierte Rotation gibt es nicht.
- **Root:** läuft nach 20 Jahren ab; Wechsel nur durch eine neue PKI mit neuem Vertrauensanker bei allen Empfängern.

## Prüfung durch Dritte

Upload-Prüfung `/verify` (ADR 0033/0035) weist getrennt aus: kryptografisch gültig · Kette/Profil gültig · Aussteller durch die Learny-PKI bestätigt · Widerrufsprüfung (CRL) · öffentlich vertrauenswürdige CA: nein. Externe Prüfung: Root importieren und z. B. mit OpenSSL (`openssl verify -crl_check_all -CAfile bundle.pem -untrusted issuing.pem signing.pem`) oder pyHanko prüfen (`tools/pki/validate_pki.py`).

## Nicht umgesetzt

OCSP (kein Responder, keine OCSP-URL in den Zertifikaten), automatische Root-/Issuing-Rotation, Delta-CRLs, CA-Zertifikate für Dritte, Schlüsselimport/-export, LTV/PAdES-B-LT, Eintrag in Vertrauenslisten (AATL, EUTL, Betriebssysteme).

## Nachtrag (ADR 0037/0038)

Administratoren sehen ausgestellte Nachweise im Register (`/admin/credentials`) und können einzelne Nachweise gezielt widerrufen; das Widerrufen eines Signaturzertifikats geschieht weiterhin nur hier in der PKI (`PkiService.RevokeAsync`). Der PKI-Status des PDF-Signaturzertifikats wird im Register-Detail angezeigt (Suche über den Thumbprint).

Abgrenzung im Ausstellungsregister (ADR 0037): Der **X.509-Thumbprint** identifiziert das Signaturzertifikat eines PDFs und kann viele Nachweise treffen; ein **Credential-Widerruf** lässt das Zertifikat und alle anderen damit signierten PDFs unberührt (getestet). Wird ein PKI-Signaturzertifikat über `PkiService.RevokeAsync` widerrufen, melden die PDF-Prüfung (`signer_revoked`) und die Registerdetails (PKI-Status) die Folge für alle damit signierten PDFs; die Credentials selbst (Badge/CLR) werden dadurch nicht widerrufen.

## Hinweis zu Manifest und Zeitnachweis (ADR 0039)

Die Learny-PKI beglaubigt das **PDF-Signaturzertifikat** (CMS). Das Integritätsmanifest wird dagegen mit dem Badge-Schlüssel (ES256) signiert; beide Vertrauensstränge bleiben getrennt. Die PKI wird dafür nicht dupliziert; sie dient als zusätzlicher unabhängiger Anker, wenn die CMS-Signatur über `learny_pki` bestätigt ist. Ein OpenTimestamps-Nachweis ersetzt weder Zertifikatskette noch CRL.
