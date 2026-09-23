# 005 — Echo protection and stall recovery

**Date:** 2026-09-22
**Status:** Approved (2026-09-22)
**Size:** M
**Area:** `web/app/src/audio/*`, `web/app/src/routes/Present.tsx`, `src/PresenterAi.Application/Presenting/Presenter.cs`,
their tests
**Requirement brief confirmed:** 2026-09-22 (G1), in chat
**Depends on:** the web-fix branch `fix/web-auth-gating-and-build` (it also edits `Present.tsx`); this plan branches
from `develop` after that fix merges.

## 1. Problem

1. **Echo.** With loudspeakers, the AI's voice reaches the microphone. GPT-Live is full duplex, so it can take its own
   voice for the audience: it stops, answers itself, or keeps a slide open. The presenter also re-arms the advance timer on
   every `user` transcript delta (`Presenter.cs:533`), so echo transcribed as user speech can hold a slide open.
   The mic already asks for `echoCancellation: true` (`web/app/src/audio/capture.ts:78-86`), but Chromium's echo
   canceller uses WebRTC-received audio as its reference, and the app plays the voice through Web Audio
   (`playback.ts:28`, `node.connect(context.destination)`), which is not guaranteed to be in that reference.
2. **Stall.** In a live run on 2026-09-22 (19:30), the presenter sent slide 1, nudged once at +15 s
   (`Presenter.cs:608-620`), and then waited for more than 10 minutes. The model streamed output continuously
   (100 deltas per 10 s) but never *voiced* output (`AudioLevel.IsVoiced`, RMS ≥ 120), so `_heardOutput` stayed false,
   the advance timer never armed, and nothing followed the single nudge. Upstream time was spent invisibly.

## 2. Scope

**In:**
- echo-canceller reference through a WebRTC loopback;
- a client echo gate with barge-in;
- flushing the buffered playback on a sustained barge-in;
- stall escalation (a second nudge, then pause with a warning);
- per-slide output diagnostics.

**Out:**
- push-to-talk;
- server-side audio processing;
- GPT-Live session settings;
- server-side bridge close on logout.

## 3. Current state (as-built)

- **Capture:**
  - `getUserMedia` uses AEC, NS and AGC (`capture.ts:78-86`);
  - `capture-processor.ts` resamples to 24 kHz, sends 20 ms Int16 frames and reports a peak level every 100 ms;
  - `muted` drops frames in the worklet.
- **Playback:**
  - `playback-processor.ts` is an 8 s ring at 24 kHz and reports `buffered` every 250 ms;
  - `flush` empties the ring, but nothing in the app calls `AudioPlayback.flush()`;
  - the node connects to `context.destination` (`playback.ts:28`).
- **Bridge:**
  - server audio arrives as binary WS frames, and `Present.tsx:118` enqueues each into playback;
  - mic frames go out only in `presenting` (`bridgeClient.ts:165-171`), then `Presenter.SendAudioCore`
    (`Presenter.cs:770`) → `LiveSession.SendAudio`.
- **Advance:**
  - `OnAudio` sets `_heardOutput` on voiced output and re-arms the part-gap or advance timer (`Presenter.cs:500-521`);
  - `OnNudge` fires once per slide (`_nudged`, `Presenter.cs:608-620`);
  - `PauseCore` mutes upstream input and appends the pause instruction (`Presenter.cs:700-712`);
  - `ResumeCore` re-arms the nudge (`:714-744`).
- **Oracle to change deliberately:** `PresenterTests.No_output_audio_for_15_seconds_sends_exactly_one_nudge`
  (`tests/PresenterAi.Application.Tests/Presenting/PresenterTests.cs:182-192`) pins exactly one nudge over 45 s.
  This plan replaces that behaviour with the approved escalation, so the test is rewritten to the new rule. It is not
  deleted.

## 4. Design

### 4.1 Echo-canceller reference (web)

