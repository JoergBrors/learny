# Externe Validierung von Open Badges 3.0 und CLR 2.0

Stand: 2026-10-28 · Entscheidung: [ADR 0038](adr/0038-jwt-profil-fuer-open-badges-und-clr.md) · Testausstellung und Register: [ADR 0037](adr/0037-testausstellung-ausstellungsregister-und-credential-widerruf.md)

## Ausgangslage (externe Validierung vom 2026-10-28)

| Artefakt | Befund des 1EdTech-Validators |
|---|---|
| `master-badge.json` | 2 Fehler: *Undefined JSON-LD term: JwtProof2020*; *kein unterstützter DataIntegrityProof* |
| `master-badge.jwt` | fataler Fehler beim Verifizieren der JWT-Signatur (`JsonNode.get(String)` liefert null) |
| `clr-credential.json` | 5 Fehler: `JwtProof2020` (auch in den eingebetteten Badges) |
| `clr-credential.jwt` | Parserfehler / Java-NullPointer-Exception |

Die Originaldateien liegen nicht im Repository (personenbezogen); die Fälle sind in `CredentialRegisterTests` als Fixtures nachgestellt (das frühere Profil wird vom unabhängigen Prüfer als nichtkonform erkannt).

## Ursachenanalyse

Verglichen wurde mit der Spezifikation (imsglobal.org/spec/ob/v3p0 §8.2–8.3 und CLR 2.0 §7). Der Validator selbst wurde nicht ausgeführt.

| Frage | Ergebnis |
|---|---|
| a) JWT-Struktur konform? | **Nein** (bisheriges Profil): `typ: vc+jwt` (Spezifikation: `JWT`), zusätzlicher Header `cty`, keine Claims `iss`/`jti`/`nbf`/`sub`, kein `credentialSubject.id`. |
| b) ES256-Signatur gültig? | **Ja**, unabhängig geprüft (Standard-`ECDsa` mit dem JWK, IEEE-P1363) — Test `JWT-Profil: neues Credential konform …`. |
| c) Schlüsselauflösung? | `kid` = `<Aussteller>#<Schlüssel-ID>`; die Schlüssel-ID steht in `/.well-known/jwks.json`. Die Spezifikation verlangt eine `kid`, die auf ein JWK auflösbar ist; ob der Validator die Fragment-URL `…/badges/issuer#…` oder `/.well-known/jwks.json` erwartet, ist **nicht belegt**. |
| d) Warum `JwtProof2020`? | Es ist kein definierter JSON-LD-Term und kein von OB 3.0 genannter Proof-Typ. Die Spezifikation lässt den `proof` bei JWT-Proof entfallen (§8.2.2); ein Linked-Data-Proof müsste ein definierter Typ sein (Konformität: `DataIntegrityProof` mit EdDSA-Suite). **Learny-Konstruktion, kein Validatorfehler.** |
| e) Was unterstützt der Validator? | Nicht ausgeführt. Laut Spezifikation: JWT mit mindestens RS256 (andere Algorithmen „schränken die Interoperabilität ein“) und `DataIntegrityProof`/EdDSA. ES256 ist **nicht als unterstützt belegt**. |
| f) NullPointer-Fehler? | Mit fehlenden Pflichtclaims (`sub`, `iss`, `jti`, `nbf`) und Fremdheader plausibel erklärt; ob der Validator daneben ES256 nicht kennt, ist offen. Kein Beleg für einen reinen Validatorfehler — und kein Beleg dagegen. |

## Was geändert wurde

- Neue Credentials (VC-JWT, ES256): Header `alg`/`kid`/`typ: JWT`; Claims `iss`/`jti`/`nbf`/`sub`; `credentialSubject.id`; JSON ohne `proof`; eingebettete Credentials im CLR als kompakte JWS.
- Altbestand (`vc+jwt` mit `proof.jwt`-Hülle) unverändert, weiter prüfbar.
- Nicht geändert: ES256 (kein RSA-Schlüssel, kein DataIntegrityProof) — siehe ADR 0038 Punkt 4.

## Ergebnis nach der Änderung

| Prüfung | Ergebnis |
|---|---|
| Unabhängiger Profilprüfer (Tests) | bestanden für Badge, Master-Badge, CLR, eingebettete Credentials |
| Undefined term `JwtProof2020` | in neuen JSON-Dateien nicht mehr vorhanden (zu bestätigen durch den Validator) |
| JWT beim 1EdTech-Validator | **nicht geprüft** (kein Zugriff aus dieser Umgebung). Manuelle Abnahme: Testnachweis ausstellen, JSON und JWT herunterladen, beide getrennt validieren und das Ergebnis hier eintragen. Stürzt der Validator weiterhin ab, ist das als Validatorbefund zu dokumentieren — nicht als bestandene oder fehlgeschlagene Signaturprüfung. |

## Manuelle Abnahme

