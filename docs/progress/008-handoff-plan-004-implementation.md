# 008 — Handoff: plan 004 implementation (identity, persistence, auth-state Redis)

**Written:** 2026-09-22, at a clean boundary (PR-A and PR-B committed, tree clean, nothing in flight).

## Goal

Land approved plan `docs/plan/004-identity-persistence-auth-state.md` (revision 2, approved 2026-09-22):
real sign-in with Google/Microsoft, presentations and sessions owned per user in Postgres, a one-time ticket on
the `/ws` handshake, and Redis holding auth state only. **14 tasks in three PRs.**

## Environment

- **Working directory:** `D:\sources\demo\presenter-ai` — **no worktree**, work happens directly in the repo.
- **Branch:** `feature/004-identity-persistence` (off `develop` at `40d445c`). Nothing pushed; no PR opened.
- **Windows gotcha:** Testcontainers does **not** discover the WSL Docker daemon.
  `export DOCKER_HOST=tcp://localhost:2375` or every integration test fails. `docker compose` is unavailable on
  the Windows CLI — the standalone `docker-compose` is what works.
- `.NET 10.0.401`. `TreatWarningsAsErrors` is on solution-wide: a warning is a build break.

## Done

| Commit | What |
|---|---|
| `50bb093` | plan 004 approved (plan, external review `docs/review/005`, ledger row, work log) |
| `7be5bd4` | **T1, T3** — EF Core schema (6 entities, migration with the `vector` extension), Redis auth-state stores |
| `1164c7d` | **T2** — `PresenterAi.Integration.Tests` with Testcontainers fixtures |
| `b9cd186` | **T12-config** — every new config key documented, compose connection strings |
| `5ced993` | work log for PR-A |
| `9d84159` | **T4, T5, T6** — JWT bearer, atomic refresh rotation, `OriginGuard`, SSO, `/v1/auth/*`, rate limiting |
| `a9eaaf7` | security tests for the auth stack, mutation-verified |
| `e81b667` | **T7, T11a** — `/v1/sessions/ticket`, the `/ws` ticket handshake, frontend real auth |
| `3710a5b` | implementation deviations recorded in the plan's approval log |

**Green at `3710a5b`:** `dotnet build -warnaserror` 0/0; `dotnet test` **176 passed** (Application 52,
Infrastructure 30, Api 72, Integration 17, Cli 5); `Api.Tests` pass with `DOCKER_HOST=tcp://localhost:9`
(no container); web lint/build/test green; OpenAPI drift green; `secrets-guard: clean`.

So **PR-A and PR-B are complete**. Only PR-C remains.

## In progress

**Nothing.** No agent is running, no uncommitted work, no unread background job. All termflow terminals for this
plan are closed.

## Next steps — PR-C, in this order

Dispatch to external `pi` agents (`openai-codex/gpt-5.6-luna:high`) via the `agent-research` skill, one bounded
package per agent; briefs go in
`C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\121ab6bf-f7d7-40c1-bca4-9b126c3c7052\scratchpad\plan-004\`
alongside the existing `impl-pr-a-brief.md`, `impl-pr-b1-brief.md`, `impl-pr-b2-brief.md` — copy their shape.

1. **T8 + T11b** — owner scoping and the `/v1` content cutover. The heaviest task.
   - Split `IPresentationRepository` (owner-scoped, Postgres-only) from a new ownerless
     `IPresentationImportSource` that `FilePresentationRepository` implements for the importer. Do **not** widen
     the one interface — review finding F4.
   - `StartAsync` takes an owner and returns `PresenterStartResult` (presentation, upstream, upstream session id)
     — nothing currently exposes which upstream the fallback chose, and `sessions.upstream` needs it (F6).
   - `/v1/presentations`, `/v1/presentations/{id}` (**404, never 403**, for another user's id), `/v1/config`;
     delete the `/api` trio. `web/app` `Library.tsx` and `Present.tsx` move to `/v1` and the paged envelope in the
     same PR — they are its only callers.
   - **Budget for nine test files**, not two: `BridgeContractTests`, `BridgeSlotTests`, `BridgeStartTests`,
     `BridgeTests`, `PresenterTests`, `CliTests`, `FilePresentationRepositoryTests`, `FakeLiveServer`,
     `LiveSessionTests`.
   - Regenerate the OpenAPI snapshot **in the same task** or the drift test goes red.
2. **T9 + T10** — session recorder and `presenter-cli import`.
   - Recorder attaches per run in both `StartAsync` callers, tracks the slide from `Slide` events, aggregates
     `Transcript` deltas into turns, and `EndAsync` is an **idempotent awaited barrier** the bridge awaits in its
     `finally` before detaching — otherwise a disconnect leaves `ended_at` null (F6).
   - `presenter-cli import <glob> --owner <email>` must store the **context file's text** into
     `presentations.context` (F5 — `ScriptParser` keeps only the path), upsert on `(owner_id, slug)`, and exit
     non-zero for an unknown or disabled owner. The CLI service graph currently has **no** persistence — add it
     (`src/PresenterAi.Cli/Program.cs:63-80`).
3. **T12-docs + T13** — strike the frozen-trio section from `docs/reference/001-api-and-code-conventions.md`,
   finish the runbook, then the wiring audit (every new registration → who resolves it, in **both** the API and
   CLI graphs).
4. Then: work log entry, and ask the user before pushing or opening a PR.

## Gotchas and settled decisions — do not relitigate

- **Verify agent claims, never accept them.** Every round so far has turned up something: PR-A silenced the whole
  transitive NuGet audit; PR-B1 tried to add a `TestingDevCompatibility` middleware that authenticated every
  request when `ASPNETCORE_ENVIRONMENT=Testing` (a worse `DevAuthHandler`) and shipped **zero tests** for the auth
  stack until sent back. Re-run the suite yourself and spot-check one mutation per security guard.
- **Mutation testing leaves production code temporarily broken.** After any agent round that mutation-tests,
  check `grep -rn "Mutation test" src/ web/` is empty and the guards are present before committing.
- Settled: eager config validation + lazy connection (D1); dev sign-in absent from the OpenAPI snapshot (D2);
  `WWW-Authenticate: Bearer` (D3); eight `NuGetAuditSuppress` entries by advisory id, never `NuGetAuditMode` (D4).
  All four are in the plan's §10.
- `ErrorCodes.cs` already contains every auth/session code the plan needs — **add none**.
- The ticket is claimed **before** the CAS slot in `PresenterBridge.HandleAsync` (`AuthenticateAsync` at :62,
  CAS at :67). Do not reorder — that is review finding F2, and the line numbers look misleading because the claim
  lives in a helper defined lower in the file.
- The `/api` trio's only callers are `web/app/src/routes/Library.tsx:15` and `Present.tsx:130`; `/api/config` has
  none.
- Never push, open a PR, merge or deploy without asking the user first.

## Reference

- Plan: `docs/plan/004-identity-persistence-auth-state.md` (§6 tasks, §7 test strategy incl. the 16-step manual
  runbook, §10 approval log + review dispositions + deviations).
- External review: `docs/review/005-plan-004-external-review.md` (9 blockers, 2 improvements, all folded in).
- Scout reports (external inkspoke inventory, presenter as-built) are in the scratchpad path above, not the repo.
