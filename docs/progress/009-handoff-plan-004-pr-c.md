# 009 — Handoff: plan 004, PR-C in progress (T8 + T11b landed; T9, T10, T12-docs, T13 remain)

**Written:** 2026-09-22 13:25, at a clean boundary (everything committed, tree clean, no agent running).
Supersedes `008-handoff-plan-004-implementation.md`, which remains accurate for everything before PR-C.

## Goal

Land approved plan `docs/plan/004-identity-persistence-auth-state.md` (revision 2). PR-A and PR-B are done; PR-C is
half done.

## Environment

- **Working directory:** `D:\sources\demo\presenter-ai`. **No worktree**; work happens directly in the repo.
- **Branch:** `feature/004-identity-persistence`, tip **`bd364cf`**. Nothing pushed, no PR opened.
- **Testcontainers needs `DOCKER_HOST=tcp://localhost:2375`** on this Windows host, or every integration test fails.
  `DOCKER_HOST=tcp://localhost:9` is the "no container" check for `Api.Tests` and `Cli.Tests`.
- **Agent briefs and reports now live in `D:\sources\demo\presenter-ai\.claude\agent-reports\plan-004\`**
  (gitignored). The session scratchpad that 008 names has been retired, so do not use that path.

## Done since 008

| Commit | What |
|---|---|
| `4159e7e` | fix: `e81b667` had shipped a red test (`TestTicketStore` not registered as its concrete type) |
| `0717d29` | **T8 + T11b**: owner-scoped Postgres presentations, `/v1` cutover, `/api` trio deleted, paging, web on `/v1` |
| `bd364cf` | work log for PR-B and PR-C part 1, review-ledger row |

**Green at `0717d29`, verified by the orchestrator, not taken from the agent report:** build 0/0; **183 passed**
(Application 52, Infrastructure 30, Api 75, Integration 21, Cli 5); Api and Cli pass with no container; web
lint/build/test green (shared 5, app 22); OpenAPI drift green; `secrets-guard: clean`. Seven mutations all fail
a test: the owner filter on load and on list, an empty list, the bridge passing a wrong owner (both directions), a
fixed upstream label, `Skip(0)`, and the import source registered in the API.

## In progress

**Nothing.** No agent is running and there is no uncommitted work. All termflow agent terminals are closed.

## Next steps, in order

### 1. Put one question to the user, and do not block the rest on it

**The plan conflicts with a decision taken during T8.** Plan T9 says the session recorder is "attached per run by the
bridge and by `RunCommand`". But T8 kept `presenter-cli run` **file-backed** (Seam 2 below), and
`sessions.user_id` and `sessions.presentation_id` are **required foreign keys**
(`src/PresenterAi.Infrastructure/Persistence/PresenterAiDbContext.cs:106-123`). A file run has no user row and no
presentation row (its owner is the literal `"local-file-content"`), so it cannot write a `sessions` row.
AC5 says only "a completed run leaves one `sessions` row…", so it does not settle whether CLI runs count.

Ask with `AskUserQuestion`:
- **(a) Recommended: only `/ws` sessions are recorded.** `presenter-cli run` against local files stays unrecorded.
  `/ws` is the product path and satisfies AC5. The CLI stays container-free. Amend the plan's T9 wording and add a
  deviation row D5 in §10.
- **(b) Add `presenter-cli run --owner <email>`.** With it, `run` loads the imported presentation from Postgres and
  records exactly like `/ws`; without it, `run` stays file-backed and unrecorded. This reuses T10's CLI persistence
  wiring and costs more work, but keeps CLI runs recorded.

Dispatch the unblocked work (step 2) **before** asking, so the agent is busy while the user answers.

### 2. T9 (recorder + bridge) and T10 (`presenter-cli import`)

Dispatch to a **fresh** `pi` agent (`openai-codex/gpt-5.6-luna:high`) via the `agent-research` skill. Model the
brief on `.claude/agent-reports/plan-004/impl-pr-c1-brief.md`: read-first list, what exists, **seams decided up
front**, out of scope, rules, verify list with mutation checks, report shape. Pre-create the report file in the same
folder, then arm a file watch plus a ~15-minute health check.

Facts the brief needs (verified at `0717d29`):
- `IPresenter.StartAsync(string id, int? fromIndex, string ownerId, CancellationToken)` returns
  `PresenterStartResult(bool Started, string PresentationId, string? Upstream, string? UpstreamSessionId, string? Model)`.
  `Upstream` is the route **label** (e.g. `primary`/`fallback`), never an endpoint or credential. Check the labels in
  `UpstreamRoutes` before quoting them.
- The bridge starts fire-and-observe: `ObserveCommand(_presenter.StartAsync(…, connection.UserId, …), connection,
  "start", true)` at `src/PresenterAi.Api/Realtime/PresenterBridge.cs:248`. The recorder needs the **result**, so
  `BeginAsync` must run on the start task's completion. The bridge's `finally` must **await `EndAsync`** before it
  detaches handlers and releases the slot (review finding F6). **Do not move the ticket claim** (`AuthenticateAsync`
  at `:62` runs before the CAS at `:67`; finding F2).
- Presenter events for the recorder: `Slide(int)`, `Transcript(PresenterTranscript)`, `Usage`, `Closed(PresenterClosed)`.
- `presenter-cli import <glob> --owner <email>`: upsert on `(owner_id, slug)`, store the context file's **text** in
  `presentations.context` and its path in `frontmatter`, and exit non-zero for an unknown or disabled owner.
  **Seam to decide in the brief:** `IPresentationImportSource` (`ListFilesAsync`, `ReadAsync`) returns *parsed*
  `LoadedPresentation`s, but the `script` column stores the **raw Markdown** that is reparsed on read. The importer
  needs the raw script text, the context text and the context path. Prefer extending the import source with a raw
  read over re-reading files in the CLI.
- The CLI graph (`src/PresenterAi.Cli/Program.cs` `BuildServices`) is `AddFileContent` + `AddFileImportSource` +
  `AddPresenter(fileBacked: true)`, with **no persistence**. T10 adds `AddPersistence` for `import`. Keep
  `Cli.Tests` passing with no container; put anything that needs Postgres in `PresenterAi.Integration.Tests`.
- Plan-named oracles: `SessionRecorderTests.A_disconnect_still_finalises_the_row`,
  `SessionRecorderTests.Two_sequential_runs_do_not_leak_handlers`, plus: completed run, failed start, fallback to
  the second upstream (assert `sessions.upstream` is the fallback's label), and an import idempotence test.

### 3. T12-docs + T13

Strike the frozen-`/api`-trio section from `docs/reference/001-api-and-code-conventions.md` (§ around line 194, since
`/api/*` is now deleted), finish the manual runbook, then the wiring audit: every new registration and who resolves
it, in **both** the API and CLI graphs.

### 4. Close out

Write a work-log entry in `docs/progress/002-work-log-phase0.md`, then **ask the user before pushing or opening a
PR**.

## Gotchas and settled decisions (do not relitigate)

- **Verify agent claims; never accept them.** Every round so far has turned up something. PR-C1 round 1 added four
  test-only ownerless shims to production types instead of updating tests, and its API test double ignored the
  owner, so a bridge passing the wrong owner stayed green. Re-run the suite yourself, and mutation-test each guard
  with backups plus an md5 check that the files come back byte-identical.
- **After committing, confirm `git status` is clean** before quoting the working tree's test result as the
  commit's. `e81b667` shipped red that way.
- **Seam 1 (settled):** `IPresenter` is a singleton and the DbContext is scoped, with `ValidateOnBuild` and
  `ValidateScopes` both on. The Postgres loader opens a scope per call (`IServiceScopeFactory`). Never weaken
  either flag.
- **Seam 2 (settled):** `presenter-cli run` is file-backed (`AddPresenter(fileBacked: true)`, owner constant
  `LocalContentOwner = "local-file-content"` in `RunCommand`), because `CliTests` runs the real `run` command with
  no container. This is what raises the step-1 question.
- **F4 (settled):** `IPresentationRepository` is owner-scoped and Postgres-only. `IPresentationImportSource` is
  registered **only** in the CLI (`AddFileImportSource`). An API test asserts the host cannot resolve it.
- Nothing test-only in `src/`. Test helpers live in the test projects
  (e.g. `tests/PresenterAi.Application.Tests/Presenting/PresenterTestExtensions.cs`).
- `ListResponse<T>` now lives at `src/PresenterAi.Contracts/ListResponse.cs`. Lists page per conventions §4
  (`page`/`pageSize`, 1/25, max 100).
- `ErrorCodes.cs` has every code needed. **Add none.**
- Earlier settled deviations D1–D4 are in the plan's §10.
- Never push, open a PR, merge or deploy without asking the user first.

## Reference

- Plan: `docs/plan/004-identity-persistence-auth-state.md` (§6 tasks T9/T10/T12/T13, §7 test strategy and runbook,
  §10 approval log and deviations).
- PR-C1 brief and both reports: `.claude/agent-reports/plan-004/impl-pr-c1-*.md`.
- Work log: `docs/progress/002-work-log-phase0.md` (PR-A, PR-B and PR-C1 entries).
