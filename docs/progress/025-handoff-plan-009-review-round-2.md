# 025 — Handoff: plan 009 (billing guards) implemented, confirmation review round 2 running

**Written:** 2026-09-23 19:14. Supersedes `024-handoff-billing-guards-planning.md` (on the 007 branch in the main tree).

## Goal

Plan 009 (`docs/plan/009-upstream-billing-guards.md`, Approved): every talk's billed GPT-Live upstream closes within a
bounded time. The user said "implement plan 009". Remaining: finish the review loop, then report to the user and ask
before any push or PR.

## Done (branch `feature/009-billing-guards`, worktree `D:\sources\demo\presenter-ai\.claude\worktrees\p009`, not pushed)

- Plan and plan review: `4b925a6`, `118d63e`; `docs/review/017` plus a ledger row.
- T1 `47925c6`. Merges: T2 `dd8c322`, T9 `c004768`, T6 `5f8fc85`, T10 `ed4a58d`, T3–T5 (core) `7029425`, T8 `9cfa342`,
  T7 `db7a712`. T11 wiring audit done by me: all keys, events, `AbortPendingStart` callers, the shutdown service,
  log lines and pong are wired.
- Implementation review round 1 (pi sol/high): `docs/review/018-plan-009-impl-review-round-1.md` plus a ledger row
  (`df76c36`). There were 3 blockers:
  - D1: the end reason leaked into the next talk;
  - A1: the writer aborted only on `WebSocketException`;
  - D2: a queued Start survived shutdown, and the observation wait was unbounded.
  Also B1, B2, A2, D3, D4, D5. All were accepted.
- Round-1 fixes (pi sol/medium): `180bebb`, `c977122`. Verified by me at `c977122`:
  - `dotnet build -warnaserror`: 0 warnings;
  - Application 375, Infrastructure 159 (+4 skipped), API 243 (twice), CLI 15, Integration 83 (+1 skipped);
  - web lint, tests (exit 0) and build pass.
  - The Application count fell from 384 because a 16-case vocabulary theory was replaced by producer-driven tests.

## In progress

- **Confirmation review round 2** (pi `openai-codex/gpt-6-sol:high`, terminal `tm-ce350ed34`, named
  "r-i009b sol-high"). Brief: scratchpad `r-i009b-brief.md`. Report (ends `REVIEW COMPLETE r-i009b`):
  `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\78e5d02d-4e0c-4a11-b2ab-7f64d531cf4b\scratchpad\r-i009b-report.md`.
  Scope: `df76c36..HEAD` only.

## Next steps

1. If the report is incomplete, arm a Monitor on that file (tag `REVIEW COMPLETE r-i009b`), plus a 15-minute health
   check of the terminal. Do not poll by hand.
2. Read the report and verify each finding against the code.
   - Save it as `docs/review/019-plan-009-impl-review-round-2.md` with a disposition, and add a ledger row to
     `docs/agentic/review-rounds-ledger.md`.
   - Commit: stage explicit files, run `bash scripts/secrets-guard.sh`, no AI attribution.
   - Round 2 is the last normal round. If real defects remain, fix them with pi sol/medium (brief like
     `i-fix1-brief.md`) and verify. A third round is the escalation trigger: do a root-cause analysis and ask the user.
3. Close terminal `tm-ce350ed34` when done.
4. Report to the user:
   - what shipped, the verification counts, and the known flake `Approved_run_after_moving_on_is_not_announced(end)`
     (test timing, per review 018);
   - the manual runbook (plan §7) still to run live with the user;
   - ask before pushing `feature/009-billing-guards` or opening a PR (stacked on #8, `feature/008-mcp-external-tools`).
5. Optional cleanup, **ask first**: remove the merged worktrees and branches `p009-core`, `p009-t2`, `p009-t6`,
   `p009-t7`, `p009-t8`, `p009-t9`, `p009-t10` (branches `feature/009-*`).

## Decisions and rules (settled)

- The user's agent rules (2026-09-23), saved in memory:
  - sol runs only through the `pi` harness (`pi --model openai-codex/gpt-6-sol:<effort>`), never `codex` directly;
  - when the codex quota is near 1% weekly (it was 13%), use an internal Opus 5.5 subagent for sol-tier work and
    reviews;
  - submit to pi with termflow `cliType: "codex"`, then confirm the screen shows Working.
- Model tiers: luna/high < agy (Gemini 3.8 Flash High) < sol/medium; reviews use sol/high. I verify every result.
- Never start the API or web servers (the user wants no billing risk). Never touch port 3000.
- The main tree `D:\sources\demo\presenter-ai` is on `feature/007-voice-control-and-tools`. Its unstaged
  `appsettings.json` change and the untracked `.mcp.json` and `.playwright-mcp/` are not ours; leave them.
- Every user-facing response ends with a `Task done: …` line.
