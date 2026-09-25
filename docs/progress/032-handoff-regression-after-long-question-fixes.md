# 032 — Handoff: full T8 + T13 regression on the final build, after the long-question fixes

**Date:** 2026-09-24 21:27. **Mode:** Master Agent. Replaces 031.

## Goal
The owner said "Run the test now": a full live regression of plan 011 T8 (14 rows) and plan 010 T13 (12 rows, with the
feedback proof), **in one pass on the final build**, then the PRs. The owner already approved "Push + open both PRs";
do not merge.

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch
  `feature/011-press-to-ask`, HEAD `b1bb46d` (final build). Not pushed.
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`, HEAD
  `69b1049`, pushed. No PR yet.
- **My background shells**, all from the 011 worktree; stop them with TaskStop:
  - API on 47913 with `Training__ReviserTimeoutSeconds=10`, task `bziwawg9o`, log `scratchpad\api-reg5.log`
  - Vite on 47914 (`bd2jjdu24`)
  - WAV server on 47915 (`bbwzavrez`)
  - Stop the API before any `dotnet build`, because it locks the DLLs.
- Scratchpad = `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
- **Chrome:** "Browser 1", tab 1324748940, Ricoh deck `/present/prs_e3a8a399ef4aea02`.
  - After any reload, inject the harness in one call:
    `const src=await (await fetch('http://localhost:47915/fm-harness.wav')).text(); (0,eval)(src);`
  - The script is `scratchpad\live-wavs\fm-harness.wav`, a copy of `fm-compact.js`, with the helpers `k(key)` and
    `evf(since)`.
  - Keep each JS call under 45 s. A timeout does not stop the page run; read the log afterwards.
- **Decks:** Ricoh at v11 (the v1 text); Vietnamese `/present/prs_c129d541fdc8fad4` at v4 (the v1 text). Revert both
  to v1 after testing.

## Done this session (all committed on 011)
- **`58a7839` + `ac588dd` — residual narration.**
  - The residual is the rest of the response that was running at Ask start; the upstream streams or holds it and
    releases it after the unmute.
  - It is held (not forwarded, not the answer) when both hold:
    - the ask is a candidate: the model was voiced within 700 ms before Ask start, or while listening;
    - the first voiced frame comes less than 1,000 ms after the send (`ResidualStartMs`).
  - It is held until a 700 ms voiced gap. Live proof: "residual model audio at send; held until a gap".
  - Transcript text leads audio by about 400–600 ms, so the answer is not clipped.
  - The existing tests' fixtures advance `ResidualStartMs` in `Harness.ToAwaitingAnswer`; the owner's option (a), no
    assertion changes.
- **`4d43109` + `b1bb46d` — capped question (row 12).**
  - The cut-off nudge says "Answer it now … Then stop and wait."
  - A `limit_sent` burst's answer budget is `AnswerStartBudgetMs` + kept speech (40 s at the cap).
  - The nudge only extends the budget: at least 15 s from the nudge, never shorter.
  - Evidence: the upstream took the cut-off 25 s burst in at about its own length and replied at +24.6 s.
- **`619a826` — backlog `docs/requirement/003-backlog-transcribed-ask.md`** (owner decisions): plan 012 after the
  PRs.
  - Use `gpt-transcribe` (same Azure resource), batch on Ask → Ask done.
  - Questions over 25 s go as text.
  - The "You:" chat line comes from the transcript.
  - Add a Clear button.
  - Also the chat panel's 1 s line splitting, and never-heard narration shown in it.
- **Suites at `b1bb46d`:**
  - Application 790, Api 292, Cli 85, Integration 109 (1 skipped).
  - Infrastructure 205 (4 skipped), but it failed 1 test on the first run after a full-solution build, twice.
    Six later runs were clean.
  - Capture the name with `--logger "console;verbosity=normal"` on the next full run and note it in the work log.
  - web was not re-run (no web change since 20 + 167).

## Live results so far (NOT the final build; everything must be re-run on `b1bb46d`)
- T8 rows 1, 2, 3, 4, 5, 10, 13 passed on `ac588dd`.
- Row 12 passed on `4d43109` (the model delegated and answered).
- The stop-at-cap case failed on `4d43109` because of the budget race, now fixed in `b1bb46d`; not yet re-run live.

## Next steps
1. **Reload the tab, inject the harness, Start, then Prev to slide 2.** Run T8 rows 1–14 (plan 011 §7) on
   `b1bb46d`.
   - **Row 2:** use clip `en-goto` ("go to the slide about the delivery roadmap"), which delegates. `en-lookup` and
     `t13-q` get direct answers.
   - **Row 12:** run it twice:
     - `en-long` played in full;
     - `sayCut` (stop the clip at the cap; recreate the helper as in this session), expecting either a nudge reply
       within 40 s or a direct answer.
   - **Extra:** a long question by Ask done: `en-pre` then `en-q1a` and `en-q1b`, about 20 s kept, expecting an
     answer in about 8 s with no residual taken as the answer.
   - **Row 11:** on the Vietnamese deck (`vi-q1a`, a 6 s pause, then `vi-q1b`).
   - **Row 14:** record usage and latencies.
2. **T13 rows 1–12 with the feedback proof** (see handoff 031, Next steps 2): baseline probe, the `ver(n)` diff,
   the replay, a same-talk probe, a fresh-session probe, and revert → probe.
   - Also the Vietnamese deck (`vi-edit`, `vi-probe`), row 9 (revert while pending), row 11 (timeout), and the
     round-4 case (fail while paused, then Next).
3. **Work log** `docs/progress/002-work-log-phase0.md`: T8 section on 011 and T13 section on 010, including:
   - these fixes;
   - the upstream close-ack timeouts (2 of 3 closes during a response: `connection_lost`, usage unconfirmed, fallback
     worked);
   - the Infrastructure flake.
   - Update plan 010/011 §7 "All rows passed" lines. Commit, merge 010 into 011, push 010.
4. **PRs:** `gh pr create --base develop` for 010; push 011 and open its PR, noting that it stacks on 010. No AI
   attribution; run secrets-guard before each commit; do not merge.
5. Stop my background shells; revert both decks to v1.

## Gotchas
- Never touch port 3000.
- Never print `.env` values.
- Stage explicit files only.
- Chain commits so that any `Failed!` blocks them. A `for` loop's exit status hid a failure once.
- End every reply with "Task done: …".