1. Als Administrator anmelden → Administration → *Zertifikate & Nachweise* → *Testnachweis ausstellen* (Master-Badge & CLR).
2. JSON und JWT herunterladen (Detailansicht), beim Validator prüfen (JSON und JWT getrennt).
3. Ergebnis (Fehlerliste, Validatorversion, Datum) unter „Ergebnis nach der Änderung“ eintragen.
4. Falls der Validator ES256 ablehnt: neue ADR zu RS256 (zweiter Schlüssel) oder DataIntegrityProof.

## Restabnahme 2026-10-28 (zweite Runde)

### Externe 1EdTech-Validierung — Stand

| Test | Format | Profil | Ergebnis |
|---|---|---|---|
| Modul-Open-Badge 3.0 | JSON / JWT | JWT-Profil ADR 0038, ES256 | **nicht ausgeführt** |
| Master-Badge 3.0 | JSON / JWT | dto. | **nicht ausgeführt** |
| CLR 2.0 | JSON / JWT (eingebettete JWS) | dto. | **nicht ausgeführt** |

Grund: `https://vc.1ed.tech/validate` war aus der Ausführungsumgebung nicht nutzbar (HTTP 500 beim Abruf; die Seite ist eine interaktive Oberfläche, Eingaben lassen sich nicht automatisiert absenden). **Es wird keine erfolgreiche externe Validierung behauptet.** Validator-Version: nicht ermittelbar. ES256-Unterstützung des Validators: **offen**; die Spezifikation nennt RS256 als Minimum („MUST be RS256 as a minimum“, andere Algorithmen schränken die Interoperabilität ein). Ein RS256-Wechsel ist erst bei nachgewiesener Ablehnung von ES256 zu entscheiden (neue ADR); das Profil wurde nicht geändert.

### Reproduzierbare Testanleitung

Wichtig: Der Validator muss den Aussteller erreichen. Testnachweise einer lokalen Instanz (`http://127.0.0.1…`) lassen sich extern **nicht** prüfen (`kid`, JWKS und `credentialStatus` zeigen auf die lokale Adresse). Daher eine öffentlich erreichbare Testinstanz mit `PUBLIC_BASE_URL=https://…` verwenden (kein Produktivsystem nötig). Anonymisierte Dateien werden deshalb nicht eingecheckt: Sie wären an einen lokalen Schlüssel und eine lokale Adresse gebunden und für den Validator wertlos; die Testnachweise enthalten ohnehin nur das Subjekt „TEST-Subjekt“.

1. Als Administrator *Zertifikate & Nachweise* → *Testnachweis ausstellen*: (a) Open Badge – Modul, (b) Master-Badge & CLR.
2. In der Detailansicht herunterladen: Modul-Badge JSON + JWT; Themenabschluss: Master-Badge JSON + JWT, CLR JSON + JWT.
3. Unter https://vc.1ed.tech/validate jede Datei **einzeln** prüfen (JSON und JWT getrennt) und festhalten: Typ, Format, Profil (hier: `ob3-jwt`, ES256), Ergebnis, Fehler/Warnungen, Validator-Version, Datum.
4. Zu kontrollieren (in Learny automatisch geprüft, am Validator zu bestätigen): `typ=JWT`, `alg=ES256`, `kid` (auflösbar über `/.well-known/jwks.json`), `iss`, `sub`, `jti`, `nbf`, `credentialSubject.id`, Credential-Typen (`VerifiableCredential`, `OpenBadgeCredential` / `ClrCredential`), JSON-LD-Kontexte (VC 2.0, OB 3.0.3, CLR 2.0.1, lokaler Kontext für `1EdTechRevocationList`), eingebettete JWS im CLR, Widerrufsstatus nach einem Testwiderruf.
5. Ergebnisse in die obige Tabelle eintragen. Stürzt der Validator ab (NullPointer), ist das als Validatorbefund zu vermerken — nicht als bestanden oder fehlgeschlagen.

### JSON ohne Proof und Verifikationsstatus

- Neue JSON-Dateien enthalten **keinen** `proof`; sie sind kein selbstständig signiertes JSON-LD-Credential. Maßgeblich ist das JWT (gleiche `id`/`jti`, `issuer`/`iss`, Subjekt/`sub`; der JSON-Inhalt ist exakt die Nutzlast ohne die Registered Claims — durch Test belegt).
- Die Prüfung unterscheidet: eingereichtes **JWT** → Signatur, Aussteller, Widerruf; eingereichtes **JSON ohne Proof** → Abgleich mit der bei Learny gespeicherten, signierten Fassung (`content_matches`) — das beweist nur „entspricht dem ausgestellten Nachweis“, nicht die Echtheit einer losen Datei. Eine Datei allein suggeriert keine Gültigkeit; der Status kommt immer aus der Online-Prüfung.
- Eingebettete Credentials im CLR sind kompakte JWS und einzeln mit demselben JWKS prüfbar (Test: unabhängige ES256-Prüfung jedes eingebetteten Credentials).
- Altbestand (`vc+jwt`, `proof.jwt`) bleibt prüfbar (Test `B5 Altbestand`).

### Datenschutz/Rollen: Register gegen ADR 0011/0012/0015

