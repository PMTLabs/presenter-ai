# 006 — Voice control and tools: code scout (research)

**Date:** 2026-09-23. **Source:** `pi` gpt-5.6-luna:high, read-only, request `p007-scout-01`, on `develop` @ `0a30aa3`.
**Why:** a live talk ignored "stop" and "end the meeting"; input to plan 007. Task 0 of plan 007 appends the live probe result here.


## Key facts for planning

- Spoken control words are not classified. Transcript deltas only open/extend the question hold; no code routes `stop`, `pause`, `resume`, or `end the meeting` to presenter commands (`src/PresenterAi.Application/Presenting/Presenter.cs:589-604`).
- `Back to the slide` is in a host instruction, not a hard-coded spoken response: `PromptBuilder.ResumeAfterQuestionInstruction()` asks GPT-Live to say a short bridge such as that phrase (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:124-125`), and the presenter sends it after the follow-up wait (`src/PresenterAi.Application/Presenting/Presenter.cs:1052-1079`).
- GPT-Live client delegation is metadata-only (`session.delegation.created` has delegation id/target/offset, not task text); managed Responses delegation can expose backend `tools`, but this repository defines no tools and does not process function-call items (`docs/research/005-gpt-live-delegation-audience-questions.md:351-393`, `src/PresenterAi.Infrastructure/Live/LiveSession.cs:552-565`).
- The existing presenter is a single-reader event loop with generation-counted timers. A future model-tool adapter must enqueue `IPresenter` commands, not mutate presenter state or call the bridge internals (`src/PresenterAi.Application/Presenting/Presenter.cs:32-37,243-307`).
- Current turn-taking is silence/timer driven: ordinary advance, part gaps, question hold/follow-up, nudges, and wrap-up fallback. The approved question-hold amendment prevents many races, but it does not create command intent detection (`src/PresenterAi.Application/Presenting/Presenter.cs:21-26,1044-1202`).

## Q1. End-to-end speech handling, question hold, resume, and missing control intents

### Input path

1. The bridge receives binary microphone frames and calls `IPresenter.SendAudioAsync` (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:306-318`). `Presenter` queues a `SendAudioCommand`; `SendAudioCore` only forwards while presenting and unmuted (`src/PresenterAi.Application/Presenting/Presenter.cs:350-364,944-945`).
2. `LiveSession.SendAudio` enqueues an audio frame; `SendAudioFrameAsync` sends `session.input_audio.append` with base64 PCM (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:191-205,401-412`).
3. GPT-Live transcript events are parsed by `LiveSession.HandleEvent`: `session.input_transcript.delta` and `session.output_transcript.delta` become `Transcript` events with `role`, `delta`, `start_ms`, and `end_ms` (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:443-484`). `WireSession` queues them into the presenter's single event channel (`src/PresenterAi.Application/Presenting/Presenter.cs:459-470`).
4. Output audio is similarly decoded from `session.output_audio.delta` and queued as `AudioReceived`; voiced frames drive answer/timer state (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:449-463`, `src/PresenterAi.Application/Presenting/Presenter.cs:534-587`).

### Presenter question flow

- `OnTranscript` forwards the transcript to subscribers, counts user characters for diagnostics, and, for a nonblank user delta while the state is `Presenting`, calls `OpenOrExtendQuestionHold` (`src/PresenterAi.Application/Presenting/Presenter.cs:589-604`). It does not inspect the text. A transcript such as “Keep continue”, “stop now”, or “end the meeting” has the same routing behavior: it is audience speech, not a command.
- `OpenOrExtendQuestionHold` sets `_questionHoldOpen`, clears the ordinary silence timer and wrap-up fallback, records the latest transcript end timestamp, resets `_answerVoiced`, and arms the 15-second question timer (`src/PresenterAi.Application/Presenting/Presenter.cs:1173-1187`). Timer events are accepted only when their generation still matches (`src/PresenterAi.Application/Presenting/Presenter.cs:282-305`).
- `OnPartGap` and `OnSilence` first call `HoldBlocksProgress`; while the hold is open they do nothing unless an eligible answer has already been voiced. Thus the hold is intended to stop a pending part, slide advance, or wrap-up close (`src/PresenterAi.Application/Presenting/Presenter.cs:708-747,1044-1065`).
- GPT-Live's `session.delegation.created` is raised by `LiveSession` (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:496-498`) and reaches `Presenter.OnDelegation` (`src/PresenterAi.Application/Presenting/Presenter.cs:620-657`). A `target: "responses"` delegation sets `_pendingBackendDelegation`; a `target: "client"` delegation sends a delegation-scoped `session.instructions.append` telling the model to answer from available material (`src/PresenterAi.Application/Presenting/Presenter.cs:646-657`).
- For managed Responses, `LiveSession` only recognizes terminal nested response event types and raises `DelegatedResponseFinished`; `Presenter` clears the pending marker and re-arms the question hold (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:551-566`, `src/PresenterAi.Application/Presenting/Presenter.cs:660-685`). It does not extract backend text.
- Audio after the latest question is eligible as the answer only when no backend delegation is pending. The first eligible voiced output sets `_answerVoiced`; then five seconds of quiet uses the follow-up path rather than immediately advancing (`src/PresenterAi.Application/Presenting/Presenter.cs:566-584`).
- On follow-up silence, or after the 15-second unanswered escape hatch, `ResumeAfterQuestion` clears the hold, sends `PromptBuilder.ResumeAfterQuestionInstruction()` upstream, and re-arms ordinary timing (`src/PresenterAi.Application/Presenting/Presenter.cs:1052-1098`). The upstream message is `session.instructions.append` because `LiveSession.AppendInstructions` builds that payload (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:166-178,583-598`).

