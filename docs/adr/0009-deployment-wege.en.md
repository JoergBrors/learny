# 0009 — Deployment: IIS by Tag, Docker, Azure

- **Status:** Accepted
- **Supplemented by:** [0024](0024-docker-hub-veroeffentlichung.en.md) (Docker Hub images and Compose installation package as another distribution channel), [0023](0023-secrets-und-badge-schluessel-unter-iis.en.md) (IIS: secrets outside the web root)
- **Date:** 2026-10-24
- **Supplemented by:** [0011](0011-mehrbenutzerarchitektur.en.md), [0012](0012-rollen-aus-entra-app-roles.en.md), [0013](0013-mcp-benutzergebundene-anmeldung.en.md) (new configuration values, see below); [0015](0015-freigaben-globale-lernmodule.en.md) requires no new settings.

## Context

The application runs in three environments: Windows/IIS (self-hosted runner), Docker on Linux, and Azure App Service.

## Decision

- **IIS:** GitHub Action `.github/workflows/deploy-iis-on-tag.yml` builds Web and MCP packages (`dotnet publish`) on a Git tag and deploys them to an IIS runner. Non-sensitive values come from `deploy/iis/deployment.json`; secrets from GitHub `Secrets`/`Variables`. Templates: `deploy/iis/web.env`, `deploy/iis/mcpsettings.json`.
- **Docker:** `docker-compose.yml` (Postgres + Web; profiles `mcp`, `production` with nginx).
- **Azure:** App Service with an external PostgreSQL database (`docs/azure-deployment.md`).
- All environments use environment variables for configuration; `.env` is local only (do not commit it).
- **Local development:** `docker-compose.dev.yml` (PostgreSQL only, port 55432) and launch configurations in `.vscode/launch.json`/`tasks.json`; see [local-development.md](../local-development.en.md).
- **New values since ADRs 0011–0013:** `ADMIN_SUBJECTS`, `OIDC_ROLE_MAP`, `MCP_PUBLIC_URL`, `MCP_TOKEN_DAYS` (Web), and `LEARNCARDS_WEB_PUBLIC_URL`, `LEARNCARDS_MCP_TOKEN` (MCP server). The IIS workflow **does not render these yet** (`deploy/iis/web.env` is overwritten on every deployment): set them as IIS environment variables or extend the workflow. The workflow also still requires the unused `MCP_OAUTH_SIGNING_KEY` secret (a placeholder is sufficient).

## Consequences

- Configuration changes must be maintained in several templates.
- Creating a tag triggers production deployment.

## Guardrails for Changes

- **Do not create or push tags** or trigger deployments without an explicit instruction.
- New environment variable → update `AppConfig.cs`, `.env.example`, `deploy/iis/web.env`, `docker-compose.yml`, `docs/*-deployment.md`.
- Do not put secrets in workflows, templates, or documentation.
- Build outputs (`bin/`, `obj/`, `publish-web-test/`) are not source files; do not edit them manually.

## Affected Code

- `.github/workflows/*`, `deploy/**`, `docker-compose.yml`, `nginx/*`, `src/*/Dockerfile`, `docs/*-deployment.md`
