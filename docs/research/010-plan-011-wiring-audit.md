# 010 — Plan 011 T7 wiring audit (press-to-ask, emission → consumption)

**Scope:** branch `feature/011-press-to-ask` at `e92ad88` (T1–T6 merged), 2026-09-24. Rule applied: plan 011 §6 T7 and
`~/.claude/docs/07-integration-boundary-audit.md` §5. Every new entity is traced from its writer to its reader, and
**NO CALLER** rows count as findings. Line numbers are at `e92ad88`. `.env` was not opened. No production code changed.
The gate mutation evidence below comes from temporary single-line edits. Each edit was reverted with
`git checkout -- <file>`, and the tree was clean before and after.

## Summary

- **Wiring:** complete. Every new command, event, frame, port and timer has a writer, a reader and a test. There is
  no NO CALLER row.
- **Grep rules:**
  - `ArmQuestionHold(` has one caller, `ArmAnswerWait()`.
  - `_exchange` and `_pendingReset` are written only in `Presenter.Asking.cs`, in the exchange handlers and the reset
    handlers.
  - Every hit of the eight greps maps to a §4.1 site, the ask's own actions, the exchange-end actions, or code that
    cannot run during an exchange.
- **Log lines:** every §4.3 page-log line, plus `ask: unmute ack timed out…`, is asserted in `PresenterAskTests`.
  T8 still has to observe the real-upstream values (listed at the end).
- **Decisions:** P-1, P-13, P-16, P-17 and P-18 are implemented where the plan puts them.
- **Checks:**
  - `dotnet build PresenterAi.slnx -warnaserror --no-incremental`: 0 warnings, 0 errors.
  - Application 668/668, Api 291/291, Infrastructure 205 passed with 4 skipped, Cli 85/85.
  - `web`: lint clean, shared 20/20, app 164/164, build OK.
  - The Integration tests were not run; the orchestrator runs them.
- **Known input confirmed and widened.** 11 of the 25 gate edits (13 of 19 `ExchangeAllows` or `EmitOrDefer` call
  sites, plus the tool-command check and the 4 `ArmAnswerWait` routes) leave every suite green. The survivors are
  sites 1, 3, 4, 5, 6, 7 and 17, both site-11 gates and both site-12 gates. These are layered gates: the Ask-start
  clearing already makes the behaviour correct without them.
- **Gaps:** 0 high, 1 medium, 4 low (see Gaps).

## Trace table

