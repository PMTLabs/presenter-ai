# 035 — Handoff: T8 passed; T13 rows 1–5 found a repeated-edit defect (fixed on 010, 011 follow-up pending)

**Date:** 2026-09-25 10:50. **Mode:** Master Agent. Replaces 034.

## Goal
Unchanged. Run a full live regression: T8 (plan 011, 14 rows) and T13 (plan 010, 12 rows, with the feedback proof).
Then write the work log and open the PRs (approved: push + open both; **do not merge**).

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch
  `feature/011-press-to-ask`, HEAD `fb4fc7b` (010 merged in) plus this handoff. Not pushed. **Not yet built or tested
  after the `fb4fc7b` merge.**
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`.
  - HEAD is `889e61f`, which includes `94b2099`. Neither is pushed yet; `69b1049` was.
  - All suites were green at `889e61f`: Application 540, Api 275, Cli 19, Integration 109 (1 skipped),
    Infrastructure 201 (4 skipped).
- **Running:** Vite on 47914 (`bxxzw46jg`) and the WAV server on 47915 (`b019f6x0y`).
- **The API is stopped.** Restart it from the 011 worktree as in 034, after the build. Use a new log, e.g.
  `<scratchpad>\api-reg10.log`. Scratchpad = `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
- **Chrome:** tab 1324754442 on the Ricoh deck. After a reload:
  - Inject the harness (see 034).
  - Override `window.confirm=()=>true` before a Revert.
  - Re-create the helpers you need; their code is in this session's transcript, and the pattern is in 034.
    - `fm.trial(clip)`: speak the edit, wait for `waiting for yes`, reply `en-no`.
    - `fm.sayCut`
    - the toasts `MutationObserver`
- **Decks:**
  - Ricoh is at **v16**: v15 = revert to v1; v16 = "program started in 2020" on slide 2 (T13 row 3–5 edit).
  - The Vietnamese deck is at v4.
  - Revert both to v1 at the end.

## Done this session
- **`94b2099` (010), Trainer mode is told to the voice model.**
  - Defect: whole talks said spoken edits aloud instead of delegating them (0 of 2 and 0 of 3; another talk 6 of 6).
  - Fix: `trainer-on`/`trainer-off` instructions are sent mid-talk, at Start with an idle toggle, and after a reconnect.
  - Mutation-checked 6 of 6. Live proof: 3 new talks, 6 of 6 edits delegated.
- **T8, all 14 rows passed.** Rows 1–7 ran on `44e6bd8`, rows 8–14 on `47c3ece`. Results are in
  `<scratchpad>\t8-final.md`; copy them into the work log.
- **T13 so far:**
  - Rows 1–2 passed.
  - Proof (a), baseline: the model had no start year before the edit.
  - Rows 3–4 passed: the question, "yes", "Got it, updating", then the chip Updating… → Updated v16.
  - Proof (b): the v16 diff shows the Before text without 2020.
  - Row 5 and proof (c): the replay spoke the 2020 sentence, **but** a repeated `revise_script` call cut the replay
    short and the rest of slide 2 was skipped.
- **`889e61f` (010), repeated-edit fix.** Consulted with agy first; the report is `docs/review/031-…`.
  - A repeat answers by the edit's state:
    - unsettled: answered as before;
    - applied: `already_done`, and `_replayOnResume` is set when the edit targets the current slide;
      `ResumeAfterQuestion` then replays the slide;
    - failed: a failure result.
  - The approval key now includes the target slides. Auto-advance does not clear approvals; a manual Next does.
  - The backend rule mentions `already_done`.
  - 5 new tests in `PresenterTrainingTests`; mutation-checked 5 of 5. Plan 010 §10 row added.

## Next steps
1. **Finish the 011 follow-up (plan 011 site 10).**
   - Change: in `AnswerRepeatedScriptEdit` (`Presenter.Training.cs`), set `_replayOnResume` only when `_exchange is null`.
   - Why: during an Ask exchange, the Ask resume (`ResumeAfterAskInstruction`) continues the slide. Otherwise
     `ResumeAfterExchange` → `ResumeAfterQuestion` would replay it from the start.
   - A flag set before an Ask is still taken over by the exchange (`Presenter.Asking.cs:287`), which is correct.
   - Add a test in `PresenterAskTests`: a repeat during AwaitingAnswer gets `already_done`, and the exchange ends
     with the Ask resume instruction, with no `slide-1-part-1` replay. Mutation-check it.
   - Add a plan 011 §10 row. Run all 011 suites, then commit.
2. Rebuild, restart the API, reload the tab, re-inject the harness, and Start the Ricoh deck with Trainer mode on.
3. Re-run T13 rows 3–5 on a slide without 2020 (slide 3 or 4). Aim for 2–3 edits and watch for
   `edit: repeated request … (already applied)` followed by a full replay. Then continue:
   - proof (d): the same-talk probe `p-2020`;
   - proof (e): a new talk, probe again;
   - row 6: a real question plus "no", giving no version;
   - row 7: Train on this;
   - row 8: `t13-edit5` on slide 3 targeting slide 5;
   - row 9: `t13-edit9`, then Revert while it is pending;
   - row 10: the Vietnamese deck, `vi-edit` + `vi-yes`, then Train on this and `vi-probe`;
   - row 11: `t13-long*` with the 10 s reviser timeout, expecting a spoken failure and a red chip;
   - the round-4 case: fail an edit while paused, then Next;
   - row 12: End, restart, record usage;
   - proof (f): Revert to v1, restart, probe.
4. Write the work log (`docs/progress/002-work-log-phase0.md`): a T8 section on 011 (from `t8-final.md`) and a T13
   section on 010, including both live fixes. Update the plan §7 "All rows passed" lines. Commit, merge 010 into 011,
   push 010, then open both PRs (010 → develop; 011 → develop, noting that it stacks on 010). **No merge.**
5. Stop the shells, close the tab, and revert both decks to v1.

## Gotchas
- Standing rules apply:
  - never touch port 3000
  - never print `.env` values
  - stage explicit files only
  - run secrets-guard before each commit
  - no AI attribution
  - end every reply with "Task done: …"
- The backend sometimes routes a plain question (`t13-q`, testing tools) to `revise_script`. The confirmation guard
  stops it ("not confirmed"). This is known, not a defect of this run.
- A repeated `revise_script` needs two `response.completed` events per delegation in tests: the tool round, then the
  answer. The harness's `Ask` helper leaves delegation `d` open, so finish it first (see `RepeatRequest`).