Das Register zeigt Administratoren Empfängername und Credential-Titel (auch privater Module) sowie Aussteller, Zeitpunkt, Fingerprints und Status. Es zeigt keine Karten, Fragen, Antworten, Ergebnisse oder Modulinhalte und liefert keine JWS in Listen. Das weicht bewusst von `ListForAdminAsync` ab (dort bleiben private Namen verborgen). Begründung: Administratoren müssen Nachweise identifizieren und widerrufen können; Name und Titel stehen im Credential, das die empfangende Person selbst herausgibt. Rollenprüfung im Dienst gegen die Datenbank; kein MCP-Zugriff. **Offene Entscheidung für den Betreiber:** Wenn private Modultitel gegenüber Administratoren geschützt bleiben sollen, wäre das Register auf Nummer/Fingerprint/Typ zu beschränken (ADR-Änderung).

### PostgreSQL

Mit einer privaten PostgreSQL-17-Instanz (Port 55433, nur lokal) geprüft:
- Leere Datenbank: vollständiger Integrationslauf, 1362/1362.
- **Bestehende Datenbank vor der Migration**: fünf Spalten und vier Indizes entfernt, Anwendung gestartet → Spalten und Indizes ergänzt, 26 Zertifikate unverändert, Altzeilen mit `issue_source = 'unknown'`, `is_test = 0`; zweiter Start ohne Fehler (idempotent).
- **Gefundene Fehler (behoben):** (1) Der Index auf `is_test` stand in `Schema.cs` vor dem Ergänzen der Spalte → eine bestehende Datenbank wäre beim Start mit „column is_test does not exist“ abgebrochen; der Index wird jetzt nach der Spalte angelegt. (2) `LOWER(uuid)` in der Registersuche → `CAST(c.id AS TEXT)`.
- Parallele Ausstellung (6) und paralleler Widerruf (6 Aufrufe → genau ein Ereignis) laufen im Integrationstest gegen PostgreSQL und SQLite.

## CLR-Validierung vom 2026-10-28 (dritte Runde): JSON-Schema und JWT-Parserfehler

Gemeldet: `clr-credential (1).json` — 2 Fehler, 1 Warnung, 1 Exception; `clr-credential (1).jwt` — 1 fataler Fehler, 1 Exception. Ausstellerzeit: lokale Testkonfiguration `http://localhost:5000`.

### 1. JSON-Schemafehler — Ursache nachgewiesen

Das offizielle Schema (`purl.imsglobal.org/spec/clr/v2p0/schema/json/clr_v2p0_clrcredential_schema.json`, abgerufen) definiert `ClrSubject.verifiableCredential` als **Credential-Objekt oder Array von Credential-Objekten** (`$defs/VerifiableCredential`, `type: object`, Pflichtfelder `@context`, `type`, `issuer`, `validFrom`, `credentialSubject`; `proof` optional). Eine kompakte JWS (String) ist dort **nicht** zulässig; sie ist nur im JWT-Payload-Schema (`ClrSubjectJwtPayload`) vorgesehen. Learny gab in der JSON-Darstellung die drei eingebetteten JWS als Strings aus → Schemaverstoß. Die Exception (`… because clr is null`) ist die Folge: Der Validator konnte aus dem schemawidrigen Dokument kein Credential-Objekt aufbauen. Der Test `CLR-JSON (Ursache)` reproduziert den Verstoß mit einer lokalen Strukturprüfung (`ClrJsonSchemaProblems`).

**Korrektur:** Die JSON-Darstellung enthält die eingebetteten Credentials als **Objekte** (der unveränderte signierte Inhalt der jeweiligen JWS, ohne erfundenen Proof). Das CLR-**JWT** behält die eingebetteten kompakte JWS (ClrSubjectJwtPayload). Identität und Inhalt sind gleich (Test), Signaturen und gespeicherte Original-JWS unverändert.

Einschränkung (bleibt): Die eingebetteten Objekte im JSON tragen keinen eigenen Proof. Der Validator prüft eingebettete Credentials des JSON-Pfads wie eigenständige Credentials und kann daher fehlende Proofs melden — ein standardkonformer, eigenständig verifizierbarer JSON-Export bräuchte echte Linked-Data-Proofs (`DataIntegrityProof`), die Learny nicht erzeugt und die nicht durch Kopieren oder Umbenennen simuliert werden. Der unterstützte Prüfweg ist das **JWT**.

### 2. JWT-Parserfehler — Ursache aus dem Validatorquelltext

Der Fehler ließ sich nicht durch Ausführen des Validators reproduzieren (Bauen und Starten heruntergeladenen Fremdcodes wurde in dieser Umgebung nicht freigegeben). Er wurde stattdessen im Quelltext von `1EdTech/digital-credentials-public-validator` (nur gelesen) nachvollzogen:

