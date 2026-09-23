# 005 — Echo protection and stall recovery

**Date:** 2026-09-22
**Status:** Approved (2026-09-22); amendment A1 (audience questions, §4.5) approved 2026-09-22
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
- per-slide output diagnostics;
- **A1:** answering audience questions (deck first, a backend model for the rest), holding the slide until the answer
  is spoken, and auto-scrolling the transcript and log panels (done, `6d51512`).

**Out:**
- push-to-talk;
- server-side audio processing;
- GPT-Live session settings;
- server-side bridge close on logout;
- **A1:** our own backend HTTP call for questions (client-delegation option A in research 005), speculative lookups
  while the audience is still speaking, and retrieval over documents outside the deck.

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
     *As built:* `far` is the loudest far-end level of the last 250 ms, not the current 20 ms, because the echo
     reaches the mic after playout; otherwise a loud syllable heard during a quiet one could open the gate.
     **Stay open:** for 300 ms after `near` falls back.
  4. **Closed:** send an all-zero frame, so the cadence is unchanged. Upstream stays unmuted, and the server's silence
     pump is untouched.
  5. **Far end inactive:** the gate passes everything, as today.
- **Barge-in:** if the gate has stayed open for ≥ 300 ms while the far end is active, the worklet posts `barge-in`.
  *As built:* only frames with speech above the threshold count toward the 300 ms, so a short noise that the
  hangover keeps open cannot flush playback.
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

### 4.5 Audience questions (amendment A1, 2026-09-22)

**Problem (live run 20:10).** After an audience question the model said "I'll check on that for you" and emitted
`session.delegation.created`. `Presenter.OnDelegation` (`Presenter.cs:572-579`) only logs client delegations, so
nothing answered, and 5 s later the ordinary silence timer (`ArmAfterVoice` → `OnSilence`, `Presenter.cs:914-926`,
`:604-625`) moved to slide 2. The part gap, the voiced wrap-up close and the wrap-up fallback can move on the same
way. Research: `docs/research/005-gpt-live-delegation-audience-questions.md`.

**Decisions (user, 2026-09-22):** deck first, a backend model for the rest; hold the slide until answered, with a
15 s fallback; GPT-Live-managed Responses delegation; default backend model `gpt-5.6-luna`; fast settings (low
reasoning effort, priority tier, low verbosity).

**Design.**
- **Delegation mode** (`LiveSession` `session.start`, `LiveSession.cs:500-512`): when the upstream's
  `DelegationModel` is set (new `Upstream:DelegationModel` and `Upstream:Fallback:DelegationModel`, both defaulting
  to `gpt-5.6-luna`; empty = client mode), send
  `delegation: { type: "responses", responses: { model, instructions, reasoning: { effort: "low" },
  service_tier: "priority", text: { verbosity: "low" } } }`. Only fields documented in the Azure GPT-Live reference
  are sent; the config object is strict. The backend `instructions`: answer audience questions about the talk titled
  X in one to three short spoken sentences; if unsure, say so.
- **Safety net:** if `session.start` is rejected with an error whose `param` starts with `session.delegation` (for
  example, no such deployment), the same upstream is retried once with client delegation, and one warn line is logged
  (`delegation: backend unavailable (…); answering from the deck only`). The mode is fixed per session, so this
  happens only at start.
  *As built:* the rule also accepts a `code` or `message` that names delegation. After a rejection, the receive loop
  stops reading, so if the server closes the socket right after the error, the session doesn't finish before the retry.
