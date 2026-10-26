# 0002 — No External NuGet Packages

- **Status:** Accepted
- **Date:** 2026-10-24 (documented; the decision dates back to the .NET port)

## Context

Build and deployment environments (corporate proxies, IIS runners) sometimes lack NuGet access. In addition, wire-protocol behavior (PostgreSQL SCRAM, OIDC validation) should remain transparent and understandable.

## Decision

Both projects (`LearnCards.Web`, `LearnCards.McpServer`) use only the .NET SDK and ASP.NET Core framework. Database access, OIDC, and the OpenAI client are implemented in-house (`HttpClient`, sockets, P/Invoke to `libsqlite3`).

## Consequences

- `dotnet build` and `publish` work offline with just the SDK.
- More custom code (e.g., `PostgresDatabase.cs`, approximately 500 lines) and direct responsibility for security.

## Guardrails for Changes

- **Do not add `PackageReference`**, even for tests or helper libraries, without a new ADR. Therefore, the tests (`tests/LearnCards.IntegrationTests`) are a package-free console application (see [testing.md](../testing.en.md)).
- Replacing the custom implementations with standard packages is facilitated by `IDatabase` and `OpenAiClient` (see [0004](0004-datenbank-abstraktion.en.md)); it must not change the API or UI.
- Do not add external package sources to `NuGet.config`.

## Affected Code

- `src/*/*.csproj`, `NuGet.config`, `src/LearnCards.Web/Data/*`, `Auth/OidcService.cs`, `Services/OpenAiClient.cs`