- `CLR20Inspector.run` baut den `RunContext` mit `JWT_CREDENTIAL_NODE_NAME = "vc"`, setzt aber **nicht** `JWT_CREDENTIAL_ALLOW_WHOLE_PAYLOAD` (`OB30Inspector` setzt beides).
- `PayloadParser.fromJwt` liest das Credential aus `payload["vc"]`. Fehlt dieser Claim, ruft es `(Boolean) context.get(JWT_CREDENTIAL_ALLOW_WHOLE_PAYLOAD)` auf; der Wert ist `null` → **NullPointerException** („`RunContext.get(String)` is null“). Danach fehlt das erzeugte Credential-Objekt, und `clr.getJson()` in `CLR20Inspector` (Zeile `accumulator.add(probe.run(clr.getJson(), ctx))`) scheitert mit „… because `clr` is null“.
- Learnys erstes JWT-Profil legte das Credential **auf die oberste Ebene** der Nutzlast. Die offizielle Spezifikation nennt dagegen für VCDM-2.0-Credentials den Claim `vc` („store the OpenBadgeCredential in the `vc` claim“), und die Referenzdatei des Validators (`clr20/simple.jwt`) hat die Form `{ "vc": { …ClrCredential… }, "iss", "nbf", "jti", "sub" }` mit eingebetteten JWS-Strings in `vc.credentialSubject.verifiableCredential`.

**Korrektur (ADR 0038, Nachtrag):** Die Nutzlast neuer Credentials ist `{ iss, jti, nbf, sub, vc: <Credential> }`. Header unverändert (`alg ES256`, `kid`, `typ JWT`). Das ist die Struktur der Referenzdatei, mit ES256 statt RS256 und `kid` statt `jwk`. **Ob die NullPointer-Fehler damit verschwinden, ist durch einen erneuten Lauf des Validators zu bestätigen** — der Quelltextbefund erklärt sie vollständig, ersetzt den Lauf aber nicht. Weitere mögliche Stolpersteine, die der Quelltext nicht ausschließt: ES256 (die Referenz nutzt RS256 mit `jwk`), `kid` als nicht erreichbare URL.

### 3. Nicht erreichbare lokale Adressen

Mit `http://localhost:5000` zeigen `iss`, `kid`, `credentialStatus.id` und `issuer.url` auf eine Adresse, die ein externer Validator nicht erreicht. Das ist kein Strukturfehler, führt aber zu getrennten Meldungen (Issuer/Schlüssel nicht abrufbar, Widerrufsliste nicht abrufbar). Learny akzeptiert die lokale HTTP-Adresse für Tests (Development bzw. ohne `PUBLIC_BASE_URL`). **Für eine echte externe Verifikation** sind eine öffentlich erreichbare HTTPS-Aussteller-/Statusadresse (`PUBLIC_BASE_URL`), erreichbare `/.well-known/jwks.json` und `/api/public/revocation-list` Voraussetzung. Der Validator wurde mit den beiden Dateien nicht erneut ausgeführt; **ein Validatorlauf, der mit Exception abbricht, gilt weder als bestanden noch als fehlgeschlagen.**

### 4. Drei Darstellungen — nur eine ist signiert

| Darstellung | Signiert | Zweck |
|---|---|---|
| CLR-**JWT** (`clr.jwt`) | ja (ES256 über Header und Nutzlast) | maßgeblicher, unterstützter Prüfweg |
| CLR-**JSON** (`clr`) | nein | Lesbare Darstellung desselben Inhalts (schemakonform, Objekte); Abgleich mit der gespeicherten JWS über die Online-Prüfung |
| eigenständig signiertes JSON-LD (Data Integrity) | — | **nicht angeboten** |

Ältere Credentials (Profil `vc+jwt`, Credential auf oberster Ebene, `proof.jwt`-Hülle) bleiben unverändert und prüfbar; es wurde nichts neu signiert.

## Open Badges 3.0 — Validierung vom 2026-10-28 (vierte Runde, nur Badges)

Gemeldet (vc.1ed.tech): `master-badge (1).jwt` — 14 Tests, 1 fataler Fehler, 2 Warnungen („öffentlicher Schlüssel über `kid` nicht abrufbar“, `kid` → `http://localhost:5000`); `master-badge (1).json` — 14 Tests, 1 Fehler („The verifiable credential is missing a proof.“), 2 Warnungen.

### Ursache 1 (JWT): `kid` zeigte nicht auf ein JWK — Learny-seitig, unabhängig von `localhost`

Aus dem Validator-Quelltext (`ExternalProofProbe`, nur gelesen): Ist `kid` eine http(s)-URL, ruft der Validator **genau diese URL** ab und liest die Antwort **direkt als einzelnes JWK** (`jwk.get("kty")`, `x`, `y`, `crv`). Die bisherige `kid` `…/badges/issuer#<Schlüssel-ID>` lieferte das **Ausstellerprofil** (ohne `kty`) — selbst mit öffentlich erreichbarer Adresse wäre die Prüfung gescheitert. Die gemeldete Fehlermeldung nennt zusätzlich `localhost` (extern nicht erreichbar) — das ist eine separate Testeinschränkung.

