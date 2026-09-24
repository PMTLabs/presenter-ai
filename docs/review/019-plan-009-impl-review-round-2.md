# 019 — Plan 009 implementation review, round 2 (confirmation)

**Date:** 2026-09-23. **Reviewer:** pi `openai-codex/gpt-6-sol:high`, read-only, request `r-i009b`. **Subject:** `df76c36..c977122` (the round-1 fixes on `feature/009-billing-guards`).

**Disposition (orchestrator):** all findings accepted after checking the code.
- The Start/End race is confirmed. `CancelConnect` records a reason only when the snapshot is not idle
  (`Presenter.cs:241`), but a Start that is queued or running stays idle until `SetState(Connecting)`. An End in that
  window neither records its reason nor cancels anything, because `_startCts` is not published yet. The End then waits
  behind the Start, and the upstream connects before it is closed.
- The observation bound is confirmed to be bypassable. `ObserveEndAsync` awaits `_presenter.EndAsync` without a bound
  (`PresenterBridge.cs:170` → `:481`), before the 15 s wait begins. There is also a check-then-publish gap between
  `ProcessStartAsync`'s epoch check and `Volatile.Write(ref _startCts)`.
- The writer's bare `catch (OperationCanceledException)` is confirmed (`PresenterBridge.cs:672`).
- The D5 oracle gap is accepted as a nit.

**Root cause shared by both blockers:** an off-loop signal (End or AbortPendingStart) reaches a Start only through
loop-published state (the snapshot or `_startCts`). That state does not exist while the Start is queued, or between its
acceptance and publication. The fix is to give each Start a handle created at enqueue time, which off-loop signals
cancel directly.

---

# r-i009b — confirmation review

Read-only inspection of `df76c36..HEAD`; no app or tests run.

| Round-1 finding | Verdict and oracle check |
|---|---|
| D1 | **Partially fixed.** Reset/idle guard (`Presenter.cs:243-245,620-624`) fix sequential talks; `PresenterTalkGuardTests.cs:98-112` would fail without the reset. Startup race below remains untested. |
| A1 | **Partially fixed.** Non-WebSocket exceptions now abort (`PresenterBridge.cs:675-680`); the injected `InvalidOperationException` test (`BridgeBillingGuardTests.cs:150-169`) fails under the old catch. Unrelated cancellation still silently stops the writer. |
| D2 | **Partially fixed.** Queued-start epoch (`Presenter.cs:194-198,251-257,550-558`) and observation timeout (`PresenterBridge.cs:168-176,786-799`) have relevant tests (`PresenterLifetimeTests.cs:70-118`, `BridgeBillingGuardTests.cs:237-264`) that fail without their respective changes. The actual stalled-loop path still defeats the bound; see below. A fresh browser Start **after** the previous slot is released captures the latest epoch, so no wrongful rejection found there. |
| B1 | **Fixed.** Persistent heartbeat row (`SessionRecorderTests.cs:212-225`) would fail if persisted as `disconnect`; an aborted socket cannot deliver a `closed` frame. |
| B2 | **Fixed.** Producer-path assertions (`PresenterTalkGuardTests.cs:238-282`, `PresenterToolTests.cs:115-135`, `PresenterVoiceCommandTests.cs:304-325`, `BridgeBillingGuardTests.cs:174-189`) fail on wrong producer mappings, unlike the removed echo theory. |
| A2 | **Fixed.** Independent override warning (`Presenter.cs:680-684`); `PresenterTalkGuardTests.cs:115-121` fails without it. |
| D3 | **Fixed.** Unconfirmed usage and positive rounding (`SessionRecorder.cs:346-359,494-495`); both ambiguous-input/short-duration cases (`SessionRecorderTests.cs:183-210`) fail under the old logic. |
| D4 | **Fixed.** Script value is identified as such and the ceiling disclosed (`Present.tsx:359-360,475-484`); picker label tests (`Present.spec.tsx:435-456`) reject the old labels. |
| D5 | **Fixed in code**, weak oracle. Timestamp-only inbound update (`PresenterBridge.cs:626-640`) removes per-frame timer changes, but `BridgeBillingGuardTests.cs:57-81` also passes with the *old* per-frame `Change` implementation. |

**Direct-effect findings**

- **D · blocker — D1 startup/End race.** `Presenter.cs:243-248,550-558,620-650`: a Start already queued can begin while `Snapshot()` remains idle. End enqueued in that window stores no reason; Start clears any pending reason and only *later* publishes `connecting` and installs `_startCts`. End cannot interrupt a slow load/connect immediately; if the guard expires first, `EndAsyncCore` records `max_length` rather than the requested `user`. Synchronize End intent with Start acceptance and test this interleaving.
- **D · blocker — D2 bound is bypassed by the real presenter.** `PresenterBridge.cs:166-175,472-490`: after 90 seconds, cleanup calls **unbounded** `ObserveEndAsync` *before* its new 15-second observation wait. If the real presentation loader ignores cancellation, End is queued behind the stuck Start (`Presenter.cs:650-665`); the slot never reaches the new bound. The test's `TestQueuedPresenter.EndAsync` returns immediately (`ApiFactory.cs:203-211`), so it cannot detect this. Also, `Presenter.cs:550-558,639-648` has an abort-epoch-check → `_startCts`-publication gap: an abort there cancels neither the command nor the future CTS; that Start can still create an upstream. Gate both transitions atomically before claiming the observer/slot is safe.
- **A · should-fix — uncaught writer cancellation source.** `PresenterBridge.cs:672-680`: `OperationCanceledException` from the send hook or socket while `_lifetime` is **not** cancelled is swallowed, terminating the writer without aborting the live receive loop. Restrict the cancellation catch to lifetime cancellation; treat unrelated cancellation as `writer_failed` and test it.
- **B · nit — D5 mutation gap.** `BridgeBillingGuardTests.cs:57-81` checks deadline semantics, not timer-rearm frequency; restoring `MarkAlive`'s old `ITimer.Change` on every frame still passes. Add an instrumented timer or remove the claimed regression oracle.

## IS THIS BRANCH READY TO MERGE?
**No.** Resolve the startup End race and make the observation cleanup genuinely bounded without permitting a late upstream.

REVIEW COMPLETE r-i009b