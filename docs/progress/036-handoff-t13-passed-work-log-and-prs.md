# 036 — Handoff: T13 passed on the final build; work log, PRs and cleanup remain

**Date:** 2026-09-25 13:35. **Mode:** Master Agent. Replaces 035.

## Goal
Unchanged. The full live regression is now done: T8 (14/14, handoff 035) and T13 (12/12 plus proofs a–f and the
round-4 case). Remaining: the work log, then the PRs (approved: push + open both; **do not merge**), then cleanup.

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch
  `feature/011-press-to-ask`, HEAD `047da8e` plus this handoff. Not pushed. All suites green at `047da8e`:
  Application 827, Api 292, Cli 85, Infrastructure 205 (4 skipped), Integration 109 (1 skipped).
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`, HEAD
  `7126d97`. Not pushed: `94b2099`, `889e61f`, `7126d97`. Suites green at `7126d97` (Application 555, Api 275, Cli 19,
  Infrastructure 201 (4 skipped), Integration 109 (1 skipped); one Integration flake, see t13-final.md item 5).
- **Running (mine, stop with TaskStop):** API 47913 `b8inhcr1w` (log `<scratchpad>\api-reg13.log`, timeout 10 s),
  Vite 47914 `bxxzw46jg`, WAV 47915 `b019f6x0y`. Scratchpad =
  `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
- **Docker:** WSL was restarted by the owner; the original containers `presenter-ai-postgres-1` (5433) and
  `presenter-ai-redis-1` (6382) were started with `docker start` (compose from the worktree creates a different
  project, `011-press-to-ask`; its containers were removed, its volume kept). Run compose/docker inside WSL with
  `MSYS_NO_PATHCONV=1 wsl -d Ubuntu-24.04 -e sh -c '...'`. Integration tests need `DOCKER_HOST=tcp://localhost:2375`.
- **Chrome:** tab 1324754442 on the Vietnamese deck `/present/prs_c129d541fdc8fad4`, a talk **paused** (End it).
- **Decks:** Ricoh at **v30** (= v1 content, reverted). Vietnamese at **v6** → revert to v1 at the end.
- The termflow MCP was disconnected this session (pi ran via `pi -p` in Bash instead).

## Done this session
- `f72be23` (011): interaction test, repeat inside an exchange replays once (no production change).
- **Row 5 defect:** the repeat's unconditional replay spoke a finished slide twice. Investigated, pi (gpt-6-sol
  medium) reviewed (docs/review/032). Fixed:
  - `7126d97` (010): `NarrationCoverage` + `_repeatReplayDue`/`SettleRepeatReplay` — replay only when the output
    transcript since the slide was presented does not cover it (≥90 % words in order + last 6 words together + all
    parts sent). 8 + 7 tests, mutation-checked 6 of 6. Plan 010 §10 row.
  - Merge `9deb3c4`; `047da8e` (011): settle at Ask start before `ReplayDue` takeover; test mutation-checked. Plan
    011 §10 row.
- **T13 live results** are in `<scratchpad>\t13-final.md` (all rows pass; row 8 run 1 recorded as a model early stop
  by owner decision; known issues 1–5 listed there). T8 results: `<scratchpad>\t8-final.md`.

## Next steps
1. End the paused Vietnamese talk (record usage). Collect reviser token counts from api-reg11/13 if logged.
2. Write the work log `docs/progress/002-work-log-phase0.md`: a T8 section on 011 (from t8-final.md) and a T13
   section on 010 (from t13-final.md, including the live fixes `94b2099`, `889e61f`, `7126d97`, `047da8e` and the known
   issues). Update plan 010 §7 and plan 011 §7 "All rows passed" lines (dates 2026-09-25). Commit the 010 parts on
   010, merge 010 into 011, commit the 011 parts. Run secrets-guard before each commit; stage explicit files.
3. Push 010 (`git push`), push 011 (`git push -u origin feature/011-press-to-ask`), open both PRs to `develop`:
   010 first; 011 noting it stacks on 010. **Do not merge.**
4. Cleanup: revert the Vietnamese deck to v1 (Ricoh already v1 content); stop API/Vite/WAV by task id; close tab
   1324754442.

## Gotchas
- Standing rules: never touch port 3000; never print `.env` values; stage explicit files; secrets-guard before each
  commit; no AI attribution; end every reply with "Task done: …"; ask before merging.
- Minimum `Training:ReviserTimeoutSeconds` is 10 (validated at startup).
- Stop the API before any `dotnet build` (DLL locks).
