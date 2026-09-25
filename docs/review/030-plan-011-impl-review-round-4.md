# 030 — Plan 011 implementation review, round 4 (narrow, live-run fixes)

**Date:** 2026-09-24. **Reviewer:** pi `openai-codex/gpt-6-sol:high`. **Scope:** `f5b2982..9c37735` live-run fixes of
plan 011 T8 and, merged from plan 010, its T13 fixes (`git diff f5b2982..HEAD -- src tests web`). Owner-approved
escalation ("Narrow pi review"). Verbatim report below, then the fixes.

---


Scope: `git diff f5b2982..HEAD -- src tests web`. Read-only review; no builds or tests run.

## Blocker

- **A — A deferred failure notice disappears if its replay is cancelled by navigation.** `ReconcileWithHead` replaces the spoken failure with `_replayLead` whenever `_replayOnResume` or `exchange.ReplayDue` is set (`src/PresenterAi.Application/Presenting/Presenter.Training.cs:516-525`). A failed current-slide edit while paused followed by Next/Prev/GoTo before Resume calls `PresentSlide`, which discards both `_replayOnResume` and `_replayLead` without ever delivering the notice (`src/PresenterAi.Application/Presenting/Presenter.cs:962-970, 2060-2110`). The same loss occurs when an exchange with a failed edit ends by navigation: `EndExchange(Navigated)` drops the replay (`Presenter.Asking.cs:901-959`), then navigation clears its lead. Navigating from the last slide to wrap-up (`Presenter.cs:2064-2070, 2041-2053`) does not even clear the lead until close, but still never speaks it. This is the same failure-notice/replay class fixed for immediate replay, Resume, and exchange Resume, missed when the promised replay is abandoned. Preserve a spoken failure notice on non-End abandonment (or otherwise account for it); do not carry the lead to an unrelated slide. Current new tests only exercise paths where the replay actually happens (`tests/PresenterAi.Application.Tests/Presenting/PresenterTrainingTests.cs:584-647`, `PresenterAskTests.cs:227-253`).

## Improvements / oracle gaps

- **B — New-talk edit reset test does not distinguish talk boundaries from arbitrary snapshots.** `web/app/src/store/presenterStore.spec.ts:175-205` only checks the `idle → connecting` transition and the new edit. An incorrect implementation that clears `edits` on *every* non-idle `applySnapshot` also passes: it loses an in-progress edit whenever a presenting/paused snapshot arrives during the same talk. Include a snapshot while the new talk is running and assert its edit survives, in addition to the new-talk reset.
- **B — Playback-window test does not identify check-in playback.** `tests/PresenterAi.Application.Tests/Presenting/PresenterAskTests.cs:2627-2645` supplies one 3,000 ms `Answer` audio event *before* the check-in and no check-in audio event after it begins. A wrong implementation that accounts for answer audio only and ignores voiced check-in audio still passes, despite the stated subject being five seconds after **check-in audio** finishes. Feed a separate voiced check-in frame after check-in begins and check the timeout against that frame's playback end.

## Lifecycle and coverage notes

`_resumeAwaitingVoice` and its generation-guarded timer are cleared through `ClearTimers` on pause, navigation, start/close, and slide presentation, and on a new question; resumed voice or the 15 s fallback arms the regular timer (`Presenter.cs:1099-1131, 2493-2552, 2599-2606`). Exchange-owned playback, mic, check-in and reply fields are discarded with the exchange; the follow-up/filler paths reset `CheckInBegun` (`Presenter.Asking.cs:512-517, 597-608, 839-844, 901-959, 1279-1288`). The late-reply guard is cleared at Ask start, Start and close (`Presenter.Asking.cs:269, 744-757`; `Presenter.cs:704, 2385-2389`). `_replayLead` is reset on start/close and consumed once on an actual replay (`Presenter.Training.cs:121-131, 195-219, 561-575`), subject to the abandonment finding above. The language-sensitive pending prompt and polite replies have focused direct tests; no further missed site established in this range.

## IS THIS BRANCH READY TO MERGE?

**No.** Fix the abandoned-replay failure-notice blocker; the two test-oracle gaps are improvements.

<!-- REVIEW-COMPLETE r011-impl-r4 -->
---

## Fixes

- **A (blocker), abandoned replay:** confirmed. `69b1049` (plan 010): `PresentSlide` takes a pending `_replayLead`
  as the lead of the new slide's instruction instead of discarding it; a held target keeps it for its release
  replay; a slide without narration sends `ScriptEditFailedInstruction`; `StartWrapUp` leads the wrap-up with it.
  Tests: `PresenterTrainingTests.Failed_edit_whose_replay_is_abandoned_by_navigation_leads_the_next_slide` (the
  notice is spoken once and not repeated on the next replay of slide 1),
  `…_abandoned_by_the_wrap_up_leads_the_wrap_up`, and on 011
  `PresenterAskTests.Edit_of_the_current_slide_failing_during_an_exchange_ended_by_navigation_leads_the_next_slide`.
  Sites swept: every path that clears a due replay — `PresentSlide` (all navigation, Ask `Navigated` end),
  `StartWrapUp`, talk Start/close (the talk is gone, so dropping is correct).
- **B, store oracle:** `69b1049`: presenting and paused snapshots inside the new talk keep its queued edit.
- **B, check-in playback oracle:** `9c37735`: two answer chunks 500 ms apart; the window ends 5 s after the
  *chained* playback end. Mutation check: computing playback from "now" instead of the previous end fails it.
