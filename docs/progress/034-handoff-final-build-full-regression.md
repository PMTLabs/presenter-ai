# 034 — Handoff: final build ready; run the full T8 + T13 regression, then the PRs

**Date:** 2026-09-25 09:40. **Mode:** Master Agent. Replaces 033. The owner said "Now do the automation task for
me", meaning the regression per 033.

## Goal
A full live regression, T8 (plan 011, 14 rows) and T13 (plan 010, 12 rows, with the feedback proof), in one pass on
the final build. Then the PRs (approved: push + open both; **do not merge**).

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch
  `feature/011-press-to-ask`, HEAD `44e6bd8` plus this handoff. **This is the final build.** Not pushed.
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`, HEAD
  `69b1049`, pushed. No PR yet.
- **My background shells** (from the 011 worktree; stop them with TaskStop):
  - API on 47913, running `44e6bd8`: task `be97pv4e0`, log `<scratchpad>\api-reg8.log`
  - Vite on 47914: `bxxzw46jg`
  - WAV server on 47915: `b019f6x0y`
  - Scratchpad = `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
  - To restart the API: read the Postgres password from `docker-compose.yml` into a variable and never print it; set
    `ConnectionStrings__Postgres` (localhost:5433), `ConnectionStrings__Redis` (localhost:6382) and
    `Training__ReviserTimeoutSeconds=10`; then run `dotnet run --no-build --project src/PresenterAi.Api`. Stop the API
    before any build.
- **Chrome:** new tab group, tab **1324754442** on `/present/prs_e3a8a399ef4aea02`, signed in with the dev button
  ("Sign in (Development)", no credentials).
  - After a reload, inject the harness:
    `const src=await (await fetch('http://localhost:47915/fm-harness.wav')).text(); (0,eval)(src);`
  - If the page says "Another presenter page is already connected", click `fm.btn('Take over')`.
  - Trainer mode resets on every Start; turn it on with `fm.btn('Trainer mode')`.
  - Keep each JS call under 45 s. A timeout does not stop the page loop; read the log afterwards.
- **A human-like `askOnce(clip, reply)` helper lives only in the page**, so re-create it after a reload:
  - Ask, the clip, Enter.
  - At each `check-in window`, wait 1.5 s. Reply only if the assistant said 25 or more characters since the send and
    no `delegated after speech` came; otherwise wait for the next window.
  - Background: the upstream creates delegations 4.5–5.7 s after "One moment.", and replying to a filler is a
    harness artifact.
- **Decks:** Ricoh at v13 (v12 = 2020 on slide 2, v13 = DevOps on slide 4); Vietnamese at v4. Revert both to v1 at the
  end.

## Done since 033 (all committed on 011)
- **`c132b68` is proven live.** After four Asks, 2 of 2 spoken edits were delegated (`revise_script … waiting for
  yes`); before the fix, 0 of 4 were.
- **`e31a7ed`, nudge stop on delegation.** A delegation accepted by the exchange stops the answer-now nudge, which
  had fired 0.2 s after "backend answer ready".
- **`e31a7ed`, Responses channel errors.** An error whose message starts with "Responses " (live: `invalid_request_error`
  "Responses websocket closed before a terminal event.") now ends a pending backend delegation, as `backend_error`
  already did. Before, the re-delegated answer counted as filler until the ceiling.
  - Plan §10 row added (2026-09-25). Both fixes are mutation-checked.
- **`44e6bd8`, flaky Infrastructure test fixed.**
  - The test is `LiveSessionTests.Unmuted_server_event_raises_InputAudioUnmuted`.
  - It waited for the first of two handlers on the same event, then read the second, a race under suite load.
  - It now waits for both; the assertions are unchanged. 5 of 5 full runs were clean.
- **Suites at `44e6bd8`:** Application 798, Api 292, Cli 85, Integration 109 (1 skipped), Infrastructure 205
  (4 skipped).
- **Upstream facts for the work log:**
  - A transient backend error, `invalid_request_error` "Responses websocket closed…", happened once; the model said
    "Something went wrong" and re-delegated.
  - Close-ack timeouts: see 032.
  - The last close today was confirmed (`usageConfirmed: true`).

## Next steps
1. Reload the tab, inject the harness, and re-create `askOnce` (see above). Then Start, Trainer mode on, and Next to
   slide 2.
2. Run **T8 rows 1–14** (plan 011 §7) on `44e6bd8`, using 032 Next steps 1 for the per-row notes:
   - row 2 with `en-goto`, then a follow-up Ask during "One moment";
   - row 8 with `t13-edit` or `en-edit2`: confirm, then press Ask on `edit: queued`;
   - row 12 twice: the full `en-long`, and `sayCut`, stopped at the cap;
   - an extra long Ask-done question (`en-pre`, `en-q1a`, `en-q1b`);
   - row 11 on the Vietnamese deck `/present/prs_c129d541fdc8fad4` (`vi-q1a`, a 6 s pause, `vi-q1b`);
   - row 14: usage and latencies.
   - If any row fails, stop and report to the owner.
3. Run **T13 rows 1–12** with the feedback proof (031/032 Next steps 2).
4. Write the work log in `docs/progress/002-work-log-phase0.md`, update the plan §7 "All rows passed" lines, commit,
   merge 010 into 011, push 010, and open both PRs. No merge.
5. Stop my shells, close tab 1324754442, and revert both decks to v1.

## Gotchas
- Standing rules apply:
  - never touch port 3000
  - never print `.env` values
  - stage explicit files only
  - run `bash scripts/secrets-guard.sh` before each commit
  - no AI attribution
  - end every reply with "Task done: …"
- **Clips:**
  - `p-2020` is a question.
  - `en-edit` is sometimes spoken instead of delegated.
  - `t13-edit`, `t13-edit-b` and `en-edit2` delegate.
