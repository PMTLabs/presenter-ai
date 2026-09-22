# 002 — Phase 0a: .NET 10 core port, API bridge and React web app (parity with the Node MVP)

**Date:** 2026-09-21
**Status:** Approved (2026-09-21)
**Size:** L
**Area:** new `src/PresenterAi.*`, `tests/`, `web/`, `docker-compose.yml`, `.github/workflows`; existing `src/` (Node) untouched until plan 004
**Requirement brief confirmed:** 2026-09-21 (G1) — one brief covers plans 002, 003 (identity/persistence/Redis) and 004 (admin site, retire Node)
**Conventions:** `docs/reference/001-api-and-code-conventions.md` governs every non-frozen endpoint, error body,
JSON rule and the generated client (adopted 2026-09-21, after G2 — see approval log).
**Design source:** `docs/research/002-phase-0-platform-architecture.md` (§2.1–2.3, §2.7, §3 steps 0.1–0.4 — except
the SSE plumbing of 0.3, deferred to phase 1 where the first job exists, and the Sessions screen of 0.4, deferred to
plan 003 where session rows exist)

---

## 1. Goal

The presenter runs on the platform it will ship on — a .NET 10 API and a React 19/TypeScript web app, in a git
repository with Docker and CI — and presents the Ricoh deck exactly as the Node MVP does today, proven by the
ported test suite, live smoke on both upstreams and a real-Chrome run. Nothing user-visible is new; the value is
that plans 003/004 and the pipeline phases build on this once.

## 2. Requirement (as confirmed at G1) — the part this plan delivers

- **Problem:** the MVP is a single-user Node prototype (files as database, `.env` keys, no login, no operator
  tooling); the automated pipeline would otherwise be built twice.
- **In scope (plan 002 = steps 0.1–0.4):** git init + first commit + push to
  `https://github.com/PMTLabs/presenter-ai.git` (git-flow `master`/`develop`, feature branches); solution
  `PresenterAi.{Domain,Application,Infrastructure,Api,Contracts}` + tests; port of script parser/writer, prompt
  builder, chunking, RMS, `LiveSession` (silence pump), `Presenter` (on `TimeProvider`) with every Node test
  scenario ported and an in-process fake GPT-Live server; byte-identical `/ws` protocol; `web/` bun workspaces
  `shared/app/admin` (React 19 + Vite + TS + Tailwind, InkSpoke UI kit copied), presenter page + library; Dev
  sign-in scheme; Dockerfiles + compose (Postgres/pgvector, Redis) + GitHub Actions; file-backed repository as a
  temporary adapter; CLI replacements for `scripts/live-smoke.mjs` and `scripts/headless-run.mjs`.
- **Out of scope (this plan):** OAuth (003), Postgres/Redis usage (003 — the containers are only provisioned
  here), metering/quotas (003), admin site (004), deleting the Node tree (004), Stripe, BYO keys, knowledge base,
  pipeline features, Kubernetes, any change to GPT-Live protocol behaviour; **explicitly deferred from the design
  source:** SSE progress plumbing (phase 1, with the first generation job) and the Sessions screen (003, with the
  `sessions` table).
- **Users and surfaces:** presenters via `web/app` (library, presenter page); developers via `presenter-cli`
  (smoke, run); API on 47913, Vite dev on 47914.
- **Behaviour:** happy path unchanged from the MVP (Start → narration → auto-advance → keys → deck buttons → mic
  after permission → usage → `session.closed`). Failure/edge for this plan: upstream primary fails to start →
  fallback tried in order (`MAX_UPSTREAM_ATTEMPTS` semantics kept); a second browser client → closed with 1013
  `busy`; startup `error` from the service → socket closed, presenter back to idle with the message; mic denied →
  listen-only session continues; API restart → the browser reconnects with backoff and shows idle.
- **Constraints and assumptions:** Node stays runnable and is the behavioural reference until the parity gate
  (AC2–AC5) passes; no MediatR; `TreatWarningsAsErrors`; secrets never committed (`.gitignore` covers `.env`,
  `appsettings.Local.json`, `data/`, `web/**/.env*`); live verification costs ~$2 of session time in this plan;
  the first push to `master` (initial commit) is authorised by the brief; deployment target is VM + compose.
- **Acceptance criteria (from the brief, those owned by this plan):**
  1. AC1 — `git log` on `PMTLabs/presenter-ai` shows the initial commit; `.env` is not in it.
  2. AC2 — `dotnet test` passes with the ported scenario set (≥ the 52 Node cases) incl. the fake GPT-Live server.
  3. AC3 — `presenter-cli smoke --provider azure|openai` speaks one sentence and closes with `usage.seconds` on both.
  4. AC4 — the Node web page pointed at the .NET API presents Ricoh slides 1–5 (interim parity check).
  5. AC5 — the React app presents the Ricoh deck end to end with AC1–AC10 of plan 001 re-verified in real Chrome.
  6. (AC13 partial) CI is green on `develop`.
- **Decisions made:** three plans / one brief; public SaaS; git init here + push to PMTLabs; Node retired after
  parity; Clean Architecture 5 projects; Google + Microsoft via copied `SsoService` (003); metering + quotas now,
  Stripe later (003/005); React 19 + Vite + TS + bun + Tailwind + InkSpoke UI kit; admin core + operations (004);
  platform keys only; pgvector extension + schema only (003); VM + compose, live checks per sub-step.

## 3. Current state (as-built)

Node 22 prototype; `node --test` reports 52 passing cases (51 top-level `test()` calls + 1 subtest), live-verified
2026-09-21 (`docs/progress/001-work-log.md`).

- `src/server/presenter.js:32` — `class Presenter extends EventEmitter`; constants `NUDGE_MS=15000`,
  `WRAP_UP_FALLBACK_MS=15000`, `MAX_UPSTREAM_ATTEMPTS=4`, `PART_GAP_MS=2500`, `partGapFor()`,
  `isNormalClose=/request/` (`:23-30`); `start(id,{fromIndex})` `:130` (upstream attempt loop via injected
  `createSession(opts, attempt)`), `presentSlide(i,{interrupt})` `:204` (notes → `appendThinking`, narration
  chunks sent one part at a time `:220-253`), auto-advance `:306-318`, nudge `:321-329`, wrap-up `:332-349`,
  manual `next/prev/goto` `:362-391`, `pause/resume` `:394-418`, `snapshot()` `:100`. Timers are `setTimeout`;
  tests drive them with `mock.timers`. Every one of these producers calls the session's append/close methods
  directly — the Node event loop is what serialises them.
- `src/server/live-client.js:27` — `class LiveSession`; silence pump `PUMP_INTERVAL_MS=20`, `PUMP_SLACK_MS=120`,
  `SILENCE_FRAME` (960 bytes) `:23-25`, `#pump` `setInterval` `:73`, `silenceMs` getter `:65`; events
  `started/audio/transcript/appended/usage/delegation/upstream-error/closed`; startup `error` → reject, close
  socket, `#finish('startup_error')`; `close()` with 5 s terminate fallback.
