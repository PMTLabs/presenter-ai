# 001 — AI Presenter: GPT-Live-driven HTML slide presenter (MVP)

**Date:** 2026-09-21
**Status:** Approved (2026-09-21)
**Size:** L (external real-time voice integration + browser audio + slide driver), scoped to an MVP
**Area:** new repo — `src/server/`, `src/web/`, `decks/` (sample + Ricoh), `presentations/`, `test/`
**Requirement brief confirmed:** 2026-09-21 (G1, 1 round of 4 questions) — amended the same day, see §2 "Amendments after G1"

---

## 1. Goal

Run `npm start`, open `http://localhost:3000`, press **Start**, and hear GPT-Live-1 present your
Ricoh HTML deck slide by slide from a Markdown script — advancing automatically when it finishes
each slide, taking spoken questions mid-flow (full duplex), and returning to the script — so you
can rehearse today. Any same-origin HTML deck (custom `show(n)`, reveal.js, or plain sections) works.

## 2. Requirement (as confirmed at G1)

- **Problem:** You need to rehearse a presentation now and have no tool that can voice a slide deck
  from a script while also listening and answering questions live.
- **Goal:** A small Node app that opens a reveal.js deck in the browser, narrates each slide through
  GPT-Live-1 (full-duplex) from a Markdown script, auto-advances, and lets you interrupt by voice.
- **In scope:**
  - Node backend (Express + `ws`): serves UI + deck, holds the upstream WebSocket to GPT-Live
    (`session.start`, audio relay both ways, context appends, graceful `session.close`); reads
    `LIVE_ENDPOINT` / `LIVE_API_KEY` / `LIVE_MODEL` / `LIVE_VOICE` from `.env`; endpoint path
    auto-detected (`*.azure.com` → `/openai/v1/live/sessions`, else `/v1/live/sessions`);
    Bearer + `api-key` headers.
  - Browser UI (vanilla JS, no build): reveal.js deck in an iframe driven via its API; mic capture →
    24 kHz PCM16 → backend; playback of returned PCM16; transcript panel; controls Start /
    Pause-Resume / Next / Prev / Mute / End; keyboard Space / → / ← / Esc.
  - Script parser: `presentation.md` = YAML frontmatter (`deck`, `voice`, optional `context`,
    optional `advanceSilenceMs`) + `## Slide N` sections (narration) + optional `> notes:` blockquote
    (quiet context). Context file → system `instructions`.
  - Presenter loop: on slide N → show slide + `session.thinking.append` (notes) +
    `session.instructions.append` (narration; >500 tokens chunked). "Narration done" = no
    `session.output_audio.delta` for `advanceSilenceMs` (default 2000) after narration started →
    advance. Manual Next/Prev overrides immediately. Audience speech interrupts natively; system
    prompt tells the model to answer briefly then resume the current slide.
  - Sample reveal.js deck + sample `presentation.md` + `.env.example` + README runbook.
- **Out of scope / non-goals:** PowerPoint input; voice-driven navigation ("go to slide 3" — needs
  Responses delegation + tools; v2); WebRTC / browser-direct transport; sideband; multi-user;
  persistence; auth on the local UI; exact-wording guarantee.
- **Users and surfaces:** you alone, `http://localhost:3000`; one presenter page; one backend process.
- **Behaviour:**
  - Happy path: `npm start` → open page → Start → `session.started` → slide 1 shown + narrated →
    silence → slide 2 … → last slide → wrap-up line → `session.close` → usage seconds shown.
  - Interruption: you speak mid-narration → model answers → auto-advance timer held while anyone is
    speaking → script resumes after silence.
  - Failures: upstream `error` events shown in UI log; upstream socket drop → "disconnected", Start
    re-creates a session at the current slide; missing `.env` keys → server exits naming the
    variable; missing deck → 404 with message.
  - Any `session.closed` → controls reset, transcript kept.
- **Constraints and assumptions:** Node ≥ 20 (22.23 installed), ESM JS, npm; deps `express`, `ws`,
  `dotenv`, `gray-matter`; reveal.js from CDN in sample deck; your own deck must live under `decks/`
  (same-origin for driving); mic `echoCancellation: true` + Mute fallback; session config uses the
  Azure-documented `audio: { output: { voice } }` shape (OpenAI-direct `audio.format` shape only
  partially documented → risk R1); `LIVE_MODEL` is whatever the provider expects; ≈ $0.05/min.
- **Acceptance criteria:**
  1. `npm start` with a valid `.env` serves the page; with a missing key it exits naming the variable.
  2. Start yields `session.started` in the server log and "Connected" in the UI within 5 s.
  3. Slide 1's narration is heard and its text appears in the transcript panel.
  4. Hands-off, the deck advances to slide 2 after narration ends (within `advanceSilenceMs` + 1 s).
  5. Pressing → during narration immediately shows the next slide and its narration begins.
  6. Speaking a question mid-narration produces a spoken answer; the script then continues.
  7. Pause stops auto-advance and mutes the mic; Resume restores both.
  8. End (or last slide finished) sends `session.close`; UI shows final `usage.seconds`; server logs
     `session.closed`.
  9. A `## Slide` narration longer than 500 tokens is delivered in full (chunked, no `error`).
  10. Unit tests: script parser (frontmatter, sections, notes, chunking), endpoint/auth resolution,
      advance-on-silence state machine.
- **Decisions made:** Provider → auto-detect by host; Deck → reveal.js; Advance → hybrid auto +
  manual; Script → single Markdown with frontmatter.

**Amendments after G1 (user message, 2026-09-21):**
- The deck to rehearse is `D:\sources\ricoh\management\plan\Ricoh_Project_Delivery_Overview_Slide_Deck_v1.0.html`
  — a **custom single-file HTML deck, not reveal.js**. The deck driver therefore auto-detects the
  deck type (custom `show(n)` deck / reveal.js / plain `section.slide` toggling) instead of assuming
  reveal.js; the reveal.js adapter is kept because it is ~5 lines. AC3–AC5 and the runbook run
  against the Ricoh deck (11 slides).