**Korrektur:** Neue Credentials tragen die `kid` `<PUBLIC_BASE_URL>/badges/keys/<Schlüssel-ID>`; dieser öffentliche Endpunkt liefert **genau ein JWK** (`application/jwk+json`, EC P-256, nur öffentlicher Anteil). `/.well-known/jwks.json` (JWKS) und `/badges/issuer` bleiben. Die ältere `kid`-Form bleibt für Altbestand gültig und prüfbar. Der CLR-Datenmodell- und Inhaltscode wurde nicht geändert; die `kid` gilt für alle neu signierten Credentials.

### Ursache 2 (JSON): fehlender Proof — erwartetes Verhalten der JSON-Darstellung

Der Validator verlangt bei einem JSON-Credential einen eingebetteten Proof (`EmbeddedProofProbe`: `Ed25519Signature2020`, `DataIntegrityProof` mit `eddsa-rdfc-2022` oder `ecdsa-sd-2023`). Learny bietet **kein** eigenständig signiertes JSON-LD an.

| Darstellung | Prüfbar | Learny |
|---|---|---|
| A) signiertes Badge-**JWT** (externer Proof) | ja, kryptografisch | maßgeblicher Nachweis |
| B) **JSON** ohne Proof | nein — reine Datendarstellung desselben Inhalts | wird so gekennzeichnet: HTTP-Header `X-Credential-Representation: unsigned-json` und `X-Credential-Signed-Form: …/badge.jwt`, Hilfetext, Doku |
| C) JSON-LD mit **DataIntegrityProof** | ja | **nicht angeboten**; erforderte eine eigene Architekturentscheidung (RDF-Canonicalization und Ed25519/ECDSA-Cryptosuite ohne NuGet-Paket) |

Der Fehler „missing a proof“ bei der JSON-Datei ist deshalb erwartet und **keine Abweichung des JWT**. Es wurde kein `JwtProof2020` wieder eingeführt und nichts umbenannt.

### Algorithmus: RS256 ist nicht erforderlich

Der Validator erlaubt `alg ∈ {RS256, ES256}` (`ExternalProofProbe`: „MUST be RS256 or ES256“), liest JWKs vom Typ `RSA` und `EC` (P-256) und verifiziert ES256 mit `ECDSA256`. Die Spezifikation fordert RS256 „as a minimum“, der Validator akzeptiert aber ES256. **Es ist keine Umstellung nötig**; ADR 0038 bleibt (ES256).

### Unabhängige ES256-Prüfung (automatisiert)

Test ohne Learny-Verifier: Header (`alg ES256`, `typ JWT`, `kid`, keine weiteren Header), Claims (`iss`=`issuer.id`, `jti`=`id`, `nbf`=`validFrom`, `sub`=`credentialSubject.id`, Credential im Claim `vc`), Signatur mit .NET-`ECDsa` und dem JWK der `kid`-URL (IEEE-P1363), eindeutige Zuordnung `kid` ↔ JWKS-Eintrag, keine privaten Parameter (`d`, `p`, `q` …) in JWKS, Ausstellerprofil und `kid`-JWK, Ablehnung manipulierter Signatur, Header und Nutzlast, Struktur von Modul- und Master-Badge nach Open Badges 3.0, stabiler Fingerprint. Schlüsselrotation: Learny führt einen Schlüssel; die ältere `kid`-Form bleibt für Altbestand prüfbar (ein späterer Schlüsselwechsel bräuchte mehrere Einträge im JWKS und `/badges/keys/<ID>` je Schlüssel).

### Admin-Anzeige

Die Detailansicht trennt: Signaturformat, Signatur (lokal geprüft), Aussteller, Schlüssel-URL und Erreichbarkeit („Schlüssel nicht erreichbar: lokale Adresse … kein Signaturfehler“), Widerruf („Statusprüfung durch externe Prüfer nicht möglich“ bei lokaler Adresse), **externe 1EdTech-Prüfung: „Nicht geprüft“**. Eine lokale Prüfung gilt nie als externe Konformitätsprüfung.

### Stand der externen Prüfung

Neu ausgestellte Badges mit der korrigierten `kid` wurden **noch nicht** erneut beim Validator geprüft. Voraussetzung: öffentlich erreichbare HTTPS-Instanz (`PUBLIC_BASE_URL`), erreichbare `/badges/keys/<ID>`, `/.well-known/jwks.json` und `/api/public/revocation-list`. Das JSON bleibt beim Validator ohne Proof ein Fehler (siehe oben). Keine 1EdTech-Zertifizierung.

## Validierung vom 2026-10-29 (fünfte Runde): kid-404, Widerrufsliste, Achievement-URLs, CLR-JWT-Ausnahme

Quelle der Validator-Analyse: `1EdTech/digital-credentials-public-validator` (Stand `67532ac`, 2026-10-27), **nur gelesen**. Die gehostete Instanz `vc.1ed.tech/validate` antwortete aus der Ausführungsumgebung mit HTTP 500; ein lokaler Build des Fremdcodes wurde nicht ausgeführt. **Ein erneuter externer Lauf ist daher offen** (Anleitung: „Manuelle Abnahme“ oben). Keine 1EdTech-Zertifizierung.