- **Deck first:** the live model already has the deck (`instructions` plus per-slide notes as `thinking`).
  `PromptBuilder`'s audience rules change to: answer immediately from the narration and background context when they
  cover it; otherwise delegate; never say that you checked or found something before a result arrives (at most "One
  moment."); do not resume the narration until the question is answered.
- **Backend results** are injected into the live conversation by GPT-Live (`response.event` envelopes).
  `LiveSession` reads the envelopes only to log the terminal `response.completed` or an error; no client function
  tools are defined.
  *As built:* `LiveSession` also raises `DelegatedResponseFinished(delegationId, type)` for those terminal events,
  because `Presenter` needs it for the hold (see below). No text is forwarded.
- **Client mode** (safety net or empty model): on `session.delegation.created` with `target: "client"`, `Presenter`
  immediately appends a delegation-scoped instruction (`AppendInstructions(..., delegationId)`): "No lookup is
  available. Answer now in one to three sentences from the narration and background context, or say plainly that the
  material does not cover it; then continue the current slide."
- **Question hold** (`Presenter`, single event loop):
  - a non-blank `user` transcript delta while presenting or wrapping up opens or extends the hold: it clears the
    silence/part-gap timer and the wrap-up fallback, resets `answerVoiced`, and (re)arms a 15 s question timer; the
    nudge escalation (T4) is left alone;
  - voiced output that arrives after the latest user delta sets `answerVoiced` and re-arms the ordinary post-voice
    timer as today;
  - `session.delegation.created` resets `answerVoiced` (so "One moment." is not the answer) and re-arms the 15 s timer;
    *as built:* this happens only while presenting. For a `responses` delegation, no speech counts as the answer until
    that delegation's terminal `response.event` arrives (info `question: backend answer ready`, or warn `question:
    backend answer failed (<type>)`), and that event re-arms the 15 s timer. Otherwise "One moment." spoken after the
    delegation event, followed by a backend that takes longer than the silence window, would still advance the slide;
  - a silence or part-gap expiry while the hold is open is ignored until `answerVoiced`; after that it releases the
    hold and does its normal action (next part, next slide, wrap-up close);
  - the 15 s timer releases the hold and restores normal timing (ordinary silence if the slide has voiced output,
    otherwise the nudges continue); it never advances synchronously;
  - pause, stall pause, navigation, end and close clear the hold.
  - *Follow-up window (2026-09-22, user request after the first live run):* the model no longer bridges back on its
    own. After answering it stays silent (prompt rule), and once the answer is followed by `FollowUpWaitMs` (5 s) of
    quiet (setting `Presenter:FollowUpWaitMs`, 2500–60000, env `FOLLOW_UP_WAIT_MS`; guide `docs/guides/002-audience-questions.md`), `Presenter` appends `slide-N-resume-K`: say a short bridge, then restart the interrupted sentence (the live
    run resumed mid-sentence and was hard to follow). A new question inside the window reopens the hold. The resume
    replaces the old "normal action on silence", so the slide never advances straight after an answer; the ordinary
    timers take over once the model speaks again, or after the usual silence if it does not. The 15 s timer stops once
    the answer is heard, so a long answer is not cut short; an unanswered hold also ends with the resume instruction.
    Cost: a false hold (noise transcribed as speech while the model keeps talking) adds up to 5 s at the next pause.
- **Diagnostics:** info lines `question: hold opened`, `question: delegated (backend|client)`, `question: answered
  after N ms`, `question: released after 15 s without an answer`; no transcript text at info level.

```
audience ──speech──► GPT-Live ──input_transcript──► Presenter: hold opens, timers cleared, 15 s armed
  deck covers it: GPT-Live answers ──voiced output──► answerVoiced ──silence──► hold released → normal action
  deck doesn't:   GPT-Live ──delegation.created(responses)──► Presenter: answerVoiced reset, 15 s re-armed
                  GPT-Live ⇄ backend (gpt-5.6-luna) ──injected answer──► voiced output ──silence──► released
  no answer:      15 s ──► hold released, normal timing (nudges if the slide never spoke)
```

### 4.6 Alternatives considered

| Option | Why not (now) |
|---|---|
| Push-to-talk | No echo at all, but voice interruption becomes a key press; the user chose voice barge-in. |
| Half-duplex (mic off while the AI speaks) | No voice barge-in mid-sentence. |
| Loopback reference only | Loudspeakers in a large room still leak past AEC; the gate is the safety net. |
| A WASM echo canceller (Speex or WebRTC AEC3) | Heavy, and duplicates the browser's AEC; revisit if the live check fails. |
| Server-side gating | The server cannot see the browser's playout timing, so it would guess the far-end window. |
| A1: our own backend call (client delegation) | Full control of context and validation, but it needs transcript reconstruction, an HTTP client and separate credentials; about twice the code. Revisit if managed delegation misbehaves (research 005, option A). |
| A1: a classifier deciding deck vs backend | An extra call and latency on every question; the live model already makes that choice when it delegates. |

## 5. Impact and risk

- **Latency:** the loopback adds about 20–50 ms of output latency. Buffered playback already smooths network jitter.
- **Autoplay:** the `<audio>` element must start inside the Start click; the fallback covers a refusal.
- **Headphones:**
  - **risk:** before `c` adapts, speech during AI audio needs to be 12 dB above 0.5 × far;
  - **why it clears quickly:** echo-only frames with headphones drive `c` down within about 1 s;
  - **verify:** covered by a unit test and the live check.
- **Tests:** thresholds are constants in `echoGate.ts`, tunable after the live check without a design change.
- **A1, false holds:** room noise or leaked echo transcribed as `user` speech opens the question hold. Blank deltas are
  ignored and the hold lasts at most 15 s; Next/Prev/Pause still override it.
- **A1, backend availability:** if the `gpt-5.6-luna` deployment does not exist in the Azure resource, `session.start`
  is rejected; the safety net restarts with client delegation, so the talk runs deck-only and logs why.
- **A1, cost and latency:** backend calls are billed at Responses rates on the priority tier, only for delegated
  questions. The latency is measured in the live run, not promised.
- **A1, rollback:** set `Upstream:DelegationModel` (and the fallback's) to empty for client mode; the hold is isolated
  in `Presenter`.
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
| T8 | A1 question hold | `Presenter.cs`, `PresenterTests.cs` | `dotnet test` Application | `Question_holds_slide_until_the_answer_is_voiced_and_goes_quiet`; `Question_holds_a_pending_part`; `Question_during_wrap_up_prevents_close`; `Unanswered_question_releases_after_15_seconds_without_advancing`; `Audio_before_the_latest_user_delta_is_not_the_answer`; `Question_before_any_output_keeps_the_nudges`; `Pause_and_navigation_clear_the_hold` |
| T9 | A1 delegation mode, options and safety net | `UpstreamOptions.cs`, `LiveSessionOptions.cs` and their binding, `LiveSession.cs`, `LiveSessionTests.cs` (fake WebSocket server) | `dotnet test` Infrastructure; `-warnaserror` | `Session_start_sends_responses_delegation_when_a_model_is_set`; `Empty_delegation_model_sends_client_delegation`; `Rejected_delegation_retries_once_with_client_delegation`; `Response_event_completion_and_error_are_logged` |
| T10 | A1 delegation handling and prompt | `Presenter.cs`, `PromptBuilder.cs`, `PresenterTests.cs`, `PromptBuilderTests.cs` (goldens) | `dotnet test` Application | `Client_delegation_gets_a_scoped_answer_now_instruction`; `Responses_delegation_resets_the_answer_and_rearms_the_hold`; the prompt golden dies if "answer from the material / delegate otherwise / never claim to have checked / do not resume until answered" disappears |
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
  5. **A1:** ask one question the deck answers (expect an immediate answer, then the same slide continues) and one it
     does not (expect at most "One moment.", then a short backend answer). The log shows `question: …` lines and no
     slide change before each answer finishes, and no `delegation: backend unavailable` line.
  6. **A1:** say something, then stay quiet while the model says nothing: after 15 s the talk carries on.

## 8. Open questions

None. The barge-in flush is set to 300 ms and the stall response to pause-and-warn; the user decided both on
2026-09-22.

## 9. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-22 | Requirement brief confirmed (G1) | Layered echo fix, flush after 300 ms of barge-in, pause and warn after two failed nudges |
| 2026-09-22 | Plan approved (G2) | The user chose "Approve and implement"; implementation starts after the web-fix branch lands |
| 2026-09-22 | A1 decisions | Deck first + backend; hold until answered (15 s); GPT-Live-managed Responses delegation; `gpt-5.6-luna`; low effort, priority, low verbosity; research 005 by `agy` and `pi` sol |
| 2026-09-22 | A1 approved | The user chose "Approve and implement" for T8–T10 |