- `src/server/audio-util.js:4,21,23` — `pcmRms(buffer, stride)`, `VOICE_RMS_THRESHOLD=120`, `isVoiced`.
- `src/server/script-parser.js:5,9,16,113` — `SLIDE_HEADING` regex, `DEFAULT_CHUNK_CHARS=1400`,
  `parsePresentation(markdown,{id})` (contiguity validation, `> notes:` split), `chunkText()`.
- `src/server/prompt.js:3,5,49,64,68,72,76,80` — `CONTEXT_CHAR_BUDGET=48000`, `buildSystemInstructions`
  (delivery + audience rules, outline, context), `buildSlideInstruction`, `buildNotesContext`,
  `buildResumeInstruction`, `buildPauseInstruction`, `buildNudgeInstruction`, `buildWrapUpInstruction`.
- `src/server/config.js:19,38,56-80` — `resolveLiveUrl` (Azure host → `/openai/v1/live/sessions`, else
  `/v1/live/sessions`, explicit `/live/sessions` kept, http→ws), `authHeaders` (Bearer + `api-key` on Azure),
  `upstreams: [primary, fallback?]` from `UPSTREAM_*` / `FALLBACK_OPENAI_*`.
- `src/server/index.js:26-35,39-50,58-80` — `GET /api/presentations` rows `{id,title,slideCount,deck,driver}` or
  `{id,title,error}`; `GET /api/presentations/:id` → `{id, meta, slides, hasContext}` (400 on parse error, 404 on
  ENOENT; id must match `^[\w.-]+$`; context path must stay inside the project); `GET /api/config` →
  `{model, voice, advanceSilenceMs}`; static `/decks` (404 text `:78`), static `src/web` with `Cache-Control: no-cache` `:80`;
  `attachBridge` `:111` — single client (second → `1013 busy` `:134`), server→client messages
  `state|slide|transcript|usage|closed|log|error|pong` `:122-129,202`, client→server commands
  `start{presentation, fromIndex?}` (`presentation` defaults to `sample` `:172`) `|next|prev|goto{index}|pause|resume|mute|unmute|end|ping`
  `:171-201`, binary frames → `presenter.sendAudio`,
  client close → `presenter.end()`. `createPresenter` `:84` builds `createSession` per `config.upstreams[attempt]`.
- `src/web/app.js:109-157` — WS client (`binaryType='arraybuffer'`, reconnect backoff), `handleMessage`;
  `startAudio()` (device-rate `AudioContext`, `resumeWithTimeout`, capture start **not awaited** — finding 8);
  `focusKeys` `:243`, key handling Space/→/←/M/S/Esc `:330-359`, Start sends `{type:'start', presentation}` `:305`,
  buttons/deck navigation send `next`/`goto` `:60,322,339`; `window.__presenterDebug()` `:371`. `src/web/deck-driver.js:4,35` —
  `ADAPTERS showFn|reveal|sections`, `DeckDriver.load/goto/count`, hashchange → `onExternalNavigate`.
  `src/web/audio-playback.js:3,46,52`, `src/web/audio-capture.js:3`, `src/web/worklets/*.js` (capture resamples
  to 24 kHz / 480-sample frames; playback ring buffer 8 s, resample from 24 kHz).
- Tests: `test/config.test.js` (11), `script-parser` (9, incl. the 11-slide Ricoh file), `prompt` (7),
  `audio-util` (1 file, exports `voicedFrame`), `presenter` (18, `FakeSession` speak/silence/hear/drop),
  `integration` (5, real server + `test/fake-live-server.js` with `voicedDelta`, `silent` flag, terminates
  clients on close; `upstreamReceived(pred)` polling helper). `package.json:13` `node --test "test/*.test.js"`.
- Scripts: `scripts/live-smoke.mjs [--fallback]`, `scripts/headless-run.mjs [id] [--max-seconds N]
  [--stop-after-slide N]` (speech/silence bar), `scripts/browser-e2e.mjs` (CDP, never completed a run).
- Fixtures: `decks/ricoh/index.html` (535 KB, `show(n)`), `decks/sample/index.html`,
  `presentations/{ricoh-delivery-overview,ricoh-context,sample,sample-context}.md`.
- Environment: not a git repo; `.env` holds `UPSTREAM_ENDPOINT`, `UPSTREAM_KEY`, `FALLBACK_OPENAI_KEY` (Azure
  10 RPM / 10k TPM); port 3000 belongs to another project (pid 55348) — never touch; toolchain present: .NET SDK
  10.0.401, bun 1.3.14, Node 22.23.2, Docker 29.6.2, `gh` 2.83; GitHub `PMTLabs/presenter-ai` exists, private,
  empty. Windows quirks: `AudioContext({sampleRate:24000})` hangs (use device rate); Chrome MCP DPR 1.5 (JS clicks).
- InkSpoke conventions to copy (read from `D:\sources\work\inkspoke\inkspoke-main`): `Directory.Build.props`
  (Nullable, ImplicitUsings, `TreatWarningsAsErrors`), xUnit + **Moq 4.20.72** + FluentAssertions
  (`tests/InkSpoke.Api.Tests/InkSpoke.Api.Tests.csproj:34`), `.github/workflows/test.yml` (pinned bun 1.3.14), `web/` bun workspaces
  (`shared`, `site`, `admin`; React 18.3, Vite 6, Tailwind 3.4, TanStack Query 5, Zustand 5, react-router 6,
  vitest), `web/admin/src/components/{layout/AdminLayout.tsx,layout/AdminRoute.tsx,ui/ThemeToggle.tsx}` (the UI
  kit is small), `InkSpoke.Api/Program.cs` endpoint-group wiring, `docker-compose.yml` (Postgres 16, Redis 7,
  MinIO, healthchecks).

## 4. Design

### 4.1 Approach

A **port, not a redesign**: every module above gets a C# or TypeScript counterpart with the same constants,
the same event names and the same wire protocol, and every Node test scenario is re-expressed against it before
any behaviour is touched. The one structural change is that timers go through `TimeProvider` so the state-machine
tests use `FakeTimeProvider` instead of `mock.timers`.

Projects (`docs/research/002` §2.1): `PresenterAi.Domain` (Presentation, Slide, value objects — small),
`PresenterAi.Application` (`Scripts/ScriptParser`, `Scripts/ScriptWriter`, `Scripts/TextChunker`,
`Presenting/PromptBuilder`, `Presenting/Presenter`, `Presenting/AudioLevel`, ports `ILiveSession`,
`ILiveSessionFactory`, `IPresentationRepository`, `IDeckStore`), `PresenterAi.Infrastructure`
(`Live/LiveSession` on `ClientWebSocket`, `Live/UpstreamOptions` + `LiveUrlResolver`, `Content/FilePresentationRepository`,
`Content/FileDeckStore`), `PresenterAi.Api` (minimal APIs, `/ws` bridge `PresenterBridge`, `Auth/DevAuthHandler`,
static SPA + `/decks`), `PresenterAi.Contracts` (DTOs: `PresentationSummary`, `PresentationDetail`, `ConfigDto`,
bridge message records). A `PresenterAi.Cli` console project provides `smoke` and `run`.

