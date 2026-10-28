# 0021 — Zertifikate und signierte Open Badges 3.0

- **Status:** Akzeptiert (Grundlage umgesetzt; Oberfläche teilweise offen, siehe „Offene Punkte“)
- **Ergänzt durch:** [0031](0031-kompetenzmodell-und-pruefungssnapshots.md) (Kompetenznachweise, Prüfungssnapshot im Credential)
- **Datum:** 2026-10-26
- **Ergänzt:** [0019](0019-einheitliches-quizsystem.md) (verifiziertes Ergebnis), [0011](0011-mehrbenutzerarchitektur.md), [0015](0015-freigaben-globale-lernmodule.md) (Rechte), [0022](0022-polygon-verankerung-und-kostenschranke.md)
- **Bezug:** [0002](0002-keine-nuget-pakete.md)

## Kontext

Nach bestandenem Modulquiz soll ein prüfbarer Abschlussnachweis entstehen. Der Auftrag setzte „Auftrag 3/3“ voraus; er war im Repository nicht vorhanden, daher legt diese ADR die Grundlage fest.

## Entscheidung

1. **Auslöser:** Nur `QuizService` bewertet. Nach einem **bestandenen, sitzungsverifizierten Modulquiz** (`session_id` gesetzt, kein Kartenquiz) ruft er `CertificateService.TryIssueAsync` auf. Fehler dabei gefährden das Quizergebnis nie. Ausstellung ist idempotent je (Benutzer, Modul, Modulversion); eine neue globale Version erhält ein eigenes Zertifikat.
2. **Format:** Open Badge 3.0 / W3C Verifiable Credential als **VC-JWT** (kompakte JWS, ES256 = ECDSA P-256, mit den Bordmitteln von .NET, kein Fremdpaket). Typen `VerifiableCredential`+`OpenBadgeCredential`, `credentialSubject` = `AchievementSubject` mit Achievement, Ergebnis und Name der lernenden Person;
   `credentialStatus` = `1EdTechRevocationList` (`/api/public/revocation-list`). Aussteller-Profil `/badges/issuer`, öffentlicher Schlüssel als JWK unter `/.well-known/jwks.json`. Der Download enthält das Credential plus `proof.jwt`. Der Prüf-Hash ist SHA-256 der JWS.
3. **Schlüssel:** Privater Signaturschlüssel nur aus `BADGE_SIGNING_KEY_PEM` bzw. `BADGE_SIGNING_KEY_FILE` (Secret-Verwaltung/Key Vault); Ohne Konfiguration wird der Schlüssel einmalig als Datei im (persistenten) Datenverzeichnis erzeugt; im Betrieb meldet der Start dazu eine Warnung — für Produktion den Schlüssel aus der Secret-Verwaltung setzen und sichern. Nie in der Datenbank, nie in Logs. Rotation: neuen Schlüssel setzen (neue `kid`); alte Badges bleiben mit dem alten öffentlichen Schlüssel prüfbar, solange dieser aufbewahrt wird (Mehrschlüssel-Prüfung ist offen).
4. **Prüfung:** `POST /api/public/verify` prüft Signatur (nur ES256, kein `alg: none`), Aussteller, Integrität (JSON = signierte Nutzlast), Registrierung und Widerruf; die Antwort enthält keine Lernendendaten. Unabhängig von Learny prüfbar sind Signatur (JWK) und der Merkle-Nachweis (siehe 0022).
5. **Widerruf:** Nur Administratoren, nur mit Begründung; das Badge bleibt, gilt aber bei jeder Prüfung als widerrufen; die Verankerung bleibt bestehen.
6. **Rechte und Datenschutz:** Lernende sehen nur eigene Zertifikate. **Auditoren** sehen ausschließlich Zertifikate **global veröffentlichter** Module (ADR 0015: dort lesen sie auch Ergebnisse); keine Quizantworten, Chats, privaten oder nur freigegebenen Module. Jede Einsicht/Prüfung steht in der Verifikationshistorie.
   **Administratoren** verwalten Zertifikate (Nummer, Status, Verankerung); Lernende und Modulnamen privater Module sehen sie nicht. Öffentliche Seiten und der öffentliche Status zeigen keine Lernendendaten (Modulname nur bei globalen Modulen). MCP-Tokens erreichen diese Endpunkte nicht.
7. **Datenmodell:** Tabellen `certificates`, `anchor_batches`, `app_settings` (siehe database-schema.md); `certificates.user_id` ist `ON DELETE CASCADE`.

## Offene Punkte

- Umgesetzt sind Dienste, API und Tests. **Noch nicht umgesetzt:** rollenabhängige Seitenleiste, Dashboards, Zertifikats-/Badge-Seiten, QR-Code, Druckansicht, Prüfseite, Auditor- und Admin-Oberfläche, Hilfeseiten-Abschnitte (Folgearbeit).
- Mehrschlüssel-Prüfung nach Schlüsselrotation; Schlüsselspeicherung im Key Vault ist dokumentiert, nicht automatisiert.

## Nachtrag (ADR 0038)

Signaturprofil und `kid` neu ausgestellter Credentials: siehe [0038](0038-jwt-profil-fuer-open-badges-und-clr.md) (Credential im JWT-Claim `vc`, `kid` = URL eines einzelnen JWK). Die Aussagen zu `proof.jwt`/`JwtProof2020` in diesem ADR gelten nur noch für Altbestand.
