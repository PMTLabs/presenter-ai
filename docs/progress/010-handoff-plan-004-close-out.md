# 010 — Handoff: plan 004, T13 and close-out remain

**Written:** 2026-09-22 15:01, at a clean boundary: everything committed, tree clean, no agent running, no cron
armed apart from the wake-up. Supersedes `009-handoff-plan-004-pr-c.md`.

## Goal

Land approved plan `docs/plan/004-identity-persistence-auth-state.md` (revision 2). Status:

- PR-A and PR-B are done.
- PR-C is done except **T13 (wiring audit)** and the close-out.

## Environment

- **Working directory:** `D:\sources\demo\presenter-ai`. **No worktree.**
- **Branch:** `feature/004-identity-persistence`, tip **`ec679dd`**. Nothing pushed, no PR opened.
- **Testcontainers:** needs `DOCKER_HOST=tcp://localhost:2375`. `tcp://localhost:9` is the no-container check.
- **Agent briefs and reports:** `D:\sources\demo\presenter-ai\.claude\agent-reports\plan-004\` (gitignored).

## Done since 009

| Commit | What |
|---|---|
| `e63a393` | Plan §10 deviations **D5** and **D6** (below) |
| `7332e81` | Conventions: the `/api` trio is struck (§10 is now a history note); **Vite dev proxies fixed from `/api` to `/v1`** (a gap dating from PR-B); ledger row |
| `4ac958d` | **T9 + T10:** per-run `SessionRecorder`, the bridge barrier, `presenter-cli import`, `presenter-cli run --owner`, and the AGENTS.md first-run runbook |
| `ec679dd` | Work-log entry for PR-C part 2 |

**Green at `4ac958d`, verified by the orchestrator:**

- build 0/0;
- **206 passed** (Application 52, Infrastructure 31, Api 78, Integration 36, Cli 9);
- Api and Cli pass with no container;
- web lint/build/test (shared 5, app 22);
- `secrets-guard: clean`;
- my own five mutations all fail a test.

**User decision, 2026-09-22:** "Follow plan, owner optional".

- `run <slug> --owner <email>` loads from Postgres and records a session.
- `run` without `--owner` is file-backed and unrecorded. This is **D5**.
- **D6:** a failed start writes no `sessions` row.

## Next steps, in order

### 1. T13: wiring audit (do it directly; it is verification, not implementation)

Plan §6 T13. Trace every new registration to what resolves it, in **both** graphs:

- **API graph:** `src/PresenterAi.Api/Program.cs`.
- **CLI graphs** (`src/PresenterAi.Cli/Program.cs`), three of them:
  - `BuildServices`: file-backed `run`/`smoke`, **no persistence**;
  - `BuildRunServices`: `run --owner`;
  - `BuildImportServices`: `import`.

Checklist:

- `AddPersistence`: `PresenterAiDbContext`, `IPresentationRepository`, and `ISessionRecorderFactory` (registered
  there with `TryAddSingleton`).
- `AddRedis`: the ticket, SSO code and SSO state stores.
- The identity services.
- `ISessionRecorder`: attached in **both** `StartAsync` callers (`PresenterBridge` start → `PrepareRecorder`;
  `RunCommand.RunWithPresenterAsync` with `recorderFactory`).
- `ITicketStore`: issued in `SessionEndpoints`, claimed in the bridge.
- `IPresentationImportSource`: **CLI only**. An API test asserts that the API host cannot resolve it.
- Every new config key → the class that reads it (plan §4.3 list).
- Every new endpoint → a caller in `web/` or a test.
- Flag anything that has no caller.

Record the checklist as a work-log entry.

The plan's "Serilog at debug, one log line per entry point during a full sign-in → import → present → close
cycle" needs a live upstream and a browser. That is **manual, for the user**: list it as not run, with the runbook
steps.

### 2. Close out

- Update plan 004's status and approval log (implementation complete, pending the user's manual runbook).
- **Ask the user before pushing or opening a PR.** Base is `master`. Per plan §8, PR-A/B/C are one branch; ask
  whether they want one PR or three.
- Offer the manual runbook in plan §7, steps 1–16. The ones only a human can do:
  - real Chrome Ricoh run;
  - two Google accounts;
  - `is_disabled` on the dev user;
  - closing a tab mid-run, then querying `sessions`.

## Known small items (not blocking, decide at close-out)

- `ImportCommand` and `RunCommand --owner` let an unreachable database surface as an unhandled exception after
  Npgsql's retries (`EnableRetryOnFailure`). A missing connection string is handled (exit 2).
- `ImportCommand` and `RunCommand` each keep a `FindRepositoryRoot`, and so do the test factories. This duplication
  predates PR-C (`RunCommand` had it).
- The bridge ends the presenter while `Current` still points at the dying connection, so its final frames are
  queued to a closed socket. This is harmless (the frozen tests pass), but it was a change of order made for the
  F6 barrier.

## Gotchas and settled decisions (do not relitigate)

- Verify agent claims; mutation-test guards with backups plus an md5 restore check; confirm `git status` is clean
  after committing.
- Seams 1/2, F2, F4 and F6 as in 009. D1–D6 are in plan §10.
- Never push, open a PR, merge or deploy without asking the user.

## Reference

- Plan: `docs/plan/004-identity-persistence-auth-state.md` (§6 T13, §7 runbook, §10).
- Briefs and reports: `.claude/agent-reports/plan-004/impl-pr-c2-*.md`.
- Work log: `docs/progress/002-work-log-phase0.md` (PR-C part 2 entry).
- Ledger: `docs/agentic/review-rounds-ledger.md`.