### Why the observed controls fail

There is **no intent-detection path** from transcript text to `PauseAsync` or `EndAsync`: `OnTranscript` does not parse words, and repository search found no product code matching stop/end intent. `PauseAsync` and `EndAsync` are only interface commands (`src/PresenterAi.Application/Presenting/IPresenter.cs:17-25`), implemented by queued `PauseCommand`/`EndCommand` (`src/PresenterAi.Application/Presenting/Presenter.cs:128-143,350-364`). The bridge invokes them only for explicit WebSocket `{ "type":"pause" }` or `{ "type":"end" }` frames (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:370-382`). Therefore GPT-Live can say “Sure, pausing here” or “Ending the meeting now” as speech, but that speech has no side effect; the presenter remains in its prior state and its timers continue.

`Back to the slide` is documented in the active host prompt, not a presenter audio string. The code sends the instruction containing that example at resume time (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:124-125`, `src/PresenterAi.Application/Presenting/Presenter.cs:1072-1079`). The model produces the actual voice. The system prompt also says to answer/delegate and not resume until answered, but does not define spoken pause/end commands (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:39-43`).

## Q2. GPT-Live delegation, tools, and result return

### What the repository's research documents

`docs/research/005-gpt-live-delegation-audience-questions.md` explicitly documents these shapes and names:

- Client trigger: `session.delegation.created`, with a top-level event and nested metadata such as `id`, `type: "delegation"`, `target: "client"` or `"responses"`, and `offset_ms`; client delegation carries **no task text** (`docs/research/005-gpt-live-delegation-audience-questions.md:351-366`).
- Spoken/quiet client result events: `session.commentary.append` (spoken), `session.thinking.append` (quiet context), or `session.instructions.append` (behavior steering), each carrying `delegation_id` and content; appends are documented as at most 500 tokens and acknowledged by `session.commentary.appended`, `session.thinking.appended`, or `session.instructions.appended` with `client_event_id` (`docs/research/005-gpt-live-delegation-audience-questions.md:377-393`). There is no explicit client-delegation “done” event; repeated appends can continue the same delegation (`:393-394`).
- Managed Responses startup: `session.start` contains `session.delegation: { type: "responses", responses: { model, instructions, tools, tool_choice, parallel_tool_calls, max_output_tokens, ... } }`; the documented `tools` array includes function definitions and hosted `web_search` examples (`docs/research/005-gpt-live-delegation-audience-questions.md:26-93`). The research marks undocumented fields/attachments as unknown rather than assuming them (`:103-105`).
- Managed trigger/result envelope: `response.event` has an outer `delegation_id` and nested `event`; documented nested names include `response.created`, `response.output_item.added`, `response.output_text.delta`, `response.output_item.done`, and `response.completed` (`docs/research/005-gpt-live-delegation-audience-questions.md:148-173`).
- Function-tool cycle: nested `response.output_item.done` can contain an item shaped like `{ "type":"function_call", "call_id", "name", "arguments" }`; the client returns `{ "type":"response.item.create", "item": { "type":"function_call_output", "call_id", "output" } }`, then sends `response.create` to resume (`docs/research/005-gpt-live-delegation-audience-questions.md:181-221`).

### What this code implements

- `LiveSession.CreateStartEvent` always sends `session.start` with model, instructions, audio output voice, and a delegation object (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:612-626`).
- If `Upstream:DelegationModel` is empty (or the one startup delegation attempt is rejected), it sends `{ "type":"client" }`; otherwise current defaults select `{ "type":"responses", "responses": { "model", "instructions", "reasoning: {effort: "low"}", "service_tier":"priority", "text:{verbosity:"low"} } }` (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:628-647`). No `tools` array is defined in the implementation.
- `session.delegation.created` is forwarded raw through `ILiveSession.Delegation` (`src/PresenterAi.Application/Presenting/ILiveSession.cs:7-17`, `src/PresenterAi.Infrastructure/Live/LiveSession.cs:496-498`). The presenter extracts only nested `target` and `id` (`src/PresenterAi.Application/Presenting/Presenter.cs:627-637`). It does not reconstruct task text from transcript deltas.
- `AppendInstructions`, `AppendThinking`, and `AppendCommentary` exist and all include `delegation_id` in the JSON payload (`src/PresenterAi.Application/Presenting/ILiveSession.cs:26-32`, `src/PresenterAi.Infrastructure/Live/LiveSession.cs:166-178,583-598`). Current client-delegation handling uses **instructions**, not the research-recommended spoken `commentary` result (`src/PresenterAi.Application/Presenting/Presenter.cs:649-657`).
- `HandleResponseEvent` looks only for `response.completed` or nested type names containing error/failed/incomplete, then raises `DelegatedResponseFinished`; it does not expose `response.output_text.delta`, `response.output_item.done`, `function_call`, or tool arguments to application code (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:551-566`). There is no `response.item.create`, `response.create`, tool registry, or tool-result dispatcher in `src/`.