Web: bun workspaces `web/shared` (generated OpenAPI client, `authStore`, theme, `ui/`), `web/app` (routes
`/`, `/present/:id`; presenter store in Zustand; audio + deck modules ported to TS), `web/admin` (placeholder
`AdminLayout` + `AdminRoute` copied, one "coming in 004" page — so the workspace shape is final now).

Interim parity trick: `PresenterAi.Api` can serve the **Node** `src/web` folder as its static root when
`Content:WebRoot` points at it (AC4) — the protocol is identical, so the old page is the first client of the
new API before any React code exists.

### 4.2 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| A — port module-for-module with the Node tests as the oracle, `/ws` protocol frozen | behaviour provable; old page usable as a parity client; smallest surprise | carries a few Node-isms (event names) into C# | **Chosen** |
| B — redesign the presenter around SignalR + a new message schema | idiomatic .NET, typed hub | rewrites the client too; binary audio over SignalR needs MessagePack; no parity client; timing behaviour re-discovered from scratch | Rejected — throws away the verified part |
| C — keep the Node backend, only build the React app now | fastest visible progress | phase 0's point is the .NET platform; auth/DB/admin would land on Node and be redone | Rejected — contradicts the brief |
| D — single `PresenterAi.Api` project with folders | least ceremony | the brief chose 5 projects; the pipeline phases add Application slices that need the boundary | Rejected at G1 |

### 4.3 Data / config / API model

Configuration (`appsettings.json` + `appsettings.Example.json`, user-secrets in dev, env vars in containers):

```
Upstream:Endpoint, Upstream:Key, Upstream:Model (gpt-live-1), Upstream:Voice (marin)
Upstream:Fallback:Endpoint (https://api.openai.com), Upstream:Fallback:Key, Upstream:Fallback:Model
Presenter:AdvanceSilenceMs (3000), Presenter:LogEvents (false)
Content:RootDir (repo root in dev: presentations/ and decks/ live there), Content:WebRoot (web/app/dist | src/web for AC4)
Auth:Dev:Enabled (true only in Development), Auth:Dev:UserId, Auth:Dev:Email
Serilog:*; Kestrel port 47913
```

`.env` is not read by .NET; a one-line note in README maps the names. The `.env` file stays for the Node MVP
until plan 004.

HTTP — **exact Node shapes**, typed in `Contracts` and golden-tested against JSON captured from the Node
server (`tests/PresenterAi.Api.Tests/Golden/*.json`):

| Endpoint | Shape |
|---|---|
| `GET /api/presentations` | unchanged: `[{id,title,slideCount,deck,driver}]`, or `{id,title,error}` per unparsable file |
| `GET /api/presentations/{id}` | unchanged: `{id, meta:{id,title,deck,driver,voice,context,advanceSilenceMs,chunkChars}, slides:[{index,number,title,narration,notes}], hasContext}`; 404 ENOENT, 400 parse error / invalid id |
| `GET /api/config` | unchanged: `{model, voice, advanceSilenceMs}` — no new fields, no keys |
| `GET /decks/**` | unchanged: static; 404 text `Deck not found: <path>. Put your deck under decks/<name>/.` |
| `GET /health` | new: 200 `{status:"ok"}` |
| `GET /openapi/v1.json` | new: .NET 10 `AddOpenApi()` / `MapOpenApi()` — the source of the generated TS client |
| SPA fallback | new: `MapFallbackToFile("index.html")` so `/present/:id` deep links load the React app; `/api/*`, `/ws`, `/decks/*` excluded |

All under the Dev auth scheme (anonymous when `Auth:Dev:Enabled`); plan 003 swaps the scheme. The frozen trio
keeps its Node error bodies (`{error: message}`); `/health`, `/openapi/v1.json`, the fallback and **every later
endpoint** use RFC 9457 Problem Details + `code` per the conventions document §5–§6 (`internal.error`,
`validation.failed`, …), produced by one exception handler registered in T2.

WebSocket `/ws` — identical to `index.js:122-205`, plus the optional `{"type":"auth","ticket":"…"}` first frame
from conventions §8 (accepted and ignored under the Dev scheme; sent by the React client, not by the Node page). Canonical frames, frozen; `BridgeContractTests` replays each
one and T11 replays them with the real Node page:

```
client → server (text)   {"type":"start","presentation":"ricoh-delivery-overview","fromIndex":0}
                         {"type":"next"} {"type":"prev"} {"type":"goto","index":3} {"type":"pause"} {"type":"resume"}
                         {"type":"mute"} {"type":"unmute"} {"type":"end"} {"type":"ping"}
client → server (binary) PCM16 mono 24 kHz, 20 ms = 960 bytes per frame
server → client (text)   {"type":"state", ...snapshot fields as presenter.js snapshot()}  {"type":"slide","index":0}
                         {"type":"transcript","role":"assistant|user","delta":"…","start_ms":null,"end_ms":null}
                         {"type":"usage", ...}  {"type":"closed","reason":"client_request", ...}  {"type":"log","level":"info","message":"…"}
                         {"type":"error","message":"…","code":"busy|protocol|start|null"}  {"type":"pong"}
server → client (binary) PCM16 mono 24 kHz output audio, as received from the upstream
second client            {"type":"error","code":"busy",...} then close 1013 "busy"
```

One deliberate difference: `start.presentation` is **required** (Node defaults to `sample`, `index.js:172`); a
missing id is `error{code:'protocol'}`. Bridge message records in `Contracts/Bridge/*.cs` are the single source
for the TS types (generated).

### 4.4 Sequence diagram of the hot path (ported)

```
Browser Start (web/app PresenterPage)            CLI `presenter-cli run <id>`
  → ws.send {type:'start', id}                     → Presenter.StartAsync(id) directly (no bridge)
    → PresenterBridge.OnText → Presenter.StartAsync(id, fromIndex?)
      → IPresentationRepository.LoadAsync(id)  (FilePresentationRepository: presentations/<id>.md → ScriptParser)
      → PromptBuilder.SystemInstructions(...)
      → attempt loop (≤ MAX_UPSTREAM_ATTEMPTS): ILiveSessionFactory.Create(upstreams[attempt])
          → LiveSession.ConnectAsync(): ClientWebSocket → wss://…/live/sessions, send session.start, await session.started (10 s)
              ├─ startup `error` → close socket, Finish('startup_error') → next attempt
              └─ started → wire events; start silence pump (PeriodicTimer 20 ms, PUMP_SLACK_MS 120)
      → PresentSlide(0): thinking.append(notes) → instructions.append(part 1)
  ◀ {type:'state'} {type:'slide', index:0}
Browser mic frame (binary) → PresenterBridge.OnBinary → Presenter.SendAudio → LiveSession.SendAudio (input_audio.append) ; pump accounts sentMs
GPT-Live output_audio.delta → LiveSession 'audio' → Presenter.OnAudio: voiced? (AudioLevel.Rms ≥ 120) → ArmAfterVoice
  → timer(advanceSilenceMs | partGap) fires (TimeProvider) → next part | PresentSlide(i+1) | WrapUp → Close
  ◀ binary audio → browser AudioPlayback worklet ; {type:'transcript'} ; {type:'usage'} ; {type:'closed'}
Producers that reach the upstream — each a separate call path into the session in Node, serialised only by the
event loop:
  timers   — advance-silence (presenter.js:306-318), part gap (:220-253), nudge (:321-329), wrap-up fallback (:332-349)
  bridge   — next/prev/goto (:362-391), pause/resume (:394-418), mute/unmute, end; browser keys, buttons and deck
             hashchange all arrive as these commands (app.js:60,322,339)
  upstream — output audio (voiced? → arm), user transcript (hold/re-arm), appended acks, usage, closed
  CLI      — `presenter-cli run` issues the same commands without the bridge
In .NET every producer enqueues a PresenterEvent on ONE channel consumed by ONE loop (§4.6); that loop is the only
caller of ILiveSession, and LiveSession has ONE send loop. Races that get their own tests: timer fires while a
manual goto is queued; pause during a part gap; end while a part is in flight; user speech inside the advance window.
```

