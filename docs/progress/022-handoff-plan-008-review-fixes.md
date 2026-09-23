# 022 — Handoff: plan 008 review round 1 fixes

**Written:** 2026-09-23 16:05. Supersedes `021-handoff-plans-007-008-review-and-integration.md`. The "Agents" and
"Gotchas" sections of handoffs 020 and 021 still apply.

## Goal

Implement plan 007, then plan 008 (per-user MCP external tools). Delegate, and run tasks in parallel whenever they
don't depend on each other. Verify every agent result yourself before committing: build, full suite, read the diff
against the plan, and run at least one mutation of your own.

Agents, from least to most capable:
- `pi --model openai-codex/gpt-6-luna:high` (cheap, small tasks);
- `agy --dangerously-skip-permissions --model "Gemini 3.8 Flash (High)"`. It hit 100% at 14:54 and resets around
  16:50; its limit counts as reached only when the first 5-hour figure is 100%;
- `pi --model openai-codex/gpt-6-sol:medium` (hard tasks);
- reviews: `pi --model openai-codex/gpt-6-sol:high`.

## Running right now (check these first)

Scratchpad `$S` =
`C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\78e5d02d-4e0c-4a11-b2ab-7f64d531cf4b\scratchpad`.

1. **Group A fixes**, sol/medium, termflow terminal `tm-571daa232`, worktree
   `D:\sources\demo\presenter-ai\.claude\worktrees\p008-fa` (branch `feature/008-fix-a`, from `b87ab30`).
   - Covers review 014 findings 1 (the blocker: a confirmed action could be replayed after a 401), 3, 4, 5, 6, 8, 9,
     10, 11 and 12.
   - Brief `$S/f008-a-brief.md`; report `$S/f008-a-report.md`, which ends with `IMPLEMENTATION COMPLETE f008-a`.
2. **Group B fixes**, luna/high, terminal `tm-893088fd8`, worktree `...\.claude\worktrees\p008-fb` (branch
   `feature/008-fix-b`, from `b87ab30`).
   - Covers findings 2 (byte-budget discovery), 7 (log-safe labels), 13 (the effective "Always ask" switch on the
     Tools page) and 14 (the guide's start budget).
   - Brief `$S/f008-b-brief.md`; report `$S/f008-b-report.md`, which ends with `IMPLEMENTATION COMPLETE f008-b`.

If a report stays empty, look at the terminal with `get_terminal_screen`. If the agent died, restart it in a new
terminal with the same brief and tell it to continue from the uncommitted work.

## Done since handoff 021

**Plan 007:**
- Round 3 fixes in `0218363`: continue after a check-in "no" resumes; the range flag is reset on a new question;
  the late-fault log only fires for abandoned runs; the guide's timer text is corrected.
- Review 013 and the ledger row: `300e254`.
- Pushed, **PR #7 open into `develop`**: https://github.com/PMTLabs/presenter-ai/pull/7. Not merged.
- The user chose to **skip the live voice runbook for now**; it runs later together with plan 008.
- Follow-up recorded in review 013: after "no", a resume *tool* call for an unrecognised phrase waits for the 15 s
  escape.

**Plan 008:** integration branch `feature/008-mcp-external-tools`, worktree `...\.claude\worktrees\p008-t5`, head
`b87ab30`.
- `5ce52bb`, `08a1469`: plan 007 fixes merged in.
  - `SnapshotTool` forwards `RequiresConfirmation`, `Timeout`, `Source` and `Title`. With the forwarding mutated,
    36 tests fail.
  - Tool calls and approved runs share `InvokeBoundedAsync`.
  - The question reads "Shall I use {title} on {server}?" (new `ITool.Title`, and `McpTool.Title`).
- Task 6 REST endpoints: `9c5b7a5` (agy, then sol/medium after agy hit its limit), merged in `6647fbe`.
  - Per-user tools rate limit, generated web client, full `SecretHygieneTests`.
  - The Postgres `AddAsync` now runs inside the EF execution strategy.
- Task 9 docs: `c360db3` (luna), merged in `3f1bfc8`. Guide `docs/guides/003-external-tools.md`, README, `AGENTS.md`,
  reference 001.
- Task 8 Tools page: `a7ffb6c` (luna, reformatted on request), merged in `04ebf24`.
  - I tightened the write-tool fixture (`alwaysAsk: false`) so the effective-switch mutation now fails.
  - The callback uses a `<div>`, not a nested `<main>`.
- Review round 1 (sol/high), saved as `docs/review/014-plan-008-impl-review-round-1.md` with a disposition table,
  plus the ledger row: `b87ab30`. Classes: A1, B4, C2, D7. **Every finding is to be fixed**, except the
  callback-refresh test half of finding 13.
- Partial wiring audit done by me (Task 10): the named clients are guarded with auto-redirect off and loggers
  removed; there is no `.Message` or exception logging under `Tools/`. The reviewer confirmed the rest (owner from
  the bridge `UserId`, the gate after `Resolve`, tool-set disposal).
- Suites at `04ebf24`: Application 316, Infrastructure 144 (+4 skipped), API 213, CLI 12, Integration 69 (+1).
  Web: shared 20, app 80. Lint and build pass.

## Next steps, in order

1. **Verify each fix report as it arrives.**
   - Read the report, the diff, and the pre-fix failure it claims for each finding.
   - Run a mutation of your own on the blocker fix: revert the method classification to substring matching → the
     escaped-method test must fail.
   - Build, run all five suites and the web checks in that worktree.
   - Commit on the fix branch, merge it into `feature/008-mcp-external-tools` (worktree `p008-t5`), and run the full
     suite there. Expect small conflicts where both groups touched `McpSessionToolSource.cs` or `McpTool.cs`.
2. **Review round 2** (sol/high, new terminal, read-only): confirm the fixes against the review 014 disposition. Use
   the same brief format as `$S/r008-impl-01-brief.md`; request `r008-impl-02`, scope `b87ab30...HEAD`. Save it as
   `docs/review/015` with a disposition and a ledger row.
3. **Ask the user** about:
   - the Task 0 live probe and the Task 10 manual runbook (plan 008 §5; they need real MCP servers and possibly the
     user's accounts), together with the deferred plan 007 voice runbook;
   - pushing `feature/008-mcp-external-tools` and opening its PR. It contains the plan 007 commits, so either base it
     on `develop` after PR #7 merges, or on `feature/007-voice-control-and-tools`. Ask which.
4. After the PRs, ask before deleting the merged worktrees and branches: `p008-t1`–`t9`, `p008-fa`, `p008-fb`,
   `p007-pi`.
5. Every user-facing response ends with a `Task done: …` line.

## Environment

- Main tree `D:\sources\demo\presenter-ai` on `feature/007-voice-control-and-tools` @ `0218363` (pushed).
  - `src/PresenterAi.Api/appsettings.json` has an **unstaged change that is not ours** (`DelegationModel` →
    `gpt-6-luna`). Leave it unstaged.
  - `.mcp.json` and `.playwright-mcp/` are untracked and not ours.
- The API on 47913 is stopped. Docker for Testcontainers: `DOCKER_HOST=tcp://localhost:2375`.
- Every new worktree needs `bun install --frozen-lockfile` in `web/` before the web checks.
- Open agent terminals: `tm-571daa232` (f008-a), `tm-893088fd8` (f008-b).

## Gotchas

- An agent's own mutation claims are not enough. In Task 8 the agent's mutation left a gap that only a fixture change
  closed.
- Luna tends to produce very long lines; ask it for the repo style (lines of at most 120 characters).
- The "15-minute check" background sleeps are only fallbacks. A report marker is the real signal.
