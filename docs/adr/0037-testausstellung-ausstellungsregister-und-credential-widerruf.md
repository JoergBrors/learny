# 0037 — Administrative Testausstellung, Ausstellungsregister und gezielter Credential-Widerruf

- **Status:** Akzeptiert
- **Datum:** 2026-10-28
- **Ergänzt:** [0021](0021-zertifikate-und-open-badges.md), [0032](0032-themenabschluss-clr-und-master-badge.md), [0033](0033-signierte-zertifikats-pdfs.md), [0034](0034-nachweisuebersicht-pruefsiegel-und-linkedin.md), [0035](0035-learny-pki-und-zertifikatsverwaltung.md)

> **Architekturaussage.** Administratoren können gekennzeichnete Testnachweise ausstellen, ein durchsuchbares Ausstellungsregister einsehen und genau einen Nachweis gezielt widerrufen. Dafür gibt es keine zweite Zertifikatsverwaltung: Register und Widerruf arbeiten auf der Tabelle `certificates`, `certificate_pdfs`, `pki_certificates` und `audit_log`.

## Kontext

Für die externe Validierung (1EdTech) und für den Betrieb fehlten drei Dinge: ein Weg, Nachweise zu erzeugen, ohne eine Prüfung abzulegen; eine Übersicht, wer wann welchen Nachweis für wen ausgestellt hat; und ein gezielter Widerruf anhand von ID oder Fingerprint. Der bestehende Admin-Widerruf kannte weder Fingerprint noch Bestätigung, war nicht idempotent und zeigte private Modulnamen bewusst nicht an (ADR 0011/0015).

## Entscheidung

1. **Test ≠ regulär.** Testnachweise entstehen ausschließlich über `CertificateService.IssueTestAsync` (nur Administratoren, Rolle im Dienst gegen die DB). Sie haben `is_test = 1`, die Nummer `TEST-…`, den Empfänger „TEST-Subjekt“ (Konto: der ausstellende Administrator), den Namenspräfix „TEST –“, ein Evidence „TESTNACHWEIS“, **kein** Ergebnis, **keine** Kompetenz, **keinen** Prüfungssnapshot, keine Blockchain-Verankerung. Es gibt keinen Umweg um `QuizService`/`TopicCredentialService`; die regulären Abschlussbedingungen werden nicht berührt. Die Module/Themen der Testnachweise sind frische Zufalls-IDs (keine Kollision mit `ux_certificates_user_module`). Testnachweise erscheinen nicht in „Meine Abschlüsse“, der Auditoren- und Admin-Altliste und den Zählern von `AnchorService`.
2. **Identitätsmodell.** Die Credential-ID (`certificates.id`) ist die Identität. Der **Fingerprint** eines Credentials ist der SHA-256 (Hex) der gespeicherten, signierten Original-JWS (`badge_hash` für Badge/Master-Badge, `clr_hash` für das CLR, `certificate_pdfs.pdf_sha256` für das PDF). Der **X.509-Thumbprint** (`certificate_pdfs.signer_thumbprint`, SHA-1; bzw. SHA-256 eines PKI-Zertifikats) identifiziert das Signaturzertifikat des PDFs und ist **nicht** der Fingerprint eines Credentials: Mehrere Nachweise können dasselbe Zertifikat nutzen; die Thumbprint-Suche zeigt dann mehrere Treffer.
3. **Register ohne Doppelspeicherung.** Neue Spalten in `certificates` (alle `DEFAULT`/`NULL`): `is_test`, `issued_by`, `issued_by_name` (Name zum Ausstellungszeitpunkt), `issue_source` (`exam` | `admin` | `unknown`), `parent_id` (Test-Modulnachweis → Test-Themenabschluss). Reguläre Ausstellungen setzen `issue_source = 'exam'` (Systemprozess). **Altbestand** bleibt `unknown`: Aussteller und Name wurden damals nicht erfasst und werden nicht erfunden; angezeigt wird „Altbestand, Aussteller nicht erfasst“. Indizes auf `issued_at`, `badge_hash`, `clr_hash`, `(is_test, status)`.
4. **Berechtigung.** Register, Detail, Testausstellung und Widerruf liegen unter `/api/admin/credentials*` und der Seite `/admin/credentials`: nur Browser-Anmeldung (nie MCP-Token oder Dienstschlüssel; `/api/admin` steht nicht in `TokenAllowedPrefixes`), Rolle `admin` zusätzlich im Dienst. Der Aussteller stammt nur aus dem authentifizierten Kontext (`UserService`), nie aus dem Request. Listen enthalten keine JWS und keine Antworten. Das Register zeigt Empfängername und Credential-Titel auch bei privaten Modulen, weil beides im Credential steht, das die empfangende Person selbst weitergibt, und Administratoren es zum Widerruf brauchen; Lerninhalte, Karten und Ergebnisse bleiben ausgeschlossen (ADR 0011/0015 unverändert für Inhalte).
5. **Widerruf.** Genau ein Nachweis je Aufruf, adressiert über die Credential-ID; Bestätigung durch die Nachweisnummer; Grund Pflicht (≤ 500 Zeichen). Ein bedingtes `UPDATE … WHERE status = 'active'` macht ihn idempotent: Ein zweiter Aufruf liefert `changed = false` ohne weiteres Ereignis. Die signierten Originaldaten bleiben unverändert; öffentliche Prüfung, Status-Endpunkt und Sperrliste (`1EdTechRevocationList`) melden `revoked`; eine gültige Signatur ist kein Gültigkeitsbeweis. Kein kaskadierender Widerruf: Der Themenabschluss (Master-Badge + CLR, eine Identität, ADR 0032) wird einzeln widerrufen, die Modulnachweise bleiben gültig und werden im Detail als „verknüpft“ gezeigt. Ein Widerruf wird nicht rückgängig gemacht; eine Neuausstellung braucht eine neue Identität.
6. **X.509.** Das Widerrufen eines Signaturzertifikats bleibt ausschließlich `PkiService.RevokeAsync` (Seite `/admin/pki`, RFC-5280-Gründe, CRL, Seriennummern nicht wiederverwendet, Schlüsseldateien nie gelöscht). Das Register verlinkt dorthin und zeigt den PKI-Status; ein Credential-Widerruf widerruft nie ein X.509-Zertifikat und umgekehrt.
7. **Audit.** Bestehendes `audit_log`: `credential.test_issue` und `credential.revoke` mit `target` = Credential-ID, Akteur aus dem Kontext, `detail` = Korrelations-ID, Nummer, Ergebnis (bei Widerruf Grund und Fingerprint). Keine Secrets, keine vollständigen JWS.