### Documented versus assumed

**Documented in the repository research:** the event names, client append result shapes, managed Responses envelope, function-call/tool-output cycle, delegation targets, 500-token append limit, and the fact that mode selection is session-scoped (`docs/research/005-gpt-live-delegation-audience-questions.md:115-242,351-393,487-500`).

**Implemented:** only startup configuration for managed Responses/client delegation, raw delegation forwarding, delegation-scoped append primitives, terminal-response detection, and the client-mode instruction fallback (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:480-520,612-647`, `src/PresenterAi.Application/Presenting/Presenter.cs:620-705`).

**Not implemented and therefore not established by code:** that the current provider deployment actually accepts every documented managed-Responses field; any function tool behavior in a live session; tool execution latency; a complete client question-text reconstruction; cancellation semantics; or that an appended acknowledgment means the model consumed/spoke the result. The research itself flags several provider/version details as documentation-dependent or unknown (`docs/research/005-gpt-live-delegation-audience-questions.md:19,103-105,227-250,298-299`).

## Q3. Presenter control surface and model-tool suitability

### Commands and bridge frames

`IPresenter` exposes `StartAsync`, `NextAsync`, `PrevAsync`, `GotoAsync`, `PauseAsync`, `ResumeAsync`, `MuteAsync`, `UnmuteAsync`, `SendAudioAsync`, and `EndAsync` (`src/PresenterAi.Application/Presenting/IPresenter.cs:17-25`). The WebSocket bridge accepts `start`, `next`, `prev`, `goto` with an integer index, `pause`, `resume`, `mute`, `unmute`, `end`, and binary audio (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:306-318,353-382`). `ping` is transport-only.

### Safe exposure assessment

