# 0024 — Publishing Docker Hub Images with a Compose Installation Package

- **Status:** Accepted (initial release pending)
- **Date:** 2026-10-26
- **Supplements:** [0009](0009-deployment-wege.en.md) (an additional distribution method; existing methods remain unchanged)
- **Related:** ADRs [0002](0002-keine-nuget-pakete.en.md), [0003](0003-eine-deploybare-einheit.en.md), [0007](0007-mcp-server.en.md), [0013](0013-mcp-benutzergebundene-anmeldung.en.md), [0017](0017-konfigurierbare-bildspeicher.en.md), [0021](0021-zertifikate-und-open-badges.en.md), [0023](0023-secrets-und-badge-schluessel-unter-iis.en.md); review finding F1 / proposed ADR 0014 (operating mode).

## Context

Users should be able to operate Learny without source code, Git, or the .NET SDK. Previously, only source builds (`docker-compose.yml` with `build:`), tag-based IIS deployment, and Azure were supported.

## Decision

1. **Two public Docker Hub images** under personal account `joergb23`: `joergb23/learny-web` (Web + API in one unit, ADR 0003) and `joergb23/learny-mcp` (independent API client, ADR 0007). Platforms: `linux/amd64`, `linux/arm64`. Tags: `MAJOR.MINOR.PATCH[-Prerelease]` and `latest`; publish `latest` only for stable, non-prerelease releases.
2. **Publish only via GitHub Release** (`.github/workflows/publish-docker.yml`): validate SemVer → build and integration tests → amd64 smoke tests (health, non-root, no keys/databases/configuration in images) → multi-architecture push → verify platforms and `latest` → update Docker Hub descriptions (separate `continue-on-error` step with independent result) → attach `learny-docker-<version>.zip` installation package to the release. Pin external Actions to commit SHAs. Authenticate only through `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` secrets. This workflow creates no tags and triggers no other deployments; the IIS workflow remains unchanged.
3. **Images:** Multistage builds use the SDK only at build time (built on the build platform, published platform-independently), `aspnet:10.0` for both runtime images (MCP uses ASP.NET Core for HTTP), non-root user `app` (UID 1654), no secrets or data baked into images, OCI labels, and `wget` installed solely for health checks (the base image has neither `wget` nor `curl`). Configure everything using environment variables; `/app/data` is the only writable directory (Data Protection keys, badge keys/markers from ADR 0023, local images, optionally SQLite).
4. **Installation package `deploy/docker/`:** `docker-compose.yml` (Hub images only; profiles `db`, `mcp`, `proxy`), documented `.env.example`, `README.md` with instructions and complete variable table, `README-web.md`/`README-mcp.md` for Docker Hub descriptions, `mcpsettings.example.json`, nginx template. Existing source-build Compose file at repository root and `nginx/` stay available to developers.
5. **Secure defaults:** Package uses `AUTH_MODE=oidc` (not `auto`, ADR 0014). PostgreSQL has no published port. Web/MCP bind only to `127.0.0.1` and are exposed through an HTTPS reverse proxy (the Web app trusts `X-Forwarded-*` from all sources). PostgreSQL password is mandatory with no default. Do not place secrets in YAML or examples. `DockerConfigTests` checks consistency of environment variables across Compose, `.env.example`, docs, and code.
6. **MCP OAuth:** The issuer (public MCP URL) is defined in mounted `mcpsettings.json` because the MCP server has no corresponding environment variable. No new variable names were introduced deliberately.

## Consequences

- Publishing a release on a **new** tag creates that tag and thereby triggers the IIS workflow (trigger: `*`). Always create releases using existing tags; this warning appears in the workflow and `deploy/docker/README.md`.
- Updating the Docker Hub description by API requires a token with read/write/delete permissions; without these permissions images remain published and the step reports the limitation.
- Anyone can pull public images; they must never contain configuration (enforced by smoke tests and Dockerfiles).
- Open security concern at the time (review F1): with `AUTH_MODE=auto` or misspellings and absent OIDC settings, the application silently fell back to `dev`, without considering `ASPNETCORE_ENVIRONMENT`. The package defaults to `oidc`; code-level hardening was recommended in ADR 0014. See later ADR 0026 for the implemented mitigation.

## Guardrails for Changes

- New environment variables must be added together to `Infrastructure/*`, `deploy/docker/docker-compose.yml`, `deploy/docker/.env.example`, and the variable table in `deploy/docker/README.md` (otherwise tests fail).
- Never put secrets, databases, or keys into images. Never publish `latest` for prereleases. Pin Actions to commit SHAs.