| Chain | Hops (`file:line`) | Test that dies |
|---|---|---|
| Ask key/button → client | `Present.tsx:373-400` (A, or A/Enter while listening; Space ignored; muted → local toast, no frame), `Present.tsx:596-601` → `AskControls.tsx` buttons → `bridgeClient.ts:198-208` (`ask_start`/`ask_done`/`ask_extend`/`ask_cancel`) | `Present.spec` "A starts an ask and A or Enter finishes it", "A while muted…", "Enter on a focused End button…", `bridgeClient.spec` "sends the four ask commands" |
| socket → admission → `IPresenter.Ask*Async` | `PresenterBridge.cs:505-508` `Admit` → `TryAdmit :838-841` (`TryWrite`, full → 1011 `:412-420`) → `PumpAdmissionAsync :844-876` (one call in flight, awaited) → `IPresenter.cs:67-76` (defaults `false`) → `Presenter.Asking.cs:52-62` `EnqueueCommandAsync` | `BridgeAdmissionTests.Admission_calls_the_presenter_one_at_a_time_in_exact_socket_order`, `Admission_queue_filled_to_capacity_…`, `Full_admission_queue_closes_1011_…` |
| command → loop | `Presenter.cs:648` `TryHandleCommandDuringExchange` (`Asking.cs:255-306`) → `Presenter.cs:668-671` → `AskStartCore :97`, `AskDoneCore :203`, `AskExtendCore :235`, `AskCancelCore :244` | `PresenterAskTests` AC1–AC4 families |
| `end` bypasses admission | `PresenterBridge.cs:511` → `EndAsyncCore` → `EndExchange(Ended)` `Presenter.cs:2276` | `BridgeAdmissionTests.End_bypasses_admission_…`, `End_frame_with_the_pump_blocked_…` |
| disconnect cleanup | `PresenterBridge.cs:229` `StopAdmissionAsync(AbortPendingStart, 5 s)` `:885-900` → `ObserveEndAsync` | `Disconnect_while_items_wait_for_admission_…`, `Max_length_while_ask_done_waits_…` |
| mic PCM → recorder/transcriber | `PresenterBridge.cs:410` → `Presenter.cs:252` `SendAudioAsync` → `SendAudioCore :2262-2266` → `RecordAskAudio` `Asking.cs:311-325` → `AskRecorder.Append :315` + `IAskTranscription.Append :316`; full → `AskDoneCore("limit_sent") :321`; `Sending` → dropped (`:313`) | `Ask_during_narration_mutes_pauses_and_flushes_and_forwards_no_mic_audio`, `BridgeAskTests.Ask_over_ws_mutes_records_and_bursts_in_order`, `Speech_cap_of_25_s_…` |
| DI of `DisabledAskTranscriber` | `Infrastructure/DependencyInjection.cs:181` `TryAddSingleton` → ctor arg `:269` → `Presenter.cs:159,170`; API `Api/Program.cs:61`; CLI owner `Cli/Program.cs:167`, file `:188` | `StartupTests.Production_container_uses_the_disabled_ask_transcriber`, `CliTests.Owner_and_file_mode_build_the_presenter_with_the_disabled_ask_transcriber` |
| 1 s tick | `ArmAskTick` `Asking.cs:815-821` → `AskTickElapsed` → `Presenter.cs:526` → `OnAskEventAsync :787` → `OnAskTick :766` (quiet ≥ 90 s → `quiet_sent`/`quiet_cancelled`, else `EmitListening`) | `Ticks_emit_elapsed_…`, `Quiet_90_s_*`, `Extend_at_80_s_…` |
| answer budget | `ArmAnswerWait :400` → `ArmAskBudget :430` → `OnAskBudgetElapsed :414` (extends to the ceiling only while a backend delegation is pending) → `EndExchange(Resume)` | `Tool_call_outlasting_the_budget_…`, `Answer_budget_expiry_…`, `Every_question_hold_re_arm_…` |
| transcriber update → debounce → lexicon | `Asking.cs:166` `Updated` → `QueueFromProducer(AskTranscriptChanged)` → `OnAskTranscriptChanged :558` (id + revision guard; `Final` now, else 700 ms) → `OnAskPhraseElapsed :576` → `EvaluateAskPhrase :584` → `AskDoneLexicon.Match` (`VoiceCommands/AskDoneLexicon.cs:39`) → `AskDoneCore("phrase_sent")` | `Test_transcriber_phrase_finishes_after_700_ms_…`, `Stale_duplicate_or_reversed_revisions_are_ignored`, `Update_of_a_previous_ask_is_ignored`, `AskDoneLexiconTests.*` |
| unmute ack → burst | `LiveSession.cs:567-568` → `ILiveSession.cs:35` → `Presenter.cs:940` `UnmuteAcked(session)` → `OnUnmuteAcked :327` (session + sub-phase), or `UnmuteAckTimedOut :333` (generation + sub-phase) → `SendBurst :341` (9,600-byte lead-in, chunks, `answering`) | `Burst_is_not_queued_before_the_unmuted_ack`, `Ack_timeout_after_2_s_…`, `LiveSessionTests.Unmuted_server_event_raises_InputAudioUnmuted` |
| `EndExchange` → notices / replay | `Asking.cs:605-655`: `_exchange = null` first; `Resume` → notices once + `ResumeAfterExchange :657` (hold, replay, `ResumeAfterQuestion`, or the `!_heardOutput` resume + nudge); `Stay` → notices + `_replayOnResume`; other outcomes drop them (`ask: dropped N…`) | `Check_in_no_keeps_the_replay_…`, `Navigation_during_the_answer_drops_…`, `End_during_the_answer_drops_…`, site tests 8–10 |
| `ask_state` → client | `EmitListening :852`, `SendBurst :382` (`answering`), `EmitAskOff :860` → `PresenterBridge.cs:94` → `AskStateFrame :98-107` → `bridgeClient.ts:297-298` → `presenterStore.ts:117-126` (cleared `:95` idle, `:181` closed) → `AskControls.tsx` (listening / answering + Continue / idle) and `Present.tsx:185-195` toasts (`ASK_TOASTS :29-44`, key `present:ask`) | `BridgeAskTests.*`, `presenterStore.spec` "keeps the last ask_state…", `AskControls.spec` (6), `Present.spec` toast tests |
| `StopPauseGrace` | `TalkGuard.cs:76` ← `Asking.cs:140` (before the fallible steps) and `:196` (after `PauseCore` re-arms it); grace re-armed by `_guard.Pause()` at `:143`, `:159`, `:646`, `:719` | `Ask_just_before_grace_expiry_…`, `Pause_grace_does_not_suspend_during_a_long_ask`, `Quiet_90_s_without_speech_…grace_rearmed` |
| reset transition | `ResetUpstream :706-724` ← `FailSend :387`, start rollback `:158`, `EndExchange :654`; off-loop `CloseResetSessionAsync :726` → `UpstreamResetClosed` → `OnUpstreamResetClosedAsync :742` (generation match → usage once; always dispose); `OnClosed` → `FoldPendingReset` `Presenter.cs:2349` | `Reset_close_finishing_after_end_…`, `Late_old_session_closed_after_reset_…`, `Newer_reset_makes_an_older_completion_stale`, `No_paused_or_suspended_snapshot_after_closed` |

