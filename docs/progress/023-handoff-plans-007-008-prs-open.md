# 023 — Handoff: plans 007 and 008 in review as PRs

**Written:** 2026-09-23 17:10. Supersedes `022-handoff-plan-008-review-fixes.md`.

## State

- **Plan 007:** PR #7 (`feature/007-voice-control-and-tools` → `develop`), open, not merged.
- **Plan 008:** PR #8 (`feature/008-mcp-external-tools` → `feature/007-voice-control-and-tools`, stacked), open, not
  merged. Head `3a1d763`. Retarget it to `develop` after #7 merges.
  - Implementation reviews: `docs/review/014`, `015`, `016`, with ledger rows. Round 3 was the escalation round:
    two status-badge races deferred as known limitations by the user's decision (documented in guide 003,
    "Status and troubleshooting").
  - Suites at `1095717` (docs-only since): Application 318, Infrastructure 155 (+4 skipped), API 213, CLI 12,
    Integration 75 (+1 skipped); web shared 20, app 82; lint and builds pass.

## Decisions (settled)

- The plan 008 Task 0 live probe, the Task 10 manual runbook and the plan 007 voice runbook are deferred, to be run
  together in one live session with the user later.
- PR #8 is stacked on the 007 branch.
- The status races (review 016 findings 1–2) are known limitations, with no round 4. Their follow-up is a per-server
  generation token plus the two interleaving tests.

## Open follow-ups

1. The live session: Task 0 probe, Task 10 runbook, plan 007 voice runbook (needs real MCP servers and the user).
2. Status generation token (review 016).
3. Timing flakes seen once each: `StartupTests.Out_of_range_follow_up_wait_fails_startup("2499")`,
   `Wrap_up_without_audio_ends_after_fallback`.
4. Plan 007 review 013 follow-up: after "no", a resume *tool* call for an unrecognised phrase waits for the 15 s escape.

## Environment

- Main tree `D:\sources\demo\presenter-ai` on `feature/007-voice-control-and-tools`. It is one commit ahead of the
  remote (handoff 022), and this file adds a second; both are unpushed docs.
  - `src/PresenterAi.Api/appsettings.json` has an unstaged change that is not ours; leave it.
  - `.mcp.json` and `.playwright-mcp/` are untracked and not ours.
- The only remaining worktree is `.claude\worktrees\p008-t5` (the PR #8 branch). Local branches `feature/008-fix-a`
  and `feature/008-fix-b` are merged leftovers; delete them only with the user's approval.
- Docker for Testcontainers: `DOCKER_HOST=tcp://localhost:2375`. Never touch port 3000.
