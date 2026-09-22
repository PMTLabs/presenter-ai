# 004 — Implementation review round 3: plan 002 (T13, T14, review-2 fixes)

**Reviewer:** `pi --model openai-codex/gpt-5.6-terra:medium`, review-only (no edits, no tests, no server, no `.env`), 2026-09-21.
**Scope:** commits `906af83..41d093a` on `feature/002-dotnet-core-port` — new scope (web workspaces, React presenter page, review-2 class fixes, OpenAPI `{id}` parameter), not a re-review of earlier findings; the round-2 class fixes were verified site by site.
**Classification:** F1 D (major), F2–F4 D (minor), F5–F7 B (minor). No A, no C. Ledger: `docs/agentic/review-rounds-ledger.md`.

## Disposition (orchestrator)

| Finding | Class | Fix applied |
|---|---|---|
| F1 route teardown leaks mic/audio context | D | _pending_ |
| F2 StrictMode double deck load | D | _pending_ |
| F3 presenter detail request ignores errors | D | _pending_ |
| F4 Library shows `detail` before `errorMessages[code]` | D | _pending_ |
| F5 reconnect test proves two delays, not the schedule/cap | B | _pending_ |
| F6 "parses every message" asserts only binary | B | _pending_ |
| F7 listen-only test does not prove playback started | B | _pending_ |

---

## Summary
Reviewed `906af83..41d093a` without modifying the project or running builds, tests, or servers. The React presenter leaks the microphone/audio context on route teardown, and its deck-load effect is not StrictMode-safe. The latter explains the duplicated deck-adapter log observation. The claimed duplicated *server* log frames do not have a matching duplicate append path in the reviewed client/store; see the dedicated section.

## Findings

### F1 — Route teardown leaves microphone capture and audio context alive
**Class:** D  
**Severity:** major  
**Where:** `web/app/src/routes/Present.tsx:50-51,84-92`

**What:** The connection effect cleans up only `bridge.disconnect()`. It neither calls `stopAudio()` nor cancels a pending `startAudio()` capture request. Leaving `/present/:id` after Start can therefore leave the media track and `AudioContext` alive; if navigation occurs while `getUserMedia`/worklet setup is pending, `AudioCapture.stop()` has already seen no stream and the later completion acquires a mic track with no owner.

**Why it matters:** This is a privacy/resource leak and diverges from the Node page’s ownership check that releases a microphone that resolves after audio has stopped.

**Concrete fix:** Make presenter teardown stop audio and dispose/detach the deck. Add an ownership/cancellation token to `startAudio`/`AudioCapture.start()` so an asynchronously acquired stream is immediately stopped when its caller has been torn down. Test unmount during an outstanding `getUserMedia` request and assert tracks are stopped and context is closed.

### F2 — StrictMode runs two unguarded deck loads
**Class:** D  
**Severity:** minor  
**Where:** `web/app/src/routes/Present.tsx:52-82`; `web/app/src/main.tsx:7-9`

**What:** React development StrictMode runs the presentation-fetch effect setup twice. Its cleanup is absent, so both requests may resolve and each constructs a `DeckDriver`, assigns `iframe.src`, and calls `deck.load()`. Each successful load logs `deck adapter: …` at `web/app/src/deck/deckDriver.ts:111`; this is the direct cause of the observed duplicate `deck adapter` entry.

**Why it matters:** It races iframe loads and leaves stale hash listeners; it also makes development verification logs misleading.

**Concrete fix:** Use an `active` flag/AbortController in the effect, dispose/detach the previous driver in cleanup, and ignore stale response/load completions. Add a StrictMode render test asserting one deck-load/log entry.

### F3 — Presenter detail request ignores all API failures
**Class:** D  
**Severity:** minor  
**Where:** `web/app/src/routes/Present.tsx:54-82`

**What:** The generated-client call handles only `{ data }`; it ignores its `error` result and has no rejection handler. A 404/400 or network failure leaves a blank presenter iframe with no explanation, and a rejected request is unobserved.

**Why it matters:** This misses the §12 error-narrowing/error-copy convention on one of only two HTTP call sites.

**Concrete fix:** Handle `{ error }`, narrow it with `isProblem`, use `errorMessages[code]` as primary copy and `detail` only as fallback; render an error state and catch transport rejection.

### F4 — Library displays server detail instead of the required stable error copy
**Class:** D  
**Severity:** minor  
**Where:** `web/app/src/routes/Library.tsx:20-25`; `web/shared/src/api/errorMessages.ts:3-45`

**What:** `Library` narrows Problem Details but chooses `problem.detail`/`title` before `code`. The checked-in `errorMessages` map is never consumed by either app route.

**Why it matters:** This contradicts conventions §12: UI copy for each code must live in and be selected from the central map; `detail` is a fallback rather than the primary text.

**Concrete fix:** Import `errorMessages` and select `errorMessages[problem.code] ?? problem.detail ?? problem.title`; cover it with a Problem Details response test.

### F5 — Reconnect test does not prove the specified exponential schedule or cap
**Class:** B  
**Severity:** minor  
**Where:** `web/app/src/ws/bridgeClient.spec.ts:42-58`; `web/app/src/ws/bridgeClient.ts:84-85`

**What:** The test checks only 500 ms then 1,000 ms. A wrong linear implementation such as `Math.min(10_000, 500 * (retry + 1))` passes those two retries but produces 1,500 ms rather than 2,000 ms on the third and does not prove the 10 s cap.

