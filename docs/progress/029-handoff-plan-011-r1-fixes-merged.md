# 029 — Handoff: plan 011 round-1 fixes merged; a test hang to investigate, then review round 2

**Date:** 2026-09-24. **Mode:** Master Agent.
- Internal Opus subagents implement and fix.
- `agy` handles isolated lanes.
- pi `gpt-6-sol:high` reviews; codex quota is about 11%.
- Verify every lane yourself before claiming it is green.
- This handoff replaces 028.

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch `feature/011-press-to-ask`, HEAD `56601c3` or later, stacked on plan 010.
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`. Only T13, the live run, remains.
- **Lane worktrees**, all merged and still available: `011-lane-presenter` (fix agent `aa1b6c98d0ef1c8da`) and `011-lane-bridge` (fix agent `a75bdb67f7ab912eb`). Old lanes: `011-lane-lexicon` and `011-lane-web`.

## Done since 028
- **Review 027 round-1 fixes merged:**
  - presenter `5547c91` (merge `4c07896`)
  - bridge tests `4473cce` (merge `56601c3`)
- **What changed:**
  - The unmute ack is correlated by `client_event_id`, with ordinal "ack debt" as a fallback.
  - Deferred notices survive the send-failure reset.
  - Voice yes/no to a tool confirmation now works during the answer, using the 1.5 s turn-taking rule.
  - **Follow-up ask** (owner decision): Ask during the answer stops it, listens again, and resumes from the original sentence; the web shows Ask while `answering`.
  - The transcriber is disposed once.
  - Plan claims corrected.
  - The burst oracle is now exact byte for byte; the ack wait runs on the fake clock.
- **Review record:** 027 has a note on the finding that appeared only in the draft; the ledger count is fixed (`a1114e6`).
- **Verified at `56601c3`:**
  - build: 0 warnings
  - Application 688, Infrastructure 205 (4 skipped), Cli 85, Api 292, Integration 109 (1 skipped)
  - web: lint clean, 166 app + 20 shared tests, both builds OK
  - secrets-guard clean
  - My own mutation (accept any ack) is caught by 4 `Late_ack_of_an_earlier_ask…` tests.

## Open issues (found by the orchestrator; details in `scratchpad\011-impl-review-inputs.md`)
1. **Application suite HANG:** in 6 back-to-back `dotnet test tests/PresenterAi.Application.Tests --no-build` runs, run 1 passed and run 2 hung for more than 9 minutes before I killed it. No testhost was left running. This is intermittent, and must be found before merging.
2. **Possible flake:** `PresenterExternalToolTests.Approved_run_after_moving_on_is_not_announced(action: "end")` failed once, during a mutation run.

## Next steps
1. Delegate the hang to an Opus subagent in `011-lane-presenter`, after fast-forwarding it to `feature/011-press-to-ask`:
   - Loop the Application suite with `--blame-hang-timeout 60s` (and `--blame-hang-dump-type none` if dumps are heavy) until it reproduces. Find the hanging test.
   - Fix the root cause, whether in a test or in production code. Never weaken a test.
   - Also loop the external-tool test 30 times.
   - Report whether the hang predates plan 011 by trying the same loop on `b39fde9` in a detached worktree.
2. Merge, re-verify everything (commands above; Integration needs `DOCKER_HOST=tcp://localhost:2375`), then loop the Application suite 10 times clean.
3. Implementation review round 2 (confirmation), pi sol/high:
   - Terminal `tm-dbeffa917` is idle in the 011 worktree. Pre-create the scratchpad report `r011-impl-r2.md`, with tag `<!-- REVIEW-COMPLETE r011-impl-r2 -->`.
   - Scope: `558b1db..HEAD` against review 027 plus the owner decisions (follow-up ask, voice confirmation).
   - Save it as `docs/review/028-plan-011-impl-review-round-2.md` and add a ledger row. A round-3 need means escalating to the owner.
4. Joint live run with the owner: 011 T8 (plan §6 T8), plus 010 T13 (Vietnamese deck K2 Bài 1, Trainer mode, "có", Train on this, revert).
   - The API on 47913 currently runs from the main checkout, which is plan 010 only. For the joint run, start the API from the 011 worktree, since it contains 010. Check the owner PID of 47913 before stopping anything.
   - Vite runs on 47914 from the main checkout's web; restart it from the 011 worktree too.
   - Record both runs in the work log with usage seconds.
5. PRs: 010 into `develop`, then 011. Ask the owner before any push.

## Gotchas
- **Known pre-existing issues:**
  - React "not wrapped in act()" warnings in `Present.spec.tsx`
  - the intermittent `BridgeBillingGuardTests.Silent_browser_is_aborted…` (don't weaken it)
- **agy trust prompt:** press Enter with `execute_command`, an empty command and `cliType: "codex"`.
- **Shell:** `docker exec` output is swallowed in this shell. Never touch port 3000.
- **Owner decisions** are all in the plan's §10: P-1 25 s cap, P-13 turn-taking, P-14 1011 close, P-16 no upstream mute during an answer, P-17 Continue, P-18 unmute ack plus lead-in, follow-up ask, voice confirmation during the answer. Don't reopen them.