- `.env` already holds the real credentials as `UPSTREAM_ENDPOINT` and `UPSTREAM_KEY` (Azure AI
  Foundry host, no path). The env names in this plan are `UPSTREAM_*` accordingly.
- No narration script or context file exists for the Ricoh deck → the plan adds a task to draft
  `presentations/ricoh-delivery-overview.md` (+ context) from the slides' own text, for the user to
  review before rehearsing.

## 3. Current state (as-built)

Greenfield. Verified 2026-09-21:

- `D:\sources\demo\presenter-ai\` contains only `docs/requirement/001 prd.md`, `.env` and
  `hooks.log`. No `package.json`, no git repo.
- `.env` defines exactly two variables: `UPSTREAM_ENDPOINT` = `https://<name>.services.ai.azure.com`
  (Azure AI Foundry host, **no path**) and `UPSTREAM_KEY` (value not inspected). So the resolver must
  append `/openai/v1/live/sessions` and send Azure-style auth; `UPSTREAM_MODEL` will need to be the
  Foundry deployment name if it differs from `gpt-live-1`.
- Sibling `D:\sources\demo\anyvoice\` is a .NET/Avalonia voice app (`AnyVoice.slnx`,
  `src/AnyVoice.Core`) — no Node code to reuse.
- Toolchain: Node v22.23.2, npm 10.9.8 → built-in `node --test` with `mock.timers` is available;
  no test framework dependency needed.

**The Ricoh deck** (`…\Ricoh_Project_Delivery_Overview_Slide_Deck_v1.0.html`, 535 KB, 97 lines,
self-contained: inline CSS, two `data:image/png` images, no external scripts):
- 11 × `<section class="slide …">`; the visible one has class `active`. Titles: 1 Project Delivery
  (cover) · 2 Products Supported by the Team · 3 Team Org Chart · 4 Offshore Resource Plan ·
  5 Planning, Monitoring, Reporting & Governance Rhythm · 6 Current Hybrid Working Model ·
  7 Proposed Hybrid Delivery Model · 8 Outcome-Based Capacity for CPE and DCD · 9 Execution Flow ·
  10 CPE/DCD Roadmap · 11 Questions & Alignment.
- Navigation script (last line of the file): global `function show(n)` toggles `.active`, updates
  `.slide-no` footers and the `#progress` bar, and sets `location.hash = 'slide-' + (i+1)`; on load
  it reads `#slide-N`. Own `keydown` handler on its window (ArrowRight/PageDown/Space → next,
  ArrowLeft/PageUp → prev, Home/End) and two on-page buttons `#prev` / `#next`.
  ⇒ Driveable from a same-origin iframe via `contentWindow.show(i)`; the deck's own buttons/keys are a
  **parallel navigation path** that must be synced back via the iframe's `hashchange` (see §4.4).
- No narration script, speaker notes or context `.md` exists beside the deck (only `.pptx` and
  `.backup.html` variants).

**Upstream API facts (research, 2026-09-21)** — sources: Microsoft Learn *Use GPT-Live for
real-time voice* + *GPT-Live event API reference* + *Delegate work in GPT-Live* (ms.date
2026-09-04), OpenAI *Managing GPT-Live sessions* / *Delegation and tools in GPT-Live*, Vercel AI
Gateway *GPT-Live* (2026-09-15, includes a working Node `ws` sample):

| Fact | Value |
|---|---|
| Transport | WebSocket; Azure `wss://<res>.openai.azure.com/openai/v1/live/sessions`; OpenAI/gateways `wss://<host>/v1/live/sessions`; header `Authorization: Bearer <key>` |
| Startup | client sends `session.start { session: { model, instructions, audio:{output:{voice}}, delegation:{type:"client"} } }`; wait for `session.started` (carries `session.id`, `expires_at`). Config object is **strict** (unknown fields rejected). `model`, `instructions`, `audio` immutable after start. |
| Audio | raw PCM16 LE mono 24 000 Hz, base64; in: `session.input_audio.append { audio }` (not acked; even byte count); out: `session.output_audio.delta { delta, start_ms, end_ms }`; **no output-done event** |
| Transcripts | `session.input_transcript.delta` / `session.output_transcript.delta { delta, start_ms, end_ms }`; fragments interleave; **no turn-complete event** |
| Context appends | `session.instructions.append` (behaviour/speech, "say X exactly"), `session.thinking.append` (quiet facts), `session.commentary.append` (say aloud, may paraphrase); each `{ event_id, delegation_id: null, content ≤ 500 tokens }`; acked by `*.appended { client_event_id }` when the timeline reaches the estimated injection point |
| Mute | `session.input_audio.mute` / `.unmute` → `.muted` / `.unmuted` |
| Close | `session.close` → drain → `session.closed { reason, usage.seconds }`; reasons `close_requested|expired|content|remote_hangup|connection_lost` |
| Usage | `session.usage.updated { usage.seconds (cumulative), context_window.usage_ratio }` ≈ once/min |
| Errors | `error { error: { type, code, message, param, client_event_id? } }`; startup error ⇒ no `session.started`; command error does not close the session |
| Delegation | client delegation `session.delegation.created` carries **id/metadata only, no task text** → unusable for reliable voice-command navigation → deferred (non-goal) |
| Limits | `instructions` ≤ 16 384 tokens; 500 tokens per append |
| Price | $0.05/min billed per second, silence included |

## 4. Design

### 4.1 Approach

Three small modules on the server, three on the client, one JSON protocol between them.

