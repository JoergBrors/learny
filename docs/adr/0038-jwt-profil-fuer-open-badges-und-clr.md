# 0038 — JWT-Profil für Open Badges 3.0 und CLR 2.0

- **Status:** Akzeptiert
- **Datum:** 2026-10-28
- **Ersetzt teilweise:** [0021](0021-zertifikate-und-open-badges.md) und [0032](0032-themenabschluss-clr-und-master-badge.md) (nur Signaturprofil und `proof.jwt`-Hülle; alles Übrige bleibt)
- **Ergänzt:** [0037](0037-testausstellung-ausstellungsregister-und-credential-widerruf.md)

> **Architekturaussage.** Neu ausgestellte Credentials werden als VC-JWT nach dem Abschnitt „JSON Web Token Proof Format“ von Open Badges 3.0 / CLR 2.0 signiert. Das JWT ist der Proof; die JSON-Datei trägt keinen erfundenen Proof-Typ mehr. Bereits ausgestellte Credentials bleiben unverändert und prüfbar.

## Kontext

Die externe 1EdTech-Validierung (2026-10-28) meldete für `master-badge.json` / `clr-credential.json` „Undefined JSON-LD term: JwtProof2020“ und „kein unterstützter DataIntegrityProof“, für die JWTs einen Parser-/NullPointer-Fehler im Validator. Der Vergleich mit der Spezifikation (abgerufen von imsglobal.org/spec/ob/v3p0, §8.2.3–8.3) ergab für das bisherige Profil:

| Befund | Spezifikation | Bisher |
|---|---|---|
| JOSE-`typ` | „If present, MUST be `JWT`“ | `vc+jwt` |
| Weitere JOSE-Header | „NOT allowed“ | `cty: vc` |
| `iss` (= `issuer.id`), `jti` (= `id`), `nbf` (= `validFrom`), `sub` (= `credentialSubject.id`) | Pflicht | fehlten; `credentialSubject.id` fehlte |
| `proof` | darf bei JWT-Proof entfallen; ein Linked-Data-Proof muss ein definierter Proof-Typ sein (Konformität: `DataIntegrityProof`/EdDSA-Suite) | erfundener Typ `JwtProof2020` (kein JSON-LD-Term) |
| `alg` | „MUST be RS256 as a minimum“; andere erlaubt, „limits interoperability“ | ES256 |

Die Fehlermeldung `JwtProof2020` ist damit **verursachte** Fehlkonstruktion (undefinierter Term), kein Validatorfehler. Die NullPointer-Fehler beim JWT sind mit fehlendem `sub`/`iss`/`jti`/`nbf` (und evtl. ES256) **plausibel erklärt, aber nicht bewiesen**: Der 1EdTech-Validator wurde nicht ausgeführt; welche Algorithmen er tatsächlich akzeptiert, ist nicht belegt. Die ES256-Signatur selbst ist unabhängig (Standard-.NET-`ECDsa`, JWKS) gültig, die `kid` löst über `/.well-known/jwks.json` auf.

## Entscheidung