## Grep classification (`Presenter*.cs`)

### `AppendInstructions(` (22 hits)

| Hit | Class | Reason |
|---|---|---|
| `Presenter.cs:840` client-mode instruction | unreachable | Start only; no exchange exists before Start |
| `:1004` narration (`SendNextPart`, from `PresentSlide`/part gap) | unreachable | Paused while listening; in the answer phases the part gap and silence timers pass `HoldBlocksProgress` (`:2429`, open hold) first; navigation ends the exchange first |
| `:1245` out-of-range GoTo reply | site 15 | Exchange utterances go to `CompleteExchangeUtterance` (`:1214-1217`) and never reach this branch |
| `:1447` end confirmation | sites 15, 16 | A voice End at check-in is unclear (not acted on). A tool ConfirmEnd is refused while listening (`Asking.cs:260-266`) and ends the exchange `Stay` first in later phases (`:272-274`) |
| `:1507` client-delegation answer-now | site 7 | `OnDelegation` returns while Paused (listening); in the answer phases it is part of the answer |
| `:1966`, `:1974` nudge | site 12 | `OnNudge` gate `:1955`, `ArmNudge` gate `:2480` |
| `:2015` wrap-up (`StartWrapUp`) | unreachable | Reached from `OnSilence` after `HoldBlocksProgress`, or from Next, which ends the exchange first |
| `:2094` pause instruction (`PauseCore`) | ask's own / Stay | The ask's own pause at Ask start, a user Pause → `Stay` first, a check-in "pause" (`Asking.cs:531-534`), or the reset (`:709`) |
| `:2218`, `:2222` `ResumeCore` | exchange end | Resume ends the exchange (`Stay` while listening, `Resume` later) before `ResumeCore` runs |
| `:2450` `ResumeAfterQuestion` | site 11 / exchange end | `HoldBlocksProgress`/`OnQuestionHoldElapsed` are gated; `ResumeAfterExchange` calls it after `_exchange = null`; the voice-yes path (`:1304-1306`) is not reached during an exchange (utterances go to the exchange) |
| `:2651` limit warning | site 13 | `EmitOrDefer` |
| `Asking.cs:688`, `:695` | exchange end | `ResumeAfterExchange`, after `_exchange = null` |
| `Presenter.Training.cs:365` edit declined | site 18 | `EmitOrDefer` |
| `Training.cs:382` edit pending | site 8 | `EmitOrDefer` |
| `Training.cs:398` `EnterHold` hold instruction | site 8 | `EnterHold` gated by `ExchangeAllows(EnterHold)` `:380`; the other entry is `ResumeAfterExchange :663` (exchange end) |
| `Training.cs:412` `PresentHeldSlide` | unreachable / exchange end | Only via `PresentSlide` (navigation, advance, replay), none of which runs during an exchange |
| `Training.cs:491` edit failed | site 9 | `EmitOrDefer` |
| `Training.cs:557` `ReplayCurrentSlide` | site 10 | During an exchange the reconcile sets `exchange.ReplayDue` (`:500-505`); the replay runs from `ResumeAfterExchange :672` |

### Other greps

