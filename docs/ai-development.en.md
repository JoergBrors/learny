# Developing with AI Assistants

This repository is designed for targeted changes by AI assistants (Claude Code, Copilot, Codex, and others). The guidance is organized into three layers:

| Layer | File | Purpose |
|---|---|---|
| Entry point | [AGENTS.md](../AGENTS.md) (`CLAUDE.md` points to it) | Rules, where to change what, and verification steps |
| Decisions | [docs/adr/](adr/README.md) | *Why* the system is structured this way, including change guardrails |
| Overview | [architecture.md](architecture.md), [database-schema.md](database-schema.md), [local-development.md](local-development.md), [testing.md](testing.md) | Structure, data model, operations, tests |
| Domain decisions | [ADR 0015](adr/0015-freigaben-globale-lernmodule.md) | Sharing, global modules, result attribution |
| Topics and JSON | [ADR 0027](adr/0027-themen-lernpfade-versionierung-und-abschlusspruefungen.md), [ADR 0028](adr/0028-themenmodule-json-und-mcp-verwaltung.md) | Topics composed from owned modules, cloning, module/topic JSON, MCP tools for owned topic drafts |
| Reviews | [docs/reviews/](reviews/) | Review reports (e.g., [multi-user review](reviews/2026-10-24-mehrbenutzer-pruefbericht.md)) |

For an upstream assistant (ChatGPT project) that turns requests into ready-to-run Codex/Claude Code tasks, see [gpt-project-prompt.md](gpt-project-prompt.md).

## Writing a Good Change Request

```text
Goal:          What should change for users or systems?
Area:          Affected component (e.g., card JSON, MCP tool, quiz)
ADRs:          Relevant ADRs (e.g., 0006, 0010), or "identify from AGENTS.md"
Acceptance:    How do we know it works? (build, manual flow, sample JSON)
Do not change: Explicit constraints (e.g., "no breaking schema changes")
```

Example: *“Cards should have an optional `difficulty` field (easy|medium|hard). Follow ADRs 0006 and 0004. Acceptance: build succeeds, `demoodule/demo.json` still imports, field appears in the schema at `/api/schema/import/cards`.”*

## Expected Assistant Workflow

1. Read the relevant ADR(s) and affected files.
2. Briefly state a plan if multiple files/layers are involved.
3. Implement the change, run `dotnet build LearnCards.slnx`, and start integration tests (`dotnet run --project tests/LearnCards.IntegrationTests --no-build`; see [testing.md](testing.md)).
4. Update documentation and any relevant ADR (new decision → new ADR and entry in [adr/README.md](adr/README.md)).
5. Report changed files, checks performed, and checks **not** performed.

## When Is a New ADR Necessary?

- New dependency, persistence/authentication/transport mechanism
- Changed card JSON, API contract, or data model (also update the live import schema, MCP descriptions, `wwwroot/templates/*.json`, `demoodule/*.json`, and `Help.razor` in the same task; the test for “examples, templates, schema, MCP, help” protects consistency; changes to text formatting also require `RichText`, `rte.js`, and `rich_text_rules`)
- New deployment variant or release-process change
- Deviation from an existing guardrail

Simple bug fixes, UI refinements, and text changes do not require an ADR.

## Maintenance

- ADRs form an immutable history; document changes in a new ADR with a reference (see [adr/README.md](adr/README.md)).
- If the directory structure changes or tests are added, update [AGENTS.md](../AGENTS.md) in the same PR.
- Tests: package-free integration tests in `tests/` ([testing.md](testing.md)). Add tests for new user-scoped features; using a NuGet testing framework requires a new ADR ([ADR 0002](adr/0002-keine-nuget-pakete.en.md)).