1. Neue Credentials: Header nur `alg: ES256`, `kid`, `typ: JWT`; Nutzlast = Credential plus `iss`, `jti`, `nbf`, `sub`; `credentialSubject.id` = pseudonyme `urn:uuid:` (stabil je Benutzer, ohne interne ID). Kein `proof` in der JSON-Ausgabe (`GET /api/certificates/{id}/badge|clr`); das JWT kommt getrennt (`badge.jwt`, `clr.jwt`). Eingebettete Credentials im CLR sind kompakte JWS.
2. **Altbestand** (`typ: vc+jwt`) bleibt byteidentisch: Signatur, Hash, Hülle `proof.jwt`, Prüfung. `BadgeSigner.IsLegacyProfile` erkennt es am Header; `CredentialFromJws` liefert in beiden Profilen das Credential.
3. Prüfung: `VerifyAsync` akzeptiert die JWS, JSON mit `proof.jwt` (Altbestand) und JSON ohne Proof; Letzteres wird gegen die bei Learny gespeicherte JWS und deren Inhalt geprüft (Manipulation wird erkannt). Die PDF-Pakete tragen die JWS als `.jwt`-Dateien; `PdfVerificationService` liest sie von dort (ältere PDFs weiter aus `proof.jwt`).
4. **Nicht entschieden / offen:** RS256. Die Spezifikation fordert es als Minimum. Learny signiert weiter mit ES256 (ADR 0021; bestehender Schlüssel, keine neue Schlüsselverwaltung). Ein zweiter RSA-Schlüssel oder `DataIntegrityProof` (eddsa-rdfc-2022, RDF-Canonicalization — ohne NuGet-Paket aufwendig) wäre eine eigene Entscheidung, falls der Validator ES256 ablehnt. Keine bloße Umbenennung von `JwtProof2020`.
5. Es wird keine 1EdTech-Zertifizierung behauptet.

## Konsequenzen

- JSON-Downloads sind nicht mehr „selbst prüfbar“ ohne JWT/Learny; das JWT ist der maßgebliche Nachweis.
- Tests (`CredentialRegisterTests`, `TopicCredentialTests`, `PdfTests`) prüfen das Profil unabhängig vom Learny-Verifier.

## Leitplanken für Änderungen

- Nur `BadgeSigner.SignCredential` für neue Credentials; kein neues Proof-Format ohne ADR; Altbestand nie umsignieren.

## Betroffener Code

- `Services/Certificates/BadgeSigner.cs` (`SignCredential`, `CredentialFromJws`, `IsLegacyProfile`), `CertificateService*.cs`, `CertificatePdfService.cs`, `PdfVerificationService.cs`

## Nachtrag 2026-10-28 (CLR-Validierung): Credential im Claim `vc`

- Die Nutzlast neuer Credentials ist `{ iss, jti, nbf, sub, vc: <Credential> }` (zuvor: Credential auf oberster Ebene). Grund: Der 1EdTech-CLR-Validator liest das Credential nur aus `vc` und bricht sonst mit einer NullPointerException ab (Quelltext `CLR20Inspector`/`PayloadParser`); die Spezifikation nennt `vc` für VCDM-2.0-Credentials, die Referenz-JWTs des Validators verwenden diese Form. Es wurden bisher keine Credentials im Zwischenformat produktiv ausgestellt (nur Testläufe); `CredentialFromJws` liest `vc`, die Zwischen- und Altformate bleiben lesbar.
- CLR-JSON: `verifiableCredential` enthält Credential-Objekte (JSON-Schema), das CLR-JWT eingebettete JWS (ClrSubjectJwtPayload). Kein Proof wird erfunden; ein eigenständig signiertes JSON-LD ist weiterhin nicht Teil des Profils.
- ES256 bleibt; RS256/`DataIntegrityProof` nur bei belegter Notwendigkeit (neue ADR).

## Nachtrag 2026-10-28 (Open-Badges-Validierung): `kid` und Algorithmus

- `kid` neuer Credentials = `<PUBLIC_BASE_URL>/badges/keys/<Schlüssel-ID>`, ein Endpunkt, der genau ein JWK liefert (der Validator liest die `kid`-URL direkt als JWK). Die ältere `…/badges/issuer#<ID>` lieferte das Ausstellerprofil; sie bleibt für Altbestand gültig (`BadgeSigner.Verify` akzeptiert beide Formen).
- **RS256 ist nicht erforderlich:** Der Validator erlaubt RS256 und ES256 (Quelltext `ExternalProofProbe`); ES256 bleibt. Die Aussage „ES256 nicht belegt“ oben ist damit für diesen Validator belegt widerlegt.
- JSON-Darstellung = unsignierte Datendarstellung (Header `X-Credential-Representation: unsigned-json`); ein eigenständig signiertes JSON-LD (DataIntegrityProof) bleibt außerhalb des Profils.