| Grep | Hit | Class and reason |
|---|---|---|
| `AppendThinking(` | `Presenter.cs:971` slide notes in `PresentSlide` | Unreachable: no `PresentSlide` during an exchange (see above) |
| `AppendThinking(` / `AppendCommentary(` | `:1858-1859` `OnApprovedToolCompleted` | Site 5: run-generation check (bumped at Ask start) and `ExchangeAllows(AnnounceTool)` |
| `ContinueResponses(` | `:1798` `TryContinueResponses` | Site 6: `ExchangeAllows(ContinueResponses)`, with the tracker cleared at Ask start. No variant-B one-shot exists: T1 chose variant A (`vad`) |
| `SubmitToolOutput(` | `:1604` | Site 2: the ask's own busy refusal while listening |
| `SubmitToolOutput(` | `:1780` | Site 3: tracker cleared (`:1767`) and gate `:1774` |
| `Flush?.Invoke(` | `:2098` `PauseCore` | The ask's own pause, or a Stay pause |
| `Flush?.Invoke(` | `:2298` `EndAsyncCore` | End: `EndExchange(Ended)` runs first (`:2276`) |
| `Flush?.Invoke(` | `Training.cs:396` `EnterHold` | Site 8 (gated, or at exchange end) |
| `Flush?.Invoke(` | `Training.cs:410` `PresentHeldSlide` | Unreachable during an exchange |
| `Flush?.Invoke(` | `Training.cs:553` `ReplayCurrentSlide` | Site 10 (exchange end) |
| `Unmute(` | `Asking.cs:158` | Start rollback |
| `Unmute(` | `Asking.cs:220` | Ask done (P-18) |
| `Unmute(` | `Asking.cs:624` | Exchange end, only if the exchange muted it and the user did not |
| `Unmute(` | `Presenter.cs:2255` `UnmuteCore` | Site 14: refused while listening |
| `PresentSlide(` | `:851` | Start |
| `PresentSlide(` | `:1944` advance in `OnSilence` | Behind `HoldBlocksProgress` |
| `PresentSlide(` | `:2036`, `:2051`, `:2071` navigation | Navigation ends the exchange `Navigated` first |
| `PresentSlide(` | `:2173` `ResumeAfterReconnectAsync` | Resume ends the exchange first, and a reconnect cannot be pending during an exchange |
| `PresentSlide(` | `Training.cs:562` `ReplayCurrentSlide` | Site 10 |
| `ArmQuestionHold(` | `Asking.cs:404` only | Inside `ArmAnswerWait()`, outside an exchange. The former callers (`Presenter.cs:1533`, `:1566`, `:1620`, `:2564`) all call `ArmAnswerWait()`. Mutating each back to `ArmQuestionHold()` fails `Every_question_hold_re_arm_…` (all 4 killed) |
| writes to `_exchange` | `Asking.cs:177` (start), `:608` (`EndExchange`) | Only there |
| writes to `_pendingReset` | `Asking.cs:713` (`ResetUpstream`), `:747` (`OnUpstreamResetClosedAsync`), `:760` (`FoldPendingReset`, also called from `OnClosed` `Presenter.cs:2349`) | Only in the reset handlers |
| upstream `Mute()` | `Asking.cs:141` | Ask start |
| upstream `Mute()` | `Asking.cs:625` | The user mute deferred to the exchange end |
| upstream `Mute()` | `Presenter.cs:2158` `ReconnectAsync` | Cannot run during an exchange |
| upstream `Mute()` | `Presenter.cs:2236` `MuteCore` | During an exchange, `Mute` is handled by the command table (`Asking.cs:286-302`) and never reaches `MuteCore`. P-16 holds |

### Gate mutation evidence (the known input)

Each gate was mutated alone, then the Application and Api suites were run.

