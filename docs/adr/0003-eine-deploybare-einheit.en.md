# 0003 — Blazor Server and REST API in a Single Deployable Unit

- **Status:** Accepted
- **Date:** 2026-10-24
- **Supplemented by:** [0011](0011-mehrbenutzerarchitektur.en.md) (user context in components and API), [review report](../reviews/2026-10-24-mehrbenutzer-pruefbericht.en.md), [0015](0015-freigaben-globale-lernmodule.en.md) (sharing/publication logic in `SharingService`; UI and API are access layers only)

## Context

The original solution consisted of a FastAPI backend, React frontend, and reverse proxy (including CORS and SSE edge cases).

## Decision

`LearnCards.Web` combines the Blazor Web App (interactive server components), Minimal API under `/api`, and OIDC endpoints under `/auth/*` in **one** process. Services are singletons (`Program.cs`): `IDatabase`, `CardRepository`, `OpenAiClient`, `QuizService`, `ChatService`, and `OidcService`.

## Consequences

- No CORS, one deployment artifact, and simple local development.
- Blazor Server maintains one SignalR connection per user; horizontal scaling requires sticky sessions.
- The REST API remains available for external clients (MCP, automation) and compatible with the original Python implementation.

## Guardrails for Changes

- Business logic belongs in `Services/` or `CardRepository`, **not** in `.razor` files or endpoint lambdas.
- New feature → service method, then an endpoint in `Api/ApiEndpoints.cs` if external access is required, with the UI built on top.
- Register new services as singletons in `Program.cs`; **never store per-user state in singletons, caches, or static fields** (currently satisfied according to the review report). Always pass user identity as parameters (`ownerId`/`userSub`), not fields.
- API routes and JSON fields constitute a contract (see [0006](0006-kanonisches-karten-json.en.md)); do not rename them without good reason.

## Affected Code

- `src/LearnCards.Web/Program.cs`, `Api/ApiEndpoints.cs`, `Components/**`, `Services/**`