- **Good candidates, with explicit-intent policy:** `pause` and `resume`. They have clear semantics and state checks (`PauseCore` requires presenting; `ResumeCore` requires paused) and are reversible (`src/PresenterAi.Application/Presenting/Presenter.cs:869-918`).
- **Possible candidates, but require strict confirmation/validation:** `next`, `prev`, and `goto`. They are deterministic navigation tools; `goto` rejects out-of-range indexes (`src/PresenterAi.Application/Presenting/Presenter.cs:817-867`). A model should invoke them only when the audience clearly asks for navigation, not merely because a question mentions “next” or a slide number.
- **Possible but high-impact:** `end`. It is irreversible for the current live session and closes the upstream session; expose only on an unambiguous end request, ideally with a confirmation policy (`src/PresenterAi.Application/Presenting/Presenter.cs:947-980`).
- **Do not expose as ordinary presenter tools:** `start` (it needs an owner and presentation id and begins a session), `mute`/`unmute` (they control whether the application can hear future voice commands), and `SendAudioAsync` (raw transport, not a semantic tool). `MuteCore`/`UnmuteCore` can mutate the local flag even outside the normal presenting transition (`src/PresenterAi.Application/Presenting/Presenter.cs:920-945`).

### Threading and state constraints

All mutable presenter state belongs to one bounded channel with one reader; public calls enqueue immutable command records and await their completion (`src/PresenterAi.Application/Presenting/Presenter.cs:14-37,178-191,243-264`). A model tool adapter should call the interface asynchronously and let the loop serialize it with audio/transcript/timer events. It must not call `NextCore`, `PauseCore`, or fields directly.

The state machine is `Idle`, `Connecting`, `Presenting`, `Paused`, `Ending` (`src/PresenterAi.Application/Presenting/PresenterState.cs:3-11`). Navigation is allowed only in presenting/paused; `EndAsync` returns false in idle/ending; stale events are filtered by session identity and timer generation (`src/PresenterAi.Application/Presenting/Presenter.cs:817-867,947-980,282-307`). A tool result is asynchronous: the bridge currently emits state/log events rather than a command-result protocol (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:387-404`).

## Q4. System instructions and per-slide prompts

### Construction

`Presenter.StartAsyncCore` builds system instructions with `PromptBuilder.SystemInstructions(title, slides, context)` and passes them in `SessionRequest` to the selected `LiveSession` (`src/PresenterAi.Application/Presenting/Presenter.cs:390-408`). The live session embeds them in `session.start.session.instructions` (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:612-626`).

On each slide, `PresentSlide` sends speaker notes as `PromptBuilder.NotesContext` through `AppendThinking`, chunks narration using `TextChunker`, and sends each part through `PromptBuilder.SlideInstruction` as `AppendInstructions` (`src/PresenterAi.Application/Presenting/Presenter.cs:475-526`). Other host-generated prompt sites are nudge, pause, resume, client delegation fallback, question resume, and wrap-up (`src/PresenterAi.Application/Presenting/Presenter.cs:751-813,869-918`; `src/PresenterAi.Application/Presenting/PromptBuilder.cs:112-131`).

### Current instructions

The system prompt tells the model to speak each narration in order, keep a natural pace, stop after a slide, and not start the next slide itself (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:31-37,39-43`). For audience speech it says: stop/listen; answer immediately from narration/background context or delegate; never claim to have looked something up before a result; at most say “One moment.”; do not resume until answered; after answering stay silent for follow-up (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:39-43`).

The concrete prompts are:

- slide part: present the labelled slide narration and stop/pause as appropriate (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:74-110`);
- pause: “Pause now. Stay silent and do not speak until you are told to resume” (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:118-119`);
- client fallback: answer from narration/context or say material does not cover it, then stay silent (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:121-122`);
- question resume: say a short bridge such as “Back to the slide”, restart the interrupted sentence, or say only the bridge if the slide was finished (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:124-125`);
- nudge: begin the current slide from the supplied narration (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:127-128`);
- wrap-up: thank the audience, then stop speaking (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:130-131`).

