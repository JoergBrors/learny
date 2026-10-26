# 0001 — Document Architecture Decisions as ADRs

- **Status:** Accepted
- **Date:** 2026-10-24

## Context

LearnCards is increasingly developed with AI assistants. Without explicitly recorded decisions, assistants (and new team members) may “fix” intentional design choices—such as the absence of NuGet packages—or break implicit contracts such as the card JSON format.

## Decision

We record each architecture-shaping decision as an ADR under `docs/adr/`. Each ADR includes a **Guardrails for Changes** section that serves as direct guidance when making changes. [AGENTS.md](../../AGENTS.md) points AI assistants to these ADRs.

## Consequences

- Changes affecting an ADR must update or supersede it in the same commit/PR.
- Minor additional effort in exchange for a traceable history and more focused AI-assisted changes.

## Guardrails for Changes

- Keep ADRs short (one page), with one decision per file.
- Do not delete old ADRs; link them using the status “Superseded by.”