```
server PCM ─► AudioPlayback worklet ─► MediaStreamAudioDestinationNode
                                           │ track
                          RTCPeerConnection A ──(local loopback)──► RTCPeerConnection B
                                                                       │ ontrack
                                                        <audio autoplay>.srcObject ─► speakers
                                                                                  (Chromium AEC reference)
```

- **New module:** `web/app/src/audio/echoReference.ts`. It builds the loopback: two peer connections, host candidates
  only, Opus with DTX off and about 96 kbit/s mono. It returns `{ element, close() }`.
- **Setup:** `startAudio` creates it inside the Start click, so autoplay is allowed.
- **Fallback:** if `RTCPeerConnection` is missing, or the connection is not playing within 2 s, the worklet connects to
  `context.destination` as today, and one log line says so.
- **`stop()`:** closes both peer connections and detaches the element.

### 4.2 Echo gate (web)

- **Where it runs:** in the capture worklet, fed by the playback worklet's output level. A `MessageChannel` connects
  the two worklet ports directly, not through the main thread. Playback posts the RMS of every 20 ms it renders.
- **Pure module:** `web/app/src/audio/echoGate.ts`, imported by `capture-processor.ts`, so the gate is unit-testable
  without Web Audio. It works per 20 ms mic frame:
  1. **Far-end active:** the far-end RMS was above the floor (0.004) in the last `tailMs` (250 ms).
  2. **Coupling estimate:** while the far end is active and the gate is closed, track `c`, the running low percentile
     of `near / far` over 2 s. It starts at 0.5 and is floored at 0.01. With headphones `c` falls quickly, so speech
     passes; on loudspeakers `c` settles at the room's echo level.
  3. **Open:** when `near > max(0.01, 4 · c · far)` (12 dB above the estimated echo) for 2 consecutive frames (40 ms).
     **Stay open:** for 300 ms after `near` falls back.
  4. **Closed:** send an all-zero frame, so the cadence is unchanged. Upstream stays unmuted, and the server's silence
     pump is untouched.
  5. **Far end inactive:** the gate passes everything, as today.
- **Barge-in:** if the gate has stayed open for ≥ 300 ms while the far end is active, the worklet posts `barge-in`.
  The page calls `playback.flush()` and logs "barge-in: playback flushed". This is at most once per open period.
- **Live comparison switch:** the query parameter `?echoGate=off` bypasses the gate. It is not a user setting.
- **Diagnostics:** `window.__presenterDebug()` gains `echo: { reference: "loopback" | "fallback", gateOpenRatio,
  coupling, bargeIns }`.

### 4.3 Stall recovery (server, `Presenter`)

- **Escalation:** 15 s with no voiced output sends nudge 1, as today (`slide-N-nudge`). Another 15 s sends nudge 2
  (`slide-N-nudge-2`). Another 15 s calls `PauseCore()` and logs at warn: "The model stopped responding on slide N —
  Resume or End."
- **Voiced output at any step** cancels the escalation, as `_heardOutput` does today.
- **After the stall pause:** `ResumeCore` re-arms from nudge 1 (existing `ArmNudge`). End works as usual.
- **Wrap-up** keeps its own fallback (`OnWrapUpFallback`); it is unchanged.
- **Web:** while `paused`, Present shows the newest warn line from the presenter log as a banner above the deck. No
  protocol change is needed.

### 4.4 Diagnostics (server)

`Presenter` counts output frames per slide, voiced and unvoiced, and user transcript characters. It logs one info line
when a slide ends (advance, jump, stall pause or end), for example: `slide 1: output 480 frames (12 voiced), user
transcript 0 chars`.

### 4.5 Alternatives considered

| Option | Why not (now) |
|---|---|
| Push-to-talk | No echo at all, but voice interruption becomes a key press; the user chose voice barge-in. |
| Half-duplex (mic off while the AI speaks) | No voice barge-in mid-sentence. |
| Loopback reference only | Loudspeakers in a large room still leak past AEC; the gate is the safety net. |
| A WASM echo canceller (Speex or WebRTC AEC3) | Heavy, and duplicates the browser's AEC; revisit if the live check fails. |
| Server-side gating | The server cannot see the browser's playout timing, so it would guess the far-end window. |

