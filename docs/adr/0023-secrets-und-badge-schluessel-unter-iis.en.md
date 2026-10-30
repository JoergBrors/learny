# 0023 — Secrets and Badge Keys under IIS: Outside the Web Root, Initialization Marker, Fail-closed

- **Status:** Accepted (practical IIS test pending)
- **Date:** 2026-10-26
- **Supplements:** [0009](0009-deployment-wege.en.md), [0021](0021-zertifikate-und-open-badges.en.md), [0014 (proposal)](../reviews/2026-10-24-mehrbenutzer-pruefbericht.en.md)
- **Related:** [0002](0002-keine-nuget-pakete.en.md), [0005](0005-authentifizierung.en.md)

## Context

The IIS deployment placed the rendered `.env` file in the web root and mirrored that directory with `robocopy /MIR`. This could delete `data\` (database, Data Protection keys, badge keys) during every deployment. A missing badge key was silently regenerated, invalidating verification of all previously issued badges. PEM import under IIS also failed (HTTP 500.30, CNG).

## Decision

1. Store `.env` and badge keys under `IIS_CONFIG_ROOT` (default `%ProgramData%\LearnCards\<Site>`) in `config\` and `keys\`, never in the web root. `ENV_FILE` (app-pool environment variable) points to `.env`; `BADGE_SIGNING_KEY_FILE` is configured inside `.env`.
2. NTFS: disable inheritance; grant SYSTEM, administrators, and the deployment account full control. Grant the app pool read-only access (the keys directory is writable only during initial setup). On the web root, the app pool has read/execute rights; writes are limited to `data` and `logs`.
3. Use `robocopy /MIR /XD data logs`; exclude `.env` and `*.pem` from backups.
4. **Initialization marker:** `badge-signing-key.pem.initialized` (key ID, not a secret). Generate a missing key only if no marker exists (fresh install), atomically (temporary file, move without overwrite). Reload and verify with a signature self-test. If a marker exists, the key ID differs, or certificates already exist in the database, fail startup (**fail closed**; no silent rotation).
5. Import keys with `ECParameters` (no NCrypt key store); set “Load User Profile” = True. Request Filtering denies `.env`, `.pem`, `.key`, `.initialized`, `.db` (IIS already blocks `.config` by default), and `data`, `logs`, `keys`.
6. Before startup, the deployment checks permissions; afterward, it tests HTTP access to sensitive paths and checks the key ID against the marker. Findings cause deployment failure.

## Consequences

- Existing keys are copied, never deleted or rotated; issued badges remain valid. Rotation must be a deliberate manual action.
- If the marker (not the key) is lost, the key can be replaced only after checking the database. Therefore the marker must be backed up.
- A real test under the IIS app-pool identity is possible only after the first deployment.
