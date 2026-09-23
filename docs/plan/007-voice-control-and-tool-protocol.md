# 007 — Voice control, human-like turn-taking and the tool protocol

**Status:** Approved (2026-09-23), revision 1 (after external review round 1)
**Size:** L (Application presenter + a new tool layer, Infrastructure live wire, API bridge, web page).
**Branch:** `feature/007-voice-control-and-tools` from `develop`. **Follow-up:** plan 008 adds MCP servers to the
tool catalogue built here.

## 1. Requirement (confirmed 2026-09-23)

A live talk ignored spoken commands. "Can you stop now" got "Sure—pausing here." and the narration carried on;
"Just end the meeting" got "Understood. Ending the meeting now." and then "Back to the slide. Third, …". The user
wants the presenter to obey at once and handle questions like a person, on a standard, extensible tool protocol that
can later call external tools without flooding the model's context.

**Decisions (user):**

| Question | Answer |
|---|---|
| How commands are recognised | Hybrid: model function tools through GPT-Live managed Responses, plus an instant local check for short, clear commands |
| External tool standard | MCP (plan 008), on the catalogue built here |
| Human-like behaviours | All: obey instantly; natural Q&A turn-taking; voice navigation; outside knowledge (plan 008) |
| Phasing | Two plans: 007 (protocol, presenter tools, turn-taking), 008 (MCP bridge) |
| "End the meeting" | Confirm first: pause, ask, end on yes |
| After an answer | Check in ("Shall I carry on?"), resume on yes or after quiet, from the interrupted sentence |
| Without managed Responses | The instant checks still work; richer requests get a spoken "I can't do that here" |
| Many tools (edit at G1) | Above a threshold, only pinned tools go inline; the rest are reached through `find_tools` / `call_tool` |

**Out of scope:** MCP and any external tool (plan 008); telling speakers apart; instant checks in languages other
than English; changing how answers are delegated.

**Acceptance criteria:**
1. "Stop" / "pause" / "wait" while it talks: speech stops within about 1 s of the end of the utterance, the state
   becomes Paused, and nothing continues until "continue" or the Resume button.
2. "Continue" / "carry on" / "keep going" resumes from the interrupted point.
3. "Next slide", "go back", "go to slide 3" and "go to the slide about the controller" navigate. An impossible target
   gets a spoken reply.
4. "End the meeting" pauses and asks for confirmation. Yes ends the talk (recorded as ended), no carries on, and no
   answer within about 10 s stays paused.
5. After an answer it checks in, then resumes on yes or after quiet, with a natural bridge; it never says a bridge
   after a command.
6. A command word inside a question ("what happens when you stop?") does not trigger a command.
7. Without managed mode, criteria 1, 2, 4, next, back and numbered navigation still work; richer requests get a spoken
   refusal.
8. The transcript above replays correctly in tests: "Can you stop now" pauses; "Just end the meeting" asks to confirm.
9. Tests cover 1–8, the build and all suites pass, and the live probe result is recorded.
10. With 100 tools registered, the session start carries only the pinned tools plus `find_tools` and `call_tool`, and
    its size stays within a fixed budget (a test measures the real start payload).
11. `find_tools` ranks the relevant tool first for a set of test queries; `call_tool` refuses an unknown name or
    invalid arguments and says why in its result.

## 2. Current state (as built)

- **Speech is never read as a command.** `Presenter.OnTranscript` forwards user deltas and, while presenting, only
  opens or extends the question hold (`src/PresenterAi.Application/Presenting/Presenter.cs:589-605`). Pause and End
  run only from bridge frames `pause` / `end` (`src/PresenterAi.Api/Realtime/PresenterBridge.cs:376-380`).