| Site | Gate | Result | Why it survives |
|---|---|---|---|
| 1 | `Presenter.cs:1025` `ForwardAudio` | **survives** | Listening is Paused with the permit closed (`PauseCore`→`ClosePermit`, or `Asking.cs:192`), and user deltas never reopen it during the exchange (`:1135-1139`) |
| 2 | `:1601` `AcceptToolCall` | killed (`New_tool_call_while_listening_is_refused_busy…`) | — |
| 3 | `:1774` `SubmitToolOutput` | **survives** | The tracker is cleared at Ask start, so `IsCallPending` fails first (`:1767`) |
| 4 | `:1522` `DelegatedResponse` | **survives** (confirms the orchestrator's finding) | The tracker is cleared, so the response is `Ignored`, and no delegation can open while Paused |
| 5 | `:1852` `AnnounceTool` | **survives** | `_runGeneration++` at Ask start fails the generation check first |
| 6 | `:1797` `ContinueResponses` | **survives** | The tracker is cleared, so `ShouldSendContinueResponses` is false |
| 7 | `:1495` `OpenDelegationHold` | **survives** (the first run was "killed" by a one-off `BridgeAskTests` failure under full-suite load; the rerun survived) | `_state != Presenting` returns while listening; the gate always allows in the answer phases |
| 8 | `Training.cs:380` `EnterHold`, `:381` defer | killed | — |
| 9 | `Training.cs:490` defer | killed | — |
| 11 | `:2416` `OnQuestionHoldElapsed` | **survives** | The generic hold timer is cleared at Ask start and never armed during an exchange (the only caller is `ArmAnswerWait` outside one) |
| 11 | `:2429` `HoldBlocksProgress` | **survives** | `_questionHoldOpen` with `_answerVoiced` false, or a non-`None` interaction, already blocks during the exchange |
| 12 | `:1955` `OnNudge`, `:2480` `ArmNudge` | **both survive (each alone)** | The nudge timer is cleared at Ask start (`ClearTimers`) and in `SendBurst :373`, and `OnNudge` also needs Presenting |
| 13 | `:2650` defer | killed | — |
| 14 | `:2246` `Unmute` | killed (Application + `BridgeAskTests.Unmute_frame_…`) | — |
| 16 | `Asking.cs:262` `ToolCommand` | killed | — |
| 17 | `:1989` `WrapUpEnd` | **survives** | `_questionHoldOpen` is set for the whole answer; Paused while listening |
| 18 | `Training.cs:364` defer | killed | — |

## §4.4 handoff table re-checked

| Handoff | Code | Verdict |
|---|---|---|
| PCM and ask/control frames → loop | `TryAdmit` non-blocking (`PresenterBridge.cs:838-841`), a single pump awaiting each call (`:844-876`), full → 1011 (`:412-420`), cleanup `StopAdmissionAsync` + `AbortPendingStart` + ≤ 5 s (`:885-900`, `:229`) | matches |
| `end` | direct `EndAsync` (`:511`), not admitted | matches |
| User transcript at check-in | loop-arrival times only, `OnExchangeUserDelta` `Asking.cs:472-490` (`LastUserDeltaAt`, `CheckInQuietMs`); no `start_ms` | matches |
| Input clock after the burst | `SendAudioCore` forwards outside `Listening`/`Sending`; the upstream `Mute()` is deferred (`Asking.cs:293-298`, `:625`) | matches |
| Upstream reset close | `ResetUpstream` → off-loop close → generation-matched `UpstreamResetClosed`; dispose always, once | matches |
| Tick, budget, phrase debounce | `QueueFromProducer(event(generation))`, generation checked in `OnAskTick :768`, `OnAskBudgetElapsed :416`, `OnAskPhraseElapsed :578`; the variant-B one-shot was not built (variant A) | matches |
| Transcriber updates | `AskTranscriptChanged(askId, update)`, id + `Revision > LastRevision` (`:560-561`) | matches |
| Unmute ack → burst | ack guarded by session + sub-phase (`:329`); timeout by generation + sub-phase (`:335`); `EndExchange` nulls `_exchange` and bumps the ack generation (`:610`) | matches |
| Mute → unmute → chunks | one loop handler (`SendBurst`) enqueues everything with no await | matches |
| Pre-Ask completions | `_runGeneration++`, tracker/approved/navigating cleared (`Asking.cs:181-184`) | matches; this clearing is why the layered gates survive mutation |
| Trainer `Changed` → reconcile | `EmitOrDefer` (sites 8, 9, 18) and `exchange.ReplayDue` (`Training.cs:500-505`) | matches |
| Grace vs Ask start | `StopPauseGrace` before any fallible step, no await after the reconnect re-check (`Asking.cs:121-140`) | matches |
| End, max length, disconnect | `EndExchange(Ended)` in `EndAsyncCore :2276` and `OnClosed :2346`; no lock or permit | matches |
| `ask_state` → client | raised on the loop, forwarded by `PresenterBridge.cs:94` | matches |

## Plan decisions checked in code

| Decision | Where | Verdict |
|---|---|---|
| P-1: 25 s cap of kept speech, unpaced, `limit_sent` | `AskRecorder.cs:23` `MaxRetainedMs = 25_000` (kept bytes after compression); `RecordAskAudio :318-322`; `SendBurst` loops without pacing (`:354-361`); `speechRemainingMs` in `EmitListening :856`; toast `Present.tsx:36` | implemented |
| P-13: turn-taking | `OnExchangeUserDelta :472-490`: pre-CheckIn deltas are UI-only; in CheckIn a delta opens an utterance only after 1,500 ms of transcript quiet. `CompleteExchangeUtterance :512-549` accepts only Yes/Resume/No/Pause and gives one follow-up. `IsTrailingAskDelta :493` covers deltas after the follow-up timeout | implemented |
| P-16: never an upstream mute during the answer | the upstream `Mute()` grep above; the user Mute is deferred (`:293-298`), `EndExchange :625`; `No_upstream_mute_is_sent_between_ask_done_and_the_exchange_end` | implemented |
| P-17: Continue | `ResumeCommand` in the answer phases → `EndExchange(Resume, "continued")` (`Asking.cs:278-282`); `AskControls.tsx` answering branch → `onContinue` → `resume` (`Present.tsx:601`) | implemented |
| P-18: unmute ack + 200 ms lead-in | `AskDoneCore :220-231` (unmute, 2,000 ms timer, loop free); `SendBurst :348` lead-in `AskLeadInMs * BytesPerMs` = 9,600 bytes of zeros, then the chunks | implemented |

## Gaps found

| # | Gap | Severity | Suggested fix |
|---|---|---|---|
| G1 | 13 gate call sites (sites 1, 3, 4, 5, 6, 7, both site-11 gates, both site-12 gates, 17) have no oracle that isolates them. Plan §4.1 says "one gated test per row" and AGENTS.md asks for mutation evidence per oracle. The behaviour is pinned, but the gate line is not: the Ask-start clearing (run generation, tracker, permit, timers) already makes each site inert. A regression in that clearing would be caught; a regression in a gate would not. | medium (test gap; no behaviour defect) | Record these as layered defence-in-depth gates in the plan's site table and the T4 PR note, with the mutation table above. Optionally add white-box tests that re-open the precondition (for example a delegation opened after Ask start through a test seam). Do not remove the gates: they keep the invariant local to each emitter. |
| G2 | Assistant transcript deltas from work started before Ask still reach the page (`Presenter.cs:1124` `Transcript?.Invoke`), `RecordRecentTurn` and `SessionRecorder` (`Sessions/SessionRecorder.cs:84`) while listening. The invariant covers audibility and actions only, so this is allowed, but the user sees text of an answer they never hear. | low | T8 step 2 should record whether leftover assistant text appears. If it does, suppress assistant deltas for the UI while listening, or accept it and document it. |
| G3 | `IAskTranscription.Dispose()` is called twice on one ask: `AskDoneCore :208` then `EndExchange :613`, and `OnAskTick :777` then `EndExchange`. The port does not state that `Dispose` must be idempotent, and a real transcriber may not tolerate a second call. | low | Dispose once, in `EndExchange` only, or document idempotent `Dispose` on `IAskTranscription`. |
| G4 | `BridgeAskTests.Ask_over_ws_mutes_records_and_bursts_in_order` failed once during a full Api-suite run (the site-7 mutant, which cannot change behaviour). It passed in 15 isolated reruns and in the second sweep. The failure text was not captured. | low (possible flake under load) | Watch CI. If it recurs, capture the failing assertion and make the wait event-driven. |
| G5 | Plan §4.4 (c) still shows `off{quiet_sent}`. The code, §4.3 and `docs/reference/001` send `answering{quiet_sent}`, then `off{continued}`. | low (doc) | Fix the diagram in the plan. |

No high-severity gap and no missing wiring.

## Log lines left for T8

Every §4.3 line is asserted in `PresenterAskTests`:
- `listening (from …)` `:1003`
- `extended` `:1316`
- `sent (…)` `:1055`
- `cancelled (…)` `:1296`, `:1646`
- `send failed at chunk` `:1233`
- `no answer within` `:479`, `:974`
- `exchange ended (…)` `:1264`, `:1605`
- `dropped N deferred notices` `:938`
- `done by phrase` `:1770`
- `tool command refused` `:414`
- `tool call refused (busy)` `:64`
- `unmute refused while listening` `:342`
- `unmute ack timed out` `:1104`

None is test-only-unverifiable. T8 must still record the real-upstream values:

- `ask: sent (<reason>) — recorded …, kept …, voiced …, rms bands …`: real RMS bands for the R4 tuning, and a `limit_sent` at the cap (runbook 12).
- `ask: listening (from presenting)` with the flush latency (runbook 1), and `(from paused)` (runbook 5).
- `ask: check-in` and the check-in resolution (`question: confirmed; resuming` / `ask: check-in answered no…`).
- `ask: mute deferred to the exchange end` (runbook 13).
- `ask: cancelled (quiet_cancelled)` (runbook 6) and `ask: extended` (runbook 7).
- Whether `ask: unmute ack timed out…` ever appears against the real upstream (expected: never; the T1 ack took 38–45 ms).
