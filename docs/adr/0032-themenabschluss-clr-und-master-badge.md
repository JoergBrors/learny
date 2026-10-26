# 0032 — Themenabschluss mit CLR 2.0 und Master-Open-Badge

- **Status:** Akzeptiert
- **Datum:** 2026-10-26
- **Ersetzt teilweise:** [0027](0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md) (dort war „keine Themenzertifikate“ festgelegt)
- **Ergänzt:** [0021](0021-zertifikate-und-open-badges.md), [0031](0031-kompetenzmodell-und-pruefungssnapshots.md), [0030](0030-einheitliche-modul-und-themenversionierung.md)

## Kontext

Module stellen nach verifiziertem Quiz Open Badges aus (ADR 0021/0031). Für Themen (Lernpfade mit Modul- und Abschlussprüfungen) fehlte ein überprüfbarer Gesamtnachweis. Grundlagen: 1EdTech **CLR Standard 2.0** und **Open Badges 3.0** (JSON-LD-Kontexte `https://purl.imsglobal.org/spec/clr/v2p0/context-2.0.1.json`, `…/ob/v3p0/context-3.0.3.json`, W3C Verifiable Credentials 2.0). Die Umsetzung braucht weder Mitgliedschaft noch Konformitätszertifizierung noch Pakete (ADR 0002).

## Entscheidung

1. **Abschlussbedingung** (`TopicCredentialService.EnsureCompletionAsync`): Eine veröffentlichte Themenversion gilt für einen Benutzer als abgeschlossen, wenn alle Module der Version abgeschlossen sind (Karten gelernt, aktive Modulprüfungen bestanden), die Abschlussprüfung bestanden ist (falls vorgesehen) und mindestens eine serverseitig verifizierte Prüfung existiert. Alle Leistungen beziehen sich auf die eingefrorenen Modulkopien dieser Version (Modulversionen, ADR 0030) und denselben Benutzer; Bewertung nur im `QuizService`. Module ohne aktive Modulprüfung erhalten kein Modulzertifikat (nichts Verifizierbares) und stehen im Snapshot ohne Ergebnis.
2. **Modulzertifikate in Themen.** Bestandene Modulprüfungen in Themen stellen Modulzertifikate aus (`QuizService.IssueModuleCredentialAsync`, Scope `topic`, Version = Modulversion der eingefrorenen Kopie, inkl. Kompetenznachweisen nach ADR 0031). Die Abschlussprüfung des Themas erzeugt kein Modulzertifikat. Nachträglich (ältere Ergebnisse) werden sie aus den gespeicherten bewerteten Antworten ausgestellt.
3. **Eine Zertifikatsidentität.** Der Themenabschluss ist eine Zeile in `certificates` (`kind = 'topic'`; `module_id`/`module_name`/`module_version` = Thema; Zertifikatsnummer, Widerruf, Blockchain-Verankerung wie bisher). `badge_jws` ist das **Master-Badge**, `clr_jws` das **CLR**; Prüfungssnapshot `learny-topic-completion-snapshot` (`exam_snapshots.kind = 'topic'`) hält Module, Ergebnisse, Kompetenzstatus und Abschlussprüfung fest. Eindeutig je Benutzer, Thema und Themenversion; wiederholter Abruf stellt nichts neu aus.
4. **Abschlusszeitpunkt.** `completed_at` ist der Zeitpunkt, an dem die letzte Voraussetzung erfüllt war (spätester *erster* Prüfungserfolg bzw. spätestes Markieren einer Karte als gelernt) — nicht die letzte beliebige Aktivität — und wird mit der Ausstellung unveränderlich gespeichert. Ausgestellt wird beim Bestehen einer Themenprüfung sowie beim Öffnen von „Meine Abschlüsse“ (`GET /api/certificates/`, idempotent).
5. **Master-Badge** (`OpenBadgeCredential`, VC 2.0): Achievement „Erfolgreicher Themenabschluss“ (`achievementType: CertificateOfCompletion`), Kriterium „Alle erforderlichen Module der angegebenen Themenversion erfolgreich bestanden.“ (plus „Zusätzlich wurde die verpflichtende Abschlussprüfung bestanden.“), Themenversion als `version`, `activityEndDate`, Ergebnis, `evidence` mit Verweisen auf die Modulzertifikate und den Snapshot (Hash). Es verweist **nicht** auf das CLR.
6. **CLR** (`ClrCredential`): `@context` = [VC 2.0, CLR 2.0.1, OB 3.0.3], `credentialSubject` vom Typ `ClrSubject` mit `achievement` (Thema und Module), `verifiableCredential` (Master-Badge und alle Modulabschlüsse als signierte Credentials) und `association` (Modul `isChildOf` Thema). Das CLR umschließt, die Badges referenzieren es nicht (keine Zirkel). Signiert wie die Badges (VC-JWT, ES256, `proof.jwt`); Download `GET /api/certificates/{id}/clr`. Widerruf des Themenabschlusses widerruft Master-Badge und CLR (Sperrliste: `/verify/{id}` und `/verify/{id}/clr`); die Modulabschlüsse bleiben einzeln gültig.
7. **IdentityObject.** Neu ausgestellte Credentials schreiben den Namen in `identityHash` (Pflichtfeld des OB-3.0-Schemas, bei `hashed: false` im Klartext); frühere Badges mit `identity` bleiben unverändert signiert.