### Bereits geprüfte Eigenschaften (Berichte vom 29.10.)

Master-Badge-JWT, CLR-JWT und drei eingebettete Badge-JWT: ES256-Signatur gültig; öffentlicher Schlüssel = JWKS-Eintrag; JSON und dekodierte JWT-Inhalte gleich. Diese Befunde sind automatisiert abgedeckt (Integrationstests „D1–D3“, „CLR-JWT B–E“, „kid: …“; unabhängige `ECDsa`-Prüfung ohne Learny-Verifier).

### 1. `kid`-URL `/badges/keys/{id}` lieferte 404 — Ursache belegt (Betriebs-/Konfigurationsfehler)

Die Route ist im Code vorhanden (`CertificateEndpoints.cs`). Die Live-Antwort (`GET https://learny.brors.date/badges/keys/bb771e1b1ffa0a67`) ist die **IIS-Detailseite „404.8 – Not Found“** (Hidden Segment) mit `Physical Path …\LearnCards\badges\keys\…`, nicht eine Antwort der Anwendung. Ursache: `web.config` sperrte per `requestFiltering/hiddenSegments` das Segment `keys` **an beliebiger Stelle der URL** — also auch `/badges/keys/…`. Routing, Authentifizierung und Schlüssel-ID waren korrekt (das JWKS liefert dieselbe `kid`); Tracking-Parameter spielen keine Rolle (mit/ohne identisch 404).

**Korrektur:** Das Segment `keys` ist nicht mehr gesperrt (`web.config`). Schlüsseldateien liegen ohnehin außerhalb des Webroots (ADR 0023) und bleiben über die Dateiendungen `.pem`/`.key`/`.initialized` sowie die Segmente `data`/`logs` (u. a. `data\keys`) gesperrt. Der Workflow-Schritt „Verify Secure Configuration“ prüft jetzt zusätzlich, dass `/badges/keys/<kid>` ein einzelnes EC-P-256-JWK ohne privaten Anteil liefert, das dem JWKS-Eintrag entspricht. **Wirksam erst nach dem nächsten Deployment** (nicht ausgelöst).

### 2. Widerrufsliste: `revoked_credentials` statt `revokedCredential`

Der Validator (`RevocationListProbe`) liest `revokedCredential`. Die Antwort enthält jetzt **beide** Felder aus demselben Register: `revokedCredential: [{id, revoked: true}]` (Validator) und `revoked_credentials: [{id, revocation_reason}]` (bestehende Clients). Master-Badge und CLR werden gemeinsam gelistet (ADR 0032), Modulnachweise nie automatisch. Ist die Liste nicht erreichbar, bleibt der Status „nicht überprüfbar“ (keine bestätigte Gesamtgültigkeit). Test: „Sperrliste: Feld revokedCredential …“.

### 3. Achievement-/Criteria-URLs (404)

`achievement.id` und `criteria.id` sind in Open Badges 3.0 URIs, die auflösbar sein *sollten*; ein 404 darauf ist kein Pflichtfehler des Validators. Learny veröffentlicht sie jetzt dennoch: `/badges/achievements/{moduleId}/{version}[/criteria]` und `/badges/achievements/topics/{topicId}/{version}[/criteria]` (JSON-LD; Browser erhalten eine lesbare Seite). Der Inhalt stammt **ausschließlich aus dem bei der Ausstellung signierten Credential** (historischer Stand, nichts aus aktuellen Einstellungen); veröffentlicht werden nur Typ, Name, Beschreibung, Version und Kriterientext — keine Kompetenzen, Ergebnisse, Personen- oder Lerninhalte. Unbekannte IDs: 404. Eigener Rate-Limit-Zähler. Test: „achievement.id und criteria.id sind öffentlich abrufbar …“.

### 4. Evidence-Endpunkt

`GET /api/public/certificates/{id}/evidence` ist unverändert (Test E3: Snapshot-Hash, keine Antworten/Lernendendaten).

### 5. CLR-JWT: Schemafehler und Java-Exception — Ursache aus dem Quelltext belegt