- **"Back to the slide" is our prompt.** `PromptBuilder.ResumeAfterQuestionInstruction` asks for that bridge
  (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:124-125`); `ResumeAfterQuestion` sends it after the
  follow-up quiet or the 15 s escape (`Presenter.cs:1069-1080`). In the failing talk, "Just end the meeting" opened a
  hold, the model said "Ending now", the follow-up quiet expired and the bridge was sent.
- **A paused talk cannot hear anything, at three gates:**
  - `PauseCore` mutes the upstream microphone (`_session?.Mute()`, `Presenter.cs:869-881`);
  - `SendAudioCore` forwards only while `Presenting` (`Presenter.cs:944-945`);
  - the browser sends microphone frames only while its snapshot is `presenting` (`web/app/src/ws/bridgeClient.ts:186-190`).
  `MuteCore` / `UnmuteCore` touch the upstream only while `Presenting` (`Presenter.cs:920-942`).
- **Output keeps flowing after a pause.** `OnAudio` publishes every output frame before looking at the state
  (`Presenter.cs:534-549`); the bridge forwards all of them (`PresenterBridge.cs:58-61`) and the page queues them
  (`web/app/src/routes/Present.tsx:140`). The page flushes playback only on its own barge-in detector
  (`Present.tsx:227-231`).
- **Delegation.** `LiveSession.CreateDelegation` sends `responses` with model, instructions, reasoning, service tier
  and verbosity when `Upstream:DelegationModel` is set, else `client`; **no `tools`**
  (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:628-650`). A startup delegation error retries once in client
  mode (plan 005). `HandleResponseEvent` only reports nested `response.completed` / error-like types
  (`LiveSession.cs:550-566`). The presenter tracks one pending backend answer in a single string,
  `_pendingBackendDelegation` (`Presenter.cs:73,649,660-680`), cleared with the question hold (`Presenter.cs:1189-1195`).
- **Documented tool cycle** (research 005 §2.5, `docs/research/005-gpt-live-delegation-audience-questions.md:181-221`):
  tools are defined only in `session.delegation.responses.tools` (function and hosted `web_search`); a call arrives as
  nested `response.output_item.done` with `item {type:"function_call", call_id, name, arguments}`; the client answers
  with `response.item.create {item:{type:"function_call_output", call_id, output}}` and then `response.create` (no
  delegation id). A `response.completed` during a tool round has `output: []`. The mode is fixed per session.
  **The live model decides whether to delegate at all** (research 005:441). **This deployment has never run a tool.**
- **User transcript arrives only as deltas** (`session.input_transcript.delta`, `LiveSession.cs:480-482`), with
  `start_ms` / `end_ms`; there is no "utterance done" event. Fragments can be uneven (research 005:670-676).
- **One event loop.** All presenter state is owned by a single reader; public calls enqueue a command and await its
  completion by that same loop (`Presenter.cs:27-37,128-143,178-219`); timers are generation-counted
  (`Presenter.cs:282-307`). Late session events are dropped by session identity (`Presenter.cs:589-592`).
- **No tool infrastructure.** No registry, dispatcher, JSON-schema validation or MCP code in `src/`
  (`docs/research/006-voice-control-and-tools-scout.md`).

## 3. Design

### 3.1 Tool protocol (Application, `PresenterAi.Application/Tools`)

- **`ITool`**: `Name` (`^[a-zA-Z0-9_-]{1,64}$`), `Description` (≤ 1,024 chars), `Parameters` (a JSON-schema object,
  ≤ 4 KiB serialised), `Tags`, `Pinned`, and `Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken)`.
  The shape is the OpenAI function-tool format, which MCP tool listings map onto (plan 008).
- **`ToolResult`**: `{ ok, message, data? }`, serialised as the `function_call_output` string (≤ 4 KiB; longer data
  is truncated with a note). Errors are results, not exceptions.
- **`ToolRegistry`** holds tool registrations. It rejects duplicates, oversized definitions and more than 12 pinned
  tools at registration.
