# 028 — Handoff: plan 011 implemented, fixing implementation review round 1

**Date:** 2026-09-24, 17:45. **Mode:** Master Agent.
- Internal subagents (Opus) implement and fix.
- `agy` handles isolated lanes.
- pi `gpt-6-sol:high` reviews; codex quota is about 11%.
- Verify every lane yourself before claiming it is green.

## Goal
1. **Plan 011, press-to-ask:** branch `feature/011-press-to-ask`, worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, stacked on plan 010.
   - Implemented T1–T7; the round-1 review fixes are in flight.
2. **Plan 010:** branch `feature/010-live-presenter-training`, main checkout. Only T13, the live run, remains.
3. After fixes: one joint live run with the owner (011 T8 + 010 T13), then PRs into `develop`. Ask before pushing.

## Done
- **T1 live probe PASSED**, after many rounds. Evidence is in `docs/progress/002-work-log-phase0.md`, last sections.
  - The probe uses `presenter-cli ask-probe`, `presenter-cli tts` (gpt-audio-1.5 on Azure) and `scripts/run-ask-probe-suite.ps1`. The WAVs are in the scratchpad `ask-probe-wavs`.
- **Owner decisions, all in the plan's §10 approval log:**
  - 25 s kept-speech cap, sent unpaced (P-1)
  - turn-taking check-in: a new utterance after at least 1.5 s of quiet (P-13)
  - never mute the upstream during an answer (P-16)
  - Continue button (P-17)
  - wait for the unmute ack, then a 200 ms lead-in (P-18)
  - 1011 close on overflow (P-14)
  - saved transcript: record the question like any other
- **Lanes merged into `feature/011-press-to-ask`:**
  - T2 contracts `2539b17`
  - T3 lexicon (agy) `15cc555`
  - T6 web (agy) `0327fb6`
  - T4 presenter (Opus) `87cef21`
  - T5 bridge (Opus) `e92ad88`
  - T7 audit `6ec8751` and `558b1db` (`docs/research/010-plan-011-wiring-audit.md`)
- **Verification at `e92ad88` and later:**
  - build: 0 warnings
  - Application 668, Infrastructure 205 (4 skipped), Cli 85, Api 291, Integration 109 (1 skipped)
  - web: lint clean, 164 app + 20 shared tests, build OK
- **Implementation review round 1** is saved as `docs/review/027` with a ledger row, committed `8ae2e4a`. It found 3 blockers and 1 owner decision; the owner decided both questions (see below).

## In progress (two Opus agents, parallel, separate files)
- **Presenter fix agent** (resume with SendMessage to agent `aa1b6c98d0ef1c8da`)
  - Worktree `.claude/worktrees/011-lane-presenter`, branch `feature/011-lane-presenter`, based on `8ae2e4a`.
  - It fixes review findings 1, 2, 3, 4, 5, 8 and 9:
    - #1: correlate the unmute ack by `client_event_id`
    - #2: keep deferred notices across the send-failure reset
    - #3: voice yes/no for a tool confirmation during an answer, with the 1.5 s turn-taking rule — owner approved
    - #4: a second Ask during the answer is a follow-up question: stop the answer, ask, answer, then check in and resume from the ORIGINAL interrupted sentence. No "paused" notice. The web shows Ask during `answering` — owner approved
    - #5: dispose the transcriber once
    - #8: qualify the plan's "one gated test per row" claim
    - #9: fix the stale diagram
- **Bridge test fix agent** (resume with SendMessage to agent `a75bdb67f7ab912eb`)
  - Worktree `.claude/worktrees/011-lane-bridge`, branch `feature/011-lane-bridge`, based on `8ae2e4a`.
  - It fixes #6 (exact burst-stream oracle) and #7 (fake-clock ack wait, removing the G4 race) in `BridgeAskTests.cs` and `BridgeAdmissionTests.cs`.

## Next steps
1. When the agents report, merge the presenter lane first, then the bridge lane, into `feature/011-press-to-ask`. Reconcile the ack-id API change in the bridge tests if needed.
2. Verify in the 011 worktree:
   - `dotnet build PresenterAi.slnx -warnaserror`
   - all suites; Integration needs `DOCKER_HOST=tcp://localhost:2375`
   - `cd web && bun run lint && bun run test && bun run build`
   - `bash scripts/secrets-guard.sh`
   - Re-run one mutation yourself, e.g. accept any ack.
3. Implementation review round 2 (confirmation), pi sol/high:
   - Terminal `tm-dbeffa917` is idle in the 011 worktree; reuse it.
   - Report to the scratchpad `r011-impl-r2.md`, with tag `<!-- REVIEW-COMPLETE r011-impl-r2 -->`.
   - Save it as `docs/review/028-…` and add a ledger row. Round 3 means escalating to the owner.
4. Joint live run with the owner: 011 T8 (plan §6 T8), plus 010 T13 (Vietnamese deck K2 Bài 1, Trainer mode, "có", Train on this, revert).
   - The API must run from a checkout containing 011. The main checkout is on 010; restart the API from the 011 worktree or merge first. The API currently runs on 47913 from the main checkout — check the port owner's PID before stopping it.
   - Record both runs in the work log with usage seconds.
5. PRs: 010 into `develop`, then 011. Ask the owner before any push.

## Gotchas
- **Review inputs:** `scratchpad\011-impl-review-inputs.md`.
- **Known pre-existing issues:**
  - React "not wrapped in act()" warnings in `Present.spec.tsx`
  - the intermittent `BridgeBillingGuardTests.Silent_browser_is_aborted…` (don't weaken it)
- **agy terminals:** the trust prompt needs `execute_command` with an empty command and `cliType: "codex"` to press Enter. Close the agy terminals after their lanes merge; they are all closed now.
- **Shell:** `docker exec` output is swallowed in this shell.
- **Ports:** never touch 3000; the API is on 47913 and Vite on 47914 (background shells from the pre-compaction session).