```
presenter-ai/
├── package.json                  type:module; scripts start/dev/test; deps express ws dotenv gray-matter
├── .env.example  .gitignore  README.md
├── src/server/
│   ├── index.js                  Express static (src/web, decks) + /api/presentations + WS /ws bridge
│   ├── config.js                 load .env, validate, resolveLiveUrl(), authHeaders()
│   ├── script-parser.js          parsePresentation(md) → {meta, slides[]}; chunkText(text, maxChars)
│   ├── prompt.js                 buildSystemInstructions(meta, slides, context); buildSlideInstruction(slide, i, n, chunk k/K)
│   ├── live-client.js            class LiveSession (EventEmitter): connect/start/sendAudio/append*/mute/close
│   └── presenter.js              class Presenter: state machine, silence timer, bridges client ⇄ LiveSession
├── src/web/
│   ├── index.html  styles.css    presenter page: deck iframe (left), controls + transcript + log (right)
│   ├── app.js                    UI controller; WS to /ws; keyboard; wires audio + deck driver
│   ├── deck-driver.js            DeckDriver: same-origin iframe, auto-detected adapter (showFn | reveal | sections), hashchange sync
│   ├── audio-capture.js          getUserMedia(echoCancellation) → AudioContext(24k) → capture worklet → 20 ms Int16 frames
│   ├── audio-playback.js         playback worklet with Float32 ring buffer fed by binary frames
│   └── worklets/capture-processor.js, playback-processor.js
├── decks/ricoh/index.html        verbatim copy of the Ricoh deck (the presentation to rehearse)
├── decks/sample/index.html       3-slide plain HTML deck in the same `show(n)` style (first run + tests)
├── presentations/ricoh-delivery-overview.md, ricoh-context.md   drafted from the slides' text; user-reviewed
├── presentations/sample.md, sample-context.md
└── test/                         node --test: config, script-parser, prompt, presenter (mock timers), integration (fake GPT-Live WS server)
```

**Deck driver** (`deck-driver.js`): the deck is loaded in a same-origin iframe (`/decks/<name>/…`).
On `load`, `DeckDriver` picks the first adapter whose `detect(win)` passes, unless frontmatter
`driver:` pins one:

| Adapter | detect | goto(i) | count | Used by |
|---|---|---|---|---|
| `showFn` | `typeof win.show === "function"` && `win.document.querySelectorAll(".slide").length > 0` | `win.show(i)` | `.slide` count | Ricoh deck, sample deck |
| `reveal` | `win.Reveal?.slide` | `win.Reveal.slide(i, 0)` | `Reveal.getTotalSlides()` | any reveal.js deck |
| `sections` | `querySelectorAll("section.slide").length > 0` | toggle `.active` on index i | count | plain decks without a nav script |

Reverse sync: the driver listens to the iframe window's `hashchange` (`#slide-N`) and, if N-1 ≠ the
presenter's index, `app.js` sends `{type:"goto", index}` so the deck's own buttons/keys stay in
step with narration. The iframe gets `tabindex="-1"` and `app.js` re-focuses the parent after every
`goto`, so the page's keyboard map wins by default.

**Client ⇄ server protocol on `/ws`** (one browser at a time; a second connection is refused):

| Direction | Message |
|---|---|
| ↑ text | `{type:"start", presentation:"sample"}` · `next` · `prev` · `{type:"goto", index}` · `pause` · `resume` · `mute` · `unmute` · `end` |
| ↑ binary | raw PCM16 24 kHz frame (960 bytes = 20 ms) → server base64-encodes → `session.input_audio.append` |
| ↓ text | `{type:"state", state, slideIndex, slideCount, paused, muted, sessionId, expiresAt}` · `{type:"slide", index}` · `{type:"transcript", role:"user"\|"assistant", delta, start_ms, end_ms}` · `{type:"usage", seconds, ratio}` · `{type:"closed", reason, seconds}` · `{type:"log", level, message}` · `{type:"error", message, code}` |
| ↓ binary | raw PCM16 from `session.output_audio.delta` (decoded server-side) |

**Presenter state machine** (`presenter.js`, pure logic, injectable clock for tests):

- States: `idle → connecting → presenting ⇄ paused → ending → idle`.
- `start(presentation)`: parse script, build system instructions, `LiveSession.connect()`, on
  `session.started` → `presentSlide(0)`.
- `presentSlide(i)`: emit `slide i` to browser; if notes → `thinking.append`; narration → 1..K
  `instructions.append` (chunks ≤ `chunkChars`, default 1400 ≈ 350 tokens, sentence-boundary
  split, labelled "part k of K, continue immediately"); set `heardOutputSince = null`; arm a
  **nudge** timer (15 s): if no output audio by then, append "Begin presenting slide N now." once.
- On `session.output_audio.delta`: forward binary to browser; `heardOutputSince ??= now`; (re)arm
  the **silence timer** for `advanceSilenceMs`.
- On `session.input_transcript.delta`: forward; (re)arm the silence timer too (audience speaking
  holds the advance).
- Silence timer fires while `presenting` and `heardOutputSince != null` → `presentSlide(i+1)`, or if
  last slide → `instructions.append("That was the last slide. Thank the audience in one sentence
  and stop.")`, then on the next silence → `end()`.