### 4.5 Surface list

| Surface | Change | Task |
|---|---|---|
| GitHub repo `PMTLabs/presenter-ai` | initial commit, `master`/`develop`, feature branch | T1 |
| `PresenterAi.slnx`, `Directory.Build.props`, 5 src + 1 cli + 3 test projects | new | T2 |
| `docker-compose.yml`, `src/PresenterAi.Api/Dockerfile`, `.github/workflows/ci.yml` | new | T3 |
| `Application/Scripts/*` (+ tests) | port | T4 |
| `Application/Presenting/PromptBuilder` (+ tests) | port | T5 |
| `Application/Presenting/AudioLevel` (+ tests) | port | T6 |
| `Infrastructure/Live/LiveSession`, `LiveUrlResolver`, `UpstreamOptions` (+ fake server, tests) | port | T7, T9 |
| `Application/Presenting/Presenter` (+ tests) | port | T8 |
| `Api`: `/api/presentations*`, `/api/config`, `/decks`, `/health`, `/ws`, Dev auth, static root | new (same contract) | T10 |
| Node page served by .NET (`Content:WebRoot=src/web`) | interim parity | T11 |
| `PresenterAi.Cli` `smoke`, `run` | replaces `scripts/live-smoke.mjs`, `scripts/headless-run.mjs` | T12 |
| `web/` workspaces, `web/shared` client + kit, `web/admin` placeholder | new | T13 |
| `web/app` presenter page, library, Dev sign-in | port of `src/web` | T14 |
| Real-Chrome + CLI verification, work log | verification | T15 |
| README quick start (dual stack until 004), wiring audit, approval log | docs | T16 |

### 4.6 Concurrency and timing model (the port's real risk)

- **One Presenter loop.** `Channel<PresenterEvent>` (bounded, `FullMode.Wait`; audio-level events coalesced)
  consumed by a single task; state is touched only on that task. Timers from `TimeProvider.CreateTimer` only
  *enqueue* events — no state change inside a timer callback. Commands arriving while `Start` is in its upstream
  attempt loop are queued, not dropped; `Start` while running is ignored with a log line (Node behaviour).
- **One upstream send loop.** `LiveSession` owns a `Channel<OutboundFrame>`; `SendAsync` is called from exactly one
  task (≤1 concurrent send, ≤1 concurrent receive per `ClientWebSocket`). The silence pump is
  `PeriodicTimer(20 ms, timeProvider)` whose tick enqueues "fill to now"; accounting is `sentMs` vs
  `timeProvider.GetElapsedTime(startTimestamp)` — monotonic, never `DateTime.Now` — and is updated only by the send
  loop, so no `Interlocked` on the counter. A delayed tick catches up in one bounded burst (`PUMP_SLACK_MS`), as in
  `live-client.js:70-85`.
- **Receive loop** assembles fragments until `EndOfMessage`, dispatches text (JSON) and binary (audio) events;
  close frames and `WebSocketException` end the session through one `Finish(reason)` guarded by
  `Interlocked.CompareExchange`, so a client `CloseAsync` racing a server close runs it once.
- **Cancellation lifecycle:** one `CancellationTokenSource` per session cancels pump, receive loop and send loop;
  `CloseAsync` waits ≤5 s then `Abort()`; tests with `FakeTimeProvider` assert no pump ticks after `Finish`.
- **Bridge ownership:** the single-client slot is `Interlocked.CompareExchange` on the socket reference; the loser
  gets `error{code:'busy'}` + 1013 before any audio is accepted. Browser-bound audio is a bounded channel sized
  for ~10 s; a client that cannot drain it is closed 1011 — **no drop-oldest** (not Node parity, and audible).

## 5. Impact and risk

| Question | Answer |
|---|---|
| State management — what survives a crash mid-operation? | Nothing needs to: a live session is in-memory by design; on API crash the upstream socket drops (billing stops), the browser reconnects and shows idle. Same as Node. |
| Data consistency — orphans, races, double-apply? | Single client per API instance enforced at the bridge (1013); `Presenter` is single-threaded via a `Channel` consumer, so commands and audio never interleave mid-transition (Node relied on the event loop for this — the port must make it explicit). |
| User experience — root problem or symptom; any surprise? | No visible change; the only new surface is the Dev sign-in name in the header. |
| Backward compatibility — existing data / sessions / configs? | `presentations/*.md` and `decks/` are read as-is; `.env` names are documented → `Upstream:*`. Node keeps running on 47913 only when the .NET API is not — the runbook says which one to start. |
| Error recovery — what happens on failure; can it recover? | Startup error → next upstream → idle with message; handshake timeout 10 s; close fallback 5 s; browser backoff reconnect. |
| Logging & debugging — enough to diagnose in the field? | Serilog with a session id per live session, upstream event dump behind `Presenter:LogEvents`, `/health`; the CLI `run` prints the speech/silence bar as before. |
| Edge cases — empty, huge, repeated, concurrent, interrupted? | Missing/invalid presentation → `error{code:'start'}`; 535 KB deck served statically; repeated Start while running → ignored with a log (as Node); second client → 1013; mic denied → listen-only; user speaks → advance held (ported test). |

**Risks:**
- R1 — timing semantics differ (`PeriodicTimer` drift vs `setInterval`, timer callbacks on the thread pool) →
  pump accounting is by wall clock (`sentMs` vs elapsed), not by tick count; `Presenter` runs on one consumer
  loop; parity tests use `FakeTimeProvider`; live smoke + Chrome run in T12/T15 catch what tests cannot.
- R2 — `ClientWebSocket` on Windows and Azure `api-key` header → verified in T7 by the fake server and in T12 live.
- R3 — WebSocket backpressure: 50 binary frames/s each way → bounded channels sized for ~10 s, single send loop
  per socket, close 1011 on a client that cannot drain; never drop frames silently (§4.6).
- R4 — `TreatWarningsAsErrors` slows the port → keep it (brief), fix warnings as they appear.
- R5 — bun on Windows for the workspaces → bun 1.3.14 present; CI runs on ubuntu.
- R6 — port scope creep ("while I'm here") → the parity suite is the definition of done; anything else is 003+.
- R7 — the "byte-identical protocol" claim drifts during the port → the canonical frames in §4.3 are replayed by
  `BridgeContractTests` and by the old Node page in T11; a new or renamed field is a failing test, not a judgement call.

