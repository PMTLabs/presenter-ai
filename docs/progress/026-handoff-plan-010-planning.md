# 026 — Handoff: plan 010 (live presenter training and script versioning), planning done

**Date:** 2026-09-24
**Branch:** `feature/010-live-presenter-training` (from `develop` @ `10e231d`). Main repo checkout, no worktree.
**Mode:** Master Agent. Orchestrate, delegate implementation to internal subagents, and delegate reviews to external pi `gpt-6-sol:high`.

## Goal

A trainer changes the script by voice during a live talk. The backend reasoning model rewrites the affected slide(s), the presenter holds narration while the update is pending and then replays the slide, and every script version can be recovered and reverted. The full requirement is in plan §2.

## Done

- G1: requirement brief confirmed. Decisions are in plan §2 and §9.
- Plan written: `docs/plan/010-live-presenter-training.md`, 1,245 lines, 13 tasks in phases A–E.
- External plan review by pi `gpt-6-sol:high`, three rounds. All findings are folded into the plan.
  - Round 1: `docs/review/021`.
  - Round 2: `docs/review/022`. The class fix was state reconciliation, a commit/End lock, and a presenter-owned edit intent.
  - Round 3: `docs/review/023`. Fixed D14 (atomic reconcile snapshot) and D15 (a timed-out close never releases a lock permit it did not take). The owner stopped the loop at round 3.
- The review ledger has one row per round in `docs/agentic/review-rounds-ledger.md`.
- Owner decisions: yes/no confirmation words are per language (en and vi now, more can be added); R14 and R15 edge cases accepted; D2 single API instance; D7 a revert becomes the head and pending edits still apply on top.

## Not done / next

1. **G2:** the plan status is "round-3 fixes applied, awaiting final owner approval". The owner chose "Round 3 then approve". Round 3 was not clean, but its two blockers were fixed exactly as the reviewer proposed. Ask the owner for final approval, then set `Status: Approved (date)` and add the approval-log row.
2. Nothing is committed yet. Stage explicitly:
   - `docs/plan/010-…`
   - `docs/review/021-023`
   - `docs/agentic/review-rounds-ledger.md`
   - this handoff

   Run `bash scripts/secrets-guard.sh` first.
3. Implementation starts only on the owner's explicit "implement plan 010".
   - Phase lanes and their dependencies are in plan §6.
   - Implementation goes to internal subagents; reviews go to pi sol/high.
   - Codex quota was at 13% on 2026-09-24. When it runs low, switch reviews to an internal Opus subagent.

## Gotchas

- The `/ws` frames are deliberately unfrozen, additive only. T8 updates the AGENTS.md line and the conventions doc.
- On Windows, integration tests need `DOCKER_HOST=tcp://localhost:2375`.