## 5. Impact and risk

- **Latency:** the loopback adds about 20–50 ms of output latency. Buffered playback already smooths network jitter.
- **Autoplay:** the `<audio>` element must start inside the Start click; the fallback covers a refusal.
- **Headphones:**
  - **risk:** before `c` adapts, speech during AI audio needs to be 12 dB above 0.5 × far;
  - **why it clears quickly:** echo-only frames with headphones drive `c` down within about 1 s;
  - **verify:** covered by a unit test and the live check.
- **Tests:** thresholds are constants in `echoGate.ts`, tunable after the live check without a design change.
- **Rollback:**
  - `?echoGate=off` disables the gate for one run;
  - reverting the loopback returns playback to `context.destination`;
  - the server change is isolated in `Presenter`.

## 6. Tasks

| # | Change | Files | Verify | Test that dies if this breaks |
|---|---|---|---|---|
| T1 | Loopback reference + fallback | `audio/echoReference.ts` (new), `audio/playback.ts`, `audio/capture.ts` (`startAudio`) | vitest with a fake `RTCPeerConnection`; `bun run build`; `check-dist` | `echoReference.spec.ts`: the track reaches the element; missing RTCPeerConnection → fallback to `context.destination`; `close()` closes both peers |
| T2 | Echo gate module + worklet wiring (MessageChannel, far-level posts) | `audio/echoGate.ts` (new), `worklets/capture-processor.ts`, `worklets/playback-processor.ts`, `audio/capture.ts` | vitest; `check-dist` confirms the worklet chunk bundles the gate | `echoGate.spec.ts`: echo-level near is blocked (zeros); near 12 dB above echo passes after 40 ms; 300 ms hangover; far inactive → pass-through; `c` adapts down with headphone-like input |
| T3 | Barge-in flush + `?echoGate=off` + debug fields | `audio/capture.ts`, `routes/Present.tsx` | vitest | `Present.spec.tsx`: a `barge-in` message calls `playback.flush()` once; the gate-off flag reaches the worklet |
| T4 | Stall escalation + pause warning | `Presenter.cs`, `PresenterTests.cs` | `dotnet test` Application | Rewritten `No_output_audio…` → `Silent_model_gets_two_nudges_then_the_run_pauses_with_a_warning`; `Voiced_output_after_the_first_nudge_cancels_the_escalation`; `Resume_after_a_stall_rearms_the_first_nudge` |
| T5 | Paused warning banner | `routes/Present.tsx` | vitest | `Present.spec.tsx`: paused + warn log → banner; presenting → no banner |
| T6 | Per-slide diagnostics line | `Presenter.cs`, `PresenterTests.cs` | `dotnet test` | the line's counts after a scripted slide |
| T7 | Verification | — | `bun run lint/test/build`; the full .NET suite; Playwright smoke of the Present page; the user's live check (§7) | — |

## 7. Test strategy and live runbook

- **Unit tests:** the tests above; each is mutation-checked by the orchestrator.
- **Integration:** the existing bridge and presenter suites stay green; Playwright confirms that the Present page loads
  with the loopback and falls back cleanly in headless Chromium.
- **Live runbook (user, on loudspeakers with no headphones):**
  1. Start the sample deck. It should play all 3 slides with no self-interruption, and the log should show
     `reference: loopback`.
  2. Speak loudly over the AI. The gate opens, `barge-in: playback flushed` appears, and the AI answers.
  3. Repeat with `?echoGate=off` to compare.
  4. Optional: mute the mic before the AI speaks. A stall then shows two nudges, then paused with the warning banner.

## 8. Open questions

None. The barge-in flush is set to 300 ms and the stall response to pause-and-warn; the user decided both on
2026-09-22.

## 9. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-22 | Requirement brief confirmed (G1) | Layered echo fix, flush after 300 ms of barge-in, pause and warn after two failed nudges |
| 2026-09-22 | Plan approved (G2) | The user chose "Approve and implement"; implementation starts after the web-fix branch lands |