**Rollback:** the Node MVP is untouched and runnable (`npm start`) until plan 004; all work is on
`feature/002-dotnet-core-port` — abandoning the branch restores the previous state.

## 6. Tasks

**Traceability**

| AC | Tasks | Automated oracle (fails if the hop breaks) |
|---|---|---|
| AC1 initial commit, no `.env` | T1 | CI `secrets-guard` step (`git ls-files` must not match `^\.env$|appsettings\.Local\.json`) |
| AC2 ported suite green | T2, T4–T10 | the ported suites + `BridgeContractTests` |
| AC3 CLI smoke on both providers | T7, T9, T12 | `CliTests.Smoke_selects_provider_and_reports_usage_seconds` (fake server); live run is additional evidence |
| AC4 Node page on .NET API | T10, T11 | `BridgeContractTests.Old_client_start_with_presentation_field_starts_that_deck` (non-sample id); Chrome run is additional evidence |
| AC5 React app parity | T13, T14, T15 | vitest `bridgeClient`/`deckDriver`/`startAudio` suites + `SpaFallbackTests`; Chrome run is the acceptance evidence |
| AC13 (partial) CI green | T3, T16 | the CI workflow + `docker compose config -q` + `StartupTests.Container_validates_on_build` |

### T1 — Git init, ignore rules, initial commit, push (AC1)
- **Files:** `.gitignore`, `.gitattributes`, `.editorconfig`, `scripts/secrets-guard.sh`
- **Change:** `git init`; `.gitignore` covering `.env*` (except `.env.example`), `appsettings.Local.json`,
  `data/`, `bin/ obj/ node_modules/ dist/ TestResults/ .vs/`, `*.pid`. `appsettings.Development.json` is
  **tracked** and must hold only non-secret settings (`Content:WebRoot`, ports, log levels); secrets go to
  `dotnet user-secrets`. `scripts/secrets-guard.sh` fails when `git ls-files` matches `^\.env$`,
  `appsettings\.Local\.json`, or any tracked file assigns a real-looking value to a setting whose name ends in
  `key`/`secret`/`token`/`password` (any case, any whitespace around `=`/`:`, JSON/YAML/env/CLI spellings — the
  class is stated in the script header and pinned by `scripts/secrets-guard.selftest.sh`; review round 1 F1).
  First commit; `git remote add origin https://github.com/PMTLabs/presenter-ai.git`; push `master`; create and
  push `develop`; branch `feature/002-dotnet-core-port`.
- **Verify:** `scripts/secrets-guard.sh` exits 0; `gh repo view PMTLabs/presenter-ai --json defaultBranchRef`
  shows `master`; `git status` clean on the feature branch.
- **Test that dies if this breaks:** CI step `secrets-guard` (T3) — a committed `.env` turns the pipeline red.

### T2 — Solution scaffold (AC2 prerequisite)
- **Files:** `PresenterAi.slnx`, `Directory.Build.props`, `src/PresenterAi.{Domain,Application,Infrastructure,Api,Contracts,Cli}/*.csproj`,
  `tests/PresenterAi.{Application,Infrastructure,Api}.Tests/*.csproj`, `src/PresenterAi.Api/Program.cs`,
  `appsettings.json`, `appsettings.Example.json`