- **`ToolSessionCatalogue`**: an **immutable snapshot** built once at Start from the registry:
  - total ≤ `Tools:MaxInlineTools` (default 16): every tool inline;
  - above it: pinned tools inline, plus `find_tools {query}` (up to 5 matches `[{name, description, parameters}]`,
    ranked by a local keyword score — name ×3, tags ×2, description ×1; lower-case, simple plural stripping) and
    `call_tool {name, arguments}` (validates, runs a searchable tool; unknown name or invalid arguments →
    `{ok:false, message}` naming the problem);
  - the serialised tool list is budgeted at 32 KiB; the meta-tools and every invocation close over this snapshot, so
    registry changes apply only to the next session.
- **Argument validation**: a small validator for the subset used here (object, properties, required,
  `additionalProperties:false`, string / integer / number / boolean, enum, minimum / maximum). Plan 008 revisits it.

### 3.2 Tool execution contract (never blocks the loop)

1. `ToolCallRequested` reaches the loop. The loop records the call in the **tool-round tracker** (§3.3) with the
   captured session and run generation, looks the name up in the session catalogue, and **starts** the invocation
   without awaiting it. It then returns to reading events.
2. Presenter tools call the public `IPresenter` methods, which enqueue commands to the same loop — the loop is free,
   so they complete. A spoken command, a tool call and a button all go through the same queued command.
3. The invocation's continuation posts `ToolInvocationCompleted` back to the loop. An exception becomes
   `{ok:false, message:"tool failed"}`; a 5 s per-call timeout becomes `{ok:false, message:"timed out"}`; malformed
   argument JSON or an unknown name completes immediately with `ok:false`.
4. On `ToolInvocationCompleted`, the loop submits the output **exactly once** — only if the captured session is still
   the current one and the run generation is unchanged; otherwise it is dropped and logged. Duplicate completions and
   duplicate upstream `call_id`s are ignored.

### 3.3 Live wire and the tool-round tracker

- **LiveSession (Infrastructure):**
  - `SessionRequest` gains `Tools` (the catalogue definitions);
  - in managed mode `delegation.responses` gains `tools`, `tool_choice:"auto"` and `parallel_tool_calls:false`;
  - client mode sends no tools;
  - a startup rejection of the new fields falls back to client mode through the existing retry;
  - new event `ToolCallRequested(delegationId, callId, name, argumentsJson)` from nested `response.output_item.done`
    with a `function_call` item;
  - new `SubmitToolOutput(callId, output)` (`response.item.create`) and `ContinueResponses()` (`response.create`) on
    the single writer;
  - `DelegatedResponseFinished` keeps reporting nested completions and failures, now with the delegation id, for
    the tracker to judge;
  - `SessionInfo` gains the delegation mode in use (`responses` / `client`).
- **Tool-round tracker (Application, replaces `_pendingBackendDelegation`)**, keyed by delegation id:
  - a function call adds it to the delegation's *current round* (deduplicated by `call_id`);
  - a `response.completed` for a delegation whose current round has calls is a **tool round**, not the answer;
  - when every call of every open round has its output submitted, the presenter sends **one** `response.create`
    (it names no delegation, so it is a global barrier) and starts a new round for each continued delegation;
  - a `response.completed` for a delegation with an empty current round is its **final** answer: the delegation
    closes and the question-hold logic treats the backend answer as ready (as today);
  - a failure, or a top-level `backend_error`, closes the delegation and drops its outstanding calls;
  - session close clears the tracker;
  - "a backend answer is pending" becomes "any delegation is open".
- **Assumption to check in Task 0:** that one `response.create` resumes every delegation waiting on outputs. If the
  probe shows otherwise, the plan is revised before Task 2.

### 3.4 Built-in presenter tools (pinned)

| Tool | Arguments | Does |
|---|---|---|
| `pause_presentation` | – | `PauseAsync` |
| `resume_presentation` | – | `ResumeAsync` |
| `next_slide` | – | `NextAsync` |
| `previous_slide` | – | `PrevAsync` |
| `go_to_slide` | `slide_number` (1-based integer) | `GotoAsync(n-1)`; out of range → `ok:false`, "there are slides 1 to N" |
| `end_presentation` | `confirmed` (boolean) | Starts the end confirmation (§3.7); ends only when a confirmation is awaiting its answer and `confirmed:true` |

