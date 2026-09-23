# 018 — Plan 009 implementation review, round 1

**Date:** 2026-09-23. **Reviewer:** pi `openai-codex/gpt-6-sol:high`, read-only, request `r-i009`. **Subject:** `2d2f63e...db7a712` (plan 009 T1–T10 merged on `feature/009-billing-guards`).

**Disposition (orchestrator):** all findings accepted after checking the code. D1 confirmed (`_pendingEndReason` is set first-wins and never reset). A1 confirmed (the writer calls `Abort` only for `WebSocketException`). D2 accepted: presenter disposal is the backstop, but a queued Start survives `StopAsync`, and the post-expiry observer wait has no bound. B1, B2, A2, D3, D4 and D5 are fixed in the same round. The intermittent `Approved_run_after_moving_on_is_not_announced(end)` failure is test timing (a polling window), not a race, per the reviewer's trace; it is recorded as a known flake with no code change.

---

# r-i009 — plan 009 implementation review
The presenter now has a shared talk guard, pause-close/reconnect, reasoned close events, and per-run tool cancellation.
The branch is **not ready to merge**: end reasons leak between talks, and some writer failures do not abort a live browser.
Shutdown and start-observation cleanup still have races or unbounded waits contrary to the billing-lifetime contract.
The new tests cover many paths but miss the actual heartbeat frame/row conjunction and several close-path mappings.
This is a read-only review of `2d2f63e...HEAD`; no app was run and no tests were changed.

## Blockers

### D1 — Stale end reason overrides all subsequent talks
**Class D · blocker.** `src/PresenterAi.Application/Presenting/Presenter.cs:237-251,597-636,2085`: `EndAsync` sets `_pendingEndReason` with first-wins `CompareExchange`; neither Start nor `OnClosed` resets it. Start resets `_requestedEndReason` but not `_pendingEndReason`. After an ordinary End, start another talk and let its max timer or idle timer expire: `EndAsyncCore` substitutes the *previous* `user` reason for `max_length`/`idle`; a later heartbeat, shutdown, or reconnect failure is similarly misreported in the frame and recorder. Even calling End while idle poisons the next run. Smallest fix: make the pending reason per-run, clear it at a safely synchronized Start transition, and preserve first-wins only within that run; test two consecutive talks with different close paths and an End while idle.

### A1 — Writer abort handles only one class of send failure
**Class A · blocker.** `src/PresenterAi.Api/Realtime/PresenterBridge.cs:662-697`: `WriteLoopAsync` aborts on `WebSocketException` but not other failures from `_beforeSocketSendAsync`, `SendAsync`, or `CloseOutputAsync` (for example `InvalidOperationException` on a state transition or `IOException` from transport). The writer task faults and the receive loop stays alive; inbound pongs keep heartbeat alive and repeated model output keeps idle alive, so the upstream can bill until the hard ceiling. `tests/PresenterAi.Api.Tests/BridgeBillingGuardTests.cs:128-146` injects specifically `WebSocketException`. Smallest fix: catch/log non-cancellation writer exceptions and call `Abort(writer_failed)` in the same path; test a non-WebSocket send failure and verify the slot and upstream are released.

### D2 — Stopping can miss a queued start; expiry can wait without a bound
**Class D · blocker.** `src/PresenterAi.Api/Realtime/PresenterBridge.cs:538-545,162-174,789-796` and `src/PresenterAi.Application/Presenting/Presenter.cs:248-259,634-639`: shutdown aborts the *currently existing* CTS and only sends End if the instantaneous snapshot is non-idle. A start already queued but not executing has no `_startCts` yet and an idle snapshot; `StopAsync` returns while that start can subsequently connect. At start-observation expiry, cleanup also waits for `WaitForStartObservationCompletionAsync()` **without any bound** after the nominal 90 s + 5 s, if the loader/observer ignores cancellation. Smallest fix: gate new starts atomically against stopping/abort, cancel a queued start before it executes, and arrange a bounded observer/slot finalization that cannot later create an upstream (without releasing ownership before that invariant holds). Add tests for stopping with a queued Start and for an observer that does not finish when the 90 s fake-clock bound fires; the existing expiry test releases its gate itself (`BridgeBillingGuardTests.cs:185-193`).

## Improvements

### B1 — Heartbeat oracle does not identify frame and persisted row
**Class B · should-fix.** `tests/PresenterAi.Api.Tests/BridgeBillingGuardTests.cs:14-37` checks `Closed.EndReason` and `TestSessionRecorderFactory.LastClosed`, but never a `closed.endReason` frame or actual `sessions.end_reason` row. A bridge that drops `closed`, or a real recorder that always persists `disconnect`, passes this test. The separate frame test (`:231-244`) tests only an explicit user End. Smallest fix: exercise each observable where possible (the socket may already be aborted on heartbeat), and use a persistent-recorder integration oracle for the row; do not describe the in-memory recorder event as persisted storage.