- **Change:** net10.0, Nullable, ImplicitUsings, `TreatWarningsAsErrors`; project references in the allowed
  direction only; Serilog console; `GET /health`; `AddOpenApi()` + `MapOpenApi()` at `/openapi/v1.json`; Kestrel
  on 47913; `AddProblemDetails()` + `IExceptionHandler` mapping `DomainException` → Problem Details with `code`
  and `traceId` (conventions §5), `Contracts/ErrorCodes.cs` catalogue exported as `x-error-codes` in the OpenAPI
  document (§6), JSON options camelCase + string enums (§3); xUnit + **Moq 4.20.72** + FluentAssertions
  (InkSpoke's convention, `InkSpoke.Api.Tests.csproj:34`); `Microsoft.Extensions.TimeProvider.Testing`;
  `ValidateOnBuild`/`ValidateScopes` on the container.
- **Verify:** `dotnet build -warnaserror` clean; `dotnet test` runs the smoke tests; `curl :47913/health` → 200;
  `curl :47913/openapi/v1.json` lists `/health`.
- **Test that dies if this breaks:** `Api.Tests/HealthTests.Health_returns_ok`, `Api.Tests/OpenApiTests.Document_lists_all_mapped_endpoints`,
  `OpenApiTests.Every_error_code_has_title_and_status`, `ProblemDetailsTests.Unhandled_exception_is_500_with_traceId_and_no_message`
  (WebApplicationFactory).

### T3 — Docker and CI (AC13 partial)
- **Files:** `docker-compose.yml`, `src/PresenterAi.Api/Dockerfile`, `.github/workflows/ci.yml`
- **Change:** services `postgres` (`pgvector/pgvector:pg17`, healthcheck), `redis` (`redis:7-alpine`),
  `api` under profile `full`; multi-stage Dockerfile (sdk → aspnet, non-root); CI on push/PR: `dotnet test`,
  `bun install --frozen-lockfile && bun run lint && bun run build` (after T13), cache restore.
- **Verify:** `docker compose config -q` passes; `docker compose up -d postgres redis` healthy;
  `docker compose --profile full up --build` serves `/health`; the Actions run on the feature branch is green.
- **Test that dies if this breaks:** CI steps `secrets-guard`, `compose-config` (`docker compose config -q`) and
  `dotnet test` — a broken compose file or a tracked secret fails the run. (AC13)

### T4 — Port scripts: parser, writer, chunker (AC2)
- **Files:** `Application/Scripts/{ScriptParser,ScriptWriter,TextChunker,PresentationScript}.cs`,
  `tests/PresenterAi.Application.Tests/Scripts/*Tests.cs`, fixtures copied from `presentations/`
- **Change:** `ScriptParser.Parse(markdown, id)` → `PresentationScript {Meta{Id,Title,Deck,Driver,Voice,Context,
  AdvanceSilenceMs,ChunkChars=1400}, Slides[{Index,Number,Title,Narration,Notes}]}` with the same heading regex,
  `> notes:` handling, contiguity and `chunkChars ≥ 200` errors; frontmatter via YamlDotNet; `TextChunker.Chunk`
  sentence-boundary; `ScriptWriter.Format` (inverse, needed by phases 1+ and by the round-trip test now).
- **Verify:** the 9 parser scenarios (incl. the 11-slide Ricoh file) + round-trip `Parse(Format(x)) == x` pass.
- **Test that dies if this breaks:** `ScriptParserTests.Headings_must_be_contiguous_from_1`, `ScriptWriterTests.Round_trips_ricoh`.

### T5 — Port PromptBuilder (AC2)
- **Files:** `Application/Presenting/PromptBuilder.cs`, `tests/…/PromptBuilderTests.cs`
- **Change:** the seven builders with identical text (copied verbatim, including the delivery and audience rules),
  `ContextCharBudget=48000` truncation with warning callback.
- **Verify:** 7 scenarios pass; a golden-file test compares `SystemInstructions` output against a snapshot
  produced by the Node `buildSystemInstructions` for the sample presentation.
- **Test that dies if this breaks:** `PromptBuilderTests.System_instructions_match_node_snapshot`.

### T6 — Port AudioLevel (AC2)
- **Files:** `Application/Presenting/AudioLevel.cs`, tests
- **Change:** `Rms(ReadOnlySpan<byte>, stride)` on `Span<short>`, `VoiceThreshold=120`, `IsVoiced`.
- **Verify:** silent frame → not voiced; `voicedFrame` fixture (ported bytes) → voiced; RMS 119 → not voiced,
  RMS 120 → voiced (threshold boundary); odd-length buffer handled.
- **Test that dies if this breaks:** `AudioLevelTests.Voiced_fixture_is_voiced` and
  `AudioLevelTests.Threshold_boundary_119_vs_120` — an `IsVoiced => false` stub fails both;
  `AudioLevelTests.Silence_is_not_voiced` covers the other direction.

### T7 — Port LiveSession + fake GPT-Live server (AC2, AC3)
- **Files:** `Infrastructure/Live/{LiveSession,LiveSessionFactory,LiveEvents}.cs`, `Application/Presenting/ILiveSession.cs`,
  `tests/PresenterAi.Infrastructure.Tests/Live/{FakeLiveServer,LiveSessionTests}.cs`
- **Change:** `ClientWebSocket`; `ConnectAsync` sends `session.start{model,instructions,audio.output.voice,delegation.client}`
  and awaits `session.started` (10 s timeout); startup `error` → close + `Finish("startup_error")`; events as C#
  events/`Channel`; `AppendInstructions/Thinking/Commentary(content, eventId, delegationId=null)`, `Mute/Unmute`,
  `SendAudio`, `CloseAsync` (5 s abort fallback), `Terminate`; **silence pump**: `PeriodicTimer(20 ms)`, wall-clock
  `sentMs` accounting, `PUMP_SLACK_MS=120`, 960-byte silence frame; `SilenceMs` exposed. `FakeLiveServer`
  (Kestrel WebSocket in-process) ports `test/fake-live-server.js`: records events, `voicedDelta`, `silent`
  flag, `session.closed{reason:'client_request', usage}`, terminates clients on close.
- **Verify:** tests against the fake — connect/start; startup error closes socket; the pump contract: with no
  input for 500 ms the fake receives exactly 960-byte all-zero frames totalling 500 ms minus the 120 ms slack
  (19 frames — the Node pump keeps `sentMs` up to `PUMP_SLACK_MS` behind the clock, `live-client.js:76`); after
  200 ms of real input frames **no** silence is sent within the 120 ms slack; a tick delayed by 300 ms
  (FakeTimeProvider) is caught up in one burst of 9 frames; `SilenceMs` is monotonic and equals the silence bytes sent; mute stops input; close
  returns usage and reason; no ticks after `Finish`.
- **Test that dies if this breaks:** `LiveSessionTests.Pump_sends_only_the_gap_not_every_tick` (an implementation
  that sends silence on every tick regardless of real audio fails it), `LiveSessionTests.Pump_catches_up_after_delayed_tick`,
  `LiveSessionTests.Startup_error_closes_socket_and_finishes`, `LiveSessionTests.No_ticks_after_finish`.

### T8 — Port Presenter (AC2)
- **Files:** `Application/Presenting/{Presenter,PresenterState,PresenterOptions}.cs`, `tests/…/Presenting/{FakeSession,PresenterTests}.cs`
- **Change:** state machine with the same constants and event ids (`slide-N-part-K`, `slide-N-notes`, `pause-N`,
  `resume-N`, `wrap-up`, 1-based); commands `Start/Next/Prev/Goto/Pause/Resume/Mute/Unmute/SendAudio/End`;
  `OnAudio` counts voiced frames only; user transcript re-arms; nudge 15 s; wrap-up fallback 15 s; `OnClosed`
  resume index + `IsNormalClose`; all timers via `TimeProvider`; a single `Channel<Command>` consumer.
- **Verify:** the 18 presenter scenarios (fallback across upstreams, parts gated one at a time, silent frames do
  not count, user speech holds advance, pause mutes, end closes) with `FakeTimeProvider`, plus the race set from
  §4.4: timer fires with a queued `goto` (goto wins, one slide instruction sent); `pause` during a part gap (next
  part not sent until resume); `end` while a part is in flight (close once, no further appends); user speech inside
  the advance window (advance held, re-armed after).
- **Test that dies if this breaks:** `PresenterTests.Parts_are_sent_one_at_a_time_after_speech_gap`,
  `PresenterTests.Falls_back_to_next_upstream_on_startup_error`, `PresenterTests.Goto_queued_during_timer_wins_and_sends_once`,
  `PresenterTests.End_during_part_closes_once`.

### T9 — Upstream options and URL/auth resolution (AC2, AC3)
- **Files:** `Infrastructure/Live/{UpstreamOptions,LiveUrlResolver,UpstreamAuth}.cs`, `Api` options binding, tests
- **Change:** `Upstream:*` / `Upstream:Fallback:*` → ordered `Upstreams[]`; `LiveUrlResolver.Resolve(endpoint)`
  and `UpstreamAuth.Headers(key, url)` identical to `config.js`; validation error names the missing key
  (`Missing required setting: Upstream:Key`) and fails startup.
- **Verify:** the 11 config scenarios; `dotnet run` without `Upstream:Key` exits 1 with the message.
- **Test that dies if this breaks:** `LiveUrlResolverTests.Azure_host_maps_to_openai_v1_live_sessions`.

### T10 — API endpoints, file-backed content, `/ws` bridge, Dev auth (AC2, AC4)
- **Files:** `Api/Endpoints/{PresentationEndpoints,ConfigEndpoints}.cs`, `Api/Realtime/PresenterBridge.cs`,
  `Api/Auth/DevAuthHandler.cs`, `Infrastructure/Content/{FilePresentationRepository,FileDeckStore}.cs`,
  `Contracts/*`, `tests/PresenterAi.Api.Tests/{BridgeTests,PresentationEndpointTests}.cs`
- **Change:** endpoints as in §4.3; `/decks` static with the same 404 text; static SPA root from
  `Content:WebRoot` with `Cache-Control: no-cache`; bridge: one client, 1013 busy, JSON commands and messages
  byte-identical, binary → `SendAudio`, client close → `End`; Dev auth scheme active only in Development.
- **Verify:** the 5 integration scenarios ported (server + bridge + presenter + `FakeLiveServer` +
  `UpstreamReceived` polling); `BridgeContractTests` replays every canonical frame from §4.3 (incl. `start` with a
  non-sample `presentation` and asserts that deck's slide-1 instruction reaches the fake; `start` without
  `presentation` → `error{code:'protocol'}`); two sockets connecting simultaneously → exactly one wins, the other
  gets `busy` + 1013 before any binary frame is accepted; client closes while a server → client send is in
  flight → no unhandled exception, presenter ends once; golden JSON for the three HTTP endpoints; SPA fallback
  serves `index.html` for `/present/abc` and 404 for `/api/nope`.
- **Test that dies if this breaks:** `BridgeTests.Start_presents_slide_1_and_auto_advances`,
  `BridgeContractTests.Old_client_start_with_presentation_field_starts_that_deck`,
  `BridgeTests.Simultaneous_clients_exactly_one_wins`, `BridgeTests.Client_close_during_send_ends_once`,
  `PresentationEndpointTests.Json_matches_node_golden`, `SpaFallbackTests.Deep_link_serves_index`.

### T11 — Interim parity: Node page on the .NET API (AC4)
- **Files:** `appsettings.Development.json` (tracked, non-secret: `Content:WebRoot=../../src/web`), runbook entry
- **Change:** run the .NET API alone on 47913 (Node stopped), open the old page, present Ricoh slides 1–5 in
  Chrome (auto-advance, → key, Space pause/resume, deck › button, Esc).
- **Verify:** `window.__presenterDebug()` shows `wsOpen`, `framesSent` rising, usage pill after Esc; server log
  shows `session.closed` with `client_request`. Record in the work log.
- **Test that dies if this breaks:** `BridgeContractTests` (T10) is the automated oracle for the same frames the
  old page sends; the Chrome run (runbook #4) is the acceptance evidence for AC4.

### T12 — CLI: `smoke` and `run` (AC3)
- **Files:** `src/PresenterAi.Cli/{Program,SmokeCommand,RunCommand}.cs`
- **Change:** `presenter-cli smoke --provider azure|openai` (one sentence, ~20 s, prints `usage.seconds`);
  `presenter-cli run <id> [--max-seconds N] [--stop-after-slide N]` (headless Presenter with the same
  speech/silence bar), sharing `Application` + `Infrastructure` via the same DI registrations as the API.
- **Verify:** against the fake: `smoke --provider azure` and `--provider openai` each pick the right upstream
  (asserted by the fake's received `session.start` host/headers), receive voiced audio, print `usage.seconds`, exit 0;
  `run sample --stop-after-slide 2` narrates slides 1–2 and sends `end` when slide 3 is announced (Node
  `headless-run.mjs` semantics: the stop fires on the *next* slide event), printing parts, transcript and the speech/silence bar. Live: both real
  providers speak and close (AC3 evidence).
- **Test that dies if this breaks:** `CliTests.Smoke_selects_provider_and_reports_usage_seconds`,
  `CliTests.Run_sample_against_fake_server_stops_after_slide_2`.

### T13 — `web/` workspaces, shared client and kit, admin placeholder (AC5)
- **Files:** `web/package.json` (bun workspaces), `web/tsconfig.base.json`, `web/shared/{package.json,src/api/*,src/auth/authStore.ts,src/ui/*}`,
  `web/app/{vite.config.ts,tailwind.config.js,src/main.tsx,src/App.tsx}`, `web/admin/{…,src/pages/ComingSoon.tsx}`,
  `web/shared/scripts/generate-client.ts`
- **Change:** React 19, Vite 6, TS strict, Tailwind 3.4, ESLint (typescript-eslint), vitest; OpenAPI →
  `openapi-typescript` + `openapi-fetch` client generated from `/openapi/v1.json`, plus `errorCodes.ts` (string
  union from `x-error-codes`), `isProblem()` narrowing helper and `errorMessages.ts` (conventions §12); copy `AdminLayout`,
  `AdminRoute`, `ThemeToggle` and the Tailwind theme from inkspoke (origin commit in headers); Vite dev server on
  47914 proxying `/api`, `/ws`, `/decks`, `/health` to 47913.
- **Verify:** `bun install`, `bun run lint`, `bun run build` for both apps; the generated client is **checked in**
  and CI regenerates it from the running API's `/openapi/v1.json` and fails on a diff; CI green with the web steps.
- **Test that dies if this breaks:** CI step `openapi-drift`; `web/shared` vitest `client.spec.ts` (typed call to
  `/api/presentations`) and `errorCodes.spec.ts` (a Problem Details body narrows to a known code). (AC5)

### T14 — Presenter page, library, Dev sign-in (AC5)
- **Files:** `web/app/src/{routes/Library.tsx,routes/Present.tsx,store/presenterStore.ts,ws/bridgeClient.ts,
  audio/{capture,playback}.ts,audio/worklets/{capture-processor,playback-processor}.ts,deck/deckDriver.ts,
  components/{Transcript,SlidePill,UsagePill,LogPanel}.tsx}`, `web/shared/src/auth/devSignIn.ts`
- **Change:** 1:1 port of `src/web` (WS client with backoff that sends the optional `auth` frame first,
  `binaryType='arraybuffer'`, non-blocking mic start,
  device-rate `AudioContext` + `resumeWithTimeout`, keys, transcript grouping, deck adapters, `window.__presenterDebug`);
  Library lists `/api/presentations`; header shows the Dev user; `S/Space/→/←/M/Esc` unchanged.
- **Verify:** `bun run dev` → present the sample deck end to end; vitest for `deckDriver` (adapter detection on
  fixtures), `bridgeClient` (message parsing, `start` carries `presentation`, reconnect backoff after a dropped
  socket, snapshot → idle after reconnect) and `startAudio` (`getUserMedia` rejected → playback still starts,
  `micReady=false`, no error thrown).
- **Test that dies if this breaks:** `deckDriver.spec.ts` `detects showFn deck`, `bridgeClient.spec.ts`
  `reconnects with backoff` and `start sends presentation id`, `startAudio.spec.ts` `continues listen-only when mic denied`.

### T15 — Real-Chrome verification and work log (AC5)
- **Files:** `docs/progress/002-work-log-phase0.md` (new; parity gate record)
- **Change:** in Chrome: Ricoh deck from the React app — AC1–AC10 of plan 001 re-run (missing-setting exit,
  Start ≤ 5 s, narration + transcript, auto-advance, → key, spoken question (AC6 — first time), pause/resume,
  Esc → usage, long slide in parts, tests); `presenter-cli run ricoh-delivery-overview --stop-after-slide 3`.
- **Verify:** every row of the AC table has evidence; usage seconds recorded (~$1).
- **Test that dies if this breaks:** manual (runbook #6–#9).

### T16 — Wiring audit, docs, PR (AC13 partial)
- **Files:** `README.md` (new quick start: compose, `dotnet run`, `bun run dev`; Node section marked "until plan 004"),
  `docs/guides/001-presenting-a-new-deck.md` (commands updated), this plan's approval log
- **Change:** trace every new DI registration → resolver, every option key → reader, every bridge message →
  handler on both sides, every endpoint → client call (`web/shared` or CLI); log line at each entry point; open the
  PR `feature/002-dotnet-core-port → develop`. Optional improvement (outside the brief, do only if asked): branch
  protection on `develop` requiring CI.
- **Verify:** audit table in the work log with no "no caller" rows; PR checks green.
- **Test that dies if this breaks:** `StartupTests.Container_validates_on_build` (builds the real container with
  `ValidateOnBuild` — an unresolvable registration fails), `OpenApiTests.Document_lists_all_mapped_endpoints`, CI.

## 7. Test strategy

- **Unit (Application.Tests, Infrastructure.Tests):** parser/writer/chunker (10), prompt (7 + snapshot), audio
  level (4), presenter (18 + 4 race cases, `FakeTimeProvider`), URL/auth/options (11), LiveSession vs
  `FakeLiveServer` (8 incl. the pump contract). Deliberately not unit-tested: Kestrel static files, Serilog.
- **Integration (Api.Tests):** `WebApplicationFactory` + `FakeLiveServer`: start → slide 1 → auto-advance;
  manual next; pause/resume mutes/unmutes; end → closed with usage; second client 1013 (simultaneous connect);
  client close during send; fallback to second upstream when the first fake returns a startup error;
  `BridgeContractTests` (canonical frames, non-sample id, missing id); golden JSON for the HTTP endpoints; SPA
  fallback; OpenAPI lists every endpoint; container validates on build. CLI `smoke` and `run` against the fake.
- **Web (vitest):** deck adapter detection, bridge message parsing/backoff/`start` payload, mic-denied
  listen-only start, generated client typing.
- **Live (not in CI):** `presenter-cli smoke` on both providers; Chrome run (T11, T15).

**Manual runbook**

| # | Step | Expected |
|---|---|---|
| 1 | `git clone` the repo fresh; `docker compose up -d postgres redis` | both healthy; no `.env` in the tree |
| 2 | `dotnet test` | all green (≥ 52 scenarios + new ones) |
| 3 | `dotnet run --project src/PresenterAi.Api` without `Upstream:Key` | exits 1: `Missing required setting: Upstream:Key` |
| 4 | with secrets set, `Content:WebRoot=src/web`, open http://localhost:47913 (old page), Start on Ricoh | narration, auto-advance to slide 2, → to 3, Space pause/resume, › button followed, Esc → usage pill; server logs `session.closed client_request` |
| 5 | `presenter-cli smoke --provider azure` then `--provider openai` | one sentence each, `usage.seconds` printed |
| 6 | `bun run dev` in `web/`, open http://localhost:47914, sign in as Dev user, open Ricoh, Start | same as #4 from the React app; header shows Dev user |
| 7 | while it narrates, ask a question aloud | model answers, then bridges back to the script; slide does not advance during the exchange |
| 8 | open a second tab and press Start | second tab shows "Another presenter page is already connected" |
| 9 | kill the API mid-session; restart | browser shows disconnected → reconnects → idle; no orphan upstream session (usage stops) |
| 10 | `presenter-cli run ricoh-delivery-overview --stop-after-slide 3` | three slides narrated, `end` sent when slide 4 is announced, speech/silence bars, `closed reason=client_request` |

## 8. Rollout / phasing

Feature branch `feature/002-dotnet-core-port` → PR to `develop` after T16; no deploy in this plan. Ship order:
T1–T3 (infra) → T4–T9 (pure ports, each mergeable) → T10–T12 (API/CLI, AC3/AC4 gate) → T13–T15 (web, AC5 gate)
→ T16. The Node MVP remains the fallback throughout; plan 003 starts only when AC2–AC5 are recorded.

**Implementation mode (user instruction, 2026-09-21):** coding is delegated to external `pi` agents through the
`agent-research` skill, strictly per its workflow (pre-created report file, one terminal per role, verify the TUI
is ready, one submitted message and `Working`, monitor the report file and terminal health, save results, close
the terminal). Claude orchestrates, briefs each task with the plan section + acceptance criteria + verify step,
checks the result, and fixes only trivial build-blockers directly. Tier per task:

| Tier | Command | Tasks |
|---|---|---|
| scout | `pi --model openai-codex/gpt-5.6-luna:medium` | reading inkspoke sources before T13 (UI kit, Tailwind theme) and T2 (`Program.cs` wiring); any "how does X work" question during the port |
| implement, moderate | `pi --model openai-codex/gpt-5.6-luna:high` | T3, T4, T5, T6, T9, T12, T13 |
| implement, complex | `pi --model openai-codex/gpt-5.6-terra:medium` (`:high` for T7/T8/T10) | T2, T7, T8, T10, T14 |
| review (separate terminal, no tests run) | `pi --model openai-codex/gpt-5.6-terra:medium` with the A/B/C/D classification from the skill | after T9, after T12, after T15 |
| Claude directly | — | T1 (git init/push — authorised by the brief), T11 and T15 (real-Chrome runs via the browser MCP), T16 audit, trivial fixes |

One implementer terminal at a time per area; T4–T6 and T9 may run in parallel terminals since they touch disjoint
files. Every implementer brief ends with "run `dotnet test` (or `bun run test`) and write the result to the
report file"; the review ledger goes to `docs/agentic/review-rounds-ledger.md` as the skill requires.

## 9. Open questions

None. Deferred by the brief: OAuth/Postgres/Redis usage (003), admin (004), Stripe (005).

## 10. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-21 | Requirement brief confirmed (G1) | 3 rounds of questions (12 questions); one brief for plans 002–004 |
| 2026-09-21 | External plan review | `pi` gpt-5.6-sol:medium, review-only; 9 findings (A1 B1 C4 D3), all folded in: protocol contract frozen with canonical frames (`start.presentation`), exact HTTP shapes + golden tests, producer paths enumerated + §4.6 concurrency model, oracles added for T1/T3/T6/T7/T8/T10/T12/T13/T14/T16, OpenAPI wiring + SPA fallback, traceability matrix, SSE/Sessions explicitly deferred, Moq per InkSpoke, citations corrected; branch protection moved to optional. Report: `docs/review/001-plan-002-external-review.md` |
| 2026-09-21 | Plan approved (G2) | approved by the user after the external review findings were folded in; implementation starts on an explicit `implement 002` |
| 2026-09-21 | Oracle correction during T7 | T7 verify text said the 500 ms pump test totals "500 ms ±1 frame"; the Node pump keeps `sentMs` up to 120 ms behind the clock, so the correct totals are 19 frames (500 ms) and 9 frames (300 ms catch-up). Wording fixed; no behaviour change |
| 2026-09-21 | Amendment after approval | `docs/reference/001-api-and-code-conventions.md` adopted (Problem Details + `code`, bare resources + `{items,page,pageSize,total}`, `area.reason` codes with generated TS union, `/api` trio frozen until 004); T2/T10/T13/T14 wording updated to reference it — no scope change |
| 2026-09-21 | Implementation complete (T1–T16) | Parity via ported tests: Application 51, Infrastructure 30, Api 46, Cli 5, web shared 3/3 and app 11; three review rounds (`docs/review/002`–`004`); live smoke passed on Azure and OpenAI; Chrome runs passed for T11 (usage 98.6 s) and T15 (usage 98.4 s); wiring audit: `docs/research/004-plan-002-wiring-audit.md`. Deviations from the plan text: stop-after-slide semantics corrected to Node parity, `[FromRoute]` added for the OpenAPI `{id}` parameter, and deck routing order fixed. |
| 2026-09-21 | T17 added by the user after T16 | Fluid full-width presenter layout with a resizable deck | transcript/log split (`react-resizable-panels` v4, remembered per browser), decided by survey; landed in `2d4af67` with `Present.layout.spec.tsx` (app vitest 21) and a Chrome check (drag/keyboard resize, reload keeps the split, Start/End round-trip usage 25.8 s). Follow-ups from the user reports fixed on the way: control bar styled (`f434b47`), dark-theme text colour (`9a7b0ee`). |