Results carry the new state and slide (`{ok:true, message:"paused on slide 3 of 11"}`), or say that nothing changed
("already paused") so the model does not claim an action twice.

**Making the live model delegate (managed mode only).** Tools are reachable only if the live model delegates. The
system instructions gain a policy: "For any request to go to a particular slide or topic, or any presenter action
other than a plain stop, continue, next or back, delegate it. Never say you did something until the result says so."
The backend instructions list the slide titles (so "the slide about the controller" becomes a number) and say: use
the tools for any request to pause, continue, move or end; never claim an action the tool did not confirm.

### 3.5 Instant voice commands (Application, `VoiceCommands`)

- **Utterance assembly:**
  - user deltas are appended verbatim; the utterance keeps the first delta's `start_ms` and the last one's `end_ms`;
  - it completes after `UtteranceGapMs` (700 ms) with no new user delta, on a generation-counted timer;
  - past 120 characters or 6 s it is marked "not a command" until it completes;
  - it resets on a session change, Start and End.
- **Matcher:**
  - lower-case, strip punctuation, collapse whitespace, and remove an allowlist of fillers (`please`, `can you`,
    `could you`, `just`, `now`, `okay`, `ok`, `hey`, `the`, `a`);
  - then require **exact equality** with a known phrase, compared both spaced and with spaces removed, so
    fragmentation like `next`+`slide` → `nextslide` still matches;
  - a numbered pattern `go to slide N` / `slide N` (digits or one…twenty) is also local, so numbered navigation works
    without the model.

| Intent | Phrases (after stripping) |
|---|---|
| Pause | stop, pause, wait, hold on, stop talking, stop there, pause there |
| Resume | continue, carry on, keep going, go on, go ahead, resume, keep continue |
| Next | next, next slide, go next, move on |
| Previous | back, go back, previous, previous slide, last slide |
| Go to | go to slide N, slide N |
| End | end, end meeting, end presentation, end talk, finish, stop presentation |
| Yes / No | yes, yeah, yep, sure, do it / no, nope, not yet, don't |

- **Eligibility (echo safety).** An utterance is *during speech* if voiced model output that was forwarded overlaps
  its `[start_ms, end_ms]`, or, when timestamps are missing, if voiced output was forwarded within 300 ms of it.
  - During speech, only **Pause** is eligible. It is harmless and reversible, and a pause during speech is exactly a
    barge-in.
  - Every other intent needs the model to have been silent throughout the utterance, so an echo of the model's own
    voice can at worst pause the talk.
  - Yes and No additionally need the matching phase (§3.7).
  - An ineligible utterance is treated as ordinary speech.
- **Dispatch:**
  - a matched command runs on the loop through the same cores the buttons use;
  - a command is **not** a question: it clears the question hold that its first delta opened, so no resume bridge
    follows;
  - if the model also delegates and calls the same tool, the tool reports "already …" and the model only
    acknowledges.
- **Log:** `voice: pause (instant)`, `tool: go_to_slide 4 → ok (slide 4 of 11)`, `voice: "yes" ignored (model
  speaking)`.

### 3.6 Pausing: keep listening, stay quiet

- **Input stays open while paused, at every gate:**
  - the browser sends microphone frames in `presenting` and `paused`;
  - `SendAudioCore` forwards in `Presenting` and `Paused` when not muted;
  - `MuteCore` / `UnmuteCore` act on the upstream in both states;
  - `PauseCore` no longer mutes.
  Mute (M) stays the explicit way to stop listening, in either state. The stall pause uses the same `PauseCore`, so a
  stalled talk can also be resumed by voice.
- **Output is gated while paused.** Each pause emits one `Flush` (bridge frame `{type:"flush"}`; the page drops queued
  audio; old clients ignore unknown frames). While Paused, `OnAudio` forwards a frame only inside a **speech permit**
  and drops everything else:
  - **What opens a permit:** a user utterance completing while paused that is not an executed command (the model may
    answer it or acknowledge), or the end-confirmation question (§3.7);
  - **What the permit accepts:** only audio whose `start_ms` ≥ that utterance's `end_ms`, or, without timestamps,
    audio that arrives after the permit opened;
  - **When it closes:** after 1,500 ms of output quiet, or on any state change.
  So narration still in flight from before the pause is never played. A button pause with no speech afterwards stays
  silent.
