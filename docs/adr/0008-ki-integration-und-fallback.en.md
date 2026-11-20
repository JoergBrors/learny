# 0008 — AI Integration with Offline Fallback and Token Budget

- **Status:** Accepted
- **Date:** 2026-10-24
- **Supplemented by:** [0016](0016-bilder-in-lernkarten.en.md) (card images are never included in prompts; images work without an LLM/API key), [0011](0011-mehrbenutzerarchitektur.en.md) (chat history and quiz results belong to the user), [0015](0015-freigaben-globale-lernmodule.en.md) (quiz/chat support owned, shared, and global cards; offline fallback is unchanged; results retain the private/shared/global context)

## Context

Chat, quiz generation, and grading use an LLM. Local development should work without an API key; JSON-mode LLM responses can be truncated if `max_tokens` is too low.

## Decision

- `OpenAiClient` calls the Chat Completions API using `HttpClient` (`OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_BASE_URL`—compatible with Azure OpenAI or local endpoints).
- `CompleteJsonAsync` retries truncated or invalid JSON responses with an **increased token budget** (up to 8000). `QuizService` dynamically calculates the grading budget based on the number of answers.
- Without `OPENAI_API_KEY`, `ChatService` and `QuizService` use a **clearly identified offline mode** (questions drawn from cards, keyword-based grading heuristic).
- Prompts live in `QuizService`/`ChatService`. Chat history is saved per user and card; API clients receive streams via SSE (`POST /api/chat/`).

## Consequences

- Online and offline code paths must remain consistent.
- LLM outputs are nondeterministic; parsers must be tolerant and handle error cases.

## Guardrails for Changes

- Never remove the offline path; every new AI feature needs a fallback and an indicator in its result.
- Use `CompleteJsonAsync` for all JSON-output LLM calls; size the token budget according to expected output.
- Only the Blazor interface (`ChatPanel`) persists chat history, not `POST /api/chat/` (review finding F10).
- Send only necessary data in prompts (no secrets or unnecessary personal information).
- Prompt changes affecting response formats must also update parsers in `QuizService` and models in `Domain/Models.cs`.

## Affected Code

- `Services/OpenAiClient.cs`, `QuizService.cs`, `ChatService.cs`, `Components/Shared/ChatPanel.razor`, `QuizPanel.razor`, `QuizRunner.razor`