There is no system or per-slide instruction saying that the spoken phrases “pause”, “stop”, “end the meeting”, or “keep continue” are commands that must call host APIs. The managed backend prompt is separate and only requests one to three short spoken sentences about the talk, with “if unsure, say so” (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:638-647`).

## Q5. Existing plans, research, and backlog

Findings:

- The original MVP plan explicitly put voice-driven navigation (`go to slide 3`) out of scope because it would need Responses delegation plus tools; it listed `next_slide`/`goto_slide` tools as a rejected v2 candidate, not an implemented design (`docs/plan/001-ai-presenter-gpt-live-mvp.md:41-49,266-273,450-456`).
- The current approved plan 005/A1 addresses question holding, managed Responses delegation, and stall recovery. It explicitly says tools are not defined and excludes an application-owned HTTP question call in this amendment (`docs/plan/005-echo-protection-and-stall-recovery.md:19-31,156-170`).
- Research 005 contains the most complete tool/function-calling design discussion: client versus managed Responses delegation, tool event shapes, and Option A/Option B trade-offs (`docs/research/005-gpt-live-delegation-audience-questions.md:644-707`). It is research/design, not proof that the current upstream supports the whole proposed shape.
- The auto-script/Q&A proposal contains preparation-time intent, approved answers, `answer`, `deck`, `defer`, `avoid`, and `pre-empt` policies; it says runtime retrieval would later use Responses delegation plus tools (`docs/research/001-auto-script-pipeline-proposal.md:230-270,306-327`). This is a backlog/design direction, not a live intent router.
- Phase-0 architecture proposed an `Infrastructure/Llm` area and later knowledge-base retrieval, but the as-built `src/` has no corresponding LLM/tool adapter (`docs/research/002-phase-0-platform-architecture.md:23-45,116-131`).
- The PRD is the original quick Node MVP requirement; it mentions full-duplex questions and keyboard controls, but no agent loop, MCP, or semantic voice-command contract (`docs/requirement/001 prd.md:1-12`).

No existing product plan was found for MCP, an agent loop, model-owned presenter control, or human-like turn-taking beyond the current prompt/timer/question-hold behavior. The repository has operational references to MCP tooling in handoff material, but no MCP client/server integration in `src/`.

## Q6. HTTP/tool/MCP infrastructure and managed backend configuration

### Existing infrastructure

The only general HTTP client registration in the API is the named `sso` client (`src/PresenterAi.Api/Program.cs:36-40`). It is used by `SsoService` for OAuth token/user-info calls (`src/PresenterAi.Infrastructure/Identity/SsoService.cs:45,197-224`). No generic external-tool client, Responses HTTP client, function dispatcher, MCP SDK/client, or tool registry was found under `src/`.

The live upstream is a `ClientWebSocket`, not an HTTP Responses client (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:1-3,126-143`). The current managed delegation path therefore relies on GPT-Live itself to call the configured backend and inject the answer; presenter-ai does not make that backend call (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:628-647`, `docs/guides/002-audience-questions.md:35-38`).

### Configuration key names (names only)

Implemented upstream/delegation keys are:

- `Upstream:Endpoint`
- `Upstream:Key`
- `Upstream:Model`
- `Upstream:Voice`
- `Upstream:DelegationModel`
- `Upstream:Fallback:Endpoint`
- `Upstream:Fallback:Key`
- `Upstream:Fallback:Model`
- `Upstream:Fallback:DelegationModel`
- `Presenter:AdvanceSilenceMs`
- `Presenter:FollowUpWaitMs`
- `Presenter:LogEvents`

The option readers and defaults are in `src/PresenterAi.Infrastructure/Live/UpstreamOptions.cs:3-27`, `src/PresenterAi.Infrastructure/Live/UpstreamRoute.cs:12-51`, and `src/PresenterAi.Infrastructure/Live/PresenterOptions.cs:5-14`; binding/validation is in `src/PresenterAi.Infrastructure/DependencyInjection.cs:52-73`. `Upstream:DelegationModel` and its fallback counterpart select the managed Responses model; an empty value selects client delegation (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:628-647`).

The phase-0 document mentions future `Generation:OpenAI:Key` and related provider/model tables, but no current reader exists; treat those as proposal-only, not available infrastructure (`docs/research/002-phase-0-platform-architecture.md:157-190`). No credential values were opened or reported.

## Q7. Current advance logic, timers, and states

### States

