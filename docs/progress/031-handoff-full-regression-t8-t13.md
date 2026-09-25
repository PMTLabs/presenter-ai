# 031 — Handoff: full live regression (T8 + T13) on the final build, then PRs

**Date:** 2026-09-24 20:25. **Mode:** Master Agent. Replaces 030.

## Goal
Owner asked: "Run the test now". That means a **full** live regression of both runbooks on the final build, plus
**proof that Trainer mode incorporates feedback**. Then open the PRs: 010 into `develop`, then 011. The owner already
chose "Push + open both PRs"; hold the PRs until the regression passes.

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch
  `feature/011-press-to-ask`, HEAD `79e1142`; code last changed at `9c37735`. Not pushed.
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`, HEAD
  `69b1049`. **Pushed** to origin (tracking). No PR is open yet.
- **Running background shells (mine), all from the 011 worktree:**
  - API on 47913 with `Training__ReviserTimeoutSeconds=10`, log `scratchpad\api-reg.log`
  - Vite on 47914 (`scratchpad\vite-reg.log`)
  - WAV server on 47915
  - Stop them with TaskStop when done (task ids are in the session).
- Scratchpad = `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
- **Chrome:** "Browser 1", tab 1324748940, on the Ricoh deck `/present/prs_e3a8a399ef4aea02`.
  - `window.__fm` is injected: `say`, `btn`, `ev`, `tx`, `edit(clip, yes)`, `ver(n)` (reads a version's
    Before/After), `wait`, `has`, `slide`, `sleep`, `out`.
  - It logs frames by patching `WebSocket.prototype.send`, so it works after load.
  - The full script: `scratchpad\fm-full.js` is the older variant. The compact variant is the last `javascript_exec`
    before this handoff; re-create it if the page reloads, because a Vite HMR reload loses it.
  - Keep each JS call under 45 s. Say "yes" about 3 s after `backend answer ready`: a late yes times out (8 s), an
    early one is ignored while the question plays.
- **Decks:**
  - Ricoh is at v11 (the v1 text).
  - Vietnamese Khóa 2 Bài 1 is `/present/prs_c129d541fdc8fad4`, at v4 (the v1 text).
  - Revert both to v1 after testing.

## Clips (`scratchpad\live-wavs`, gpt-audio-1.5, voice marin)
- **T8:** en-q1a/q1b, en-q2, en-follow, en-lookup, en-yes, en-no, en-nope, en-long, vi-q1a/q1b, vi-yes, vi-no.
- **T13 edits:**
  - t13-edit ("also mention the programme started in 2020")
  - t13-edit-c (explicit "update the script")
  - t13-edit5b (slide 5: steering committee meets the first Tuesday of every month)
  - t13-edit9 (two-week sprints)
  - t13-long3 (every slide twice as long, which times out at 10 s)
  - t13-q (a real question)
  - vi-edit (biểu bì không có mạch máu)
- **Probes:** p-2020 ("In what year did the programme start?"), p-steer ("When does the steering committee meet?"),
  p-sprint, vi-probe ("Biểu bì có mạch máu không?").

## Next steps
1. **T8,** all 14 rows (plan 011 §7) on the Ricoh deck; row 11 on the Vietnamese deck. Record flush latency,
   Ask-done→answer latencies and usage.
2. **T13,** all 12 rows (plan 010 §7), with the **feedback proof** for each edit:
   - (a) **Baseline:** before the edit, ask the probe; expect "not in the deck" or no knowledge.
   - (b) **Diff:** after "applied", `__fm.ver(n)` shows the Before/After with the new sentence absent from Before.
   - (c) **Replay:** the replay or narration transcript contains it.
   - (d) **Same talk:** ask the probe; the answer uses the new fact.
   - (e) **Fresh session:** End, Start a new talk, ask the probe again. A correct answer proves the saved script is
     used, not conversation memory.
   - (f) **Undo:** Revert to v1, restart, probe again; the answer no longer contains the edit.
   - Do the same on the Vietnamese deck with vi-edit and vi-probe.
   - Also cover row 9 (revert while an edit is pending), row 11 (timeout, spoken failure, red chip), and the
     round-4 fix: fail an edit while paused, then Next; the next slide leads with "couldn't apply".
3. **Record results** in `docs/progress/002-work-log-phase0.md`, as a new "full regression" section on the 010
   branch for T13 and the 011 branch for T8. Commit, merge 010 into 011, push 010 again.
4. If there is a defect, fix it on the right branch with a test, re-run the affected rows, and tell the owner.
5. **Open the PRs** only after the regression passes:
   - `gh pr create --base develop` for 010. Summary: plan 010, T13, review rounds, live-run fixes.
   - Push 011, then open its PR into `develop` and note that it stacks on 010.
   - No AI attribution; secrets-guard before each commit. The owner already approved push + PRs; **do not merge**.

## Known, not fixed (in the work log; don't re-litigate)
- Train on this pairs the late question fragment in Ask mode, and its answer includes the "One moment." filler.
  Owner: fix after the PRs.
- The model re-calls `revise_script` after "yes" (de-duplicated), and paraphrases the confirmation question.
- The revert panel's pending notice goes stale.
- Review round 4 is done (`docs/review/030`). The owner declined a round 5.

## Gotchas
- Never touch port 3000.
- Stop the API before `dotnet build`, because it locks the DLLs.
- Never print `.env` values.
- Stage explicit files only.
- End every reply with "Task done: …".
