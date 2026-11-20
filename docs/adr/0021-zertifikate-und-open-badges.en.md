# 0021 — Certificates and Signed Open Badges 3.0

- **Status:** Accepted (foundation implemented; parts of the UI still pending; see “Open Questions”)
- **Date:** 2026-10-26
- **Supplements:** [0019](0019-einheitliches-quizsystem.en.md) (verified results), [0011](0011-mehrbenutzerarchitektur.en.md), [0015](0015-freigaben-globale-lernmodule.en.md) (permissions), [0022](0022-polygon-verankerung-und-kostenschranke.en.md)
- **Related:** [0002](0002-keine-nuget-pakete.en.md)

## Context

A verifiable completion credential should be issued when a module quiz is passed. The request assumed a prior “Task 3/3,” which was not present in the repository; this ADR therefore defines the foundation.

## Decision

1. **Trigger:** Only `QuizService` grades quizzes. After a **passed, session-verified module quiz** (with `session_id`, not a card quiz), it calls `CertificateService.TryIssueAsync`. Issuance failures must never invalidate the quiz result. Issuance is idempotent for each (user, module, module version); a new global version receives a separate certificate.
2. **Format:** Open Badges 3.0 / W3C Verifiable Credential as **VC-JWT** (compact JWS, ES256 = ECDSA P-256, using built-in .NET features, no third-party packages). Credential types `VerifiableCredential` + `OpenBadgeCredential`; `credentialSubject` is an `AchievementSubject` with achievement, result, and learner name. `credentialStatus` is `1EdTechRevocationList` (`/api/public/revocation-list`). Issuer profile: `/badges/issuer`; public key as JWK: `/.well-known/jwks.json`. Downloads include the credential and `proof.jwt`. The verification hash is the SHA-256 hash of the JWS.
3. **Keys:** The private signing key comes only from `BADGE_SIGNING_KEY_PEM` or `BADGE_SIGNING_KEY_FILE` (secret management/Key Vault). If not configured, a key is initially generated as a file in the persistent data directory and startup logs a warning; production must use and back up a secret-managed key. Never store the private key in the database or logs. Rotation replaces the key (new `kid`); old badges remain verifiable with their retained old public key (multi-key verification still pending).
4. **Verification:** `POST /api/public/verify` checks signature (ES256 only, never `alg: none`), issuer, integrity (JSON equals the signed payload), registration, and revocation. Responses do not disclose learner data. Independently of Learny, the signature (JWK) and Merkle proof (ADR 0022) can be verified.
5. **Revocation:** Administrators only, with mandatory reason. The badge remains recorded but is treated as revoked on every verification; anchoring is retained.
6. **Permissions and privacy:** Learners see only their own certificates. **Auditors** can see only certificates for **globally published** modules (ADR 0015, which also grants access to global results); never quiz answers, chats, private modules, or merely shared modules. Every access/verification is recorded in verification history. **Administrators** manage certificates (number, status, anchoring) but cannot see learner identities or names of private modules. Public pages and public status omit learner data (module names only for global modules). MCP tokens cannot call these endpoints.
7. **Data model:** `certificates`, `anchor_batches`, and `app_settings` tables (see `database-schema.md`); `certificates.user_id` uses `ON DELETE CASCADE`.

## Open Questions

- Services, API, and tests have been implemented. **Not yet implemented:** role-specific sidebar, dashboards, certificate/badge pages, QR codes, printable view, verification page, auditor/admin UI, and help-page sections.
- Multi-key verification following key rotation; Key Vault storage is documented but not automated.
