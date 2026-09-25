# 033 — Handoff: T8 regression found two more defects; fixed, live re-run pending

**Date:** 2026-09-24 22:10. **Mode:** Master Agent. Replaces 032. The owner stopped for the night and continues
tomorrow from this file.

## Goal
Unchanged from 032. The owner said "Run the test now": a full live regression, T8 (plan 011, 14 rows) and T13
(plan 010, 12 rows, with the feedback proof), in one pass on the final build. Then the PRs (approved: push + open
both; **do not merge**). On 2026-09-24 the owner chose "Fix both, then re-run" for the two defects below.

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch
  `feature/011-press-to-ask`, HEAD `c132b68` plus this handoff (the final build candidate). Not pushed.
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`, HEAD
  `69b1049`, pushed. No PR yet.
- **Nothing is running.** The API, Vite and the WAV server were stopped. Restart them from the 011 worktree:
  - API on 47913, as in 032: read the Postgres password from `docker-compose.yml` into a variable and never print it;
    set `ConnectionStrings__Postgres` (localhost:5433) and `ConnectionStrings__Redis` (localhost:6382); set
    `Training__ReviserTimeoutSeconds=10`; then run `dotnet run --no-build --project src/PresenterAi.Api` in the
    background, with the log in the scratchpad. Stop the API before any `dotnet build`, because it locks the DLLs.
  - Vite on 47914: `cd web && bun run dev:app`.
  - WAV server on 47915: `bun <old scratchpad>/wavserve.ts <old scratchpad>/live-wavs`. The old scratchpad is
    `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
    It holds the clips, `fm-compact.js` and `live-wavs\fm-harness.wav`.
- **Chrome:** "Browser 1", Ricoh deck `/present/prs_e3a8a399ef4aea02`. Reload the tab, then inject the harness:
  `const src=await (await fetch('http://localhost:47915/fm-harness.wav')).text(); (0,eval)(src);`
  - Keep each JS call under 45 s.
  - Preload clips with `fm.load(name)` before timed steps.
  - Answer a check-in inside the same call: `await fm.wait(t,/check-in window/,20000); await fm.say('en-yes')`.
    Across calls the reply lands after the 5 s window.
  - Trainer mode resets on every Start. Turn it on after Start with `fm.btn('Trainer mode')`.
- **Decks:**
  - Ricoh is at **v13**: v12 added the 2020 start date to slide 2; v13 added "DevOps support is shared by two
    engineers" to slide 4.
  - The Vietnamese deck is at v4 (v1 text).
  - Revert both to v1 at the end.

## Done this session (committed on 011)
- **`c6cca0c`, answer-now nudge.**
  - Defect (row 2 on `b1bb46d`, 1 of 3 runs): a follow-up question during a delegated answer got no answer. So did
    the next direct question. The model still obeyed resume instructions.
  - Fix: every non-`limit_sent` send gets one nudge, `PromptBuilder.AskAnswerNowInstruction()`.
  - It fires 6 s after the send (`AnswerNudgeMs`), kept at least 3 s after the last user delta
    (`AnswerNudgeQuietMs`).
  - It is skipped once there is answer audio, a pending tool or a backend delegation, and the budget is unchanged.
  - Log: `ask: no answer yet; asked the model to answer the question now`.
- **`c132b68`, the Ask's own pause and resume wording.**
  - Defect: after 3–4 Asks, plain speech went unanswered: 4 of 4 spoken edit requests in two sessions, with 15 s of
    silence each. A fresh session with no Ask delegated the same clip at once.
  - Fix: Ask start appends `AskPauseInstruction()` ("a listener is asking a question … then answer it") with the same
    `pause-N` id. Manual Pause keeps the Node golden `PauseInstruction()`.
  - `ResumeAfterAskInstruction` adds "If someone speaks to you afterwards, answer them or pass on their request as
    usual."
- **Plan 011 §10:** new rows for both fixes, including the analysis of row 8's "One moment."
  - That case was a harness artifact: the harness said "yes" 0.3 s into the check-in, and the late delegation came
    3.4 s in, which the existing filler rule covers.
  - No code change. Re-test it with a **silent** check-in.
- **Suites at `c132b68`:**
  - Application 796, Api 292, Cli 85, Integration 109 (1 skipped), Infrastructure 205 (4 skipped).
  - The Infrastructure flake did not occur (incremental build).
  - Web was not re-run (no web change).

## Live results on `c6cca0c` (before `c132b68`, so they must be re-run)
- **Rows 1–7 passed:**
  - Row 1: flush 9 ms after Ask.
  - Row 2: residual held, follow-up answered in 1.7 s.
  - Row 3: one answer covering both halves.
  - Row 4: "yes" gave "confirmed resuming".
  - Row 5: "no" gave stay.
  - Row 6: `quiet_cancelled` at 90.0 s, countdown shown.
  - Row 7: Extend reset the countdown, then `quiet_sent` with its toast.
- **Row 8:** Ask during a pending edit, no replay during the answer, and one replay (v13) after the check-in.
  Re-run it with a silent check-in.

## Next steps
1. Restart the three servers (Environment), reload the tab, inject the harness, and Start.
2. **Prove `c132b68` live first:** in one session, do 4 Asks, then speak `t13-edit-b` and `en-edit2` without Ask.
   - Expect both to be delegated (`revise_script … waiting for yes`); before the fix they were ignored.
   - Also watch for any `ask: no answer yet` nudge and whether an answer follows it.
   - If they are still ignored, stop and report to the owner.
3. Then run the full T8 rows 1–14 on the final build (032 Next steps 1 has the per-row notes: row 12 run twice, the
   extra long Ask-done question, row 11 on the Vietnamese deck, row 14 usage).
4. Run T13 rows 1–12 with the feedback proof (032 Next steps 2).
5. Write the work log, update plan §7, commit, merge 010 into 011, push 010, open both PRs. No merge (032 Next steps
   3–4).
6. Stop the servers and revert both decks to v1.

## Gotchas
- Standing rules apply:
  - never touch port 3000
  - never print `.env` values
  - stage explicit files only
  - run `bash scripts/secrets-guard.sh` before each commit
  - no AI attribution
  - end every reply with "Task done: …"
- **Clips:**
  - `p-2020` is a question, not an edit.
  - `en-edit` got a spoken change rather than a delegated edit.
  - `t13-edit` and `en-edit2` delegate in a fresh session.
- The upstream can create a delegation up to about 5 s after its "One moment." filler.