### B2 — End-reason vocabulary theory bypasses the actual close paths
**Class B · should-fix.** `tests/PresenterAi.Application.Tests/Presenting/PresenterTalkGuardTests.cs:213-226` loops over every vocabulary string and calls `presenter.EndAsync(reason)`. An implementation that maps voice-confirmed End to `completed`, wrap-up to `user`, or a spontaneous close to `error` still passes the theory: it merely echoes caller-provided strings. Separate tests cover wrap-up and spontaneous loss, but not all entries in the plan's §4.3 mapping table. Smallest fix: trigger each distinct producer (including voice/tool, takeover, handler failure and close timeout) and assert its end reason *and* diagnostic reason from that path rather than passing the expected reason in as input.

### A2 — Above-ceiling warning implemented for script, omitted for override
**Class A · should-fix.** `src/PresenterAi.Application/Presenting/Presenter.cs:668-676` warns when `scriptCap` exceeds the ceiling, but silently ignores an override above the ceiling. `docs/guides/004-talk-limits-and-billing.md:10-12` says a script **or override** above the ceiling is clamped and logged; `docs/plan/009-upstream-billing-guards.md` §4.3 requires a clamp warning. A `maxMinutes: 200` request on a 60-minute default gets 60 minutes but no warning; the same excessive value in frontmatter produces one. Smallest fix: warn for an above-ceiling override as well, independently of whether the effective min changes; add an override-specific log assertion.

### D3 — Recorder's legacy exception can substitute partial confirmed usage for an unconfirmed talk
**Class D · should-fix.** `src/PresenterAi.Infrastructure/Sessions/SessionRecorder.cs:346-365`: if `UsageConfirmed=false`, `Seconds` has a value and `EstimatedSeconds==0`, it writes `Seconds` despite the flag saying the value is not confirmed. The comment assumes this can only be a legacy `PresenterClosed`, but any current/other producer can send that combination; it can store a partial confirmed segment rather than the elapsed estimate. Also a short live talk rounded by `ToRoundedSeconds` can yield zero despite the guide's “instead of reporting zero for a talk that ran.” Smallest fix: distinguish legacy events explicitly (e.g. missing presenter timestamps) or remove the exception and always estimate when unconfirmed; derive a nonzero estimate for a positive elapsed duration and test this ambiguous input.

### D4 — Length picker advertises unclamped default
**Class D · nit.** `web/app/src/routes/Present.tsx:358-360,475`: “Default (N min)” uses the raw frontmatter value (or hard-coded 60) rather than the server's configured ceiling/default. With script `maxMinutes: 200` and ceiling 120, the picker says 200 min but server stops at 120; with a changed `Presenter:MaxTalkMinutes` the hard-coded 60 is similarly wrong. This weakens the guide's `docs/guides/004-talk-limits-and-billing.md:23-26` description of Default. Smallest fix: expose the effective server limit/config to the page or label it “Default (server limit applies)” instead of promising a number.

### D5 — Every incoming audio frame re-arms the heartbeat timer
**Class D · nit (performance).** `src/PresenterAi.Api/Realtime/PresenterBridge.cs:623-630,316-323`: `MarkAlive` takes a lock and invokes `ITimer.Change` for every receive fragment, approximately 50 times/second for an active mic. This does not itself break the deadline, but adds needless timer work in the hottest receive path (on top of voiced-output idle rescheduling). Smallest fix: update the last-inbound timestamp cheaply, let the single deadline callback compute remaining time/re-arm, or throttle changes without weakening the 45 s guarantee; measure before treating it as a release blocker.

**Orchestrator's intermittent approved-tool test:** `Presenter.cs:1689-1708` checks session identity and run generation before announcement; `:2090-2094` cancels the run and increments generation, while `InvokeBoundedAsync` at `:1581-1607` returns `cancelled` when End cancels its token. In `PresenterExternalToolTests.cs:277-305`, End is awaited *before* the gate is released, so the result cannot correctly announce after End. The `Eventually(..."not announced")` expectation relies on asynchronous `Task.Run`/queue scheduling and can miss a 3-second wall-clock window under load; there is no demonstrated post-End announcement race here. A deterministic completion/barrier instead of the short polling window would distinguish scheduling delay from a real regression. An approved result that was queued *before* End can legitimately run before End's command; cancelling the token does not reorder the channel.

## IS THIS BRANCH READY TO MERGE?
**No.** Resolve blockers D1, A1, and D2 first; then strengthen the stated money/observability oracles and address the smaller discrepancies above.

REVIEW COMPLETE r-i009