- `next/prev/goto`: clear timers; `presentSlide(target)` (instruction text says "stop the current
  narration and move on").
- `pause`: clear timers; `input_audio.mute`; `instructions.append("Pause: stay silent until told to
  resume.")`; state `paused`. `resume`: `unmute`; `instructions.append("Resume slide N where you
  left off.")`; re-arm nudge; state `presenting`.
- `end`: clear timers; `session.close`; wait for `session.closed` (5 s fallback → terminate);
  emit `closed` to browser; state `idle`. Upstream `close` without `session.closed` → emit
  `closed {reason:"connection_lost"}`; the slide index is kept so Start resumes there.

**Prompt** (`prompt.js`): system instructions = role ("You are the presenter of *<title>*…"),
delivery rules (speak narration nearly verbatim, natural pace, no stage directions, no invented
content, stop and wait after each slide), interruption rule (if the audience speaks: stop, answer
briefly using the context, say a short bridge like "Back to the slide", continue where you left
off), the slide outline (titles), then `--- Background context ---` + `context.md`. Char budget
≈ 12 000 tokens (≈ 48 000 chars); longer context is truncated with a server warning.

**Endpoint resolution** (`config.js`):
```
resolveLiveUrl(endpoint):
  url = new URL(endpoint); scheme http(s)→ws(s)
  if url.pathname includes "/live/sessions"            → keep as given
  else if host ends with .azure.com | .azure.us | .azure.cn → "/openai/v1/live/sessions"
  else                                                  → "/v1/live/sessions"
authHeaders(key, isAzure) → { Authorization: "Bearer <key>", ...(isAzure && { "api-key": "<key>" }) }
```
Env: `UPSTREAM_ENDPOINT`* `UPSTREAM_KEY`* `UPSTREAM_MODEL=gpt-live-1` `UPSTREAM_VOICE=marin`
`PORT=3000` `ADVANCE_SILENCE_MS=2000` `LOG_EVENTS=0` (1 = dump every upstream JSON event).
`*` = required — these two already exist in `.env`.

**Script format** (`script-parser.js`, gray-matter for frontmatter):
```markdown
---
title: Ricoh Project Delivery Overview
deck: decks/ricoh/index.html        # served at /decks/ricoh/index.html
driver: auto                        # optional: auto | showFn | reveal | sections
voice: marin                        # optional; overrides UPSTREAM_VOICE
context: presentations/ricoh-context.md    # optional
advanceSilenceMs: 2000              # optional
chunkChars: 1400                    # optional
---
## Slide 1 — Title
Narration to speak…
> notes: quiet context for Q&A on this slide (optional, may span lines)
## Slide 2
…
```
`## Slide N` headings define order (N must be 1..n contiguous; the title after `—`/`-` is optional);
everything up to the next `## Slide` is narration except `> notes:` blockquotes. Vertical reveal.js
stacks are not addressed (horizontal index only).

**Ricoh script drafting** (`presentations/ricoh-delivery-overview.md`): the per-slide narration is
drafted from each `<section>`'s visible text (headings, bullets, table cells, callouts, metrics),
written as a spoken first-person delivery of ~60–120 s per slide, plus `> notes:` holding the raw
figures for Q&A; `ricoh-context.md` collects the whole deck's text as background. Both are plain
Markdown you edit before rehearsing — the app never reads the deck's text at runtime.

### 4.2 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| A — Backend WebSocket relay, client delegation, app-driven slides (chosen) | Matches PRD ("backend talks to upstream"); key never reaches browser; deterministic slide control; simplest protocol | Audio hops through Node (adds ~50–100 ms); silence heuristic needed for auto-advance | **Chosen** |
| B — Browser WebRTC direct to GPT-Live + sideband from backend | Best latency, browser handles audio codecs | Ephemeral-token endpoint for Live is not yet documented for OpenAI-direct; sideband not on gateways; contradicts PRD | Rejected — for MVP |
| C — Responses delegation with `next_slide`/`goto_slide` tools | Voice-driven navigation; model signals "done" via tool | Second backend model (cost, latency); community reports the model sometimes never delegates; more surface | Rejected — v2 candidate |
| D — Old Realtime API (`gpt-realtime-2.1`) | Mature docs, turn events (`response.done`) exist | Turn-based, not duplex; not what the PRD asks for | Rejected |
| E — Pure TTS per slide (no duplex) | Trivial | Cannot listen/answer; not the requirement | Rejected |

### 4.3 Data / config / API model

Covered in 4.1: `.env` keys, `presentation.md` schema, client⇄server protocol. No persistence.
`GET /api/presentations` → `[{id:"sample", title, slideCount, deck}]` (scans `presentations/*.md`).
`GET /api/presentations/:id` → parsed script (for the UI slide list). Static: `/` → `src/web`,
`/decks/*` → `decks/*`.

### 4.4 Sequence diagram of the hot path

```
[Start click] app.js → ws.send({type:"start", presentation})
  → server/index.js onMessage → presenter.start(id)
    → script-parser.parsePresentation(file) → prompt.buildSystemInstructions()
    → LiveSession.connect(config.resolveLiveUrl(), authHeaders)
      → upstream ws open → send session.start{model, instructions, audio.output.voice, delegation.client}
      ← session.started{session.id, expires_at}          → presenter emits state{presenting} → UI "Connected"
    → presenter.presentSlide(0)
      → emit slide{0}            → app.js → DeckDriver.goto(0) → iframe win.show(0)  (Ricoh/sample; Reveal.slide for reveal decks)
      → LiveSession.thinkingAppend(notes) ; instructionsAppend(chunk 1..K)
      ← session.instructions.appended{client_event_id}   (logged)
      ← session.output_audio.delta                        → presenter: heardOutputSince, arm silence timer
                                                           → binary → app.js → playback worklet → speakers
      ← session.output_transcript.delta                    → transcript{assistant} → UI panel
[mic] capture worklet → 20 ms Int16 → ws binary → server → LiveSession.sendAudio(base64) → session.input_audio.append
      ← session.input_transcript.delta                     → transcript{user}; re-arm silence timer
[silence timer] → presenter.presentSlide(1) …  → last slide → wrap-up instruction → silence → presenter.end()
      → session.close ← session.closed{reason, usage}      → closed{seconds} → UI; state idle
Parallel paths checked:
  - page keyboard (Space/→/←/Esc) and page buttons both call app.js send() → one presenter API;
  - the DECK's own nav (its #prev/#next buttons and its keydown handler) changes the slide without the
    presenter knowing → handled: DeckDriver listens to the iframe `hashchange` (#slide-N) and app.js sends
    {type:"goto", index} when it differs from the presenter's index, so narration follows the deck;
  - exactly one presenter instance and one LiveSession per server; no second audio path.
```

### 4.5 Surface list

| Surface | Change | Task |
|---|---|---|
| `package.json`, `.env.example`, `.gitignore`, `README.md` | new | T1, T10 |
| `src/server/config.js` | new | T2 |
| `src/server/script-parser.js`, `src/server/prompt.js` | new | T3 |
| `presentations/sample.md`, `presentations/sample-context.md`, `decks/sample/index.html` | new | T3 |
| `decks/ricoh/index.html` (copy), `presentations/ricoh-delivery-overview.md`, `presentations/ricoh-context.md` | new | T3b |
| `src/server/live-client.js` | new | T4 |
| `src/server/presenter.js` | new | T5 |
| `src/server/index.js` (HTTP + WS bridge) | new | T6 |
| `src/web/audio-capture.js`, `audio-playback.js`, `worklets/*` | new | T7 |
| `src/web/index.html`, `styles.css`, `app.js`, `deck-driver.js` | new | T8 |
| `test/*.test.js` | new | T2, T3, T5, T9 |

## 5. Impact and risk

| Question | Answer |
|---|---|
| State management — what survives a crash mid-operation? | Nothing needs to: the upstream session dies with the process (billing stops). Presenter keeps `slideIndex` in memory so Start after a drop resumes at the current slide; a server restart resumes at slide 1. |
| Data consistency — orphans, races, double-apply? | Single presenter/session per process; `start` while not `idle` is rejected with an error log. Timers are cleared on every transition (helper `clearTimers()`), so a stale silence timer cannot advance twice. Chunk appends carry `event_id`s `slide-<i>-part-<k>` for error correlation. |
| User experience — root problem or symptom; any surprise? | Root problem (rehearsal now). Surprise: model may paraphrase; auto-advance may fire during a deliberate pause > `advanceSilenceMs` — documented, configurable, Prev is one key away. |
| Backward compatibility — existing data / sessions / configs? | n/a — greenfield. |
| Error recovery — what happens on failure; can it recover? | Upstream `error` → UI log (never fatal unless startup). Upstream close → `closed{connection_lost}` → Start reconnects at the current slide. Browser reload → server drops the upstream session (`session.close`) to stop billing. Missing env → exit 1 with the variable name. |
| Logging & debugging — enough to diagnose in the field? | Server logs every non-audio upstream event type with timestamp and `session.id`; audio deltas counted (logged every 100). `LOG_EVENTS=1` dumps full JSON. UI log panel mirrors errors and state changes. |
| Edge cases — empty, huge, repeated, concurrent, interrupted? | Empty narration slide → shown, no append, advances after `advanceSilenceMs` (timer armed at present time). Huge narration → chunked. Context > budget → truncated + warning. Rapid Next×3 → each call clears timers; only the last slide's appends matter (earlier ones may be spoken briefly — acceptable). Second browser tab → refused with a message. Session `expired` → treated like connection loss. |

**Risks:**
- R1 — `audio` config shape differs on OpenAI-direct (docs show both `audio.output.voice` and `audio.format`) → startup `error` is logged verbatim with the config we sent; T10 live smoke test resolves it with a one-line change. Strict config means we send only documented fields (no `store`).
- R2 — Model does not start narrating (community reports of ignored delegations) → nudge once after 15 s; Next always re-injects; logged.
- R3 — Silence heuristic mis-fires on long model pauses → `advanceSilenceMs` per presentation; prompt says "keep a steady pace, no long pauses"; Prev recovers.
- R4 — Echo (speakers → mic) → `echoCancellation: true`; README recommends headphones; Mute button.
- R5 — Token estimate for the 500-token append limit (chars/4) undercounts dense scripts (Vietnamese, CJK) → conservative 1400-char default; `chunkChars` override; an `error{code:invalid_*}` with `client_event_id: slide-i-part-k` is logged with the hint.
- R6 — Cost while idle → End button, auto-close after the last slide, usage shown live, server closes upstream when the browser disconnects.
- R7 — Deck not driveable (no `show`, no `Reveal`, no `section.slide`) → DeckDriver logs "no adapter matched" and the UI still works (narration continues; you flip slides by hand); README requires decks under `decks/` (same-origin).
- R8 — Buffered output audio keeps playing ~200 ms after an interruption → acceptable for MVP; noted for v2 (flush on user speech).
- R9 — The deck's own keyboard handler consumes Space/arrows when the iframe has focus → iframe `tabindex=-1`, parent re-focused after each `goto`, and `hashchange` sync makes deck-side navigation harmless anyway.
- R10 — Auto-drafted Ricoh narration may not match how you want to say it → it is plain Markdown; the runbook's first step is "read and edit `presentations/ricoh-delivery-overview.md`"; the app hot-reloads the script on every Start.
- R11 — Foundry deployment name ≠ `gpt-live-1` → startup `error` names the model; set `UPSTREAM_MODEL` in `.env`.

**Rollback:** greenfield — stop the process. No external state.

## 6. Tasks

### T1 — Scaffold (AC1)
- **Files:** `package.json`, `.gitignore`, `.env.example`, `README.md` (skeleton), `src/server/index.js` (placeholder that loads config and exits on error)
- **Change:** `npm init` ESM; `npm i express ws dotenv gray-matter`; scripts `start: node src/server/index.js`, `dev: node --watch src/server/index.js`, `test: node --test test/`; `.gitignore` excludes `.env`, `node_modules`, `hooks.log`; `.env.example` lists all keys with placeholders.
- **Verify:** `npm test` runs with 0 tests; `npm start` with `UPSTREAM_KEY` blanked exits 1 printing `Missing required env: UPSTREAM_KEY`.
- **Test that dies if this breaks:** `test/config.test.js › throws naming the missing variable` (T2).

### T2 — Config & endpoint resolution (AC1, AC10)
- **Files:** `src/server/config.js`, `test/config.test.js`
- **Change:** `loadConfig(env)` validates `UPSTREAM_ENDPOINT` / `UPSTREAM_KEY`, applies defaults (`UPSTREAM_MODEL`, `UPSTREAM_VOICE`, `PORT`, `ADVANCE_SILENCE_MS`, `LOG_EVENTS`); `resolveLiveUrl(endpoint)`; `authHeaders(key, url)`; `isAzureHost(host)` (matches `.azure.com`, `.azure.us`, `.azure.cn`, hence `*.services.ai.azure.com`).
- **Verify:** `node --test test/config.test.js` — cases: `https://x.services.ai.azure.com` (no path, with and without trailing slash) → `wss://x.services.ai.azure.com/openai/v1/live/sessions` + Bearer + `api-key`; `https://x.openai.azure.com` same; `https://api.openai.com` → `wss://api.openai.com/v1/live/sessions` + Bearer only; gateway host; explicit `/v1/live/sessions` path kept; `http://` → `ws://`; missing key throws naming it; defaults applied.
- **Test that dies if this breaks:** the above file.

### T3 — Script parser, prompt builder, sample presentation + sample deck (AC9, AC10)
- **Files:** `src/server/script-parser.js`, `src/server/prompt.js`, `presentations/sample.md`, `presentations/sample-context.md`, `decks/sample/index.html`, `test/script-parser.test.js`, `test/prompt.test.js`
- **Change:** `parsePresentation(markdown, {basePath})` → `{meta:{title,deck,driver,voice,context,advanceSilenceMs,chunkChars}, slides:[{index,title,narration,notes}]}`; validation errors name the slide; `chunkText(text, maxChars)` splits on sentence ends (`. ! ? \n`), never mid-word, and `chunks.join(' ')` round-trips the text modulo whitespace; `buildSystemInstructions()` / `buildSlideInstruction()` / `buildResumeInstruction()` / `buildWrapUpInstruction()` pure functions; context truncation with warning callback. Sample: 3-slide plain HTML deck using the same `show(n)` + `#slide-N` pattern as the Ricoh deck; `sample.md` with slide 2 narration ≈ 3 000 chars (forces chunking) and notes on one slide.
- **Verify:** `node --test test/script-parser.test.js test/prompt.test.js`.
- **Test that dies if this breaks:** `script-parser.test.js › chunks long narration at sentence boundaries ≤ maxChars`; `› extracts notes blockquote`; `› rejects non-contiguous slide numbers`; `prompt.test.js › slide instruction contains narration verbatim and part k/K label`.

### T3b — Ricoh deck + drafted narration script (AC3–AC6 run against this)
- **Files:** `decks/ricoh/index.html` (verbatim copy of `D:\sources\ricoh\management\plan\Ricoh_Project_Delivery_Overview_Slide_Deck_v1.0.html`), `presentations/ricoh-delivery-overview.md`, `presentations/ricoh-context.md`
- **Change:** copy the deck unchanged; extract each of the 11 sections' visible text (Node script run once, not shipped) and write: (a) the script — frontmatter `title/deck/context`, `## Slide 1 — Project Delivery` … `## Slide 11 — Questions & Alignment`, each with a spoken first-person narration (~60–120 s, plain sentences, no bullet symbols) and `> notes:` with the slide's figures/tables verbatim; (b) `ricoh-context.md` — the full deck text grouped by slide + a short "how to answer" preamble (team, products, capacity model, roadmap). **You review/edit both files before rehearsing** (R10).
- **Verify:** `node -e` parse of `ricoh-delivery-overview.md` reports 11 slides, every narration ≤ 3 chunks, no validation error; `GET /decks/ricoh/index.html` renders in the iframe with `show` detected (log line `deck adapter: showFn, 11 slides`) and the count matches the script (no mismatch warning).
- **Test that dies if this breaks:** `script-parser.test.js › parses the ricoh presentation into 11 contiguous slides` (reads the real file).

### T4 — LiveSession upstream client (AC2, AC8)
- **Files:** `src/server/live-client.js`
- **Change:** `class LiveSession extends EventEmitter` — `connect({url, headers, session})` opens `ws`, sends `session.start`, resolves on `session.started` (rejects on `error` or close before start, 10 s handshake timeout); `sendAudio(buffer)` → base64 `session.input_audio.append` (drops odd trailing byte, never sends empty); `appendInstructions/appendThinking/appendCommentary(content, {eventId, delegationId=null})`; `mute()/unmute()`; `close()` → `session.close`, resolves on `session.closed` or 5 s fallback `terminate()`. Emits `started`, `audio(Buffer, start_ms, end_ms)`, `transcript(role, delta, start_ms, end_ms)`, `appended(kind, clientEventId)`, `usage`, `delegation`, `error`, `closed(reason, seconds)`. Logs every non-audio event type; `LOG_EVENTS=1` dumps JSON. No presentation logic here.
- **Verify:** exercised by the T9 fake server; manual: `node -e` snippet connecting to the fake server prints `started` and `closed`.
- **Test that dies if this breaks:** `test/integration.test.js › start → session.started → slide 0` (T9).

### T5 — Presenter state machine (AC4, AC5, AC6, AC7, AC8, AC10)
- **Files:** `src/server/presenter.js`, `test/presenter.test.js`
- **Change:** `class Presenter extends EventEmitter` with injected `{createSession, loadPresentation, now, setTimeout, clearTimeout, log}`; implements the state machine in 4.1 exactly (states, `presentSlide`, silence timer, nudge, next/prev/goto, pause/resume, end, connection loss, wrap-up on last slide). Emits `state`, `slide`, `audio`, `transcript`, `usage`, `closed`, `log`, `error` for the bridge.
- **Verify:** `node --test test/presenter.test.js` using `mock.timers` and a fake session (EventEmitter with recorded `sent[]`).
- **Test that dies if this breaks:** `› started → presents slide 0 (thinking then instructions appends)`; `› output audio then 2 s silence → slide 1`; `› input transcript during silence window delays the advance`; `› next() clears timer and presents slide 1 immediately`; `› pause sends mute and blocks advance; resume unmutes and re-arms`; `› last slide silence → wrap-up then close`; `› no output audio for 15 s → one nudge`; `› upstream closed → state idle, slideIndex kept`; `› long narration → K appends with part labels`.

### T6 — HTTP server and WebSocket bridge (AC2)
- **Files:** `src/server/index.js`
- **Change:** Express: static `src/web` at `/`, `decks/` at `/decks`, `GET /api/presentations`, `GET /api/presentations/:id`; `WebSocketServer({server, path:"/ws"})`: one client at a time (second gets `{type:"error"}` + close 1013); text frames → presenter methods; binary frames → `presenter.sendAudio`; presenter events → text/binary frames; client close → `presenter.end()`; `SIGINT` → `presenter.end()` then exit. Startup log line `presenter-ai listening on http://localhost:PORT (upstream: <wss url without key>)`.
- **Verify:** `curl localhost:3000/api/presentations` returns the sample; `wscat -c ws://localhost:3000/ws` then `{"type":"start","presentation":"sample"}` against the fake server → receives `state`, `slide`, binary frames.
- **Test that dies if this breaks:** `test/integration.test.js` (T9) — boots this module on an ephemeral port.

### T7 — Browser audio capture and playback (AC3, AC6)
- **Files:** `src/web/audio-capture.js`, `src/web/audio-playback.js`, `src/web/worklets/capture-processor.js`, `src/web/worklets/playback-processor.js`
- **Change:** `AudioIO.start()`: `getUserMedia({audio:{channelCount:1, echoCancellation:true, noiseSuppression:true, autoGainControl:true}})`, `new AudioContext({sampleRate:24000})`, capture worklet accumulates 480 samples → Int16 → `port.postMessage(ArrayBuffer)` → `onFrame(buf)`; playback worklet keeps a Float32 ring buffer (≈ 5 s), `enqueue(Int16 ArrayBuffer)`, outputs silence when empty, exposes `bufferedMs`; `setMuted(bool)` stops posting frames (keeps the stream); `flush()`; `stop()` closes context/tracks. Fallback if `sampleRate: 24000` is unsupported: worklet-side linear resample from `sampleRate` → 24 000.
- **Verify:** dev loopback toggle (`?loopback=1` routes captured frames straight to playback; you hear yourself with ~100 ms delay) plus a mic-level meter in the UI. Loopback is ~5 lines in `app.js`, kept.
- **Test that dies if this breaks:** none automated (browser APIs) — covered by runbook steps 4–5.

### T8 — Presenter UI and deck driver (AC3, AC4, AC5, AC7)
- **Files:** `src/web/index.html`, `src/web/styles.css`, `src/web/app.js`, `src/web/deck-driver.js`
- **Change:** layout: deck iframe (16:9, ~70 % width, `tabindex="-1"`) + right panel: presentation dropdown (from `/api/presentations`), buttons Start / Pause-Resume / Prev / Next / Mute / End, status (state, slide i/n, session id, expiry, usage seconds), transcript (user/assistant, grouped by role and gaps > 1 s), log. Keyboard when focus is not in an input: Space = Pause/Resume, → = Next, ← = Prev, M = Mute, Esc = End; parent window re-focused after each `goto`. `DeckDriver(iframe, {driver})`: `load(url)` → on iframe `load` picks the adapter (`showFn` → `reveal` → `sections`, or the pinned one), logs `deck adapter: <name>, <count> slides`, warns if count ≠ script slide count; `goto(i)`; `count()`; subscribes to the iframe window's `hashchange` and calls `onExternalNavigate(index)` when `#slide-N` differs from the current index (app.js → `{type:"goto", index}`).
- **Verify:** open `/`, pick **ricoh-delivery-overview**: the Ricoh deck renders, log shows `deck adapter: showFn, 11 slides`; with the fake upstream (`UPSTREAM_ENDPOINT=ws://localhost:<fake>`), Start → slide 1 → after fake audio + silence → slide 2; → key jumps; clicking the deck's own › button also advances and the presenter follows (log `external navigate → 3`); Pause/Resume toggles; End shows seconds. Repeat with **sample** (3 slides).
- **Test that dies if this breaks:** runbook steps 3–9; `integration.test.js` covers the server side of each button message (including `goto`).

### T9 — Integration test with a fake GPT-Live server (AC2, AC4, AC5, AC8)
- **Files:** `test/fake-live-server.js`, `test/integration.test.js`
- **Change:** fake upstream `ws` server: asserts the `Authorization` and `api-key` headers are present; on `session.start` → `session.started`; on `session.instructions.append` → 3× `session.output_audio.delta` (960 zero bytes each, 20 ms apart) + one `session.output_transcript.delta` + `session.instructions.appended`; on `session.input_audio.append` → count; on `session.input_audio.mute` → `.muted`; on `session.close` → `session.closed{reason:"close_requested", usage:{seconds:7}}`. Test boots `src/server/index.js` with `UPSTREAM_ENDPOINT=ws://127.0.0.1:<fake>/v1/live/sessions`, `UPSTREAM_KEY=test`, `ADVANCE_SILENCE_MS=200`, connects a `ws` client to `/ws`, sends `start` (sample), asserts: `state{presenting}` + `slide{0}` → binary audio frame → `slide{1}` within 1 s; sends `next` → `slide{2}`; sends `{type:"goto", index:0}` → `slide{0}`; sends 960 bytes binary → fake received one `input_audio.append` with even-length base64 payload; sends `pause` → fake got `mute`; sends `end` → `closed{seconds:7}` and fake got `session.close`. Records every event the fake received for the wiring audit.
- **Verify:** `npm test` green.
- **Test that dies if this breaks:** itself — it is the chain test for T4+T5+T6.

### T10 — README runbook, wiring audit, live smoke test (all ACs)
- **Files:** `README.md`, `.env.example` (final), `docs/progress/001-work-log.md`
- **Change:** README: prerequisites, `.env` keys (`UPSTREAM_*`), how to add your own deck (`decks/<name>/`, supported deck types) and script, keyboard map, cost note, troubleshooting (startup `error` → check R1 shape / R11 model name, echo → headphones, no narration → nudge/Next, deck not detected → `driver:` frontmatter). **Wiring audit:** for every emitter → consumer edge in §4.4, confirm the log line at each hop appears in one live run (`session.started`, `deck adapter: showFn, 11 slides`, `slide 0`, `instructions.appended slide-0-part-1`, first `output_audio.delta`, `advance → 1`, `external navigate → N`, `session.close`, `session.closed`) and that `GET /api/presentations`, `/decks/ricoh/index.html`, `/decks/sample/index.html`, `/ws` are all reachable. **Live smoke test** against the real Foundry endpoint already in `.env`: run the runbook; record results in the work log; if R1/R11 bite, apply the one-line fix and re-run.
- **Verify:** runbook table below fully checked; work log lists each AC with pass/fail and the observed `usage.seconds`.
- **Test that dies if this breaks:** n/a (documentation + manual).

## 7. Test strategy

- **Unit (`node --test`, no extra deps):** `config` (URL/auth/defaults/missing), `script-parser`
  (frontmatter, sections, notes, contiguity, chunking round-trip), `prompt` (verbatim narration,
  part labels, truncation warning), `presenter` (every transition with mock timers). Deliberately
  not unit-tested: browser audio worklets, DOM code, `live-client.js` in isolation (covered by
  integration).
- **Integration:** `test/integration.test.js` — real Express + real `ws` bridge + real
  `LiveSession` + real `Presenter`, only the upstream GPT-Live faked. One flow per user action
  (start/auto-advance, next, audio relay, pause, end).
- **Manual runbook** (executed in T10 against the real GPT-Live endpoint; unchecked items block "done"):

| # | Step | Expected |
|---|---|---|
| 0 | Read and edit `presentations/ricoh-delivery-overview.md` (narration) and `ricoh-context.md` | Text says what you want to say; no `## Slide` removed or renumbered |
| 1 | `.env` already has `UPSTREAM_ENDPOINT` / `UPSTREAM_KEY`; add `UPSTREAM_MODEL=<deployment>` only if the Foundry deployment is not named `gpt-live-1`; `npm install`; `npm start` | Log: `listening on http://localhost:3000 (upstream: wss://<host>/openai/v1/live/sessions, model: …)` |
| 2 | Temporarily blank `UPSTREAM_KEY`; `npm start` | Exit 1: `Missing required env: UPSTREAM_KEY` (AC1) — restore it afterwards |
| 3 | Open `http://localhost:3000`, allow mic, pick "ricoh-delivery-overview" | Ricoh cover slide visible; log `deck adapter: showFn, 11 slides`; status `idle`, `slide 1/11` |
| 4 | Click Start | ≤ 5 s: status `presenting`, session id shown; server log `session.started` (AC2) |
| 5 | Listen | Slide 1 narration audible; transcript panel fills with assistant text (AC3) |
| 6 | Wait, hands off | Deck advances to slide 2 ≈ 2 s after narration ends; log `advance → 2` (AC4) |
| 7 | While slide 2 is narrated press → | Slide 3 (Team Org Chart) appears immediately; its narration starts (AC5) |
| 8 | Say "Can you repeat the key point?" mid-narration | Model stops, answers, then continues the slide; transcript shows your question (AC6) |
| 9 | Press Space | Status `paused`, mic muted indicator, no advance for 30 s; Space again → resumes narration (AC7) |
| 10 | Click the deck's own › button once | Deck and presenter move together; log `external navigate → N` and new narration starts |
| 11 | Let it run to slide 11 | Wrap-up sentence spoken; status `idle`; UI shows `usage: N s`; server log `session.closed close_requested` (AC8) |
| 12 | Check server log for a chunked slide | ≥ 2 `instructions.appended slide-<i>-part-k` acks, no `error` (AC9) |
| 13 | `npm test` | All unit + integration tests pass (AC10) |
| 14 | Close the browser tab while presenting | Server log `session.close` within 1 s (billing stops) |

## 8. Rollout / phasing

Single MVP drop; tasks T1–T9 are mergeable in order (each leaves `npm test` green), T10 gates
"done". v2 candidates recorded, not planned: voice-driven navigation via Responses delegation
tools; WebRTC transport; PPTX → HTML import; flush playback on user speech.

## 9. Open questions

None. Deferred by design: exact `audio` config shape on OpenAI-direct (R1 — resolved empirically in
T10 with the user's real endpoint; cannot be resolved from public docs today).

## 10. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-21 | Requirement brief confirmed (G1) | 1 round, 4 questions; all recommended options chosen |
| 2026-09-21 | Brief amended | user supplied the real deck (custom HTML, not reveal.js) and `.env` names `UPSTREAM_ENDPOINT` / `UPSTREAM_KEY`; driver auto-detect + T3b added |
| 2026-09-21 | Plan approved (G2) | approved without external review (user in a hurry to rehearse) |
| 2026-09-21 | Implemented (T1–T10) | 52/52 tests; live-verified on Azure + OpenAI and in the user's Chrome. Deviations and live-service findings in `docs/progress/001-work-log.md` (port 47913, `UPSTREAM_*`/`FALLBACK_OPENAI_*` env, silence pump, RMS-based advance, 3 s window, parts gated by speech). |
