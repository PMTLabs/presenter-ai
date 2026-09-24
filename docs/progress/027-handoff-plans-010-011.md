# 027 — Handoff: plan 010 implemented (awaiting live run), plan 011 press-to-ask in plan review

**Date:** 2026-09-24 12:51. **Mode:** Master Agent. Orchestrate: internal subagents (Agent tool, Opus; Sonnet for plain web) implement, external pi `gpt-6-sol:high` reviews (termflow terminal, report file + completion-tag watcher). Codex quota about 12%. Verify every lane yourself before claiming green.

## Goal
1. **Plan 010** (live presenter training + script versioning) on `feature/010-live-presenter-training`, main checkout `D:\sources\demo\presenter-ai`.
   - Implemented and verified; only T13, the live run, remains before the PR.
2. **Plan 011** (press-to-ask) on `feature/011-press-to-ask`, worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, stacked on 010.
   - Plan draft revision in progress.
3. The owner chose to hold T13 (the live test) until plan 011 is done, then test both together.

## Done
- **Plan 010 is approved:** `docs/plan/010-live-presenter-training.md`. Planning commit `9b8e61a`.
- **T1–T12 merged into `feature/010-live-presenter-training`.** Merge commits: lane D `d99d476`, B `3176a47`, A `b3e0fa5`, E (T10) `a0b1b2d`, C `4e32a2f`, fix `be1b79f`, F (T11/T12) `c68bbea`.
- **Implementation review round 1** (`docs/review/024`): fixes merged in `c8d6f06`. The concurrency core came back clean. Report and ledger row committed in `b39fde9`.
- **UI polish merged `dea8208`:**
  - trainer toggle bug fixed with a new additive `trainer_state` frame
  - accessible switch with a custom tooltip
  - header row: "← Library" left, info bar right
  - top-center toasts
- **Last full verification (dea8208):**
  - build: 0 warnings
  - Application 523, Infrastructure 201 (4 skipped), Api 275, Cli 19, Integration 109 (1 skipped)
  - web: lint clean, 142 app + 20 shared tests, build OK
- **Local environment:**
  - the dev database is migrated (`PresentationRevisions` applied)
  - the API is running from the main checkout on 47913 (my `dotnet run --no-build`; compose dev connection string via env, never print it)
  - Vite `bun run dev:app` is on 47914
- **Plan 011:**
  - G1 confirmed, with owner edits:
    - an **Extend** button keeps an ask open
    - transcription is a **disabled plug-in** (`IAskTranscriber`), because the owner's transcribe model is not real-time; a later pass will add it
  - Mechanism = Option 2: pressing Ask pauses and mutes upstream, the server records mic PCM, silences are compressed, and the recording is sent as a burst on Ask done.
  - Other choices: en/vi "ask done" lexicon (suffix match, inert until a transcriber exists); check-in after the answer; 90 s quiet timeout (with speech it counts as done, without speech it cancels and stays paused); everyone gets Ask, with the A key; Enter or A finishes.
  - Draft `docs/plan/011-press-to-ask.md` (uncommitted, in the worktree).
  - External review round 1 saved: `docs/review/025-plan-011-plan-review.md`, with a ledger row (uncommitted in the worktree). Result: 5 blockers (C1, A1, D2, A2, B1) and 5 improvements.

## In progress
- The plan-011 drafting subagent is revising the plan for review 025.
  - Class fixes:
    - one loop-only `AskExchange` phase that every model-facing emitter consults
    - ordered bridge admission
    - a send-failure boundary
    - an atomic ask start
    - transcriber revisions
    - a stronger T1 oracle
    - the muted-Ask toast
  - It will reply with a summary. Continue it with SendMessage to agent `a2908fce246c92ade` (the plan drafter) if needed.
- The pi reviewer terminal for 011 is `tm-d369597a1` (idle, pi sol/high, cwd = the 011 worktree). Reuse it for a round-2 confirmation.
- The old 010 reviewer terminal is `tm-1985a2af0` (idle). Keep it for the 010 implementation review round 2 after T13, or close it.

## Next steps
1. When the revision arrives, check it. Then run one pi round-2 confirmation (`tm-d369597a1`, report `scratchpad\r011-plan-r2.md`, tag `<!-- REVIEW-COMPLETE r011-plan-r2 -->`).
2. Fold in the round-2 findings, save them as `docs/review/026-…`, and add a ledger row.
3. The owner chose "External review, then approve". After the fixes, set the Status to Approved with an approval-log row, and commit the plan, the reviews and the ledger on `feature/011-press-to-ask`. If round 2 shows design-level blockers, ask the owner first.
4. **Implement 011.** T1, the live probe (`presenter-cli ask-probe`), gates everything; a FAIL means stop and go back to the owner. After that: T2 contracts → lanes (T3 lexicon, T4 presenter → T5 bridge, T6 web) in worktrees → merge and verify → T7 audit → implementation review → T8 live run.
5. **Joint live run with the owner**, covering 010 T13 (Vietnamese deck K2 Bài 1: Trainer mode, "có", Train on this, revert) and 011 T8. Record both in the work log with usage seconds, then open the PRs into `develop` (ask before pushing).

## Gotchas
- **Worktrees:** use worktrees under `.claude/worktrees/` for .NET builds. The main checkout's Api `bin` is locked while the API runs; the full-verification worktree is `010-verify` (detached).
- **Flaky test:** `BridgeBillingGuardTests.Silent_browser_is_aborted…` fails intermittently (1 in 6); it existed before this work. Don't weaken it.
- **Shell and database:** `docker exec` output is swallowed in this shell, so check the database through the API or tests. Integration tests need `DOCKER_HOST=tcp://localhost:2375`.
- **Another shell once restarted the API** (with compose dev settings). Check the owner of port 47913 before assuming it's yours.
- **Test changes allowed by plan 010:** the managed tool count 6→7, the oversize bound 4→16 KiB, and banner → toast.