**Why it matters:** The required recovery behaviour is the complete 500 ms × 2ⁿ schedule capped at 10 s, not merely two reconnects.

**Concrete fix:** Drive enough close/reconnect cycles with fake timers to assert 500, 1,000, 2,000, …, 10,000, and another 10,000 ms retry.

### F6 — “parses every message” test never asserts any parsed text event
**Class:** B  
**Severity:** minor  
**Where:** `web/app/src/ws/bridgeClient.spec.ts:74-112`

**What:** The test sends each text type but asserts only that binary audio reached its listener. A wrong implementation that ignores every text message (including `state`, `slide`, `closed`, and `log`) still passes.

**Why it matters:** The test name and T14 oracle claim coverage for frozen message parsing that it does not provide.

**Concrete fix:** Register listeners for each text event and assert the expected payload/count, including `error` and state mutation.

### F7 — Listen-only test does not prove playback was started
**Class:** B  
**Severity:** minor  
**Where:** `web/app/src/audio/startAudio.spec.ts:14-34`

**What:** `audio.playback` is constructed before microphone acquisition. An incorrect implementation that returns the object without `resumeWithTimeout()` or `playback.start()` still satisfies `toBeTruthy()` and `micReady === false`.

**Why it matters:** A denied mic must still leave working output, which is the behavior this test claims to protect.

**Concrete fix:** Assert `Context.resume` and `audioWorklet.addModule`/node connection for playback occurred, while capture rejection did not reject `startAudio`.

## Round-2 class fixes verified

- **Session disposal (F1):** `ILiveSession` now inherits `IAsyncDisposable` (`src/PresenterAi.Application/Presenting/ILiveSession.cs:5`). Producers/owners found: Presenter fallback candidates are disposed in the `ConnectAsync` catch (`Presenter.cs:374-375`); the active session is disposed after its `Closed` event (`:780-783`), after failed `CloseAsync` (`:773-775`), and on presenter shutdown (`:270-276`); direct CLI smoke ownership is disposed in `SmokeCommand.cs:117-123`; bridge ownership disposes the presenter (`PresenterBridge.cs:218-224`). The concrete session disposes socket, CTS, pump, and loops (`LiveSession.cs:200-217`). No additional `ILiveSessionFactory.Create` consumer was found.
- **Observed shutdown / background faults (F2):** `Presenter` observes producer queue and wrap-up End tasks (`Presenter.cs:192-204,567,602`); the bridge observes command faults (`PresenterBridge.cs:163-182`) and awaited disconnect End (`:184-188`); `RunCommand` retains/awaits its End task (`RunCommand.cs:80-87,153-170`). The remaining `_ =` instances in `SmokeCommand.cs:23` and `Program.cs:131` read validated options, not tasks; `Task.Run` starts owned loops/writers (`Presenter.cs:74`, `PresenterBridge.cs:249`).
- **Backpressure oracle (F5):** production `ClientConnection` receives the bounded capacity and pre-send gate only through internal test configuration (`PresenterBridge.cs:191-204,242-264`); the test fills the real channel with fake-server output and waits for 1011 (`tests/PresenterAi.Api.Tests/BridgeTests.cs:119-145`). Default production capacity remains 500 and has no gate.
- **Static deck routing:** both static file middlewares precede explicit routing (`src/PresenterAi.Api/Program.cs:52-67`); `/decks/{**path}` and fallback retain the exact 404 text (`:75-83`).

## Duplicate log cause

The duplicated `deck adapter` log is explained by StrictMode plus the uncleaned presentation-load effect: `main.tsx:7-9` enables StrictMode, and both resolutions of `Present.tsx:54-82` call `DeckDriver.load`, which logs at `deckDriver.ts:111` (F2).

I did **not** find a client/store path that can render one received server `log` frame twice: `BridgeClient.receive` emits `log` once (`bridgeClient.ts:156-158`), its sole presenter subscription calls `message` once (`Present.tsx:36-47`), and `presenterStore.message` appends it once (`presenterStore.ts:62-74`). Nor does the server’s singleton bridge subscribe twice (`PresenterBridge.cs:24-34`). Thus the work-log sentence that `appended thinking slide-2-notes` appears twice while other clients receive it once is not established by these sources. The only plausible client-side duplicate socket hazard is that stale sockets are not guarded in `open`/`message` handlers (`bridgeClient.ts:72-78`), although StrictMode’s cleanup normally closes its first socket before a presentation can start. Instrument received-frame/socket identity before attributing this observation to the reducer.

## Not verified

No builds, tests, servers, live upstreams, Chrome run, CI run, or `.env` files were opened. I did not verify runtime lockfile installation beyond static inspection, actual same-origin/cross-origin iframe behavior, or reproduce the work-log observations.

## IS THIS BRANCH READY TO MERGE?

**Blockers:** none found in this review.

**Improvements before merge:** F1 should be addressed as a major microphone/privacy lifecycle defect; F2–F7 should be addressed to make React StrictMode behavior, error handling, and the stated web oracles reliable. **No, not ready to merge while F1 remains.**

## Files examined

Plan/conventions/work log and both prior review reports; Node `src/web/{app.js,audio-capture.js,audio-playback.js,deck-driver.js}` and worklets; CI; all new `web/app` presenter/audio/deck/store/tests and `web/shared` API/auth/generation files; relevant API/Presenter/LiveSession/CLI implementation and bridge/OpenAPI tests; README; `web/bun.lock` header and local-path search; scoped diff/history.
<!-- REPORT COMPLETE -->