| Beobachtung | Ursache | Zuordnung |
|---|---|---|
| `$.credentialSubject.verifiableCredential: must be valid to one and only one schema, but 0 are valid` | `VCVersion.VCDMv2p0` ordnet `ClrCredential` dem **JSON**-Schema `clr_v2p0_clrcredential_schema.json` zu, auch für ein aus einem JWT extrahiertes Credential. Dort ist `ClrSubject.verifiableCredential` `oneOf [VerifiableCredential-Objekt, Array davon]`; eine kompakte JWS (String) ist **nicht** zulässig. CLR 2.0 B.2.2 (`ClrSubjectJwtPayload`) schreibt für die JWT-Form aber gerade `CompactJws` vor. Ein JWT-Payload-Schema existiert nicht (`…jwt_payload_schema.json` → HTTP 404). | **Validator** (falsche Schemaauswahl) |
| `Cannot invoke "java.util.List.stream()" because "JsonNodeUtil.asNodeList(…)" is null` | `CLR20Inspector.run` („embedded subject credentials“) übergibt jeden Eintrag von `$.credentialSubject.verifiableCredential` an `VerifiableCredential.Builder.build()`. Bei einem String-Knoten liefert `getJsonData().get("@context")` `null`; `VCVersion.of(null)` ruft `JsonNodeUtil.asNodeList(null).stream()` auf — `asNodeList(JsonNode)` gibt für `null` selbst `null` zurück → NullPointerException (als `NO_UNCAUGHT_EXCEPTIONS` gemeldet). Eingebettete Compact-JWS werden nicht als signierte AchievementCredentials erkannt und nicht einzeln geprüft. | **Validator** (fehlende JWT-Unterstützung, fehlende Null-Behandlung) |
| Hinweis auf erwartetes Muster | Die Referenzdatei `inspector-clr/src/test/resources/clr20/simple.jwt` enthält selbst eine Liste mit **einer kompakten JWS** in `credentialSubject.verifiableCredential`; `CLR20Tests` hat keinen JWT-Testfall. | Testlücke im Validator |

Kein Learny-Datenmodellfehler: Die Struktur wurde gegen CLR 2.0 §7.2 und B.2.1/B.2.2 geprüft (Test „CLR-JWT: Credential im Claim vc, drei eingebettete kompakte JWS …“). Das JSON enthält die eingebetteten Credentials als Objekte (schemakonform für die JSON-Form), das JWT als Compact JWS. **Das Learny-Modell wird nicht geändert, um den Validatorfehler zu umgehen.** Die Zuordnung beruht auf dem Quelltext, nicht auf einer Ausführung des Validators.

Learny-seitig ergänzt: `CertificateService.VerifyAsync` prüft eingebettete JWS eines CLR-JWT jetzt **einzeln** (`embedded_signatures_valid`, `embedded_count`); eine ungültige, beschädigte oder unvollständige eingebettete JWS macht das Gesamtergebnis ungültig, auch bei gültiger äußerer Signatur.

Fallmatrix (Learny-Tests `CLR-JWT …`): A CLR-JSON mit Objekten (unsigniert, kein Proof — erwartungsgemäß) · B CLR-JWT mit Compact-JWS → gültig · C ungültige eingebettete Signatur → ungültig · D Pflichtfeld `type` fehlt → ungültig · E beschädigte JWS → ungültig · Mischfall (eine gültige, eine fehlerhafte) → ungültig. Nicht automatisiert: der Lauf des Java-Validators.

### 6. Weitere Befunde der Berichte

- *CLR-JSON/CLR20Inspector „missing a proof“ (4×)*: erwartet für die unsignierte JSON-Darstellung (JWT-Profil, ADR 0038); kein künstlicher `DataIntegrityProof`.
- *CLR-JSON/OB30Inspector*: falscher Validator für den Typ `ClrCredential`; keine Modelländerung.

### 7. Verbleibende Einschränkungen

Die externe Gesamtprüfung des CLR-JWT bleibt wegen des Validatorfehlers offen; eingebettete Modul-JWT lassen sich einzeln mit dem `OB30Inspector` (JWT-Eingabe) prüfen. Issue-Entwurf: [reviews/2026-10-29-1edtech-clr-jwt-issue.md](reviews/2026-10-29-1edtech-clr-jwt-issue.md).

## Restabnahme 2026-10-29 (sechste Runde): offizieller 1EdTech-Validator tatsächlich ausgeführt

**Validator:** `1EdTech/digital-credentials-public-validator`, Commit `67532ac` (2026-10-27), lokal gebaut (`mvn -DskipTests clean install`, Maven 3.9.9, JDK 21) und über die dokumentierte REST-API gestartet (`GET /api/validators`, `POST /api/validate?validatorId=…` mit Multipart-Feld `file`; Spezifikation aus `/v3/api-docs`). Die öffentliche Instanz `vc.1ed.tech/validate` antwortete weiterhin mit HTTP 500. Alle Berichte liegen als JSON unter [reviews/1edtech-2026-10-29/](reviews/1edtech-2026-10-29/) (nur Testsubjekte, keine Personendaten).

**Aufbau:** Die Learny-Instanz lief lokal unter `http://localhost:5099` (`PUBLIC_BASE_URL` darauf gesetzt, damit der Validator `kid`, JWKS und Widerrufsliste erreicht), Testnachweis „Themenabschluss“ per Administrator ausgestellt (Master-Badge, CLR, drei eingebettete Badges). Die Produktionsinstanz `learny.brors.date` liefert **weiterhin** HTTP 404.8 für die kid-URL und `revoked_credentials` ohne `revokedCredential` — die Korrekturen sind dort **nicht deployt** (kein Deployment ausgelöst). Eine externe Prüfung gegen die Produktions-Credentials ist bis dahin nicht möglich.

