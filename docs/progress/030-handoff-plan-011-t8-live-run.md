# 030 — Handoff: plan 011 T8 live run (automated in Chrome), mostly passing

**Date:** 2026-09-24, 19:28. **Mode:** Master Agent.
- Opus subagents implement: the presenter agent `aa1b6c98d0ef1c8da` in lane `011-lane-presenter` has full context.
- pi sol/high reviews.
- Verify every lane yourself.
- This handoff replaces 029.

## Environment
- **Plan 011:** worktree `D:\sources\demo\presenter-ai\.claude\worktrees\011-press-to-ask`, branch `feature/011-press-to-ask`, HEAD `afacb58` + this handoff. The tree is clean.
- **Plan 010:** main checkout `D:\sources\demo\presenter-ai`, branch `feature/010-live-presenter-training`. Only its live run, T13, remains.
- **Running background shells (mine), all started from the 011 worktree:**
  - API on 47913 (log `scratchpad\api-011d.log`)
  - Vite on 47914 (`scratchpad\vite-011.log`)
  - a WAV server on 47915: `bun scratchpad\wavserve.ts scratchpad\live-wavs`, serving the TTS clips with CORS
- Scratchpad = `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\93de0d4e-38f1-485e-ae5b-9dc72c8d8d42\scratchpad`.
- **Chrome:** the owner picked "Browser 1" (deviceId `10a4dbee-…`). If tools say the page is unknown, run `tabs_context_mcp`, then `select_browser` that id. The tab is 1324748940 on `/present/prs_e3a8a399ef4aea02` (the Ricoh deck), signed in through the dev sign-in.

## How the automated live run works (reuse it)
- **Injected page script `window.__fm`:**
  - It overrides `getUserMedia`, creating a fresh `MediaStreamDestination` for each call, and wraps `WebSocket` to log every non-audio frame to `__fm.log` with a timestamp.
  - `__fm.say(name)` plays `http://localhost:47915/<name>.wav` into the fake mic AND the speakers (the owner listens on headphones).
  - Helpers: `btn(label)`, `ask(clips)`, `waitCheckIn()`, `ev(since)`, `tx(since)`, and `out(s)`, which sanitizes output (the tool blocks output that looks like a query string, and caps at about 1.5k chars).
  - A page reload loses the script: re-inject it and click Start as a trusted click.
  - JS calls must stay under 45 s.
- **Clips in `live-wavs`** (gpt-audio-1.5, voice marin): `en-q1a/q1b` (team size / active products), `en-q2` (backlog), `en-follow` (VPD stakeholder, delegated), `en-yes`, `en-no` ("No, thank you."), `en-nope` ("No."), `en-edit`, `en-edit2`, `en-long` (27 s), `vi-q1a/q1b`, `vi-yes` ("Có."), `vi-no` ("Không, cảm ơn.").
  - The TTS model sometimes answers a question instead of reading it; rephrase and retry when that happens.

## T8 results so far (the Ricoh deck; record them in the work log)
- **Passed live:**
  - 1: flush in about 10 ms.
  - 2: a late delegated result is ignored.
  - 3: 10 s gap, one answer.
  - 4: "Yes" resumes the interrupted sentence (after the fixes).
  - 5: Pause, Ask, then "No, thank you" leads to "check-in answered no, waiting on the slide".
  - 6: 90 s cancel toast.
  - 7: Extend, then sent after 90 s.
  - 8: an edit applied during Ask listening is replayed once after the check-in (v3).
  - 9: End mid-question, then reopen shows idle.
  - 10: muted Ask shows the toast and a disabled button.
  - 12: 25 s cap, `limit_sent`, toast.
  - 13: Mute is deferred and Continue works.
  - 14: a follow-up during the answer resumes the original sentence.
- **Still to run:**
  - 11: Vietnamese deck K2 Bài 1, with `vi-*` clips.
  - 15: a voice yes to a tool confirmation during an Ask answer.
  - Plan 010 T13: Vietnamese deck, Trainer mode, "có", Train on this, revert.
- **Usage seconds:** run 1 about 617+, run 2 379.8, run 3 119.8, run 4 386.4, run 5 382.8.
- **Minor observation, not fixed:** leftover narration audio right after `ask_done` can log "answered after 181 ms". This was harmless, because the check-in window follows playback.

## Defects the live run found, all fixed by the presenter agent, merged, and suites green
- `b5d7e5b`: resume waits for the resumed audio, with a 15 s fallback.
- `45bb84e`: speech before a delegation is filler.
- `7bf6f3d`: the resume after an Ask names the slide and ends the pause. Root cause: PauseInstruction said "until told", and the old resume text was ignored live.
- `41e776b`: a check-in reply survives a model blip that re-enters Answering.
- `7041027`: owner decision "hold for speech". The 5 s window starts after check-in playback, is held while mic speech is heard plus 3 s for the transcript lag (about 2.5 s live), bounded at +10 s. Late replies no longer open a question hold.
- `35aabc2`: owner decision "polite words": yes/no plus thank you, thanks, please, go ahead (after yes only), cảm ơn, ạ.
- **Verification at `afacb58`:**
  - build: 0 warnings
  - Application 765, Api 292, Infrastructure 205 (4 skipped), Cli 85
  - Integration and web not re-run since `f5b2982`; the web is unchanged.

## Next steps
1. **Finish T8:**
   - Steps 11 and 15, and plan 010 T13, on the running servers. Re-inject `__fm` if the page was reloaded.
   - Trainer mode is ON in the Ricoh talk UI.
   - **The Ricoh script is now at v3** (live test edits: "twelve people" on slide 3, the DevOps edit on slide 4). **Revert it to v1** in the Script versions panel after testing. Tell the owner.
2. **Write the T8 and T13 work-log entries** in `docs/progress/002-work-log-phase0.md`: steps, latencies, usage, the defects and their fix commits. Tick the plan §7 runbook rows.
3. **Re-verify the full suites:** Integration needs `DOCKER_HOST=tcp://localhost:2375`; then `cd web && bun run lint && bun run test && bun run build`, and `bash scripts/secrets-guard.sh`.
4. **Review:** the live fixes (`f5b2982..afacb58`) had no external review. The next review would be round 4, so escalate to the owner and propose a narrow pi review of this range.
5. **PRs:** 010 into `develop`, then 011. Ask the owner before any push.

## Gotchas
- **Owner decisions:** they are all in plan §10, including the two new ones above. Don't reopen them.
- **Restarting the API:** stop the background API task before `dotnet build`, because the DLLs are locked.
- **Known pre-existing issues:** act() warnings in `Present.spec.tsx`, and the flaky `BridgeBillingGuardTests.Silent_browser_is_aborted…`.
- **Never touch port 3000.**
