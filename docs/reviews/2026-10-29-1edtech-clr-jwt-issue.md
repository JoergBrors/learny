# Entwurf: GitHub-Issue für 1EdTech/digital-credentials-public-validator

**Titel:** CLR 2.0 JWT with embedded compact JWS in `credentialSubject.verifiableCredential` fails JSON-schema validation and throws NullPointerException

**Version:** `main` @ `67532ac` (2026-10-27) — nur Quelltext analysiert, nicht ausgeführt.

## Erwartet

Ein CLR-2.0-Credential in der JWT-Form (VC-JWT, Claim `vc`), dessen `credentialSubject.verifiableCredential` laut CLR 2.0 Abschnitt B.2.2 (`ClrSubjectJwtPayload`, Typ `CompactJws`) kompakte JWS enthält, wird akzeptiert; die eingebetteten JWS werden als signierte AchievementCredentials erkannt und einzeln geprüft (wie die Referenzdatei `inspector-clr/src/test/resources/clr20/simple.jwt` sie enthält).

## Tatsächlich

1. `JsonSchemasProbe` meldet `$.credentialSubject.verifiableCredential: must be valid to one and only one schema, but 0 are valid`: `VerifiableCredential.VCVersion.VCDMv2p0` mappt `ClrCredential` immer auf `Catalog.CLR_20_CLRCREDENTIAL_JSON` (`clr_v2p0_clrcredential_schema.json`); dort ist `ClrSubject.verifiableCredential` `oneOf [VerifiableCredential (object), array of VerifiableCredential (object)]` — Strings sind unzulässig. Für die JWT-Form existiert kein Schema und kein eigener Zweig.
2. Danach `NullPointerException: Cannot invoke "java.util.List.stream()" because the return value of "JsonNodeUtil.asNodeList(JsonNode)" is null`: `CLR20Inspector.run` (Abschnitt „embedded subject credentials“) übergibt jeden Eintrag (TextNode) an `VerifiableCredential.Builder.build()` → `VCVersion.of(jsonData.get("@context"))` mit `null` → `JsonNodeUtil.asNodeList(null)` liefert `null` → `.stream()`.

## Minimalfall

Äußeres CLR-JWT (`alg ES256`, `typ JWT`, Claim `vc` mit `type: [VerifiableCredential, ClrCredential]`, `@context` VC v2 + CLR 2.0.1, `credentialSubject.verifiableCredential: ["<compact JWS eines OpenBadgeCredential>"]`) mit `CLR20Inspector` prüfen. `CLR20Tests` enthält keinen JWT-Fall; die Referenzdatei `clr20/simple.jwt` enthält selbst eine eingebettete kompakte JWS.

## Vorschlag

(a) Für aus JWT extrahierte Credentials (`getProofType() == EXTERNAL`) ein JWT-Payload-Schema bzw. eine eigene Schemaauswahl; (b) in `CLR20Inspector` String-Knoten als Compact JWS erkennen (`fromJwt`) und mit `OB30Inspector` samt externer Signaturprüfung prüfen; (c) `JsonNodeUtil.asNodeList(JsonNode)` bzw. `VCVersion.of` null-sicher machen und eine Probe-Meldung statt einer Exception liefern.

## Quellen

CLR 2.0 §7.2, B.2.1, B.2.2 (https://www.imsglobal.org/spec/clr/v2p0/) · Schema https://purl.imsglobal.org/spec/clr/v2p0/schema/json/clr_v2p0_clrcredential_schema.json