## Konsequenzen

- Die Konformität wurde gegen Pflichtfelder, Enums und Kontextregeln der offiziellen 1EdTech-JSON-Schemas geprüft (Tests); es gibt **keine** offizielle 1EdTech-Zertifizierung und kein vollständiger JSON-Schema-Validator ist Teil des Projekts. Interoperabilität mit fremden Validatoren/Wallets ist nicht getestet.
- Themenabschlüsse hängen vom Zustand der Prüfungsversuche ab; Altdaten ohne Versuche erhalten keinen Abschluss.
- Themenzertifikate sind nicht für Auditoren sichtbar (Scope `topic`), bis eine Freigaberegel entschieden ist.

## Leitplanken für Änderungen

- Credentials und Snapshots nie nachträglich ändern; neue Inhalte → neue Version/neues Zertifikat.
- Nur verifizierte, dem Benutzer gehörende Ergebnisse der eingefrorenen Themenversion zählen; keine zweite Bewertungs-Engine.
- Keine Zirkelverweise Master ↔ CLR; keine personenbezogenen Daten im Snapshot.
- Tests: `TopicCredentialTests.cs`.

## Betroffener Code

- `Services/Certificates/TopicCredentialService.cs`, `CertificateService.Topic.cs`, `Services/TopicService.Credentials.cs`, `Services/QuizService.cs` (`IssueModuleCredentialAsync`)
- `Api/CertificateEndpoints.cs` (`/api/certificates/{id}/clr`), `Domain/Certificates.cs`, `Data/Schema.cs`

## Nachtrag (2026-10-26): Befunde der 1EdTech-Verifier

Der Open-Badges-3.0- und der CLR-2.0-Verifier meldeten für heruntergeladene JSON-Dateien: (1) „Undefined JSON-LD term: 1EdTechRevocationList“ — der veröffentlichte OB-3.0.3-Kontext definiert den Begriff nicht; neu ausgestellte Credentials tragen deshalb eine **lokale Kontext-Map** (`{"1EdTechRevocationList": "https://purl.imsglobal.org/spec/ob/v3p0/vocab.html#1EdTechRevocationList"}`) als letzten `@context`-Eintrag (laut OB-Schema zulässig; sie steht im signierten Payload). (2) Eine Ausnahme im Verifier, weil die Hülle `proof { type: JwtProof2020, jwt }` wie ein Data-Integrity-Proof gelesen wird und `proofPurpose` fehlte; die Hülle trägt jetzt zusätzlich `proofPurpose`, `verificationMethod` und `created` (außerhalb der Signatur, die Signatur bleibt das unveränderte `jwt`). Die standardkonforme VC-JWT-Form sind die Downloads `badge.jwt` und `clr.jwt` (kompaktes JWS). Bereits ausgestellte, unveränderliche Credentials behalten ihren ursprünglichen Kontext (ihre JSON-Hülle erhält aber die neuen Proof-Felder). (3) Die Warnung „issuer url http://localhost:5000 is not accessible“ ist Konfiguration: `PUBLIC_BASE_URL` muss auf die öffentliche Adresse zeigen. Eine bestandene 1EdTech-Prüfung wird weiterhin nicht behauptet.