- The pause instruction becomes: "Stay silent. If someone speaks to you, you may answer in a few words or acknowledge
  a command; do not continue the narration until told."

### 3.7 Turn-taking phases

One `_interaction` phase, generation-checked like the timers:

| Phase | Entered when | Left when |
|---|---|---|
| `None` | default | — |
| `Answering` | the question hold is open and eligible answer audio is voiced (today's `_answerVoiced`) | output quiet for 700 ms → `AwaitingCarryOn`; a command; a state change |
| `AwaitingCarryOn` | after `Answering` (the model was told to ask "Shall I carry on?") | Yes → resume now; `FollowUpWaitMs` quiet → resume; a new question → back to the hold; No → stay on the slide, hold kept for the next question; a command or button |
| `AwaitingEndQuestion` | End intent or `end_presentation` while none is pending: pause, open a permit, ask "Shall I end the presentation now?" | the question is voiced and then quiet for 500 ms → `AwaitingEndAnswer`; not voiced within 8 s → `AwaitingEndAnswer` anyway (logged) |
| `AwaitingEndAnswer` | as above; the 10 s answer deadline starts here | Yes (eligible) or `end_presentation{confirmed:true}` → End; No → Resume; deadline → `None`, stays paused (logged) |

A "yes" said while the model is still answering is not eligible (the model is speaking). A stale "yes" after a
timeout finds phase `None` and is ordinary speech.

**Precedence (every combination is tested):**

| While | Event | Result |
|---|---|---|
| End pending (either phase) | End button / bridge `end` | Ends now; buttons are explicit and never ask |
| End pending | Resume / Next / Prev / Goto (button or voice) | Confirmation cancelled, then the action |
| End pending | Voice End again | Stays pending; the deadline is not restarted |
| End pending | Take-over (plan 006) | Resumable end as in plan 006; confirmation discarded |
| End pending | Browser disconnect | Normal end as today |
| End pending | Stall timers | Cannot fire: pause cleared the timers |
| `AwaitingCarryOn` | Any button or command | Phase cleared, then the action |
| Any | Confirmed voice End | Normal End: recorded as ended; the next Start begins at slide 1 |

### 3.8 Prompts

- **System instructions:**
  - after answering, "ask briefly whether you may carry on (for example 'Shall I carry on?'), then wait";
  - the delegation policy (§3.4) in managed mode.
- **Resume bridge:** "Return to the talk with a short, natural transition of your own, then restart the sentence you
  were in; if the slide was finished, say only the transition." It is never sent after a command or outside
  Presenting.
- **Client mode** (`SessionInfo` says `client`, whether configured or after a startup rejection): appended once after
  start: "You cannot move the slides yourself. If asked for a particular slide by topic, or anything beyond pause,
  continue, next, back, a slide number or end, say briefly that you can't do that here."

### 3.9 Sequences

**Instant stop:**

```
GPT-Live --input_transcript.delta "Can you stop now" (start/end ms)--> Presenter.OnTranscript: append; hold opened
  700 ms no delta → utterance complete → matcher: Pause; during speech → Pause is eligible
  clear hold → PauseCore: timers cleared, mic stays open, pause instruction, Flush → state Paused
  Bridge → browser: {type:"flush"}, state paused, log "voice: pause (instant)"
  old narration frames (start_ms < utterance end) → dropped by the paused output gate
  the model's "Sure." (start_ms ≥ utterance end, permit open) → forwarded
```

**Tool call, "go to the slide about the controller":**

```
GPT-Live → session.delegation.created (responses) → tracker: delegation D open; question hold
GPT-Live → response.event{D, output_item.done function_call c1 go_to_slide {"slide_number":2}} → tracker: D round {c1}
Presenter (loop) → start invocation (not awaited) → loop keeps reading
  invocation → IPresenter.GotoAsync(1) → loop: GotoCore → PresentSlide → state/slide frames → tool result
  → ToolInvocationCompleted → loop: current session? → SubmitToolOutput(c1) → all outputs in → ContinueResponses()
GPT-Live → response.event{D, response.completed, output []} → tool round (D had calls)
GPT-Live → response.event{D, response.completed} → D's round empty → final → backend answer ready
```

**End confirmation:** End intent → `AwaitingEndQuestion` (pause, permit, ask) → question voiced + 500 ms quiet →
`AwaitingEndAnswer` (10 s) → eligible "yes" → `EndAsync` → recorder ends the session → idle.

**Parallel paths checked:** buttons, keyboard, the bridge frames, the stall pause, take-over and disconnect all still
enter through `IPresenter` or the existing cleanup. The only new entry points are the matcher (on the loop) and tool
completions (events on the loop).

### 3.10 Alternatives considered

- **Keyword rules only:** instant, but brittle and cannot scale to tasks or external tools. Kept only as the fast path.
- **Model tools only:** one path, but every stop waits 1–2 s, depends on the model choosing to delegate, and nothing
  works in client mode.
- **Tools on the live model itself:** GPT-Live documents tools only under `delegation.responses`.
- **Swap tools with `session.update` mid-session:** only `responses.instructions` updates are documented; dynamic
  loading goes through `find_tools` / `call_tool` over a fixed list instead.
- **Embedding search for tools:** better recall, but a new service and cost; keyword ranking can be swapped behind
  `find_tools` later.
- **Intent detection in the browser:** a second transcript path with no access to tool calls. Rejected.
- **Muting input while paused (today):** makes spoken "continue" and confirmation impossible. Rejected.

## 4. Impact and risk

- **Unproven upstream behaviour (highest risk):** tools, delegation of short commands, the global `response.create`
  and latency are untested against this deployment. Task 0 gates the work.
- **Echo:** handled by the eligibility rule (§3.5); the worst an echo can do is pause. End always asks, and its
  "yes" must be heard while the model is silent.
- **Double actions** (instant check and tool): presenter commands are idempotent in effect and tools report
  "already …".
- **Listening while paused:** the room reaches the model during a pause; the output gate keeps it quiet unless spoken
  to, and Mute stops listening entirely.
- **Protocol:** one new bridge frame (`flush`); old clients ignore it.
- **Cost / latency:** tool rounds add backend tokens; `parallel_tool_calls:false` and low reasoning keep them small.
- **Rollback:** revert the branch. A deployment that rejects the tool fields falls back to client mode.

## 5. Tasks

0. **Live probe (gate).**
   - **Change:** a manual, env-gated test (`PRESENTER_LIVE_PROBE=1`, skipped otherwise):
     `tests/PresenterAi.Infrastructure.Tests/Live/LiveToolProbeTests.cs`. It starts a managed session with the six
     presenter tools and the §3.4 policy. It then sends five times each: "next slide", "go to slide 3", "go to the
     slide about <a real title>", "end the meeting". For each it answers the calls and records whether the live model
     delegated, the event trace, and call → spoken-reply latency. It also checks one `response.create` after two
     outputs. Upstream settings come from the usual config keys; no secret is written anywhere.
   - **Verify:** the results go in `docs/research/006-voice-control-and-tools-scout.md` (route, trace shape, delegation
     rate, latency, no credentials).
   - **Gate:** title navigation must delegate at least 4 of 5 times, and the tool cycle must complete. Otherwise, stop
     and revisit with the user; the fallback is local title matching.
1. **Tool registry and session catalogue.**
   - **Change:** `src/PresenterAi.Application/Tools/` (`ITool`, `ToolResult`, `ToolRegistry`, `ToolSessionCatalogue`,
     `ToolArgumentValidator`, `FindToolsTool`, `CallToolTool`); `Tools:MaxInlineTools` option.
   - **Verify:** `ToolRegistryTests` / `ToolSessionCatalogueTests`:
     - duplicate, oversized and too-many-pinned rejected;
     - threshold 0 / 1 / 16 / 17 / 100 with all-pinned and Unicode fixtures;
     - 100 tools → pinned + `find_tools` + `call_tool` only, and the **real `session.start` payload** under the budget;
     - a `find_tools` ranking table (≥ 5 queries);
     - `call_tool` with an unknown name, a missing required argument, a wrong type or an extra property →
       `ok:false` with the reason;
     - a tool registered or replaced after the snapshot is invisible to that session.
   - **Mutations:** always inline; drop validation; reverse the ranking weights; meta-tools read the live registry.
2. **Live wire.**
   - **Change:** `LiveSession` tools in `session.start`, `ToolCallRequested`, `SubmitToolOutput`, `ContinueResponses`,
     delegation id on finishes, delegation mode in `SessionInfo`; `ILiveSession`; `FakeLiveServer` scripting of
     function calls.
   - **Verify:** `LiveSessionTests`:
     - tools only in managed mode;
     - a function call raises the event with call id, name and arguments;
     - `response.item.create` precedes `response.create`;
     - client-mode fallback after a tools rejection, with `SessionInfo` saying `client`.
   - **Mutations:** send tools in client mode; swap the two frames; lose the delegation id.
3. **Presenter tools, execution contract and tool-round tracker.**
   - **Change:**
     - `Presenter` handles `ToolCallRequested` and `ToolInvocationCompleted` (§3.2);
     - the tracker replaces `_pendingBackendDelegation` (§3.3);
     - built-in tools go in `src/PresenterAi.Application/Presenting/Tools/`;
     - backend instructions with slide titles and the tool policy.
   - **Verify:** `PresenterToolTests`:
     - each tool changes state and returns the right result;
     - `go_to_slide` out of range;
     - a queued button command and a tool both complete while a tool runs (a deadlock oracle with an explicit bound);
     - the exact sequence `call → empty completed → output/create → second call → empty completed → output/create →
       final completed`;
     - two interleaved delegations;
     - exception, timeout and malformed arguments each give exactly one `ok:false` output;
     - a completion after End, restart, navigation or take-over is dropped;
     - duplicate call ids are ignored;
     - the hold is not released on a tool-round completion.
   - **Mutations:** the loop awaits the invocation (the deadlock test times out); finish on the tool-round completion;
     submit to a replaced session; drop the exception conversion.
4. **Instant voice commands.**
   - **Change:** utterance assembly, `VoiceCommandMatcher`, the eligibility rule, dispatch on the loop, a command
     clears the question hold.
   - **Verify:** `VoiceCommandMatcherTests`:
     - the phrase table and fillers;
     - numbered navigation in digits and words;
     - every phrase under several fragmentations (`next`/` slide`, `next `/`slide`, character chunks);
     - questions containing command words do not match.

     `PresenterVoiceCommandTests`:
     - gaps of 699 / 700 / 701 ms;
     - a session replacement resets the buffer;
     - the failing transcript replays (stop → Paused with no bridge; end → confirmation asked; "keep continue" →
       resumed);
     - "what happens when you stop?" stays a question;
     - during speech, only Pause fires ("continue", "yes" and "next slide" overlapping model output are ignored);
     - an instant pause plus a tool pause → one pause.
   - **Mutations:** substring instead of equality; remove the eligibility rule; strip arbitrary words instead of the
     filler allowlist; merge across the 700 ms boundary.
5. **Pausing, phases and prompts.**
   - **Change:**
     - the paused input contract (§3.6) in `Presenter` and the output gate with permits;
     - `Flush`;
     - the `_interaction` phases and precedence (§3.7);
     - the new prompts (§3.8);
     - the bridge `flush` frame.
   - **Verify:** `PresenterTests`:
     - paused microphone frames are forwarded;
     - mute while paused suppresses them and unmute restores them;
     - pause raises one flush;
     - `audio A → pause → delayed old audio B (start < barrier)` → B not forwarded;
     - a reply after a paused utterance is forwarded;
     - a button pause with no speech stays silent;
     - each phase transition, including "yes" during an answer (ignored), an unvoiced end question (8 s fallback), a
       new question during the check-in, and a stale "yes" after the timeout;
     - the full precedence table, including recorder finalisation and plan 006 resume-at-slide on take-over;
     - no resume bridge after a command;
     - the client-mode instruction is appended once, both when configured and after a startup rejection.

     `BridgeTests`: pause sends a `flush` frame.
   - **Mutations:** mute on pause; forward output while paused without a permit; start the end deadline at the ask
     instead of after it is voiced; keep the canned bridge.
6. **Web.**
   - **Change:** `bridgeClient.ts` sends audio while `paused` and emits `flush`; `Present.tsx` flushes playback on it.
   - **Verify:**
     - `bridgeClient.spec.ts`: audio is sent while paused and not while idle; a flush frame raises an event.
     - `Present.spec.tsx`: a flush event flushes playback.
   - **Mutations:** ignore the frame; keep the presenting-only send.
7. **Docs.**
   - **Change:**
     - README: a voice commands table and a troubleshooting row;
     - `docs/guides/002-audience-questions.md`: check-in, commands, echo rule;
     - `AGENTS.md`: the `flush` frame;
     - `docs/reference/001-api-and-code-conventions.md` §8;
     - research 006: the probe results.
   - **Verify:**
     - protocol examples match the server and client types;
     - the command table matches the matcher's phrase table;
     - every config key named has a reader, a default and validation;
     - links and numbered-document references resolve;
     - no credential appears anywhere.
8. **Verification and wiring audit.**
   - Full .NET build (`-warnaserror`) and tests; web lint, tests and build.
   - Wiring audit (`~/.claude/docs/07-integration-boundary-audit.md` §5):
     - every tool reaches `IPresenter`;
     - every new event reaches the bridge and the page;
     - nothing mutates presenter state off the loop;
     - all three audio gates agree.
   - Manual runbook with the local API and the real upstream (timings measured with the page log):
     1. Start, say "stop" mid-sentence → silence within about 1 s of the end of the word, Paused; "continue" →
        resumes the sentence.
     2. "Go to slide 3", then "go to the slide about the controller" → navigates; "go to slide 40" → a spoken reply
        with the valid range.
     3. Ask a question → answer, "Shall I carry on?"; say "yes" → resumes with a natural transition.
     4. "End the meeting" → asked; "no" → carries on; again, "yes" → ends.
     5. On loudspeakers (no headphones), let it narrate a slide whose text contains "continue" or "next" → no command
        fires.
     6. Without a delegation model: 1, 4, next, back and "slide 3" work; "go to the slide about X" → "I can't do that
        here".

## 6. Test strategy

- Unit: registry, catalogue snapshot and budgets, validator, search ranking, matcher and fragmentation.
- Presenter:
  - tool execution contract and tracker;
  - voice commands and eligibility;
  - paused input and output gates;
  - phases and the precedence matrix, against the fake session and fake clock.
- Live wire: `LiveSessionTests` against `FakeLiveServer` with scripted function calls.
- Bridge: `flush` frame end to end.
- Web: paused audio sending, flush handling.
- Live: the Task 0 probe (gate), then the manual runbook.

## 7. Open questions

None. Deferred to plan 008: MCP servers as tool sources, richer schema validation, per-user permissions and
timeouts for external calls. Checked by Task 0 before building on it: the global `response.create` and delegation of
short commands.

## Approval log

| Date | Step | By |
|---|---|---|
| 2026-09-23 | Requirement brief confirmed (G1), with dynamic tool discovery added | user |
| 2026-09-23 | External plan review round 1 (`pi` sol/medium): A1 B1 C2 D10, all accepted and folded in (revision 1); see `docs/review/009-plan-007-review-round-1.md` | reviewer, orchestrator |
| 2026-09-23 | Plan approved (G2), revision 1 | user |