## Konsequenzen

- Der bisherige Endpunkt `POST /api/admin/certificates/{id}/revoke` bleibt bestehen (Kompatibilität), ist aber nicht idempotent und ohne Fingerprint/Bestätigung; neue Oberflächen nutzen den Registerweg.
- Test-PDFs entstehen über den bestehenden PDF-Dienst (Titel/Kopfzeile „TESTNACHWEIS“), mit der regulären PDF-Signatur.
- Offen: Eine Verknüpfung regulärer Modulnachweise mit ihrem Themenabschluss (`parent_id`) wird für Altbestand nicht rekonstruiert.

## Leitplanken für Änderungen

- Keine zweite Credential-/Signatur-/Widerrufslogik; Testnachweise nie aus Quizergebnissen, Snapshots oder Kompetenzen ableiten.
- `is_test` in jeder Liste/Statistik regulärer Abschlüsse berücksichtigen.
- Aussteller nie aus Request-Daten; Widerruf nie automatisch für mehrere Nachweise; Fingerprint ≠ Thumbprint.
- Dokumentation: `docs/database-schema.md`, `docs/credential-validation.md`, AGENTS.md.

## Betroffener Code

- `Services/Certificates/CredentialRegisterService.cs`, `CertificateService.Test.cs`, `CertificateService.cs`, `Api/CertificateEndpoints.cs` (`/admin/credentials*`), `Components/Pages/CredentialRegister.razor`, `Data/Schema.cs` (+ Provider-Migrationen), `CredentialRegisterTests.cs`

## Nachtrag 2026-10-28 (Restabnahme)

- PostgreSQL-Prüfung deckte zwei Fehler auf (Index vor Spalte bei Altdatenbanken; `LOWER(uuid)`); beide behoben, siehe [credential-validation.md](../credential-validation.md).
- Datenschutzabwägung (Register zeigt Empfänger und Titel auch privater Module) und offene Betreiberentscheidung dort dokumentiert.
- Die Missbrauchsbremse der öffentlichen Endpunkte bleibt unverändert; Tests nutzen eine eigene Client-Adresse.

## Nachtrag 2026-10-29

Das Register zeigt je Nachweis die Formatversion (Format 1 = Altbestand, Format 2 = VC-JWT-Profil, ADR 0038). Die Widerrufsliste (`/api/public/revocation-list`) enthält zusätzlich zu `revoked_credentials` das vom Validator gelesene Feld `revokedCredential: [{id, revoked}]`; Master-Badge und CLR werden gemeinsam gelistet, Modulnachweise nie automatisch (ADR 0032).
