# Implementation review — round 3 (`r011-impl-r3`)

Scope: **only** `0112bb9..51d1c33`, merged at `797112b`. Review only; no repository edits, builds or tests.

## Findings

**None in this diff.** Confirmation of the four round-2 findings:

1. **Confirmation attribution:** `OnToolConfirmationStarted` increments the generation on *every* new pending tool confirmation, including outside an exchange (`Presenter.cs:1650-1652`; `Presenter.Asking.cs:627-635`). It cancels an already-open exchange utterance and its debounce without clearing `LastUserDeltaAt`. A fresh utterance must clear the 1.5 s quiet gate; its confirmation generation is captured at its **first** delta and checked on completion (`Presenter.Asking.cs:594-618,657-687`). An utterance begun before confirmation cannot settle it, and one begun for a settled/replaced confirmation is ignored. `UtteranceConfirmation` is cleared on completion, on discarded pre-confirmation replies, and when reusing the exchange for a follow-up (`:633,664,1145-1146`); ending the exchange drops the entire object. The delta → tool → debounce and fresh yes/no paths have distinguishing tests (`PresenterAskTests.cs:2143-2176`). **Fixed.**
2. **Failed follow-up rollback:** every refusal goes through `RefuseStart`; after a refused rollback unmute resets the upstream, it ends the surviving exchange **once**, Stay while the talk is Paused rather than publishing another `answering` on a null session (`Presenter.Asking.cs:167-230,284-310`). Stay retains replay due and parks deferred notices for reconnect; `EndExchange` clears its timers. The three answer phases × Mute/Begin/rollback test asserts frame count, absence of late off/resume, usage, notice delivery and replay after reconnect (`PresenterAskTests.cs:2179-2255`). **Fixed.**
3. **Early refusals:** `_muted`, invalid state, failed reconnect, missing session, refused Mute and Begin all use the same refusal handler (`Presenter.Asking.cs:167-230`). A refused muted follow-up re-announces its still-live answer, allowing Continue; no answering frame is sent with a missing upstream. The muted/Continue frame sequence is separately asserted (`PresenterAskTests.cs:2258-2278`). **Fixed.**
4. **Nested oracle:** two follow-up starts preserve the first exchange's pending edit notice and versioned replay until the single final resolution. The test asserts *both* contents/ids and absence of premature effects; End and max-length instead drop them and produce only one ended off (`PresenterAskTests.cs:2281-2340`). Clearing `Deferred`, `ReplayDue`, or `ReplayChanged` in `BeginFollowUp` would fail the Continue outcome's assertions; incorrectly delivering them before the end, or on End/max-length, would also fail. **Fixed.**

## IS THIS BRANCH READY TO MERGE?

**Code-review blockers in this scoped diff:** none. **Improvements:** none required by this confirmation review. The **T8 live run is a separate known gate**; this review does not claim it has passed.

<!-- REVIEW-COMPLETE r011-impl-r3 -->