The presenter states are `Idle → Connecting → Presenting`, with `Presenting ⇄ Paused`, and `Ending → Idle` on close (`src/PresenterAi.Application/Presenting/PresenterState.cs:3-11`; transitions in `src/PresenterAi.Application/Presenting/Presenter.cs:383-451,869-980`). The live transport separately has `Idle`, `Connecting`, `Open`, `Closing`, and `Closed` (`src/PresenterAi.Application/Presenting/LiveEvents.cs:3-11`).

### Timers/constants

- **Ordinary advance silence:** `AdvanceSilenceMs`, default 3,000 ms; presentation metadata can override it. It is armed after voiced output when no parts remain; expiry advances to the next slide, starts wrap-up on the last slide, or closes after wrap-up (`src/PresenterAi.Application/Presenting/Presenter.cs:21,721-747,1098-1099,1229-1231`; default setting `src/PresenterAi.Application/Presenting/PresenterSettings.cs:3-6`).
- **Part gap:** `PartGapFor(advanceSilenceMs) = min(2,500 ms, round(0.8 × advanceSilenceMs))`; when narration parts remain, expiry sends the next `AppendInstructions` part rather than changing slides (`src/PresenterAi.Application/Presenting/Presenter.cs:26,101,1086-1091`).
- **Question hold:** 15,000 ms. User speech or delegation opens/re-arms it; while open it blocks part/slide/wrap-up progress. If no answer is eligible, it releases through the normal resume path rather than advancing synchronously (`src/PresenterAi.Application/Presenting/Presenter.cs:23,302-305,1044-1079,1122-1130`).
- **Follow-up wait:** default 5,000 ms, configured by `Presenter:FollowUpWaitMs` and validated to 2,500–60,000 ms; after answer audio, quiet invokes the question resume/bridge (`src/PresenterAi.Application/Presenting/Presenter.cs:24,578-584,1061-1065`; `src/PresenterAi.Infrastructure/Live/PresenterOptions.cs:5-14`, `src/PresenterAi.Infrastructure/DependencyInjection.cs:67-73`).
- **Nudge/stall recovery:** a 15,000 ms nudge timer fires twice (at roughly 15 s and 30 s) if no voiced output; the third expiry pauses the presenter and logs “Resume or End” (`src/PresenterAi.Application/Presenting/Presenter.cs:21,751-785,1100-1110`). A voiced frame clears the nudge timer (`src/PresenterAi.Application/Presenting/Presenter.cs:552-559`).
- **Wrap-up fallback:** 15,000 ms after the last-slide wrap-up instruction if no voiced output; it ends the session unless a question hold is open (`src/PresenterAi.Application/Presenting/Presenter.cs:22,785-797,1112-1120`).
- **GPT-Live silence pump:** independent transport timing inserts 20 ms silence frames, with 120 ms pump slack and at most 25 catch-up frames per pump (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:11-16,325-341,373-399`). It keeps the upstream input timeline moving; it is not the presenter’s slide-advance timer.

### State/guard mechanics a redesign would replace or retain

Each timer has its own generation counter. Clearing/disarming increments the generation, disposes the timer, and the event-loop switch ignores stale generations (`src/PresenterAi.Application/Presenting/Presenter.cs:282-305,1133-1170`). Session identity checks similarly discard late events from an old live session (`src/PresenterAi.Application/Presenting/Presenter.cs:589-592,620-624`). The human-like redesign would likely replace the silence heuristic and `_heardOutput`/`_answerVoiced` inference with explicit turn/command/tool state, while retaining the single event loop, cancellation/stale-result guards, and manual navigation overrides.

## topicsWithNoFindings

- No implemented semantic intent detector for spoken `pause`, `resume`, `stop`, `end`, “end the meeting”, or navigation phrases.
- No model tool/function registry in `src/`; no `response.output_item.done` function-call dispatcher; no `response.item.create`/`response.create` tool-result path.
- No MCP client/server integration in the product source.
- No generic external HTTP/tool adapter for a backend Responses call; only the SSO `HttpClient` exists.
- No agent loop or explicit human-like turn-taking protocol beyond GPT-Live full duplex plus presenter timers/question hold.
- No documented way in this repository to cancel an in-flight managed GPT-Live delegation or guarantee that a spoken acknowledgment was consumed as a result; the research marks such behavior as provider/documentation dependent.
- No current configuration reader for the phase-0 proposal’s future `Generation:*` keys.

