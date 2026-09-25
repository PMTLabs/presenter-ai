# 011 — Press-to-ask

**Date:** 2026-09-24
**Status:** Approved (2026-09-24) — T1 passed 2026-09-24 (see work log)
**Size:** M (presenter event loop, a pure recorder/compressor, a transcriber port, ordered `/ws` admission, web
presenter app, a CLI live probe, docs; no persistence, no HTTP change).
**Area:** `src/PresenterAi.{Application,Api,Cli,Infrastructure}`, `web/app`, `docs/`.
**Branch:** `feature/011-press-to-ask`, stacked on `feature/010-live-presenter-training` (its PR follows plan 010's).
**Requirement brief confirmed:** 2026-09-24 (G1, with owner edits)
**Review:** rounds 1–2 (`docs/review/025-plan-011-plan-review.md`, `docs/review/026-plan-011-plan-review-round-2.md`) folded in (see §10).

---

## 1. Goal

A user presses **Ask**: the presenter stops at once and GPT-Live hears nothing while the user speaks for as long as they
need, pauses included. On **Ask done** the recording, with its long silences squeezed out, reaches the model in one
burst, and it answers the whole question once, then checks in and resumes the sentence it was in.

## 2. Requirement (as confirmed at G1)

- **Problem:** GPT-Live ends turns with its own server-side VAD (we send no turn detection, and it has no response-cancel
  command). A user who pauses mid-question to think gets an answer, or a backend delegation, on half a question.
- **In scope:**
  - An **Ask** button in every talk (not only Trainer mode), next to Pause. Keys: **A** starts; **A or Enter**
    finishes. While asking, the control shows "Listening…" with an elapsed timer, **Ask done** and **Extend**.
  - Ask: narration stops at once and queued playback is flushed. The GPT-Live mic is muted, so upstream can neither
    answer nor delegate early. The server buffers the user's mic PCM instead of forwarding it.
  - Ask done (button, A or Enter): the server compresses silences longer than about 0.5 s (energy-based, on PCM16) so
    upstream VAD cannot split the question. It sends the recording upstream in one burst and unmutes. The model
    answers once and may delegate as usual.
  - After the answer, the existing check-in ("Shall I carry on?") runs, then the talk resumes from the interrupted
    sentence (plan 007 flow).
  - Quiet timeout: after 90 s without voice, a recording that holds speech is treated as Ask done. One without
    speech cancels the ask and the talk stays paused. **Extend** (owner edit) restarts the quiet timer. The UI shows
    the remaining time as the timeout nears.
  - **Transcription is a plug-in, built but not enabled (owner edit).** A pluggable transcriber port receives the ask
    audio and yields text. Its default registration is disabled, so today there is no live transcript during an ask
    and no spoken "ask done". The only available model is not real-time; a real transcriber comes in a later pass.
    Built and unit-tested now, behind the port: a per-language "ask done" lexicon that holds data only.
    - en: "ask done", "done asking", "that's my question", "I'm done", "over to you".
    - vi: "hỏi xong", "tôi hỏi xong rồi", "xong rồi", "đó là câu hỏi của tôi".

    The lexicon matches only at the end of an utterance (a suffix) and strips the phrase from the question. With the
    disabled transcriber it is inert; a test transcriber proves the wiring.
  - Correct interaction with Pause/Resume, Trainer mode (plan 010 hold gate, pending edits), End,
    disconnect/reconnect, navigation, and the plan 009 guards. An ask counts as activity, so a thinking pause never
    idle-ends the talk; max length still ends it.
- **Out of scope:** changing GPT-Live turn detection; enabling a real transcription model; persisting ask audio;
  asking on behalf of other connections; starting an ask by voice.
- **Constraints:** T1 is a **live probe**: a burst-sent, silence-compressed recording must be answered as one question.
  If it fails, stop and return to the owner (fallback candidate: stream + discard, "Option 1"). `/ws` changes are
  additive only and documented in `docs/reference/001` §8 and the `AGENTS.md` frame line. The recording buffer is
  bounded, with its overflow behaviour decided here. No ask audio is stored, and there is no ask-specific transcript
  store. The question's upstream transcript is saved in the session transcript through `SessionRecorder`, like any
  spoken question (owner decision C2, 2026-09-24: "record like any question"). No secrets appear in logs.
- **Acceptance criteria:**
  1. Pressing Ask during narration stops speech within about 1 s. Nothing is spoken and no delegation starts until
     Ask done, even across a 10 s silence mid-question.
  2. Ask done (button, A or Enter) yields one answer to the whole question.
  3. After the answer: check-in, then resume. 90 s of quiet → done if there was speech, else cancel and stay paused.
     Extend keeps the ask open.
  4. Works from Paused and with Trainer mode on. End, disconnect or reconnect during an ask clean up with no stray
     answer afterwards. The idle guard does not end a talk during an ask.
  5. The transcriber port exists with a disabled default. With a test transcriber, the en/vi end-of-utterance
     "ask done" phrases finish the ask and are stripped (unit and integration tests). Production behaviour is button
     and keys only.
  6. Build (`-warnaserror`), all suites, and web lint/test/build are green. The live probe and a live run are recorded
     in the work log with usage seconds.
- **Decisions:**

| # | Question | Decision |
|---|---|---|
| G1-1 | Where ask audio goes | Buffered on the server and sent in one burst at Ask done; upstream muted while asking |
| G1-2 | Keys | A starts; A or Enter finishes |
| G1-3 | Quiet timeout | 90 s; done if speech was heard, else cancel and stay paused |
| G1-4 | **Extend** (owner edit) | Restarts the 90 s quiet timer; UI counts down near the timeout |
| G1-5 | **Transcription** (owner edit) | `IAskTranscriber` port with a disabled default; the lexicon and suffix matching sit behind it, unit-tested |
| P-1 | Buffer bound and overflow (**owner, 2026-09-24, after the T1 cap sweep**) | **25 s of kept speech**, counted after the online silence compression (§4.1), so silence and thinking time do not count; raw recorded time is not capped. The burst is sent **unpaced**. At the cap the ask finishes on its own (`reason:"limit_sent"`, toast), and the UI shows the remaining speech time. Why: an unpaced burst is ingested only up to about 30 s and the rest is dropped silently; pacing delays the answer and let the model answer during the send (work log, T1 cap sweep). Wall time is bounded by the quiet timer, Extend presses and the talk's max length |
| P-2 | How the answer is triggered | Natural upstream VAD end after the burst plus an explicit 1 s zero tail; no extra event. `response.create` only if T1 shows VAD never answers (§4.1) |
| P-3 | Ask from Paused | Same answer path as from narration: the check-in decides; "yes" or its timeout resumes; "no" or Pause stays (code fact, §9) |
| P-4 | Ask while mic-muted | The UI shows a local warning for A and keeps the button disabled with a hint; the server still refuses (`refused_muted`). Mute while listening cancels the ask; Mute during the answer or check-in stops the mic but never the upstream input clock (P-16) |
| P-5 | User Resume, navigation or Mute while listening | Cancels the ask (recording discarded), then does the command. Tool-originated commands are refused while listening. In the answer phases, Continue (`resume`) ends the exchange and resumes |
| P-6 | Keys while listening | Space is ignored, so it cannot cancel the question. Escape keeps its meaning (End). Arrow keys navigate (and cancel) |
| P-7 | Phrase-stripped question text | Stays on the finished ask (logged by length only); not sent upstream until the real transcriber pass |
| P-8 | One ask exchange | A single loop-only phase from Ask start to check-in resolution; every model-facing emitter consults it (§4.1 site list) |
| P-9 | Work started before Ask | Abandoned at Ask start: run generation bumped and tool rounds cleared, as navigation does; no result is submitted and no `response.create` is sent for it |
| P-10 | Ordered bridge admission | Audio and every presenter command except `start`/`end` enter the presenter through one serial, bounded, cancellable queue per connection |
| P-11 | Waiting for the answer | `AnswerStartBudgetMs` = **15,000** (T1 suite: the longest first-answer latency at 24 s of kept speech was about 9.7 s) replaces the generic 15 s question hold during the exchange, with the finite ceiling of §4.1 |
| P-12 | Send failure | A refused append mid-burst resets the upstream (serialized reset transition, §4.1; the next Ask or Resume reconnects); never `sent` after a detected failure |
| P-13 | Burst vs live speech at check-in (**owner, 2026-09-24; replaces the `start_ms` provenance design**) | Turn-taking rule: at check-in, only a **new** user utterance counts as yes/no. Its first transcript delta must arrive after the check-in began **and** after at least 1.5 s with no user transcript. Barge-in while the check-in is still being spoken is allowed. An unclear reply gets the existing follow-up, asked once more. **Continue** always works. Why: upstream `start_ms` drifted up to 4.3 s past our input clock under upload lag, and in every run the last question delta arrived around answer start |
| P-14 | Admission overflow | The receive loop never blocks. A full admission queue closes the socket with 1011 (the existing backpressure close), and disconnect → End cleans up |
| P-15 | Probe-only input marks | `ILiveSession.MarkInputPosition`/`InputPositionMarked` (built in T1) stay **probe-only**; the presenter does not use them |
| P-16 | Upstream output is paced by input audio (T1 finding) | The answer never gets ahead of the input audio the upstream has received, and it stalls if input stops. After the burst and through the answer and check-in, input keeps flowing: live mic frames, or the `LiveSession` pump's silence when the browser sends none. The upstream `Mute` is never sent during the answer phases (§4.1) |
| P-17 | **Continue** button (owner, 2026-09-24) | While the answer or check-in runs, "Continue" shows next to Pause. It skips the check-in and resumes narration from where it was interrupted. It is driven by the additive `answering` state of `ask_state` (§4.3 frame sequence) |
| P-18 | Clipped burst start (T1 finding) | Ask done waits, without blocking, for the upstream's `session.input_audio.unmuted` ack (at most 2 s; on timeout it logs and proceeds), then sends a 200 ms zero lead-in and the burst. Evidence: the burst reached the wire at +29 ms, before the unmuted event at +56 ms, and the start of the question was lost (vi: "Chương trình đã thay đổi", once all of part 1; en: "What") |
| C2 | Ask question transcript (owner, 2026-09-24) | "Record like any question": saved through `SessionRecorder` like any spoken question; no ask audio and no ask-specific transcript store |

## 3. Current state (as built on `feature/010-live-presenter-training` @ `dea8208`)

**Upstream session (`src/PresenterAi.Infrastructure/Live/LiveSession.cs`)**
- `CreateStartEvent` sends model, instructions, voice and delegation, and no turn detection (`:683-697`). Research
  says there is no `response.cancel` or `delegation.cancel`; a pending tool round is abandoned by not sending
  `response.create` (`docs/research/005-gpt-live-delegation-audience-questions.md:241-244`). `session.update` can
  change only `delegation.responses` (`:299`). `response.create` is documented only as resuming backend inference
  after tool outputs (`:213-219`).
- `ContinueResponses` sends `response.create` (`:204-217`). `Mute`/`Unmute` send `session.input_audio.mute`/`unmute`
  (`:219-227`); mute is an **input** control and cancels no output.
- `SendAudio` returns true when it **enqueues** an `AudioFrame`, not when the socket sends it (`:229-243`;
  `Enqueue :729-731`). All frames go through **one FIFO outbound channel** (`_outbound` `:27`, send loop `:306-330`),
  which sends audio only while the session is Open (`:311-330`).
- A pump timer enqueues a `PumpFrame` every 20 ms (`:405-426`). A `PumpFrame` fills 960-byte zero frames only while
  wall time runs more than 120 ms ahead of the audio already sent (`:13-15`, `FillSilenceToNowAsync :428-442`;
  48 bytes/ms = 24 kHz PCM16 mono). **A burst sent faster than real time stops the fill until wall time catches up,
  so the silence that lets VAD end the turn must be sent explicitly.** A `PumpFrame` can be enqueued between burst
  chunks. It fills nothing once chunks are ahead of wall time, but zero frames can land before the first chunk.
- The pump runs whenever the session is Open, **whatever the mute state**. `PumpLoopAsync` checks only
  `State == Open` (`:405-426`), and `Mute` only enqueues the `session.input_audio.mute` command (`:219-227`). So with
  no browser mic frames, the input clock still advances at wall time once any burst lead has been consumed. Whether
  the upstream still advances its input clock for audio appended while muted is not verified. The plan therefore
  never mutes upstream during the answer phases (P-16).
- The upstream acknowledges `session.input_audio.unmute` with `session.input_audio.unmuted` (FakeLiveServer mirrors
  it, `tests/…/Live/FakeLiveServer.cs:296`). `LiveSession`'s receive switch has **no case for it**
  (`LiveSession.cs:544-600`), and `Unmute` only enqueues the command (`:232-235`). So nothing can wait for the ack
  today (P-18).
- The send loop counts every appended ms, pump silence included, in `_sentMs` (`SendAudioFrameAsync :444-457`). That
  is the upstream input clock. `session.input_transcript.delta` carries `start_ms`/`end_ms`, which reach the presenter
  unchanged (`:521-522`); whether they are on that input clock is unverified (T1).

**Presenter loop (`src/PresenterAi.Application/Presenting/Presenter.cs`, 2,691 lines;
`Presenter.Training.cs`, 671 lines)**
- One bounded event channel of 256 with `Wait` (`:50-56`). Commands await their write and then their completion
  (`EnqueueCommandAsync :356-362`, `WriteAsync :364-374`). Producers use `QueueFromProducer`, whose full-channel
  fallback writes asynchronously (`:376-387`). Tool-originated commands carry `InvocationCallId` (`:359`).
- Mic path: `SendAudioAsync` → `SendAudioCommand` (`:249-250`) → `SendAudioCore`, which forwards while Presenting or
  Paused and unmuted (`:2182-2183`). Audio is not activity (`:638`; pinned by
  `PresenterTalkGuardTests.Mic_frames_are_not_activity`, `tests/…/PresenterTalkGuardTests.cs:168`).
  `MuteCore`/`UnmuteCore` set `_muted` and call the session (`:2158-2180`).
- `PauseCore` (`:2016-2033`): clears timers, cancels a tool confirmation, closes the speech permit, appends
  `PauseInstruction` (`PromptBuilder.cs:149-150`), goes Paused, calls `_guard.Pause()` and raises `Flush`.
- Model-facing and narration-moving paths:
  - `OnAudio` forwards while Presenting, or while Paused under a speech permit (`:1005-1010`). The first voiced audio
    after the question enters `Answering`, unless a backend delegation is pending (`:1068-1078`). That check compares
    `EndMs` values (`IsAfterLatestQuestion :2495-2496`).
  - `OnTranscript` opens or extends the hold on every user delta while Presenting (`:1110-1128`). That resets
    `_answerVoiced`, re-arms the 15 s hold and clears silence and wrap-up timers (`OpenOrExtendQuestionHold
    :2459-2473`). It feeds the 700 ms assembler (`:1131-1151`), whose completion runs `VoiceCommandMatcher`
    (`:1177-1235`).
  - `OnQuestionHoldElapsed` releases an unanswered hold into `ResumeAfterQuestion` (`:2324-2332`), as does
    `HoldBlocksProgress` (`:2336-2350`). `ResumeAfterQuestion` appends the resume-after-question instruction only
    when `_heardOutput` (`:2352-2368`).
  - `OnDelegation` returns unless Presenting (`:1455`).
  - `OnDelegatedResponse` has no state check: it re-arms the question hold and may `TryContinueResponses`
    (`:1474-1520`).
  - `OnToolCall` accepts calls while Presenting **or Paused** (`:1545`); a confirmation opens a speech permit
    (`:1594-1601`).
  - `OnToolInvocationCompleted` accepts Presenting or Paused, submits the output and calls `TryContinueResponses`
    (`:1699-1738`); a call that is no longer tracked is dropped (`:1712-1716`).
  - `OnApprovedToolCompleted` announces only for the same session and run generation (`:1783-1801`).
  - Navigation bumps `_runGeneration` and clears approvals (`OnNavigationSucceeded :2008-2014`).
    `ToolRoundTracker.Clear` forgets delegations, so a later finish is `Ignored` (`ToolRoundTracker.cs:115-120,170`).
  - `RaiseLimitWarning` appends an instruction while Presenting (`:2552-2558`).
  - `OnNudge` re-prompts while Presenting (`:1886-1918`).
  - `OnWrapUpFallbackAsync` returns while a hold is open (`:1920-1932`).
- The check-in is spoken because of the system prompt ("Shall I carry on?", `PromptBuilder.cs:34`). The presenter
  side is Answering → `AwaitingCarryOn` → yes, no or `FollowUpWaitMs` (`:1268-1276`, `:1335-1360`).
- `Interaction` is one enum (`:105`) that 15 sites reset with `SetInteraction(Interaction.None)` (`:676`, `:1122`,
  `:1232`, `:1269`, `:1358`, `:1380`, `:1756`, `:1766`, `:2025`, `:2115`, `:2133`, `:2202`, `:2268`, `:2484`,
  `:2502`).
- Guards: `TalkGuard.Pause()` drops the idle deadline and arms the pause grace (`TalkGuard.cs:68`).
  `StartPresenting` re-arms idle (`:50`). `StopPauseGrace` reschedules with a new generation and has **no caller**
  (`:76`). A queued `GuardElapsed` is dropped unless its generation is current (`Presenter.cs:490-493`). Grace
  defaults to 120 s, minimum 30 (`PresenterSettings.cs:10`); when due, `SuspendUpstreamAsync` detaches and closes
  the upstream (`:2035-2052`). `ReconnectAsync` restores it and re-mutes only for `_muted` (`:2066-2091`).
- Ending: `EndAsyncCore` (`:2185-2239`) and `OnClosed` (`:2259-2301`); `SessionClosed` is handled only for the
  current `_session` (`:448-454`).
- Plan 010 emitters:
  - `EnqueueEdit` appends `ScriptEditPendingInstruction` and may `EnterHold` (flush + hold instruction) while
    Presenting (`Presenter.Training.cs:367-394`).
  - `ReconcileWithHead` appends `ScriptEditFailedInstruction` (`:480-484`). It replays via `ReplayCurrentSlide`
    while Presenting, or sets `_replayOnResume` while Paused (`:486-502`, `:536-549`). Only `ResumeCore` consumes
    that flag (`TrainingOverridesResume :555-573`).
  - `ReconnectAsync` runs `OnTrainingSessionReady` (reconcile) before returning (`:160-170`).

**Voice lexicons:** `VoiceCommandMatcher.Match` compares whole normalised utterances (`VoiceCommands/VoiceCommandMatcher.cs:48-74`,
normalisation `:76-97`). `ConfirmationLexicon` is the per-language data table (`VoiceCommands/ConfirmationLexicon.cs:15-18`).
`AudioLevel.VoiceThreshold = 120` RMS (`AudioLevel.cs:7`).

**Bridge and web**
- The receive loop does not await presenter work: binary frames go to `ObserveCommand(_presenter.SendAudioAsync(…))`,
  and text commands likewise (`PresenterBridge.cs:355-388`, `:444-460`, `ObserveCommand :518-538`). When the event
  channel is full, a write is pending while the next socket frame is already dispatched. **The socket order is
  therefore not a contracted loop order.** A server that cannot keep up closes with 1011 (`AGENTS.md`, parity
  semantics).
- Unknown type → `error{code:"protocol"}` (`:482`). Events become frames at `:67-87`. A disconnect ends the talk
  (`:204-219`), so a reconnecting browser never finds a running ask.
- `bridgeClient.ts`: `send` (`:161-164`), pause/resume/mute (`:182-193`), `sendAudio` while presenting or paused
  whatever the mute state (`:209-215`). The receive switch ignores unknown types (`:227-283`).
- `Present.tsx`: the key handler skips inputs, selects, textareas and the Trainer switch. Space pauses or resumes,
  arrows navigate, M mutes, S starts and Escape ends; A and Enter are unbound (`:330-356`). Pause is at `:517-527`,
  Mute at `:542-551`, toast keys at `:21`; the toast store is keyed (`store/toastStore.ts:28-49`).
- Capture: 20 ms frames (`audio/echoGate.ts:3`), with browser echo cancellation, noise suppression and AGC on
  (`audio/capture.ts:98-103`).

**Harnesses:**
- `FakeSession` records `mute`/`unmute`/`audio` without bytes (`tests/…/Presenting/FakeSession.cs:91-106`).
- `FakeLiveServer` keeps `Received` (`tests/PresenterAi.Infrastructure.Tests/Live/FakeLiveServer.cs:49`) and
  handles appends and mute (`:176`, `:292-296`).
- `SmokeCommand` is the CLI probe pattern (`src/PresenterAi.Cli/SmokeCommand.cs:15-125`).
- The CLI builds owner-mode and file-mode services (`tests/PresenterAi.Cli.Tests/CliTests.cs:81-93`).
- DI builds the presenter in `AddPresenter` (`src/PresenterAi.Infrastructure/DependencyInjection.cs:176`,
  `new Presenter(…)` `:231-262`).

**Scouting corrections:**
- `OnAudio` runs `:997-1093`, with its forwarding decision at `:1005-1010`.
- `NarrationHeld` is `Presenter.Training.cs:81`; `EnterHold` is `:385-394`.
- `bridgeClient` pause/resume is `:182-187`, mute `:188-193`, `send` `:161-164`, `sendAudio` `:209-215`.
- `Present.tsx` Pause is `:517-527`, Mute `:542-551`.

## 4. Design

### 4.1 Approach

**Design rules (plan 010 lesson, review 025 class fix).**
1. **One ask exchange.** All ask state lives in one loop-only object, `_exchange` (`AskExchange?`), whose `Phase`
   runs from Ask start to check-in resolution.
2. **One consult point.** Every source that can make the model speak or act, or move narration, consults it through
   one of two helpers, never an ad-hoc `_ask != null` check:
   - `ExchangeAllows(ModelAction)` answers now.
   - `DeferUntilExchangeEnds(ModelNotice)` queues a model-facing notice to run exactly once when the exchange ends.
3. **One ordered entry.** Audio and ask commands reach the loop through one ordered bridge admission path.
4. **No locks.** The feature adds no lock, no semaphore and no bounded wait on the loop.

The §4.4 table names the mechanism for each handoff.

**Honest invariant.** While listening, nothing the upstream produces is audible and nothing it requests is acted on:
- audio is not forwarded;
- new tool calls are refused;
- rounds started before Ask are abandoned, with no output submitted and no `response.create`;
- tool-originated commands are refused;
- model-facing notices are deferred.

The upstream may still generate output for work begun before Ask; no upstream control can cancel it (§3). T1 and T8
observe how much.

**The ask exchange (new partial `Presenter.Asking.cs`; not an `Interaction` member, because 15 reset sites would
drop it).**

| Phase | Entered by | Talk state | Upstream mic | Ends with |
|---|---|---|---|---|
| `Listening` | Ask start (atomic, below) | Paused, grace stopped | muted; mic → `AskRecorder` | Ask done / quiet / cap / phrase → `Sending`; cancel reasons → end `Stay`; End → end `Ended` |
| `Sending` | Ask done | Paused | unmute enqueued; **sub-phase `AwaitingUnmuteAck`** (no mic forwarded or recorded), then lead-in + chunks enqueued | ack or 2 s timeout → lead-in + burst queued → `AwaitingAnswer`; refused unmute/append → end `Stay` with `send_failed` + upstream reset; End/max/disconnect → end `Ended`; a user command → as in `Listening` |
| `AwaitingAnswer` | burst queued | Presenting, hold open | live, never muted (P-16) | first voiced assistant audio with no backend delegation pending (`:1068`) → `Answering`; answer budget expiry → end `Resume` (`no_answer` log) |
| `Answering` | as above | Presenting | live, never muted (P-16) | the existing 700 ms quiet → `AwaitingCarryOn` → `CheckIn` |
| `CheckIn` | `AwaitingCarryOn` | Presenting | live, never muted (P-16) | a qualifying yes (site 15), **Continue**, or `FollowUpWaitMs` → end `Resume`; a qualifying no → end `Stay` (`WaitingOnSlide`); an unclear qualifying reply → the existing follow-up, once more (the model answers it and the check-in runs again), and a second unclear reply is left to the timeout |

In any phase, navigation ends it with `Navigated`, and End, max length, disconnect or upstream loss with `Ended`. A
user Pause in `AwaitingAnswer`/`Answering`/`CheckIn` ends it with `Stay`; in `Listening` a Pause is a no-op.

**Exchange end** (`EndExchange(outcome)`, idempotent, exactly once per exchange):
- Clears `_exchange` first.
- Takes the deferred list and the replay-due flag, clearing both, then:

| Outcome | Deferred notices | Replay due | Then |
|---|---|---|---|
| `Resume` | appended once, in order | the current slide replays from its start instead of the resume instruction, once | if still held → `EnterHold()`; else the replay or `ResumeAfterQuestion`. With `!_heardOutput` it adds `ResumeInstruction` + nudge instead, so a talk asked before its first narration audio does not stall |
| `Stay` | appended once (a paused model; audio not forwarded) | `_replayOnResume = true`, consumed once by the next `ResumeCore` | stays Paused, or `WaitingOnSlide` after "no" |
| `Navigated` | dropped (logged `ask: dropped N deferred notices`) | dropped: the navigation presents its own slide | the navigation runs |
| `Ended` | dropped | dropped | End/close as today |

**Emitter sites — every one consults the exchange** (T4 has one scenario test per row, with the triggering completion
or event arriving **after** Ask start). The gates are **layered defences**, not isolating oracles: for several rows
(sites 1, 3–7, 11, 12, 17) the precondition is already closed by the Ask start itself (Paused state, closed permit,
cleared tracker, bumped run generation, cleared timers), so removing that one `ExchangeAllows` consult leaves the suites
green. Nothing reachable re-opens those preconditions after Ask, so no cheap scenario can isolate them; the mutation
table of `docs/research/010-plan-011-wiring-audit.md` records which gates are pinned alone (e.g. sites 2 and 14) and
which are layered (review 027, finding 7):

| # | Site (code) | Policy while an exchange runs |
|---|---|---|
| 1 | `OnAudio` forwarding (`:1005-1010`) | `Listening`: not forwarded. Later phases: forwarded (the answer) |
| 2 | `OnToolCall` (`:1543`; hold re-arm `:1565`) | `Listening`: immediate `Failure("busy: the audience is asking a question")`, no confirmation or permit. Later: as today (the answer may use tools); its hold re-arm goes through `ArmAnswerWait()` |
| 3 | `OnToolInvocationCompleted` (`:1699`) | Rounds from before Ask are untracked (P-9) → dropped. `Listening`: no submit, no continue (gate) |
| 4 | `OnDelegatedResponse` (`:1474`; re-arms `:1486`, `FinishBackendDelegation :1519`) | Pre-Ask delegations are `Ignored` (tracker cleared). `Listening`: no re-arm, no continue. Later: `ArmAnswerWait()` |
| 5 | `OnApprovedToolCompleted` (`:1783`) | Pre-Ask approvals fail the run-generation check → not announced |
| 6 | `TryContinueResponses` (`:1732`) | `Listening`: never sends `response.create` |
| 7 | `OnDelegation` (`:1434`) | `Listening`: already returns (Paused). Later: as today; its hold opening (`OpenOrExtendQuestionHold :2459-2473`, re-arm `:2472`) goes through `ArmAnswerWait()` |
| 8 | Trainer `EnqueueEdit` pending instruction + `EnterHold` (`Presenter.Training.cs:367-394`) | The edit is enqueued; the notice and the hold's flush + instruction are deferred |
| 9 | `ReconcileWithHead` failure instruction (`:480-484`) | Deferred |
| 10 | `ReconcileWithHead` replay/release (`:486-502`) | Sets the exchange's replay-due flag in every phase (the only owner; `_replayOnResume` is adopted into it at Ask start) |
| 11 | `OnQuestionHoldElapsed` (`:2324`), `HoldBlocksProgress` → `ResumeAfterQuestion` (`:2336-2368`) | No release while an exchange runs; the answer budget and `EndExchange` own it |
| 12 | `OnNudge` / `ArmNudge` (`:1886`, `:2386`) | Not armed or acted on during an exchange |
| 13 | `RaiseLimitWarning` instruction (`:2556-2557`) | Deferred; the `limit_warning` frame (toast) is immediate |
| 14 | `UnmuteCore` (`:2170-2180`) via `/ws` `unmute` | `Listening`: refused; the upstream stays muted and `_muted` is unchanged |
| 15 | `OnTranscript` → assembler / hold (`:1110-1128`), incl. the out-of-range GoTo reply (`:1199-1211`) and the "model speaking" command filter (`:1188`, `:1193-1197`) | **Turn-taking rule (P-13).** Before `CheckIn`, every user delta is UI-only: no assembler, no hold reset, no `_answerVoiced` reset. In `CheckIn`, a delta **opens a new utterance** only if it arrives (loop time) after `CheckIn` began **and** at least 1,500 ms after the previous user delta of any kind. Every non-qualifying delta, e.g. a late fragment of the question, stays UI-only and restarts the 1.5 s quiet window. Voiced model audio after the check-in began (a blip, or a reaction to the reply before its transcript arrives) re-enters `Answering` but does not revoke the check-in: the rule still applies and the re-entered check-in is the same one (logged once); a fresh answer wait (follow-up, filler) starts before its check-in again (T8 live run). A qualifying utterance may start while the check-in is still being spoken (barge-in), so the existing "ignored (model speaking)" filter does not apply to it. Its intent is filtered **right after matching**, before the GoTo range branch: only Yes/No/Resume/Pause act; anything else is unclear (see `CheckIn` above), never navigation or End. `start_ms` is not used |
| 16 | Tool-originated commands (`InvocationCallId`: Next/Prev/Goto/Pause/Resume/ConfirmEnd) | `Listening`: refused. Later: navigation ends the exchange `Navigated` |
| 17 | `OnWrapUpFallbackAsync` (`:1920`) | Already returns while the hold is open; the exchange keeps `_questionHoldOpen` set |
| 18 | `OnScriptEditDeclined` (`Presenter.Training.cs:360-363`), reached from a `revise_script` confirmation that is declined or times out (`Presenter.cs:1363-1368`, `:1752-1759`) during `AwaitingAnswer`/`Answering` | Deferred notice. **Voice confirmation in the answer phases (owner decision, review 027):** while a tool confirmation is pending in `AwaitingAnswer`/`Answering`/`CheckIn`, a user delta opens a reply by the site-15 turn-taking rule (a new utterance after 1.5 s of user-transcript quiet; late question fragments stay UI-only). The reply's purpose is fixed at its first delta: an utterance that opened before the confirmation began (e.g. a check-in "yes") is discarded when the confirmation starts and can neither approve nor decline it (review 028, finding 1). Only Yes approves and No declines; anything else, navigation included, is left to the confirmation timeout. Once it settles, the answer's 700 ms quiet timer resumes, so the check-in still runs |
| 19 | `OnUpstreamError` → `FinishBackendDelegation` (`:1522-1540`) | `Listening`: unreachable (tracker cleared, so no pending backend delegation). Later: `ArmAnswerWait()`. A `backend_error` carries no delegation id, so an error left over from before Ask can close a post-Ask delegation; the ceiling below bounds the effect |

**Answer wait (P-11): one phase-aware helper, `ArmAnswerWait()`.** It replaces every `ArmQuestionHold()` call in
`Presenter*.cs`: `OnToolCall :1565`, `OnDelegatedResponse :1486`, `FinishBackendDelegation :1519` (also reached from
`OnUpstreamError :1539`) and `OpenOrExtendQuestionHold :2472`.
- Outside an exchange it calls `ArmQuestionHold()`, as today.
- In `Listening` it does nothing.
- In `AwaitingAnswer` it re-arms one generation-guarded budget timer for `AnswerStartBudgetMs = 15,000`. That comes
  from the T1 suite: the longest Ask-done → first-answer-audio at 24 s of kept speech was about 9.7 s, and
  1.5 × 9.7 s rounded up to 5 s is 15 s.
- Re-arms never extend past a **finite ceiling**, `AwaitingAnswer` start + `AnswerStartBudgetMs` + 60,000 ms, even
  while tool or backend work is outstanding.
- In `Answering`/`CheckIn` it does nothing: the existing interaction timers own those phases.

On expiry: log `ask: no answer within N s`, end `Resume`. A late answer then plays like any late answer after a hold
release today. The generic 15 s hold timer is ignored during an exchange (site 11).

**Input keeps flowing (P-16, T1 finding).** The upstream paces its output by the input audio it has received: an
answer never gets ahead of it, and it stalls if input stops.
- After the burst, and throughout `AwaitingAnswer`, `Answering` and `CheckIn`, input comes from:
  - live mic frames, forwarded by `SendAudioCore` unless the user muted;
  - the `LiveSession` pump's zeros when the browser sends none. The pump is independent of mute and of the
    presenter (§3).
- Nothing in this plan stops that. The upstream `Mute()` is sent only in `Listening`, and `Unmute()` precedes the
  burst.
- A user Mute during the answer phases sets `_muted` (the mic is no longer forwarded; the pump keeps the clock) and
  **defers the upstream `Mute()` to the exchange end**. The exchange continues.
- A suspension cannot happen during an exchange (grace stopped in `Listening`; Presenting afterwards).

**Input marks are probe-only (P-15).** `ILiveSession.MarkInputPosition()`/`InputPositionMarked` (built in T1,
`ILiveSession.cs:21-25`, `:59-61`) measure the burst's position on the upstream input clock for the probe. The
presenter does not use them. T1 showed upstream `start_ms` drifting up to 4.3 s past that clock under upload lag,
so check-in uses the turn-taking rule (site 15) instead.

**Ask start (atomic on the loop, `AskStartCore`, command `ask_start`):**
1. **Checks:**
   - Refused (`ask_state off`) unless the state is Presenting or Paused (`refused_not_live`) and the mic is unmuted
     (`refused_muted`).
   - A start while an exchange is in `Listening` re-emits `listening`.
   - **A start in an answer phase (`AwaitingAnswer`, `Answering`, `CheckIn`) is a follow-up question (owner decision,
     review 027):** the same exchange listens again. There is no `off` frame: the sequence is `answering` → `listening`.
     The current answer is stopped (flush, pause instruction, upstream mute), and work of the answer is abandoned as at
     any Ask start. Deferred notices, the replay-due flag and the **resume point of the first ask** carry over: the
     exchange records at the first Ask whether the slide's narration had been heard, and answer audio never changes
     that. When the follow-up's answer ends (`Resume`), narration resumes from the sentence the first Ask interrupted
     (`ResumeAfterAskInstruction` with the follow-up wording: restart the narration sentence, do not continue an earlier answer), or with
     `ResumeInstruction` + nudge when the first Ask came before any narration audio.
     Every refusal of a follow-up (muted, not live, no session, failed reconnect, refused mute, transcriber failure)
     goes through one path: while the answer's upstream is live it sends `off{reason}` and re-sends `answering`, and
     the answer continues; when the upstream is gone (a refused rollback unmute reset it) the answer exchange ends
     once with `off{reason}` (`Stay`: deferred notices kept for the reconnect, replay due kept for the next resume) and
     is never re-announced (review 028, findings 2–3).
   - A start during the unmute-ack wait (`Sending`) first ends that exchange `Stay` (`off{paused}`), then starts a new
     one.
2. **Reconnect.** If `_suspended`, `await ReconnectAsync()`. Afterwards **re-check** the state (Presenting/Paused),
   `_session` non-null and `!_suspended`; on failure → `refused_not_live` (a failed reconnect has already ended the
   talk). The reconcile that the reconnect ran happened before the exchange existed; its `_replayOnResume` is
   adopted in step 4.
3. **Stop the grace first.** From here to the end of the handler there is no await. `_guard.StopPauseGrace()` gives a new generation, so a
   `GuardElapsed` already queued behind this command is dropped (`:490-493`). A grace that fired before this command
   ran was processed first and left `_suspended` (step 2).
4. **Fallible steps, with rollback:**
   - `_session.Mute()`. If false → roll back: re-arm the grace if Paused (`_guard.Pause()`) and emit
     `off{unavailable}`.
   - `transcription = _askTranscriber.Begin(askId)`. If it throws → roll back: `Unmute()`, re-arm the grace and emit
     `off{unavailable}`. If that `Unmute()` returns false → the **upstream reset transition** (below), so no muted
     session lingers and the next Resume or Ask reconnects.
5. **Infallible steps:**
   - Create `_exchange` (`Listening`), adopting `_replayOnResume` into replay-due. From now on no mic frame is
     forwarded.
   - `ResetUtterance()`.
   - Abandon pre-Ask work (P-9): `_runGeneration++`, `_toolRoundTracker.Clear()`, `_approvedTools.Clear()`,
     `_navigatingCallIds.Clear()`.
   - `PauseCore()` if Presenting (timers, `PauseInstruction`, Paused, `Flush`), else `ClearQuestionHold()` +
     `ClosePermit()`.
   - `_guard.StopPauseGrace()` again, because `PauseCore` re-arms it.
   - Arm the 1 s tick and emit `listening`.

   Log: `ask: listening (from presenting|paused)`.

**Recording.** `SendAudioCore`: in `Listening`, the frame goes to `AskRecorder.Append` and `transcription?.Append`
and returns true. When the recorder reports full → Ask done with `limit_sent`. In other phases, forwarding is as
today.

**AskRecorder** (new, pure, `Application/Presenting/Asking/AskRecorder.cs`). It is an online, energy-based silence
compressor over 20 ms windows (960 bytes; odd bytes and partial windows carry over).

| Constant | Value | Why |
|---|---|---|
| `VoiceRms` | 120 (`AudioLevel.VoiceThreshold`) | Browser noise suppression and AGC are on (`capture.ts:98-103`); RMS bands are logged for tuning (R4) |
| `MaxVerbatimGapMs` | 500 | Pauses up to about 0.5 s are ordinary rhythm the model already hears; longer ones are the thinking pauses that end turns |
| `GapHeadMs` / `GapTailMs` | 160 / 160 | A longer gap becomes 320 ms: it keeps the decay and attack around words and stays under the common 500 ms server-VAD silence default. GPT-Live's value is undocumented, so T1 measures it |
| `LeadMs` / `TrailMs` | 160 / 160 | Leading and trailing silence trimmed |
| `TailSilenceMs` | 1,000 (zeros) | The pump fill stalls after a burst (§3); VAD needs silence to end the turn |
| `MinSpeechMs` | 200 voiced | Below this, the ask is empty |
| `MaxRetainedMs` | 25,000 (1.2 MB) of kept speech | P-1 cap (owner): counted after compression; silence does not count. An unpaced burst is ingested only up to about 30 s upstream |
| `ChunkMs` | 200 (9,600 bytes) | One `input_audio.append` per chunk; distinguishable on the wire from 960-byte pump frames |

Algorithm: after the first voiced window, quiet windows accumulate in `pending`. On the next voiced window, a run of
up to 25 windows (500 ms) is kept as it is. When a run passes 25 windows, its first 8 windows are kept and only a
rolling 8-window tail ring is retained; the next voiced window flushes that ring. Before the first voiced window only
the ring is kept (lead-in). `Complete()` keeps up to 8 trailing windows, adds the zero tail and returns 200 ms
chunks. Stats (recorded, kept, voiced ms; RMS bands <30 / 30–120 / 120–500 / 500–2k / ≥2k; last-voice time) feed the
quiet timer and the log. T1 may retune only `TailSilenceMs` (500–2,000) and the kept gap (200–400 ms), recording
each attempt.

**Ask done → `Sending` (event-driven; no loop handler awaits; a reset closes off-loop, below):**
1. Stop the tick and dispose the transcription. Below `MinSpeechMs` voiced → end `Stay` with `empty`.
2. **Unmute and wait for the ack (P-18).**
   - `chunks = recorder.Complete()` is kept on the exchange. `_session.Unmute()` is called (false → failure boundary,
     step 3), and the sub-phase becomes `AwaitingUnmuteAck`.
   - A generation-guarded 2,000 ms timer is armed, and the handler returns: the loop is free.
   - New `ILiveSession.InputAudioUnmuted` event (default empty accessors), carrying the ack's echoed
     `client_event_id`. `LiveSession` raises it on `session.input_audio.unmuted`, as a new case in the receive switch,
     and `Unmute(out eventId)` reports the event id it sent. The presenter wires it in `WireSession` →
     `QueueFromProducer(UnmuteAcked(session, clientEventId))`.
   - **Ack attribution (review 027, finding 1).** Only the ack of this ask's own unmute proceeds. With an echoed id it
     must equal the id this ask's unmute sent. An ack without an id is attributed FIFO: the presenter counts every
     unmute it sent on the session and every ack received, and the ask proceeds only when the ack count reaches its own
     unmute's ordinal, so a late ack of an earlier ask (timed out or cancelled) or of a user unmute is consumed as debt.
     A lost ack only delays the burst to the 2 s timeout; it never releases it early.
   - On the loop, the first of its own `UnmuteAcked` (for the current session) or `UnmuteAckTimeout(generation)`
     proceeds, guarded by sub-phase and generation, so it proceeds exactly once. A timeout logs `ask: unmute ack timed out
     after 2000 ms; sending`. The later of the two is ignored.
   - Mic frames during the wait are neither recorded nor forwarded; the pump keeps the input clock (P-16).
   - **End, max length, disconnect or upstream loss** during the wait → `EndExchange(Ended)`: the stored chunks are
     dropped and the timer generation bumped, so a late ack or timeout finds no exchange and **nothing is sent**.
   - A user Resume, navigation, Mute or `ask_cancel` during the wait → the `Listening` column of the command table,
     i.e. discard the question, with no burst.
3. **Proceed:** `SendAudio(200 ms of zeros)` as a lead-in, then `SendAudio(chunk)` for each chunk, **unpaced** (P-1:
   at most 25 s of kept speech, within the ~30 s the upstream ingests from an unpaced burst). `true` means only
   *queued on the live session*. T1 measured ≤ 100 ms to put a 24 s burst on the wire locally, with upstream
   ingest lag up to 7.7 s.
4. **Failure boundary (P-12).** If `Unmute` or any append returns false (the session is closing and chunks 1…N−1 may
   already be on the wire), the ask ends `Stay` with `off{send_failed}`, never `sent`, followed by the **upstream
   reset transition** below. The toast says the connection was reset; the next Ask or Resume reconnects a **new**
   session. A failure *after* every chunk was queued (the send loop fails) is upstream loss: `SessionClosed` →
   `OnClosed`, the talk ends `upstream_lost` and the exchange `Ended`.

   **`answering{sent}` therefore means "fully queued on the live session", and the protocol doc says so.**
5. Variant B only (T1): a 3 s generation-guarded one-shot calls `ContinueResponses()` if the phase is still
   `AwaitingAnswer`.
6. `SetState(Presenting)`, `_guard.StartPresenting()`, `_questionHoldOpen = true` (null end), phase
   `AwaitingAnswer`, arm the budget, emit `answering{reason}` (after the burst is queued, not at Ask done). `EndExchange` later emits the one `off` frame with the
   outcome's reason.

   Log: `ask: sent (<reason>) — recorded 34.2 s, kept 12.1 s (voiced 9.8 s) + 1.0 s tail, 67 chunks; rms bands …`.
   No content is logged.

**Upstream reset transition (serialized; used by the send failure and by the start rollback).** It never awaits on
the loop.
1. **On the loop, synchronously:** `_resetGeneration++`. Detach the session (`_session = null`, `_sessionInfo = null`),
   so its later `SessionClosed` is ignored (`:448-454`). Set `_suspended = true` and state Paused (grace re-armed by
   `_guard.Pause()`). Move `_connectedAt` into `_pendingReset = (session, generation, connectedAt)` and clear
   `_connectedAt`, so no other path estimates that segment. Publish the snapshot and `upstream{suspended}`.
2. **Off the loop:** `session.CloseAsync()` (bounded by the session's close timeout), then
   `QueueFromProducer(UpstreamResetClosed(session, generation, seconds))`, observed like other background work.
3. **Back on the loop:** if `_pendingReset` matches that session and generation, the segment is accounted with
   `AccumulateSegment(seconds, capturedConnectedAt)` exactly once, and `_pendingReset` is cleared. **In every case**
   the session is disposed exactly once (here, and nowhere else for a reset session). A stale completion, after End,
   max length, take-over or a newer reset, does nothing else: no usage, no snapshot, no status.
4. **If the talk ends first:** `OnClosed` folds a still-pending reset segment into the usage as an estimate (usage
   unconfirmed), then clears `_pendingReset`. The late completion then only disposes.

Usage is therefore recorded exactly once on every path, and no Paused snapshot is published after `closed`.

**Answer trigger (P-2).** Server VAD already answers spoken questions (every plan 005/007 Q&A).
`session.instructions.append` steers speech at once (it drives narration), so it could answer before the audio is
committed. `response.create` is documented only for resuming tool rounds. Default **variant A**: burst + 1 s zero
tail, no event. **Variant B** (the step-4 one-shot) is built only if T1 shows A never answers.

**Quiet timer, Extend, ticks.** One 1 s timer (generation-guarded) during `Listening`:
- `quietMs = now − max(lastVoicedAt, startedAt, lastExtendAt)`.
- At ≥ 90,000 ms: Ask done `quiet_sent` if speech was heard, else end `Stay` `quiet_cancelled`.
- Otherwise it emits `listening` with `elapsedMs`, `quietRemainingMs` and `heard`.
- `ask_extend` sets `lastExtendAt = now` and emits at once.
- The client renders server values.

**Commands during an exchange** (`ProcessCommandAsync`, before the command runs; user-originated = no
`InvocationCallId`):

| Command | `Listening` | `AwaitingAnswer` / `Answering` / `CheckIn` |
|---|---|---|
| Pause | no-op | end `Stay`, then pause |
| Resume / **Continue** | end `Stay` (`resumed`, `_replayOnResume` set), then `ResumeCore` consumes it | end `Resume` (`continued`) in every answer phase, incl. `CheckIn`: deferred notices once, then the replay or `ResumeAfterQuestion`. It always works, whatever the transcript is doing |
| Next/Prev/Goto | end `Navigated` (`navigated`), then navigate | end `Navigated`, then navigate |
| Mute | end `Stay` (`muted`), then mute | `_muted = true` (mic not forwarded); the upstream `Mute()` is deferred to the exchange end, so the input clock keeps running (P-16); the exchange continues |
| Unmute | refused (site 14) | as today |
| Trainer toggle / Train on this | allowed; notices deferred (site 8) | same |
| End / max length / disconnect / upstream loss | end `Ended`: no burst, no unmute; `off{ended}` precedes `closed` while the socket is still open (a disconnected socket gets nothing) | end `Ended` |

**Guards:**
- Paused has no idle deadline, and `StartPresenting` at Ask done re-arms it (AC4).
- The pause grace is stopped for all of `Listening` and re-armed by `Stay`.
- Max length ends the talk as today.

**Transcriber port (new `Application/Presenting/Asking/`).**

```csharp
public interface IAskTranscriber { IAskTranscription? Begin(string askId); }        // null = disabled
public sealed record AskTranscriptUpdate(long Revision, string Text, bool Final);   // cumulative text of this ask
public interface IAskTranscription : IDisposable
{
    void Append(ReadOnlySpan<byte> pcm16);   // presenter loop: copy and return; never block
    event Action<AskTranscriptUpdate>? Updated;   // any thread; revisions strictly increase per ask
}
public sealed class DisabledAskTranscriber : IAskTranscriber { public IAskTranscription? Begin(string askId) => null; }
```

- `AddPresenter` registers `DisabledAskTranscriber` as the singleton and passes it to a new optional `Presenter`
  constructor parameter.
- `Updated` → `QueueFromProducer(new AskTranscriptChanged(askId, update))`. On the loop, the update is dropped unless
  the exchange is `Listening` with that id and `update.Revision > lastRevision`; stale, reordered and duplicate
  updates therefore change nothing.
- **End of utterance is decided by update time, not by the mic energy clock:**
  - `Final` → evaluate now.
  - Otherwise a 700 ms debounce is re-armed on every accepted update, as the existing assembler does (`:1146-1150`),
    and evaluates the latest text when it elapses.
- `AskDoneLexicon.Match(text)` → `AskDonePhrase(Language, Phrase, Question)` or null. A match → Ask done with
  `phrase_sent`, and the stripped question stays on the finished exchange (P-7). Log:
  `ask: done by phrase (vi), question 42 chars`.
- `transcribing` in `ask_state` = `transcription is not null`, which is always false in production.

**AskDoneLexicon** (data only, beside `ConfirmationLexicon`):
- The en/vi phrases of §2, tokenised with the matcher's normalisation (FormC, lower case, apostrophes dropped).
  Trailing fillers ("please", "ạ") and punctuation are ignored.
- Every language is checked; the **longest** phrase equal to the utterance's last tokens wins.
- "Question" is the text before the first matched token, with trailing punctuation trimmed.
- A phrase alone matches with an empty question. A phrase that is not at the end never matches.

**Bridge: ordered admission (P-10).** Each `ClientConnection` owns an `Admission` queue: a bounded channel of 256
items with one reader.
- **What goes through it:** binary audio and `ask_*`, `pause`, `resume`, `next`, `prev`, `goto`, `mute`, `unmute`,
  `trainer_mode` and `train_turn`, in socket order. `start` keeps its observed path.
- **`end` bypasses it:** `CancelConnect` off-loop, then the End command. This holds because the receive loop never
  blocks (below), so an `end` frame is always read and handled at once.
- **The pump** awaits each presenter call to **completion** before taking the next item. At most one pump call is
  ever in flight, so the loop order is the socket order without relying on any channel's waiter order. The presenter
  loop never waits on the bridge.
- **Full queue (P-14):** the receive loop uses `TryWrite`, never an awaited write. At capacity it closes the socket
  with **1011** (reason `backpressure`, the existing "cannot keep up" close in `AGENTS.md`), and the normal
  disconnect → End cleanup ends the talk.
  - Chosen over an urgent second lane for `end`: that would need a second reader and ordering rules between the lanes.
  - 256 items is about 5 s of audio behind a wedged loop, and a talk in that state is not usable anyway.
  - The queue stays bounded, and `end` never waits behind PCM.
- **Disconnect** (incl. the overflow close): the queue is completed, and the pump stops at the next item boundary and
  drops the rest. No pump call starts after disposal begins. Cleanup then:
  1. calls `_presenter.AbortPendingStart()` (off-loop ticket cancellation, `Presenter.cs:285`), which unblocks a loop
     held in a Start or reconnect;
  2. awaits the pump, bounded by 5 s (`AdmissionDrainBound`);
  3. calls `ObserveEndAsync`.

  So no item of an old connection reaches a later talk. An item already written to the presenter is
  processed before the End command, inside the old talk.
- Faults are logged as `ObserveCommand` does. `ask_state` is not sent on connect, because a disconnect ends the talk.

**Web.**
- New `components/AskControls.tsx` next to Pause.
  - Idle: **Ask**, `aria-keyshortcuts="A"`. While muted it is disabled, with the visible hint "Unmute to ask".
  - Listening: `role="status"` "Listening… m:ss", plus **Ask done**, **Extend** and **Cancel**, and the remaining
    speech time, e.g. "0:07 of speech left", from `speechRemainingMs`. Under 20 s of quiet remaining it adds "Sending
    in 0:15 if quiet" or "Closing in 0:15 if quiet".
  - Answering (`ask_state` `answering`, through the answer and check-in): "Answering…", **Ask** (a follow-up question;
    the A key works too) and a **Continue** button
    next to Pause (P-17). It sends `resume`, which skips the check-in and resumes narration from the interrupted
    sentence (`EndExchange(Resume)`). It always works.
- `presenterStore.ask` holds the last `ask_state` and is cleared on `closed` or idle.
- Keys, keeping the input, select, textarea and switch early return:
  - While listening:
    - Space: `preventDefault`, no-op.
    - A or Enter without modifiers and `!e.repeat`: `preventDefault` + `askDone`. `preventDefault` also stops Enter
      from clicking a focused button.
    - Other keys keep their meaning.
  - Not listening, A without modifiers and `!e.repeat`, while live:
    - muted → the local warning toast "Unmute the microphone to ask a question." and **no frame**;
    - otherwise → `askStart`.
  - Ctrl/Cmd+A stays select-all.
- Toasts use the key `present:ask` (§4.3).

### 4.2 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| A — mute upstream, buffer on the server, compress silences, burst at Ask done, one exchange phase (chosen) | Upstream hears nothing until the question is complete; one question, one answer; works for everyone | Answer latency grows with kept length; burst VAD behaviour unverified; pre-Ask upstream work cannot be cancelled, only abandoned | **Chosen**, gated by the T1 live probe |
| 1 — stream + discard: forward live, suppress any answer or delegation until Ask done | No buffering; natural timing | The model answers or delegates on half a question (no cancel); a delegated answer cannot be recalled | Fallback only if T1 fails (brief) |
| 2 — client-side recording (browser buffers, sends on Ask done) | Server stateless | Socket mute race; a disconnect loses the question silently; compression in JS; server gating needed anyway | Rejected |
| 3 — `session.update` / configurable turn detection | Root-cause fix upstream | Not in our `session.start`; only `delegation.responses` can change (`005:299`); out of scope | Rejected |
| 4 — ask as an `Interaction` value | Matches the scouting note | 15 reset sites would clear it with upstream still muted | Rejected; `_exchange` |
| 5 — gate individual call sites on `_ask != null` (round 0) | Small | Misses already-running work, unmute, trainer notices, replay during the answer and late transcripts (review 025 A1/A2/D2) | Rejected; one exchange phase consulted by every emitter |
| 6 — rely on the bounded channel's waiter FIFO for socket order | No bridge change | Not a contract; a cancelled pending write breaks it (review 025 C1) | Rejected; ordered admission |
| 7 — cap on wall time or raw recording | Simplest bound | Thinking time fills it, making Extend useless | Rejected (P-1) |
| 8 — instruction append after the burst as trigger | Explicit | Speaks at once, maybe before VAD commits the audio | Not built; variant B uses `response.create` |

### 4.3 Protocol and constants

**`/ws` frames (additive; clients ignore unknown frames):**

| Dir | Frame | Rules |
|---|---|---|
| C→S | `{"type":"ask_start"}` | Answered by `ask_state` (`listening`, or `off` with `refused_*`/`unavailable`). During `answering` it is a follow-up question: `listening` again, with no `off` (a refused follow-up sends `off{refused_*/unavailable}` and re-sends `answering`) |
| C→S | `{"type":"ask_done"}` | While listening → `answering` `sent`, or `off` `empty`/`send_failed`; otherwise ignored (no frame) |
| C→S | `{"type":"ask_extend"}` | While listening → `listening` with `quietRemainingMs` 90000; otherwise ignored |
| C→S | `{"type":"ask_cancel"}` | While listening, or during the unmute-ack wait → `off` `cancelled`; otherwise ignored |
| S→C | `{"type":"ask_state","state":"listening","elapsedMs":23000,"quietRemainingMs":67000,"speechRemainingMs":13400,"heard":true,"transcribing":false,"reason":null}` | On start, every 1 s, and on extend |
| S→C | `{"type":"ask_state","state":"answering","elapsedMs":41000,"quietRemainingMs":null,"speechRemainingMs":null,"heard":true,"transcribing":false,"reason":"sent"}` | Once, when the burst is queued. `reason` ∈ `sent` (the whole burst queued on the live session), `quiet_sent`, `limit_sent`, `phrase_sent` |
| S→C | `{"type":"ask_state","state":"off","elapsedMs":…,"quietRemainingMs":null,"speechRemainingMs":null,"heard":…,"transcribing":false,"reason":"continued"}` | Exactly one per ask, and one per refused start. While listening: `cancelled`, `empty`, `quiet_cancelled`, `muted`, `resumed`, `navigated`, `send_failed`, `ended`, `unavailable`, `refused_muted`, `refused_not_live`. After `answering`: `continued` (yes, Continue, follow-up timeout, budget expiry), `waiting` ("no"), `paused`, `navigated`, `ended` |

**Frame sequence (changed in this revision, additive):** `listening` (every 1 s) → [unmute-ack wait, at most 2 s,
no frame] → `answering` (once per burst queued) → `off` (once, the exchange outcome). A follow-up Ask during
`answering` goes back to `listening` in the same exchange, so the sequence may repeat
`answering` → `listening` → `answering` before the one `off`. A cancellation while listening goes straight from
`listening` to `off`. The earlier sequence (`listening` → `off{sent}`) no longer exists;
`docs/reference/001` §8 documents the new one (T5).

Extra fields on the four commands are ignored, as for `pause`. `unmute` while listening is ignored, and the server
logs `ask: unmute refused while listening`. The `state` frame shows `paused:true` while listening.

**Web toasts (key `present:ask`):**

| Trigger | Kind | Message |
|---|---|---|
| A while muted (local) / `refused_muted` | warning | Unmute the microphone to ask a question. |
| `refused_not_live` / `unavailable` | warning | Ask isn't available right now. |
| `empty` | info | No question was heard — still paused. |
| `quiet_cancelled` | info | Nothing was heard for 90 s — Ask cancelled; still paused. |
| `quiet_sent` | info | Sent your question after 90 s of quiet. |
| `limit_sent` | info | 25-second speech limit reached — your question was sent. |
| `send_failed` | error | Couldn't send your question — the connection was reset. Press Ask to try again. |
| `resumed` / `navigated` / `muted` / `cancelled` | info | Ask cancelled. |

The other reasons show no toast.

**Page-log lines:**
- `ask: listening (from presenting|paused)`
- `ask: extended`
- `ask: sent (<reason>) — …`
- `ask: cancelled (<reason>)`
- `ask: send failed at chunk N/M; upstream reset`
- `ask: no answer within N s`
- `ask: exchange ended (<outcome>), N deferred notices, replay <yes|no>`
- `ask: dropped N deferred notices`
- `ask: done by phrase (<lang>), question N chars`
- `ask: tool command refused`
- `ask: tool call refused (busy)`
- `ask: unmute refused while listening`

There is no config key.

**`IPresenter` additions** (default bodies): `AskStartAsync`, `AskDoneAsync`, `AskExtendAsync`, `AskCancelAsync`
(`Task<bool>`, default `false`) and `event Action<PresenterAskState>? AskState`.
`PresenterAskState(string State, long ElapsedMs, long? QuietRemainingMs, long? SpeechRemainingMs, bool Heard, bool Transcribing, string? Reason)` (`State` ∈ `listening`, `answering`, `off`).

### 4.4 Sequences

**(a) Ask during narration → answer → check-in → resume (AC1–AC3)**

```mermaid
sequenceDiagram
  participant U as Browser
  participant A as Bridge admission (per connection, serial)
  participant P as Presenter loop
  participant R as AskRecorder (loop-only)
  participant L as LiveSession (FIFO outbound) → GPT-Live
  U->>A: ask_start (button / A)
  A->>P: AskStartAsync (awaited to completion)
  P->>P: StopPauseGrace
  P->>L: mute (fallible → rollback)
  P->>P: exchange = Listening, abandon pre-Ask rounds
  P->>P: PauseCore (Flush), StopPauseGrace, tick
  P->>L: PauseInstruction
  P-->>U: flush, state{paused}, ask_state listening
  loop mic frames in socket order (incl. a 10 s thinking pause)
    U->>A: PCM16
    A->>P: SendAudioAsync (awaited) → R.Append, not forwarded
  end
  Note over P: pre-Ask tool/delegation completions dropped; notices deferred; unmute refused
  U->>A: ask_done (button / A / Enter)
  A->>P: AskDoneAsync (after every earlier PCM frame)
  P->>R: Complete() → kept audio + 1 s zero tail, 200 ms chunks
  P->>L: unmute (sub-phase AwaitingUnmuteAck; 2 s timer; loop free)
  L-->>P: session.input_audio.unmuted → UnmuteAcked (or the 2 s timeout, logged)
  P->>L: 200 ms zero lead-in, then input_audio.append × N (all queued; else send_failed + reset transition)
  P->>P: Presenting, hold open, phase AwaitingAnswer, ArmAnswerWait
  P-->>U: ask_state answering{sent}, state{presenting}
  Note over P,L: input keeps flowing (mic frames, or pump zeros); the upstream is never muted in the answer phases (P-16)
  L-->>P: question transcript deltas (arriving up to about answer start) → UI only
  L-->>P: (optional delegation → ArmAnswerWait, capped) answer audio → Answering
  P->>P: 700 ms quiet → AwaitingCarryOn = CheckIn (turn-taking: a new utterance after check-in start and 1.5 s of transcript quiet; Continue always works)
  P->>P: qualifying yes / Continue / FollowUpWaitMs → EndExchange(Resume): deferred notices once, replay-due or resume instruction; off{continued}
  P->>L: notices, then ResumeAfterQuestionInstruction (or slide replay)
```

**(b) Ask from Paused, including a suspended upstream and grace expiry (AC4)**

```mermaid
sequenceDiagram
  participant U as Browser
  participant P as Presenter loop
  participant G as TalkGuard timer
  participant L as GPT-Live
  Note over P: Paused, grace armed
  alt grace fired before ask_start was processed
    G->>P: GuardElapsed → SuspendUpstreamAsync (suspended)
    U->>P: ask_start
    P->>L: ReconnectAsync (awaited; admission holds later frames)
    P->>P: re-check state, session, !suspended (fail → refused_not_live / talk already ended)
  else GuardElapsed queued behind ask_start
    U->>P: ask_start
    P->>P: StopPauseGrace (new generation) → the queued GuardElapsed is dropped
  end
  P->>L: mute
  P->>P: Listening … ask_done → burst → AwaitingAnswer (as in a)
  Note over P: answer → check-in → yes/timeout resumes; "no" → WaitingOnSlide (Stay); Pause → Stay
```

**(c) Quiet timeout with and without speech; Extend (AC3)**

```mermaid
sequenceDiagram
  participant U as Browser
  participant P as Presenter loop (1 s tick)
  U->>P: ask_start
  P-->>U: listening {quietRemainingMs 90000}
  alt user speaks, then thinks silently
    U->>P: ask_extend at quiet 80 s
    P-->>U: listening {quietRemainingMs 90000}
    P->>P: tick: quiet ≥ 90 s and voiced ≥ 200 ms → Ask done (quiet_sent)
    P-->>U: answering{quiet_sent} (toast) once the burst is queued, then (a); one off{continued|waiting|…} ends it
  else nothing voiced
    P->>P: tick: quiet ≥ 90 s → EndExchange(Stay, quiet_cancelled)
    P->>P: unmute, Paused, grace re-armed, deferred notices once, replay → _replayOnResume
    P-->>U: off{quiet_cancelled} (toast "still paused")
  end
```

**(d) End during an ask or its answer, incl. a burst only partly on the wire (AC4)**

```mermaid
sequenceDiagram
  participant U as Browser / guard / bridge cleanup
  participant A as Bridge admission
  participant P as Presenter loop
  participant L as GPT-Live
  U->>P: End (read at once — the receive loop never blocks — and bypasses admission: CancelConnect off-loop, then End)
  P->>P: EndAsyncCore → EndExchange(Ended): recorder zeroed, tick/budget stopped, deferred dropped
  P-->>U: ask_state off{ended} (only if the socket is still open), then closed
  P->>L: session.close — send loop stops; queued chunks not yet sent are dropped (audio only while Open)
  A->>P: a queued ask_done / PCM delivered after End → presenter not live → ignored
  Note over P: late upstream events: session no longer current; nothing forwarded after closed
```

**(e) Disconnect, take-over or upstream loss during an ask (AC4)**

```mermaid
sequenceDiagram
  participant U as Browser
  participant A as Bridge admission
  participant P as Presenter loop
  alt socket drops / tab takes over / admission full (server closes 1011 backpressure)
    U--xA: close
    A->>A: complete queue; pump stops at item boundary
    A->>P: AbortPendingStart (off-loop), await pump (≤ 5 s), EndAsync(disconnect | takeover) → as (d)
    U->>A: reconnect → state{idle} (no ask_state); Start resumes at the slide
  else upstream socket lost (incl. after the whole burst was queued)
    P->>P: SessionClosed(current) → OnClosed → EndExchange(Ended) → talk ends upstream_lost
  else append refused mid-burst (Sending)
    P->>P: off{send_failed}; on loop: detach, suspended, _pendingReset(gen); close off-loop
    P->>P: UpstreamResetClosed(session, gen) → usage once + dispose if gen current, else dispose only
    Note over P: the old session's SessionClosed is ignored; next Ask/Resume reconnects
  end
```

**Cross-thread handoffs and their mechanisms** (self-check: every handoff this plan touches):

| Handoff | From → to | Mechanism |
|---|---|---|
| PCM and ask/control frames → loop | socket receive → admission → presenter | **Ordered admission**: nonblocking `TryWrite` into one bounded queue per connection (full → close 1011); a single pump with at most one presenter call in flight, awaiting each completion, so loop order = socket order; disconnect completes it, and cleanup aborts pending starts, awaits the pump (≤ 5 s), then ends |
| `end` | bridge → loop | Always read at once (the receive loop never blocks); bypasses admission; existing off-loop `CancelConnect` + End command. Items delivered later find the talk not live (ignored) |
| User transcript at check-in | session → loop | Existing queue; the turn-taking rule uses **loop arrival time** only (check-in start, last user delta time), both **loop-only** state; no upstream timestamp is trusted |
| Input clock after the burst | browser mic / pump timer → upstream | Mic frames via ordered admission → `SendAudioCore`; otherwise `LiveSession`'s pump (`PumpLoopAsync` timer → FIFO outbound, independent of mute and of the presenter). No ask code stops either; the upstream `Mute()` is never sent in the answer phases (P-16) |
| Upstream reset close | loop → thread pool → loop | Detach and suspend on the loop; close off-loop; `UpstreamResetClosed(session, generation)` re-enters and is applied only if `_pendingReset` matches (**loop-only generation**); the session is disposed exactly once; `OnClosed` folds a pending segment |
| 1 s tick, answer wait (budget and ceiling), variant-B one-shot, phrase debounce | timer threads → loop | `QueueFromProducer(event(generation))`, dropped unless the generation is current (**loop-only**) |
| Transcriber updates | transcriber thread → loop | `QueueFromProducer(AskTranscriptChanged(askId, update))`; dropped unless the id matches and `Revision > lastRevision` (**loop-only, monotonic**); end of utterance by update-time debounce |
| Unmute ack → burst | session receive thread / ack timer → loop | `InputAudioUnmuted` → `QueueFromProducer(UnmuteAcked(session))`, plus a generation-guarded 2 s `UnmuteAckTimeout`. The first one proceeds, guarded by sub-phase, session and generation (**loop-only**); End clears the exchange, so both become no-ops |
| Mute → unmute → chunks → (continue) | loop → upstream | One FIFO outbound channel, enqueued by one loop handler with no await. `true` = queued only; wire failure → `SessionClosed` (current session) or reset (refused append). Pump zero frames may interleave and are distinguishable by size |
| Pre-Ask tool/delegation/approval completions | tool tasks, session → loop | Existing queue; made inert by the run-generation bump + tracker clear at Ask start (**loop-only generation**) and consulted through `ExchangeAllows` |
| Trainer `Changed` → reconcile | service → loop | Existing idempotent reconcile; model-facing effects routed through `DeferUntilExchangeEnds` / replay-due (**loop-only**, consumed once in `EndExchange`) |
| Grace timer vs Ask start | guard timer → loop | `StopPauseGrace` reschedules with a new generation; `GuardElapsed` is generation-checked (**loop-only**); no await after the reconnect re-check |
| End, max length, disconnect | bridge, guard → loop | Existing End / `GuardElapsed`; the exchange is ended on the loop. The only off-loop ask work is the reset close, which is generation-guarded; there is no **lock or permit** to release |
| Upstream audio, transcripts, delegations, tool calls | session threads → loop | Existing queue; handlers consult the exchange (**loop-only**) |
| `ask_state` → client | loop → bridge → socket | Raised on the loop like `ScriptEdit`; the client renders the last frame |

Nothing depends on two background events arriving in order, on channel waiter order, or on a token checked before
an await.

**Parallel paths checked:**
1. Mic input has one entry: `SendAudioCore` (grep `SendAudio(` in `Presenter*.cs` → `:2183`).
2. Paths that make the model speak or act: sites 1–19 above. **Grep audit (done for this plan, re-run in T7):**
   - **`ArmQuestionHold()` callers:** `Presenter.cs:1486`, `:1519`, `:1565` and `:2472`. All four go through
     `ArmAnswerWait()`.
   - **`AppendInstructions(` in `Presenter*.cs`:**

   | Line | Emitter | Where it stands during an exchange |
   |---|---|---|
   | `:822` | client-mode instruction | Start only; unreachable |
   | `:985` | narration (`SendNextPart`) | Behind `PresentSlide` / part gap; gated by Paused and the open hold (site 11); navigation ends the exchange first |
   | `:1209` | out-of-range GoTo reply | Site 15: intent filtered before this branch |
   | `:1407` | end confirmation | Voice filtered (site 15); a tool is refused while listening (site 16); later a tool ConfirmEnd pauses → `Stay` first |
   | `:1467` | client-delegation answer-now | Site 7: part of the answer, allowed after listening |
   | `:1899`, `:1907` | nudge | Site 12 |
   | `:1948` | wrap-up | `OnSilence` (hold-gated) or Next (navigation ends the exchange first) |
   | `:2027` | pause instruction | The ask's own, or a user Pause → `Stay` first |
   | `:2145`, `:2149` | `ResumeCore` | Resume ends the exchange first |
   | `:2359` | `ResumeAfterQuestion` | Site 11: only via `EndExchange` |
   | `:2557` | limit warning | Site 13 |
   | `Presenter.Training.cs:362` | edit declined | Site 18 |
   | `:376` | edit pending | Site 8 |
   | `:392` | `EnterHold` | Site 8 |
   | `:406` | `PresentHeldSlide` | Via `PresentSlide`: navigation or exchange end |
   | `:482` | edit failed | Site 9 |
   | `:543` | `ReplayCurrentSlide` | Site 10 |

   `AppendThinking(`, `AppendCommentary(`, `ContinueResponses(` and `SubmitToolOutput(` are covered by sites 3, 5
   and 6, and by the notes and narration paths above.
3. Paused→Presenting: `ResumeCore`, `LeavePauseForNavigation` and Ask done. Replay is owned by the exchange while it
   runs.
4. Unmute paths: `UnmuteCore` (refused while listening), `ReconnectAsync` (cannot run while listening: the grace is
   stopped and Resume/navigation end the exchange first) and the exchange exits.
5. Paths that could stop upstream input during an answer: `MuteCore` (upstream `Mute()` deferred to the exchange
   end), suspension (impossible during an exchange), session close (ends the talk). The browser sends mic frames only
   while presenting or paused (`bridgeClient.ts:209-215`); with none, the pump fills.

### 4.5 Surface list

| Surface | Change | Task |
|---|---|---|
| `Application/Presenting/Asking/AskRecorder.cs` | Online compressor, cap, stats | T1 |
| `Cli/AskProbeCommand.cs`, `Cli/Program.cs` | Live probe | T1 |
| `ILiveSession.cs` (`MarkInputPosition`, `InputPositionMarked`, default no-op) + `LiveSession.cs` (marker frame in the send loop) | Input clock marks, probe-only (P-15) | T1 |
| `ILiveSession.cs` (`InputAudioUnmuted`, default empty accessors) + `LiveSession.cs` (receive case `session.input_audio.unmuted`) + `FakeSession` (controllable ack) | Unmute ack (P-18) | T2 |
| `IPresenter.cs`, `PresenterEvents.cs`, `Asking/{IAskTranscriber,AskTranscriptUpdate,DisabledAskTranscriber}.cs`, `DependencyInjection.cs`, test `FakeSession` (bytes; refuse append at N; controllable marks; gated `CloseAsync`), `TestAskTranscriber` | Contracts | T2 |
| `Presenting/VoiceCommands/AskDoneLexicon.cs` | Lexicon + suffix matcher | T3 |
| New `Presenter.Asking.cs` (exchange, gate, deferral, budget); hooks in `Presenter.cs` (sites 1–7, 11–17, 19, `ArmAnswerWait`, reset transition, `SendAudioCore`, `ProcessCommandAsync`, `EndAsyncCore`, `OnClosed`, `StartAsyncCore`, loop switch) and `Presenter.Training.cs` (sites 8–10, 18) | Loop behaviour | T4 |
| `Api/Realtime/PresenterBridge.cs` (admission, ask cases, frame), `docs/reference/001` §8, `AGENTS.md` frame line | `/ws` | T5 |
| `web/app` `ws/bridgeClient.ts`, `store/presenterStore.ts`, new `components/AskControls.tsx`, `routes/Present.tsx` | UI and keys | T6 |

## 5. Impact and risk

| Question | Answer |
|---|---|
| State management — what survives a crash mid-operation? | Nothing: ask audio exists only in process memory, by design. A crash ends the talk; Start resumes at the slide |
| Data consistency — orphans, races, double-apply? | One exchange, loop-only, one exit (`EndExchange`); deferred notices and replay are consumed once. The burst is enqueued at most once. Stale ticks, budgets and transcriber revisions are dropped. Socket order is enforced by admission. No new persisted data. The upstream transcript of the burst is recorded by `SessionRecorder` like any spoken question (`Infrastructure/Sessions/SessionRecorder.cs:84`), consistent with owner decision C2 (§2); no ask audio and no ask-specific transcript store. The reset close is generation-guarded, so usage is recorded once and the session disposed once |
| User experience — root problem or symptom; any surprise? | Root problem. Surprises: the talk shows paused while listening; the answer arrives a few seconds after Ask done; Resume, navigation or Mute discard the question (toast); from Paused, the check-in timeout resumes (P-3); a send failure resets the connection; trainer notices are spoken after the answer, not during it |
| Backward compatibility — existing data / sessions / configs? Migration? | Additive frames and commands, defaulted `IPresenter` members, optional constructor parameter. Admission keeps existing command semantics (same socket order, now guaranteed). No config, schema or HTTP change |
| Error recovery — what happens on failure; can it recover? | Mute or `Begin` failure → rollback (grace re-armed, unmuted or upstream reset). Refused append → `send_failed`, upstream reset, Ask again on a new session. No answer within the budget → resume as after an unanswered question |
| Logging & debugging — enough to diagnose in the field? | One line per transition, with recorded, kept and voiced ms, chunk count, RMS bands, deferred counts and budget expiry; no audio or text content |
| Edge cases — empty, huge, repeated, concurrent, interrupted? | Empty → `Stay`; the cap → auto send; a repeated start is idempotent while listening; done/extend/cancel when not listening → ignored; key repeat is guarded; End/max/disconnect while an item waits for admission, a full admission queue, and a burst partly on the wire are tested (T5); a late question delta is inert before check-in, and at check-in unless it opens a new utterance after 1.5 s of transcript quiet; edits on both sides of Ask done replay once after the exchange |

**Deliberate `/ws` addition:** four commands and one server frame (§8 and the `AGENTS.md` frame line, T5). No existing
frame changes shape. Admission changes only how the bridge hands frames to the presenter.

**Risks:**
- R1 — VAD splits a compressed question or answers early → the T1 strengthened oracle (both halves' facts); retune
  within bounds, else stop (Option 1).
- R2 — Upstream throttles faster-than-real-time audio or drops appends around unmute → T1 checks errors, the complete
  transcript and send duration; else stop.
- R3 — Pre-Ask upstream work still produces output after Ask (no cancel) → abandoned rounds, audio not forwarded
  while listening, notices deferred. T1 and T8 observe how much the upstream still produces. After Ask done, residual
  output could be taken as the answer's start; the T1 mid-narration observation bounds it.
- R4 — A fixed 120 RMS threshold in a noisy room → noise suppression and AGC on; Ask done always works; the cap
  bounds memory; RMS bands are logged.
- R5 — A late delta of the question's transcript read as a check-in yes/no → the turn-taking rule (P-13, site 15).
  In every T1 run the last question delta arrived around answer start, well before check-in. Residual risk: a
  question fragment delayed by more than the whole answer plus 1.5 s; Continue and the follow-up still recover.
  T1 records `start_ms` against the computed burst range on every run. If the clock does not match, the stated
  bounded fallback applies only with the owner's agreement.
- R6 — Answer latency on a question at the cap → `AnswerStartBudgetMs` 15 s, against the measured maximum of about
  9.7 s at 24 s of kept speech. Upstream ingest lag reached 7.7 s.
- R9 — Upstream output is paced by input audio (P-16) → input never stops in the answer phases (mic or pump); T4 and
  T5 pin it.
- R7 — `Presenter.cs` size → `Presenter.Asking.cs`; hooks only in `Presenter.cs` and `Presenter.Training.cs`.
- R8 — Admission adds per-frame await latency → one loop round trip per 20 ms frame. A loop wedged for about 5 s
  fills the 256-item queue and closes the socket with 1011, which ends the talk (P-14); that is a deliberate
  trade-off for a bounded queue and an `end` that is always read. T8 watches for spurious 1011 closes.

**Rollback:** revert the branch; no data or config. Old clients never send `ask_*`; new clients against an old server
get `error{code:"protocol"}`.

## 6. Tasks

**Order and lanes.** **T1 is a gate:** nothing else starts until its strengthened probe passes, is recorded, and the
variant (A or B) and `AnswerStartBudgetMs` are written into §10. Then T2, which is small and fixes the contracts. Then
parallel lanes:
- **A** T3 (lexicon, pure).
- **B** T4 (presenter; its transcriber tests land after T3) → T5 (bridge admission, docs, end-to-end `/ws`).
- **C** T6 (web, against the §4.3 shapes).

T7 runs after all lanes, and T8 last. Every "Test that dies" is new unless marked (existing).

### T1 — Recorder and live probe (gate)  (AC1, AC2, AC6)
- **Files:** new `src/PresenterAi.Application/Presenting/Asking/AskRecorder.cs`, new `src/PresenterAi.Cli/AskProbeCommand.cs`,
  `src/PresenterAi.Cli/Program.cs`, `src/PresenterAi.Application/Presenting/ILiveSession.cs` +
  `src/PresenterAi.Infrastructure/Live/LiveSession.cs` (input-position marker; probe-only, P-15; default no-op
  elsewhere), `scripts/make-ask-probe-wavs-tts.ps1` (`gpt-audio-1.5` TTS, en + vi WAVs outside the repo),
  `scripts/run-ask-probe-suite.ps1` (the T1 matrix plus `presenter-cli ask-probe-summary` verdict), new
  `tests/PresenterAi.Application.Tests/Presenting/Asking/AskRecorderTests.cs`,
  `tests/PresenterAi.Infrastructure.Tests/Live/LiveSessionInputMarkTests.cs`, `tests/PresenterAi.Cli.Tests/CliTests.cs`,
  `tests/PresenterAi.Cli.Tests/AskProbeSuiteTests.cs`.
- **Change:** `AskRecorder` per §4.1; `MarkInputPosition`/`InputPositionMarked` (probe-only). `presenter-cli ask-probe --provider azure|openai --part1 <wav> --part2 <wav>
  [--gap-seconds 10] [--variant vad|continue|raw] [--tail-ms 1000] [--gap-keep-ms 320] [--observe-interrupt]`,
  modelled on `SmokeCommand`:
  - WAVs must be 24 kHz mono PCM16; otherwise it prints the `ffmpeg -ar 24000 -ac 1 -c:a pcm_s16le` hint.
  - The recording is part 1, then `gap` seconds of low noise (RMS about 20), then part 2.
  - The probe prints no audio and no secrets.
- **Probe deck:** one slide whose narration holds two independent, scorable facts:
  - fact A: "In its first year the programme moved the Da Nang office to paperless contracts";
  - fact B: "The Hanoi expansion cost 4.2 billion dong".

  Question part 1: "What did the programme change at the Da Nang office in its first year," … gap … part 2: "and how
  much did the Hanoi expansion cost?". The Vietnamese run uses the equivalent facts and question.
- **Procedure** (from the repo root, secrets from the CLI secret store; WAVs in the scratch dir, never in the repo):
  1. Connect like the presenter: `PromptBuilder.SystemInstructions` for the probe deck, with delegation when the
     route has a `DelegationModel`. Narrate the slide; wait for 3 s of quiet.
  2. **Mute control:** `Mute()`, append `PauseInstruction()`, then send 3 s of part 1 at real-time pace. Expect no
     assistant audio and no user transcript.
  3. Feed the recording through `AskRecorder` in 20 ms frames (`raw` skips compression) and print the stats.
  4. `Unmute()`, wait for `session.input_audio.unmuted` (≤ 2 s), then `MarkInputPosition()`, a 200 ms zero lead-in,
     all chunks unpaced, `MarkInputPosition()`, and time the send (P-18). The marks
     are evidence only (upload lag, truncation). `continue`: `ContinueResponses()` if no voiced assistant audio
     arrives within 3 s.
  4a. **Live reply** (evidence): after the answer ends, send a short "yes" WAV (`--reply <wav>`) at real-time pace.
  5. Observe for up to 60 s:
     - user transcript deltas (text, `start_ms`/`end_ms`, arrival time), grouped into user turns separated by any
       assistant output;
     - assistant transcript and voiced-audio intervals;
     - delegation and tool events, upstream errors;
     - first answer audio relative to the last chunk queued;
     - the arrival of the last burst-transcript delta relative to answer start and end (R5);
     - user deltas' `start_ms` against the marked burst range, and the truncation detector (evidence for P-1 and
       P-13).

     Then close and print `usage.seconds`.
  6. Runs (`scripts/run-ask-probe-suite.ps1`, `gpt-audio-1.5` TTS WAVs, every run unpaced):
     - English ×3 (10 s gap);
     - Vietnamese ×1;
     - **cap** ×3 at 24 s of kept speech;
     - over-cap controls at 28 s and 40 s, expected to truncate or fail;
     - one `raw` control;
     - one `--observe-interrupt`.
  7. **`--observe-interrupt`** (observation for R3/D1; not a pass criterion). While narration audio is playing, and
     separately while a delegation is active (a part-1-only question about an absent fact triggers it), apply Mute +
     `PauseInstruction()`. Record the ms of assistant audio the upstream still produces, and any delegation finish or
     tool call after the mute.
- **Pass (every required run of the chosen variant, including the 24 s cap runs):**
  - (i) no upstream error;
  - (ii) the mute control is silent;
  - (iii) no voiced assistant audio before the last chunk is queued;
  - (iv) **one question by arrival:** the question's transcript deltas arrive within the answer window (from the
    burst until the answer ends), with a part-1 keyword ("Da Nang" / its vi equivalent) **before** a part-2 keyword
    ("Hanoi"), and no other response or delegation starts in that window. Arrival order against assistant text is
    **not** a criterion: the upstream streams its answer while the question transcript is still arriving;
  - (v) **one answer** whose assistant transcript contains fact A ("paperless") **and** fact B ("4.2"/"four point
    two"), each scored independently and quoted in the log;
  - (vi) no second unsolicited response within 15 s after it ends. The "Shall I carry on?" check-in is part of the
    answer;
  - (vii) **the question transcript finished before the answer ended** (its last delta arrives before the last answer
    audio);
  - no TRUNCATED flag, meaning the whole burst was ingested;
  - (viii) **the question's start is present**: the user transcript contains the first words of part 1 ("What did
    the programme…" / "Chương trình đã thay đổi…"). A clipped start fails the run.

  A part-1-discarding implementation fails (iv) and (v). **A split, partial or wrong answer is a FAIL.**
- **Decision rule:**
  - If `vad` passes all runs → variant A.
  - If `vad` fails **only** because no answer starts at all → run `continue`; if that passes all runs → variant B.
  - Before declaring failure, retry `--tail-ms` 500/2000 and `--gap-keep-ms` 200/400, logging each attempt.
  - From the passing runs, set `AnswerStartBudgetMs = max(15 s, 1.5 × the longest Ask-done→first-answer-audio)`,
    rounded up to 5 s, and record the send duration. **Result so far (T1 suite):** 15,000 ms (max about 9.7 s at
    24 s); send ≤ 100 ms on the wire locally; upstream ingest lag up to 7.7 s. The over-cap controls must truncate or
    fail; if one passes, the summary fails T1.
  - **Stop-and-return rule:** any other failure, or no passing setting → record everything in the work log, set this
    plan's status to "Blocked at T1", start nothing else, and return to the owner with the Option 1 fallback.
  - The `raw` control and `--observe-interrupt` are evidence only.
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests --filter AskRecorder`; probe results recorded in
  `docs/progress/002-work-log-phase0.md` (variant, constants, criteria per run, latencies, send duration, timestamp
  behaviour, truncation, interrupt observation, usage seconds); a §10 row.
- **Test that dies if this breaks:** `AskRecorderTests`:
  - `Silence_run_over_500_ms_becomes_320_ms_with_160_ms_kept_each_side`;
  - `Pauses_up_to_500_ms_are_kept_verbatim`;
  - `Leading_and_trailing_silence_trimmed_to_160_ms_then_1000_ms_zero_tail`;
  - `Window_at_rms_120_is_voiced_and_119_is_quiet`;
  - `Frames_not_aligned_to_20_ms_carry_over_bytes`;
  - `Under_200_ms_voiced_is_not_speech`;
  - `Reports_full_at_25_seconds_of_kept_speech_and_ignores_silence_toward_the_cap`;
  - `Chunks_are_200_ms_and_concatenate_to_the_compressed_stream`;
  - `Stats_bands_and_last_voice_time_are_reported`.

  `CliTests.Ask_probe_requires_both_parts_and_rejects_unknown_options`.

  `LiveSessionInputMarkTests.Marks_report_sent_ms_in_fifo_order_including_pump_silence` (probe-only evidence).
  `AskProbeSuiteTests.Summary_passes_a_complete_matrix_and_derives_the_answer_budget`,
  `Summary_fails_when_a_required_run_fails_is_truncated_or_the_over_cap_control_passes`.

### T2 — Contracts, transcriber port, DI, harness  (AC5 foundation)
- **Files:** `Presenting/IPresenter.cs`, `Presenting/PresenterEvents.cs`, new
  `Presenting/Asking/{IAskTranscriber,AskTranscriptUpdate,DisabledAskTranscriber}.cs`, `Infrastructure/DependencyInjection.cs`
  (`TryAddSingleton<IAskTranscriber, DisabledAskTranscriber>`, passed to `new Presenter(…)` `:231`), `Presenter.cs`
  (optional constructor parameter), `tests/…/Presenting/FakeSession.cs` (`SentAudio` bytes in `Sent` order;
  `RefuseAudioFromChunk`; controllable `InputPositionMarked`; gated `CloseAsync`), new `tests/…/Presenting/Asking/TestAskTranscriber.cs` (records appended bytes;
  `Publish(revision, text, final)` from a thread-pool thread).
- **Change:** §4.3 `IPresenter` members and `PresenterAskState`; the port per §4.1; `ILiveSession.InputAudioUnmuted`
  and its `LiveSession` receive case (P-18).
- **Verify:** `dotnet build PresenterAi.slnx -warnaserror`; `dotnet test tests/PresenterAi.Api.Tests --filter Startup`;
  `dotnet test tests/PresenterAi.Cli.Tests`.
- **Test that dies if this breaks:**
  - `Api.Tests/StartupTests.Production_container_uses_the_disabled_ask_transcriber` (real `Program` DI).
  - `Infrastructure.Tests/Live/LiveSessionTests.Unmuted_server_event_raises_InputAudioUnmuted` (FakeLiveServer).
  - `Cli.Tests/CliTests.Owner_and_file_mode_build_the_presenter_with_the_disabled_ask_transcriber` (`BuildRunServices`
    and `BuildServices` with `ValidateOnBuild` resolve `IPresenter` and `IAskTranscriber` =
    `DisabledAskTranscriber`).
  - The existing build of every `IPresenter` implementer.

### T3 — AskDoneLexicon  (AC5)
- **Files:** new `Presenting/VoiceCommands/AskDoneLexicon.cs`, new `tests/…/Presenting/AskDoneLexiconTests.cs`.
- **Change:** §4.1 lexicon and suffix matcher.
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests --filter AskDoneLexicon`.
- **Test that dies if this breaks:**
  - `English_phrases_match_only_at_the_end_and_are_stripped` (theory over every phrase; "ask done, what next" → null).
  - `Vietnamese_phrases_match_in_composed_and_decomposed_form`.
  - `Longest_phrase_wins_toi_hoi_xong_roi_is_stripped_whole`.
  - `Trailing_fillers_and_punctuation_are_ignored`.
  - `Phrase_tokens_inside_other_words_do_not_match`.
  - `Phrase_alone_matches_with_an_empty_question`.
  - `Every_language_has_a_phrase`.

### T4 — Presenter: exchange, gate, deferral, budget, atomic start  (AC1–AC5)
- **Files:** new `Presenting/Presenter.Asking.cs`; hooks per §4.5; new `tests/…/Presenting/PresenterAskTests.cs`
  (FakeSession with bytes, `FakeTimeProvider`, real catalogue, real revision service over the in-memory store with a
  gated fake reviser).
- **Change:** everything in §4.1 from "The ask exchange" to "AskDoneLexicon", plus the variant T1 chose. No
  `Interaction` member and no lock.
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests`.
- **Test that dies if this breaks** (the emitter tests are one per §4.1 site row; in each, the triggering event
  arrives **after** Ask start):
  - **Sites:**
    1. `Assistant_audio_while_listening_is_not_forwarded`
    2. `New_tool_call_while_listening_is_refused_busy_without_confirmation_or_permit`
    3. `Tool_invocation_started_before_ask_completing_while_listening_submits_nothing_and_sends_no_continue`
    4. `Delegated_response_of_a_pre_ask_delegation_neither_continues_nor_rearms`
    5. `Approved_tool_started_before_ask_completing_while_listening_appends_nothing`
    6. `Continue_responses_is_never_sent_while_listening`
    7. `Delegation_event_while_listening_opens_no_hold`
    8. `Edit_enqueued_during_listening_and_during_answering_defers_notice_and_hold_until_the_exchange_ends`
    9. `Edit_failing_during_the_exchange_speaks_the_failure_notice_once_after_it`
    10. `Edit_applied_during_listening_answering_or_check_in_replays_exactly_once_after_the_exchange` (theory
        across Ask done: no flush or slide instruction during the answer; one replay)
    11. `Question_hold_timer_does_not_release_the_exchange_and_an_answer_after_15_s_within_budget_is_answered`
    12. `No_nudge_during_the_exchange`
    13. `Limit_warning_during_the_answer_defers_the_instruction_and_raises_the_frame_at_once`
    14. `Unmute_command_while_listening_is_refused_and_upstream_stays_muted`
    15. `Late_burst_transcript_after_the_first_answer_audio_runs_no_command_and_keeps_answering`,
        `Check_in_hears_a_real_yes_and_a_real_no`, `Check_in_treats_next_slide_as_a_follow_up_not_navigation`
    16. `Tool_originated_next_while_listening_is_refused_and_the_ask_continues`
    17. `Wrap_up_fallback_does_not_end_the_talk_during_the_exchange`
    18. `Trainer_confirmation_timing_out_during_answering_defers_the_declined_notice_once`
    19. `Upstream_backend_error_finishing_a_delegation_during_awaiting_answer_uses_the_answer_wait_not_the_hold`
  - **Answer wait (A4):**
    - `Tool_call_outlasting_the_budget_keeps_waiting_until_the_ceiling_then_resumes_once`
    - `Every_question_hold_re_arm_during_an_exchange_goes_through_the_answer_wait` (tool call, delegated response,
      backend finish, hold open)
  - **Check-in turn-taking (P-13):**
    - `Delayed_question_delta_after_check_in_begins_is_ignored` (a "yes" fragment arrives within 1.5 s of the
      previous user delta → no resume)
    - `Real_yes_after_one_and_a_half_seconds_of_transcript_quiet_counts`
    - `Real_no_after_quiet_gives_one_stay_even_after_an_ignored_late_yes`
    - `Barge_in_yes_while_the_check_in_is_still_spoken_counts`
    - `Unclear_reply_gets_one_more_follow_up_then_the_timeout_decides`
    - `Continue_ends_the_exchange_in_every_answer_phase` (theory: `AwaitingAnswer`, `Answering`, `CheckIn`)
    - `Delayed_question_delta_after_the_follow_up_timeout_changes_nothing`
  - **Input keeps flowing (P-16):**
    - `User_mute_during_the_answer_defers_the_upstream_mute_to_the_exchange_end`
    - `No_upstream_mute_is_sent_between_ask_done_and_the_exchange_end`
  - **Reset serialization (D8):**
    - `Reset_close_finishing_after_end_max_length_or_take_over_records_usage_once_and_publishes_nothing` (theory)
    - `Late_old_session_closed_after_reset_is_ignored_and_the_session_is_disposed_once`
    - `Reset_completion_while_paused_records_confirmed_usage_once_and_stays_suspended`
    - `No_paused_or_suspended_snapshot_after_closed`
    - `Newer_reset_makes_an_older_completion_stale`
  - **Outcomes:**
    - `Check_in_no_keeps_the_replay_for_the_next_resume_once`
    - `Navigation_during_the_answer_drops_notices_and_replay`
    - `End_during_the_answer_drops_deferred_actions`
    - `Answer_budget_expiry_resumes_once_with_the_nudge_when_no_output_was_heard`
  - **AC1:**
    - `Ask_during_narration_mutes_pauses_and_flushes_and_forwards_no_mic_audio`
    - `Ten_second_and_ninety_second_silences_while_listening_send_no_part_nudge_resume_or_advance`
  - **AC2:**
    - `Ask_done_unmutes_then_queues_the_compressed_burst_in_order`: `unmute`, the ack, a 9,600-byte zero lead-in,
      then the chunks. For variant B, `continue_responses` once after 3 s without answer audio, never after answer
      audio.
    - `Burst_is_not_queued_before_the_unmuted_ack` (no `audio` entry after `unmute` until `UnmuteAcked`; the loop
      processes other events meanwhile)
    - `Ack_timeout_after_2_s_logs_and_sends_the_burst_once` (a late ack afterwards sends nothing more)
    - `End_max_length_or_disconnect_during_the_ack_wait_sends_nothing` (theory; a late ack or timeout is a no-op)
    - `Resume_navigation_or_cancel_during_the_ack_wait_discards_the_question`
    - `Answer_audio_enters_answering_despite_skewed_timestamps`
    - `Burst_transcript_next_slide_is_not_a_command`
    - `Second_ask_done_sends_nothing`
    - `Append_refused_at_chunk_n_reports_send_failed_not_sent_and_resets_the_upstream` (the old session's
      `SessionClosed` is ignored; state paused and suspended; the next Ask reconnects)
  - **AC3:**
    - `Answer_then_check_in_then_resume_instruction`
    - `Quiet_90_s_after_speech_sends_with_quiet_sent`
    - `Quiet_90_s_without_speech_cancels_unmutes_and_stays_paused_with_grace_rearmed`
    - `Extend_at_80_s_keeps_listening_until_170_s`
    - `Ticks_emit_elapsed_and_quiet_remaining_every_second`
    - `Speech_cap_of_25_s_sends_with_limit_sent_and_silence_does_not_count` (and `speechRemainingMs` in the ticks)
    - `Ask_done_without_speech_is_empty_and_stays_paused`
  - **AC4, atomic start:**
    - `Ask_just_before_grace_expiry_with_the_guard_event_already_queued_does_not_suspend`
    - `Ask_after_the_grace_fired_reconnects_rechecks_and_listens`
    - `Reconnect_failure_at_ask_start_ends_the_talk_and_leaves_no_exchange`
    - `Mute_refused_at_ask_start_rolls_back_grace_and_emits_unavailable`
    - `Transcriber_begin_throwing_rolls_back_unmutes_and_rearms_grace`
    - `Unmute_failing_during_rollback_resets_the_upstream`
  - **AC4, other:**
    - `Ask_from_paused_answers_and_checks_in`
    - `Pause_grace_does_not_suspend_during_a_long_ask`
    - `Idle_guard_does_not_end_a_talk_during_a_five_minute_ask`
    - `Max_length_during_an_ask_ends_the_talk_without_a_burst`
    - `End_during_an_ask_emits_off_ended_before_closed_and_sends_no_unmute_or_audio`
    - `Upstream_loss_during_the_answer_ends_the_exchange`
    - `User_resume_navigation_or_mute_end_the_exchange_first` (theory)
    - `Refused_while_muted_or_not_live`
    - `Second_ask_start_while_listening_is_idempotent`
    - `Ask_during_check_in_ends_the_old_exchange_and_starts_a_new_one`
    - `Next_start_after_an_ended_ask_is_clean`
  - **AC5:**
    - `Disabled_transcriber_reports_not_transcribing_and_never_finishes_an_ask`
    - `Test_transcriber_phrase_finishes_after_700_ms_without_a_newer_update` (en and vi; the log shows the stripped
      length)
    - `Phrase_followed_by_trailing_words_within_700_ms_does_not_finish`
    - `Final_update_with_the_phrase_finishes_at_once_even_after_700_ms_of_audio_quiet`
    - `Stale_duplicate_or_reversed_revisions_are_ignored`
    - `Update_of_a_previous_ask_is_ignored`
  - **Existing:** `PresenterTalkGuardTests` (all, incl. `Mic_frames_are_not_activity`), `PresenterTests`,
    `PresenterPauseCloseTests`, `PresenterTrainingTests`, `PresenterExternalToolTests`, `PresenterVoiceCommandTests`,
    `PresenterToolTests`.

### T5 — Bridge admission, frames, protocol docs, end-to-end `/ws`  (AC1–AC4, AC6)
- **Files:** `Api/Realtime/PresenterBridge.cs` (per-connection admission, ask cases, `ask_state` frame, cleanup
  awaits the pump), `docs/reference/001-api-and-code-conventions.md` §8 (frames; `sent` = queued; admission order
  guarantee), `AGENTS.md` (frame line: "… per plan 010 and 011 (`ask_start`, `ask_done`, `ask_extend`, `ask_cancel`,
  `ask_state`)"), new `tests/PresenterAi.Api.Tests/BridgeAskTests.cs` and `BridgeAdmissionTests.cs` (real DI through
  `ApiFactory`, `FakeLiveServer`, and the factory's `TimeProvider` override, added if absent).
- **Change:** §4.1 Bridge and §4.3.
- **Verify:** `dotnet test tests/PresenterAi.Api.Tests`.
- **Test that dies if this breaks:**
  - `BridgeAdmissionTests`:
    - `Admission_queue_filled_to_capacity_records_exactly_the_pcm_before_ask_done_in_order`. The real presenter's
      loop is held in an Ask-from-suspended reconnect (FakeLiveServer delays `session.started`). The client sends 200
      distinct PCM frames, then `ask_done`, then 55 more: 256 items, which fills the admission queue exactly. After
      release, the recorder input equals exactly the 200 pre-done frames in order; the 55 later frames are not
      recorded.
    - `Admission_calls_the_presenter_one_at_a_time_in_exact_socket_order` (fake `IPresenter`). Every audio, ask and
      control call records start and end; at most one call is in flight at any time, including across connection
      disposal (no call starts after disposal begins). The sequence of distinct payloads equals the socket-frame
      order.
    - `Full_admission_queue_closes_1011_and_ends_the_talk_promptly_without_a_burst`. The pump is blocked, the queue
      is full, and one more frame arrives: the socket closes 1011 `backpressure` and the old talk ends within the
      drain bound, with no burst appended. The heartbeat timeout is set far longer, so the heartbeat cannot be what
      ends it.
    - `End_frame_with_the_pump_blocked_and_the_queue_nearly_full_is_read_and_ends_the_talk_promptly` (same socket,
      no burst, heartbeat set far longer).
    - `Disconnect_while_items_wait_for_admission_drops_them_and_ends_the_talk_before_release`.
    - `End_bypasses_admission_and_a_queued_ask_done_is_ignored_afterwards`.
    - `Max_length_while_ask_done_waits_for_admission_ends_the_talk_without_a_burst`.
  - `BridgeAskTests`:
    - `Ask_over_ws_mutes_records_and_bursts_in_order`. `FakeLiveServer.Received` has `session.input_audio.mute`
      before the pause instruction, and no append of the mic frames sent while listening. Then `unmute`, the
      server's `unmuted` ack, and only after it the 9,600-byte zero lead-in and the burst appends. Filtering out 960-byte all-zero pump frames, the 9,600-byte (or final shorter) appends decode to
      exactly the compressed recording, in order.
    - `End_while_the_burst_is_partly_on_the_wire_forwards_no_answer_after_closed` (FakeLiveServer reads gated after
      N appends).
    - `Upstream_socket_failure_after_the_burst_was_queued_ends_the_talk_upstream_lost`.
    - `Unmute_frame_while_listening_keeps_the_upstream_muted`.
    - `Answer_audio_keeps_flowing_while_the_browser_sends_no_mic_frames` (P-16). FakeLiveServer paces its answer
      audio by the input ms it has received, emulating the upstream. After `ask_done` the client sends no mic frames;
      the server keeps receiving 960-byte pump frames, and every answer frame reaches the client.
    - `Ask_commands_ignore_extra_fields_and_unknown_ask_type_is_protocol_error`.
    - `Refused_ask_start_while_muted_reports_refused_muted` (raw `/ws`).
    - `Ask_done_when_not_asking_sends_no_frame`.
  - Existing: `BridgeContractTests` (all, incl. `Oversized_fragmented_text_command_closes_1009`),
    `BridgeTrainingTests`, `BridgeBillingGuardTests`, `BridgeSessionRecorderTests`.

### T6 — Web: Ask control, keys, toasts  (AC1–AC3)
- **Files:** `web/app/src/ws/bridgeClient.ts`, `store/presenterStore.ts`, new `components/AskControls.tsx` (+ spec),
  `routes/Present.tsx`.
- **Change:** §4.1 Web and §4.3 toasts. Selectors return stored references; every `dark:bg-*` sets `dark:text-*`.
- **Verify:** `cd web && bun run lint && bun run test && bun run build`.
- **Test that dies if this breaks:**
  - `bridgeClient.spec.ts`: `sends the four ask commands`, `emits ask_state`.
  - `presenterStore.spec.ts`: `keeps the last ask_state and clears it on closed and idle`.
  - `AskControls.spec.tsx`:
    - `shows Listening with elapsed time, Ask done, Extend and Cancel`;
    - `shows the quiet countdown under 20 s with sending or closing wording`;
    - `shows the remaining speech time while listening`;
    - `shows Continue next to Pause only while ask_state is answering, and it sends resume`;
    - `frame sequence listening → answering → off drives the control; listening → off (cancel) hides it`;
    - `Ask is disabled with the unmute hint when muted`.
  - `routes/Present.spec.tsx`. These are action-level tests: a mocked WebSocket answers the real key or button action.
    - `A starts an ask and A or Enter finishes it`;
    - `holding A does not finish the ask it started`;
    - `Ctrl+A does not ask`;
    - `A in the length select does not ask`;
    - `A while muted shows the unmute warning and sends no frame`;
    - `clicking the disabled Ask button while muted sends nothing`;
    - `Space is ignored while listening and Escape still ends`;
    - `Enter on a focused End button while listening sends ask_done and not end`;
    - `pressing A answered by refused_not_live shows its toast`;
    - `Ask done answered by send_failed shows the reset toast`;
    - `Resume while listening answered by off resumed shows Ask cancelled`;
    - `quiet_cancelled and limit_sent frames show their toasts` (limit text "25-second speech limit…");
    - `Continue during check-in sends resume and the off continued frame clears the control`.
  - Existing: all of `Present.spec.tsx` (incl. `Space on the trainer switch does not pause, and Escape closes its
    tooltip without ending the talk`) and `Present.layout.spec.tsx`.

### T7 — Wiring audit and full verification  (AC6)
- Trace every new entity:
  - key/button → `bridgeClient` → admission → `IPresenter.Ask*Async` → loop;
  - `SendAudioCore` → `AskRecorder` / `IAskTranscription.Append`;
  - DI: `DisabledAskTranscriber` in the API and both CLI modes;
  - tick, budget, debounce and `AskTranscriptChanged` → handlers → `AskDoneLexicon`;
  - `EndExchange` → deferred notices, replay-due → `ask_state` → store → control and toasts;
  - `StopPauseGrace` now has callers.

  Grep `AppendInstructions(`, `AppendThinking(`, `AppendCommentary(`, `ContinueResponses(`, `SubmitToolOutput(`,
  `Flush?.Invoke(`, `Unmute(` and `PresentSlide(` in `Presenter*.cs`. Each hit must be a §4.1 site row, the ask's own
  `PauseInstruction`, the exchange-end actions, or code unreachable while an exchange runs, with the reason written
  down. The result must match the §4.4 audit table. Grep `ArmQuestionHold(` to confirm its only caller is
  `ArmAnswerWait()`. Grep writes to `_exchange` and `_pendingReset` to confirm they occur only in
  `Presenter.Asking.cs` and the reset handlers. Re-check the §4.4 handoff table against the code.
- **Verify:** `dotnet build PresenterAi.slnx -warnaserror`;
  `DOCKER_HOST=tcp://localhost:2375 dotnet test PresenterAi.slnx`; `cd web && bun run lint && bun run test && bun run
  build`; `bash scripts/secrets-guard.sh`. Each §4.3 log line appears in a test or in the T8 log.

### T8 — Live run  (AC1–AC4, AC6)
- Run the §7 runbook in Chrome on the React app against the real upstream. Record each step's result, the flush
  latency, the Ask-done→answer latencies and `usage` seconds in `docs/progress/002-work-log-phase0.md`. Unchecked
  rows block merge.

### Chain map (08) — press-to-ask hop by hop

| Hop | Test that dies |
|---|---|
| key/button → client frame (incl. muted local warning) | `Present.spec A starts an ask…`, `A while muted shows the unmute warning…`, `bridgeClient.spec sends the four ask commands` |
| socket → ordered admission → loop | `BridgeAdmissionTests.Admission_queue_filled_to_capacity_records_exactly_the_pcm_before_ask_done_in_order`, `Admission_calls_the_presenter_one_at_a_time_in_exact_socket_order` |
| disconnect / End / max / overflow vs admission | `BridgeAdmissionTests.Full_admission_queue_closes_1011_…`, `End_frame_with_the_pump_blocked_…`, `Disconnect_while_items_wait_…`, `End_bypasses_admission_…`, `Max_length_while_ask_done_waits_…` |
| check-in turn-taking | `PresenterAskTests.Delayed_question_delta_after_check_in_begins_is_ignored`, `Real_yes_after_one_and_a_half_seconds_…`, `Continue_ends_the_exchange_in_every_answer_phase` |
| input keeps flowing through the answer | `PresenterAskTests.No_upstream_mute_is_sent_between_ask_done_and_the_exchange_end`, `BridgeAskTests.Answer_audio_keeps_flowing_while_the_browser_sends_no_mic_frames` |
| answer wait (all hold re-arms, ceiling) | `PresenterAskTests.Tool_call_outlasting_the_budget_…`, `Every_question_hold_re_arm_…`, site test 19 |
| reset transition (off-loop close, generation) | `PresenterAskTests.Reset_close_finishing_after_end_…`, `Late_old_session_closed_after_reset_…`, `No_paused_or_suspended_snapshot_after_closed` |
| atomic start (grace, reconnect, rollback) | `PresenterAskTests.Ask_just_before_grace_expiry_…`, `Mute_refused_at_ask_start_…`, `Transcriber_begin_throwing_…` |
| mic frame → recorder, not upstream | `PresenterAskTests.Ask_during_narration_…`, `BridgeAskTests.Ask_over_ws_mutes_records_and_bursts_in_order` |
| recorder → compressed stream | `AskRecorderTests.Silence_run_over_500_ms_becomes_320_ms_…` |
| every emitter → exchange gate | `PresenterAskTests` site tests 1–19 |
| Ask done → unmute → ack → lead-in → burst (loop and wire) | `PresenterAskTests.Ask_done_unmutes_then_queues_the_compressed_burst_in_order`, `Burst_is_not_queued_before_the_unmuted_ack`, `Ack_timeout_after_2_s_…`, `End_max_length_or_disconnect_during_the_ack_wait_sends_nothing`, `LiveSessionTests.Unmuted_server_event_raises_InputAudioUnmuted`, `BridgeAskTests.Ask_over_ws_…` |
| send failure boundary | `PresenterAskTests.Append_refused_at_chunk_n_…`, `BridgeAskTests.Upstream_socket_failure_after_the_burst_was_queued_…`, `End_while_the_burst_is_partly_on_the_wire_…` |
| burst → answer → check-in → exchange end (deferred once, replay once) | `PresenterAskTests.Answer_then_check_in_then_resume_instruction`, site tests 10 and 15, `Check_in_no_keeps_the_replay_…` |
| quiet / extend / cap / budget | `PresenterAskTests.Quiet_90_s_*`, `Extend_at_80_s_…`, `Retained_cap_…`, `Answer_budget_expiry_…` |
| guards and End | `PresenterAskTests.Pause_grace_…`, `Idle_guard_…`, `Max_length_…`, `End_during_an_ask_…` |
| event → frame → store → control/toast | `BridgeAskTests.Ask_over_ws_…`, `presenterStore.spec keeps the last ask_state…`, `AskControls.spec shows Listening…`, `Present.spec … toasts` |
| transcriber port (revisions, debounce) → phrase → done | `PresenterAskTests.Stale_duplicate_or_reversed_revisions_are_ignored`, `Final_update_with_the_phrase_…`, `AskDoneLexiconTests.*` |
| DI default is disabled (API, both CLI modes) | `StartupTests.Production_container_uses_the_disabled_ask_transcriber`, `CliTests.Owner_and_file_mode_build_…` |
| real upstream (both halves answered, 24 s cap latency, over-cap truncation, interrupt observation) | T1 suite, T8 live run |

### AC → task matrix

| AC | Tasks |
|---|---|
| 1 stop within ~1 s; nothing spoken or delegated until done, across 10 s silence | T1 (mute control, no early answer, interrupt observation), T4 (site tests 1–7, 14, 16), T5 (admission, wire order), T6, T8 (flush latency, no playback) |
| 2 one answer to the whole question | T1 (both-halves oracle, (vii), 24 s cap, no truncation), T4 (burst order, 25 s cap, send failure and reset, answer wait, input flow), T5 (answer flows without mic frames), T8 |
| 3 check-in then resume; quiet 90 s; Extend | T4 (outcomes, budget, quiet/extend), T6, T8 |
| 4 from Paused, Trainer; End/disconnect/reconnect clean; idle | T4 (atomic start, sites 8–10 and 18, outcomes, guards, reset serialization), T5 (disconnect/End/max/overflow vs admission and wire), T8 |
| 5 transcriber port disabled; phrases with a test transcriber | T2 (API + CLI DI), T3, T4 (revisions, debounce) |
| 6 build, suites, probe + live run with usage | T1, T7, T8 |

## 7. Test strategy

- **Unit:**
  - Recorder: compression, bounds, carry-over, stats.
  - Lexicon: suffix, longest match, NFD, fillers.
  - Presenter exchange over `FakeSession` with a fake clock and the real revision service: one test per emitter site,
    the outcomes, the budget, atomic start and rollback, the send-failure boundary, transcriber revisions and
    debounce.
  - Web client, store, control and action-level keys and toasts.

  Not automated: real VAD behaviour on a burst, answer quality, upstream output after mute (T1, T8).
- **Integration (real DI):** `BridgeAskTests` and `BridgeAdmissionTests` through `ApiFactory` with `FakeLiveServer`
  (only the upstream is fake), including queue saturation, disconnect, End and max length around admission, and
  wire-level burst and failure. `StartupTests` and `CliTests` cover the DI default. No persistence change, so no
  Testcontainers test.
- **Mutation evidence** (each PR note names the wrong implementation):
  - Issuing admission items without awaiting each completion → `Admission_calls_the_presenter_one_at_a_time_in_exact_socket_order`.
  - Awaiting a full admission write in the receive loop → `End_frame_with_the_pump_blocked_…`,
    `Full_admission_queue_closes_1011_…`.
  - Reading any check-in delta as yes/no → `Delayed_question_delta_after_check_in_begins_is_ignored`.
  - Muting upstream during the answer → `No_upstream_mute_is_sent_between_ask_done_and_the_exchange_end`,
    `Answer_audio_keeps_flowing_while_the_browser_sends_no_mic_frames`.
  - Leaving one `ArmQuestionHold` caller un-routed → `Every_question_hold_re_arm_during_an_exchange_goes_through_the_answer_wait`.
  - Applying a reset completion without a generation check → `Reset_close_finishing_after_end_…`.
  - Gating only new tool calls → site tests 3–6.
  - Speaking trainer notices during the answer → site tests 8–9.
  - Replaying on reconcile while Presenting in the answer, or consuming the replay twice → site test 10,
    `Check_in_no_keeps_the_replay_for_the_next_resume_once`.
  - Keeping the generic 15 s release → site test 11.
  - Resuming the assembler at the first answer audio → site test 15 `Late_burst_transcript_…`.
  - Allowing `unmute` → site test 14.
  - Reporting `sent` after a refused append, or reusing the session → `Append_refused_at_chunk_n_…`.
  - Stopping the grace after the fallible steps → `Ask_just_before_grace_expiry_…`.
  - Accepting the newest-arrived rather than the highest revision → `Stale_duplicate_or_reversed_revisions_are_ignored`.
  - Deciding the utterance end from mic energy → `Phrase_followed_by_trailing_words_within_700_ms_does_not_finish`.
  - Compressing gaps to 0 ms or keeping 10 s gaps → `Silence_run_over_500_ms_becomes_320_ms…`.
  - `>` vs `≥` on the threshold → `Window_at_rms_120_is_voiced_and_119_is_quiet`.
  - Counting silence toward the cap → `Reports_full_at_25_seconds_of_kept_speech_…`.
  - Disabling the Ask button without the local warning path → `A while muted shows the unmute warning…`.
  - Enter clicking a focused button → `Enter on a focused End button…`.
  - Key repeat finishing the ask → `holding A does not finish the ask it started`.
  - Answering only part 2 → T1 oracle (iv)/(v).
- **Manual runbook** (T8; unchecked rows block merge):

| # | Step | Expected |
|---|---|---|
| 1 | Start a talk; during slide 2 narration press **A** | Speech stops within about 1 s (record flush latency); "Listening… 0:01"; no playback while listening |
| 2 | Ask a lookup question and press **A** while "One moment" plays (tool round active), then ask a new question and **Enter** | Nothing plays while listening; one answer to the new question; note any leftover from the first round |
| 3 | Say half a question, stay silent 10 s, say the rest, press **Enter** | Nothing spoken during the silence; one answer covering both halves; log `ask: sent (sent) — …` |
| 4 | Wait | Check-in; "yes" → resumes the interrupted sentence |
| 5 | **Pause**, then **Ask**, ask, **Ask done**; answer "no" at check-in | Answer, check-in, stays on the slide silent |
| 6 | Ask, say nothing for 90 s | Countdown from 20 s; toast "Nothing was heard…"; still paused |
| 7 | Ask, speak, stay silent; **Extend** at about 70 s of quiet | Countdown resets; after 90 s more of quiet the question is sent (toast) |
| 8 | Trainer on: give a script edit for the current slide, press Ask while it is pending | Ask works; no "updating" or replay during the answer; after the check-in, one replay with the new text |
| 9 | Ask, then **End** (or close the tab) mid-question | Talk ends; no answer afterwards; reopen → idle |
| 10 | Mute, press **A**; hover the Ask button | Toast "Unmute the microphone to ask a question."; button disabled with the hint |
| 11 | Vietnamese deck (Khóa 2 Bài 1): ask with a pause | One Vietnamese answer covering the whole question |
| 12 | Ask and keep talking past 25 s of speech | "0:0x of speech left" counts down; at the cap the question is sent (toast "25-second speech limit…"); one answer |
| 13 | During the answer, press **Mute**; then at the check-in press **Continue** | The answer keeps playing to the end; Continue resumes at once |
| 14 | End; record `usage` seconds and latencies | Work log entry |

All 14 rows passed live on 2026-09-24 (row 4 after fixes); see the work log `docs/progress/002-work-log-phase0.md`.

## 8. Rollout / phasing

One branch, stacked on `feature/010-live-presenter-training`; its PR into `develop` follows plan 010's, after the
independent review. T1 must pass before any other task is merged into the branch. There is no flag: Ask is available
in every talk once deployed. Admission changes the bridge's internal hand-off for every talk and is covered by the
existing bridge suites. There is no migration and no config change.

## 9. Open questions

None. These decisions were forced by code facts and need confirmation at G2:
1. The exchange is a separate loop-only `_exchange`, not an `Interaction` value (15 reset sites, §3).
2. The ask is internally a pause with the grace stopped (first callers of `TalkGuard.StopPauseGrace`); the default
   120 s grace would otherwise close the upstream mid-ask.
3. From Paused, the answer uses the Presenting question path, because the 1.5 s paused speech permit
   (`Presenter.cs:1005-1010`, `:1300-1306`) would cut a delegated answer; the check-in decides (P-3).
4. A 1 s zero tail is appended because the pump fill stalls after audio ahead of real time (`LiveSession.cs:428-442`).
5. Answer detection ignores output timestamps. Check-in uses the turn-taking rule on loop arrival times (owner P-13),
   because T1 showed upstream `start_ms` drifting up to 4.3 s past the input clock under upload lag.
6. The P-1 cap is 25 s of kept speech (owner), because an unpaced burst is ingested only up to about 30 s.
7. Work started before Ask can only be abandoned, not cancelled (no upstream cancel, research 005:241-244). The
   invariant is "nothing audible or newly acted on", not "the upstream produces nothing".
8. `SendAudio` success means queued (`LiveSession.cs:729-731`); `sent` is defined as fully queued, and a refused append
   resets the upstream.
9. The bridge needs ordered admission, because the receive loop does not await presenter writes
   (`PresenterBridge.cs:383-388`, `:518-538`).
10. The upstream reset closes off-loop and re-enters with a generation-guarded completion, because
    `SuspendUpstreamAsync` awaits `CloseAsync` (`Presenter.cs:2035-2052`), which would block the loop and End.
11. A full admission queue closes the socket with 1011 instead of blocking the receive loop (P-14). This ends the talk
    when the loop has been wedged for about 5 s.
12. Upstream output is paced by input audio. The pump keeps input flowing regardless of mute (`LiveSession.cs:405-426`),
    and the upstream `Mute()` is deferred through the answer phases (P-16).

## 10. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-24 | Requirement brief confirmed (G1) | Owner edits: **Extend** button (restarts the quiet timer); **transcription is a disabled plug-in** (`IAskTranscriber`), because the available model is not real-time; lexicon + suffix matching built behind it |
| 2026-09-24 | External plan review round 1 (pi gpt-6-sol:high) | 5 blockers + 5 improvements folded in; class fix: single ask-exchange phase consulted by every model-facing emitter, ordered bridge admission — see docs/review/025 |
| 2026-09-24 | Owner decision C2 | "Record like any question": the ask question's upstream transcript is saved through `SessionRecorder` like any spoken question; no ask audio and no ask-specific transcript store (§2 constraint, §5) |
| 2026-09-24 | External plan review round 2 (confirmation) | 2 blockers + 3 improvements folded in: input-clock burst provenance for check-in (D6, T1-confirmed, with a stated owner fallback), nonblocking admission with a 1011 overflow close (D7), decline notice + one answer-wait helper with a ceiling (A4), serialized off-loop reset (D8), a one-in-flight admission oracle (B2) — see docs/review/026 |
| 2026-09-24 | Owner decision P-14 + plan approved (G2) | Owner accepted the 1011 `backpressure` overflow close (talk ends rather than stalling with End unread); "External review, then approve" — approved after round 2 |
| 2026-09-24 | T1 live probe (first matrix) | FAIL: a 107 s near-cap burst was truncated upstream (about 30 s ingested); (iv) arrival-order oracle wrong. Plan blocked at T1 (work log) |
| 2026-09-24 | T1 cap sweep | An unpaced burst is ingested only up to about 30 s; pacing delays the answer and let the model answer during the send (work log) |
| 2026-09-24 | Owner decision P-1 | Cap at **25 s of kept speech**, sent unpaced; auto-finish at the cap with a toast; UI shows the remaining speech time |
| 2026-09-24 | Owner decision P-13 | Check-in turn-taking rule (a new utterance after check-in start and 1.5 s of transcript quiet; barge-in allowed; one more follow-up when unclear; Continue always works) replaces `start_ms` provenance. Evidence: `start_ms` drift up to 4.3 s under upload lag; the last question delta arrived around answer start in every run |
| 2026-09-24 | T1 finding P-16 | Upstream output is paced by input audio; input keeps flowing (mic or pump) through the answer and check-in, and the upstream is never muted then |
| 2026-09-24 | Owner decision P-17 | Continue button confirmed: shown next to Pause during the answer and check-in; skips the check-in and resumes from the interrupted sentence; additive `answering` state; frame sequence `listening → answering → off` |
| 2026-09-24 | T1 finding P-18 | The upstream clipped the burst start (burst on the wire at +29 ms, before `unmuted` at +56 ms; vi and en question starts lost). Fix: wait for the `unmuted` ack (≤ 2 s, event-driven), a 200 ms zero lead-in, then the burst; T1 check (viii) "question start present" |
| 2026-09-24 | T1 suite facts | `AnswerStartBudgetMs` 15,000 (max first answer about 9.7 s at 24 s); send ≤ 100 ms on the wire locally; ingest lag up to 7.7 s; harness = `gpt-audio-1.5` TTS + `scripts/run-ask-probe-suite.ps1`; T1 oracle updated ((iv) by arrival within the answer window, (vii) question finished before the answer ended). Status set after the rerun |
| 2026-09-24 | T1 PASS — plan unblocked | Suite run 3 (`f2b42d8`): en ×3, vi ×3, cap-24 ×3, cap-28, raw pass (i)–(viii) + reply check; cap-40 fails as expected; unmute ack 38–45 ms; answer start ≤ 11.0 s. Status back to Approved; T2 next |
| 2026-09-24 | Owner decision (review 027): follow-up Ask | Ask during the answer or check-in is a follow-up question in the same exchange: the answer is stopped (flush), listening starts again with no `off{paused}`, and after the follow-up's answer and check-in narration resumes from the sentence the **first** Ask interrupted. Web: Ask button and A key during `answering` |
| 2026-09-24 | Owner decision (review 027): voice confirmation during the answer | A pending tool confirmation in the answer phases takes a spoken yes/no by the P-13 turn-taking rule (new utterance after 1.5 s of transcript quiet); late question fragments still ignored; navigation stays closed during the exchange |
| 2026-09-24 | Implementation review round 1 fixes (review 027) | Unmute acks attributed by echoed `client_event_id`, FIFO ack debt when none; deferred notices kept as content over the send-failure reset and appended once after reconnect; transcription disposed once; emitter-gate claim qualified as layered defences; §4.4(c) frame sequence corrected |
| 2026-09-24 | Implementation review round 2 fixes (review 028) | Confirmation replies attributed at their first delta (a reply begun before the confirmation is discarded); one refusal path for follow-up Asks (live answer re-announced, a reset upstream ends the exchange once with notices and replay kept); nested follow-ups pinned with notices, replay and End/max-length oracles |
| 2026-09-24 | T8 live-run fix: resume wording after an Ask | The model stayed silent after `ResumeAfterQuestionInstruction` following the Ask's `PauseInstruction`, while `ResumeInstruction` after Pause worked at once. Every resume after an Ask exchange (yes, timeout, Continue, budget; follow-up) now uses `ResumeAfterAskInstruction`: names the slide, "The pause is over. Resume … now", transition and restart-the-sentence semantics, "Then stop and wait." The barge-in resume (no pause before it) keeps its wording |
| 2026-09-24 | Owner decision: hold for speech (T8 live run) | Live input transcription lags speech by about 2.5 s (a spoken "No" at 869.3 s arrived at 872.03 s, after the 5 s window closed at 871.76 s and opened a question hold). The check-in's 5 s reply window starts when the model's check-in audio has finished playing (estimated from the forwarded audio, which arrives faster than real time), not at the 700 ms receive quiet. While the window is open, voiced mic audio (`AudioLevel.IsVoiced`, the recorder's RMS threshold) holds it until 3 s (`CheckInTranscriptGraceMs`) after the speech ends, and a reply still being assembled completes; the hold is bounded at 10 s (`CheckInMaxHoldMs`) past the normal window. A silent listener still resumes about 5 s after the check-in audio. P-13 is unchanged: the hold only extends the window, never makes a stale delta count. A user delta within 3 s after an exchange that reached its check-in ends (resume or stay) is a late reply and stays UI-only: no question hold. Voice tool confirmations do not share this window (their own 8 s + 10 s interaction timers) and are unchanged |
| 2026-09-24 | Owner decision: allow polite words (T8 live run) | The yes/no matcher (`ConfirmationLexicon` + `VoiceCommandMatcher`, used by the check-in and by tool confirmations) accepts a Yes/No phrase followed only by polite words: en "thank you", "thanks", "please" (and "go ahead" after Yes only); vi "cảm ơn", "cám ơn" and the particle "ạ" ("có ạ", "vâng ạ", "không cảm ơn", "không ạ"). Anything else after the answer stays not-a-reply ("no but what about the budget", "yes and also", "no go ahead"). Exact phrases match first, so "go ahead" alone is still Resume and navigation phrases are unaffected |
| 2026-09-24 | T8 regression fix: residual narration after Ask done | The narration response running at Ask start kept generating while muted (transcript deltas through a 23 s listen); after Ask done the rest of it was forwarded, audible, and taken as the answer (`question: answered after` 64, 195, 201 ms; genuine answers 1,273–2,965 ms, 7,974 ms for a 19.4 s question); the check-in ran on it and the talk resumed before the real answer to a 21 s question played. Row 4 (refinement): the upstream may also hold the rest while muted and release it after the unmute: narration cut at 112.55 s, nothing voiced while listening, Ask done 116.95 s, sent 117.00 s, the interrupted word "engineer." at +205 ms taken as the answer, the real answer at +1.35 s after the check-in had begun at +1.0 s. Residual first frames after the send: 64, 195, 196, 201, 205 ms; genuine answers never before 1,273 ms (the burst is ingested first). Rule (loop arrival times only, §9.5): an ask is a residual candidate when the model was voiced within 700 ms (`ResidualGapMs`) before Ask start or at any time while listening (incl. the ack wait). A candidate's first voiced frame after the send decides: earlier than 1,000 ms (`ResidualStartMs`) it opens the residual and is dropped; at or after it, or for a non-candidate, it is the answer as before. An open residual's audio (voiced and unvoiced; also a candidate's unvoiced frames before its first voiced one inside the 1,000 ms) is dropped and never marks the answer, until the first voiced frame at least 700 ms after the previous post-send voiced frame, which is the answer. Logged once when opened and once at close with the dropped ms. A budget expiry with residual voiced in the last 700 ms re-arms (at most 15 s, never past the ceiling). Residual state is per ask: reset by a follow-up Ask, gone with the exchange. Test fixtures send answer audio at least 1,000 ms after the burst |
| 2026-09-24 | T8 regression fix: cut-off nudge at the cap | At `limit_sent` the burst's transcript ends mid-sentence (e.g. "…What did the program") and no answer came in 15 s (3/3 runs, one with speech ending exactly at the cap); a complete 19.4 s question by Ask done was answered in 8.0 s. Rule: only for `limit_sent`, 3 s after the send (`CutOffNudgeMs`) with the exchange still AwaitingAnswer, no answer voiced and no tool or backend work pending, `AskCutOffInstruction` (answer what was heard in the talk's language in one to three sentences, or ask to repeat) is appended once; each user delta while AwaitingAnswer keeps it at least 2 s away (`CutOffQuietMs`); an answer, follow-up or exchange end cancels it. Re-run: the descriptive nudge at +10.3 s (the burst transcript streamed for about 8 s) left the model silent and the 15 s budget ended the exchange 4.7 s later. The nudge now says "Answer it now … Then stop and wait." (the `ResumeAfterAskInstruction` pattern) and re-arms a fresh answer budget from the nudge, never past the ceiling |