| Fall | Eingabe / Validator | Ergebnis |
|---|---|---|
| A Master-Badge | JWT / `OB30Inspector` | **VALID** — 14 Probes, 0 Fatals, 0 Fehler, 0 Warnungen, 0 Exceptions (JWT-Profil, ES256-Signatur, Schlüsselauflösung über die kid-URL, Aussteller, Widerrufsliste) |
| B CLR | JWT / `CLR20Inspector` | **EXCEPTION** — 1 Fehler (`$.credentialSubject.verifiableCredential: must be valid to one and only one schema, but 0 are valid`) und 1 Exception (`Cannot invoke "java.util.List.stream()" because the return value of "JsonNodeUtil.asNodeList(JsonNode)" is null`); 11 Probes. Beides **reproduziert**. |
| C Eingebettete Badges (3×) | JWT / `OB30Inspector` | je **VALID** — 14 Probes, 0 Fehler/Warnungen/Exceptions |
| D Master-Badge | JSON / `OB30Inspector` | ERROR: „The verifiable credential is missing a proof.“ (erwartet: unsignierte JSON-Darstellung, kein erfundener Proof) |
| E CLR | JSON / `CLR20Inspector` | 4× „missing a proof“ (Master + 3 eingebettete), **kein Schemafehler**, keine Exception; 50 Probes — die JSON-Form mit Credential-Objekten besteht das CLR-Schema |
| F Widerruf | Master-Badge-JWT nach Widerruf / `OB30Inspector` | **FATAL „Credential has been revoked“** — der Validator liest `revokedCredential` der Learny-Liste korrekt |
| Kontrolle | Referenzdatei `clr20/simple.jwt` des Validators selbst / `CLR20Inspector` | gleiche Exception und gleicher Schemafehler (die Datei ist ein v1.1-AchievementCredential mit eingebetteter JWS, also ein unvollkommener Kontrollfall — sie belegt aber, dass die Exception nicht an Learny-Daten hängt) |

**Ursache B (belegt durch Lauf + Quelltext, siehe oben „fünfte Runde“):** Der Vergleich D/E ↔ B isoliert sie: Dieselben Credentials bestehen die CLR-Schemaprüfung als JSON-Objekte (E) und scheitern als CLR-JWT mit Compact-JWS (B), obwohl CLR 2.0 B.2.2 für die JWT-Form gerade Compact-JWS vorschreibt; die nachfolgende Exception entsteht beim Aufbau eines `VerifiableCredential` aus dem String-Knoten (`VCVersion.of(null)`). Die eingebetteten Badges bestehen einzeln (C). Learny-Daten wurden nicht geändert.

**Status der externen Konformität:** Master-Badge-JWT und alle eingebetteten Modul-JWT bestehen den offiziellen OB30Inspector. Für das CLR-JWT gilt: *Interne Prüfung bestanden; externe 1EdTech-Konformitätsprüfung wegen dokumentiertem Validatorfehler nicht abschließend möglich.* Eine vollständige externe Abnahme wird **nicht** behauptet. Issue-Entwurf: [reviews/2026-10-29-1edtech-clr-jwt-issue.md](reviews/2026-10-29-1edtech-clr-jwt-issue.md).

**Öffentliche Endpunkte der Produktionsinstanz (Stand 2026-10-29, vor Deployment):** `/.well-known/jwks.json` 200 `application/json` (JWKS, ein EC-P-256-Schlüssel, kid `bb771e1b1ffa0a67`); `/badges/issuer` 200 (Profil, JWKS eingebettet); `/api/public/revocation-list` 200 (nur `revoked_credentials`); `/badges/keys/bb771e1b1ffa0a67` **404 (IIS 404.8)**. Lokal (korrigierte Fassung): kid-URL 200 `application/jwk+json` mit einem JWK, identisch zum JWKS-Eintrag; Liste mit `revokedCredential` und `revoked_credentials`.


**Hinweis (ADR 0040):** Die OpenTimestamps-Stempelung benötigt keinen externen Client mehr (nativer Client in der Anwendung); das berührt die 1EdTech-Validierung nicht, die Credentials und ihre Signaturen bleiben unverändert.


### Offene produktive Punkte (Stand 2026-10-29, unabhängig vom OpenTimestamps-Auftrag)

OpenTimestamps betrifft nur den Zeitnachweis der PDF-Pakete und behebt **keinen** der folgenden Punkte; sie bleiben offen: (1) die produktive kid-URL `/badges/keys/<id>` liefert wegen der IIS-Sperre des Segments `keys` HTTP 404.8 (Korrektur im Repository, nicht deployt); (2) die produktive Widerrufsliste enthält noch nicht das vom Validator erwartete Feld `revokedCredential` (Korrektur im Repository, nicht deployt); (3) das CLR-JWT scheitert im offiziellen 1EdTech-Validator wegen eines dokumentierten Validatorfehlers; (4) eine produktive Gesamtvalidierung steht aus. Es wird keine vollständige 1EdTech-Konformität behauptet. An Credentials und CLR-JWTs wurde in diesem Auftrag nichts geändert.
