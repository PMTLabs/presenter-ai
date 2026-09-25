# 002 — Work log: phase 0a .NET core port, API bridge, React web app (plan 002)

Plan: `docs/plan/002-phase0-dotnet-core-port-api-web.md` (approved 2026-09-21). Conventions:
`docs/reference/001-api-and-code-conventions.md`. Implementation mode: coding delegated to external `pi`
agents (plan §8); Claude orchestrates and does T1, T11, T15, T16.

## 2026-09-21

### T1 — Git init, ignore rules, initial commit, push — done

- Stopped the stray Node presenter server that still held port 47913 (pid 103372) and removed its pid file.
- `git init -b master`; `.gitignore` (`.env`/`.env.*` except `.env.example`, `appsettings.Local.json`, `data/`,
  build/IDE/node output, `*.pid`, `*.log`, `.claude/`; `!web/bun.lock` re-included for the future workspace),
  `.gitattributes` (LF in repo, `*.sh` LF, `*.ps1|cmd|bat` CRLF, binaries), `.editorconfig`,
  `scripts/secrets-guard.sh` (fails on tracked `.env`/`.env.*`/`appsettings.Local.json`/`*.key`/`*.pem` and on
  real-looking values assigned to any `*key`/`*secret`/`*token`/`*password` setting in JSON/YAML/env/CLI
  spelling, whitespace- and case-tolerant; placeholders allowed — widened in review round 1 F1 and pinned by
  `scripts/secrets-guard.selftest.sh`).
- Verified: guard exits 0 on the staged tree; on a scratch copy with `.env` force-added it exits 1 reporting
  both the forbidden path and two real-looking values (values not printed). `git ls-files | grep '^\.env'` →
  only `.env.example`.
- Commit `326cab7 chore: initial commit of the Node MVP and docs` pushed to `origin/master`
  (`https://github.com/PMTLabs/presenter-ai.git`, default branch `master`); `develop` created and pushed;
  working branch `feature/002-dotnet-core-port`.
- Note: `decks/ricoh/index.html` (535 KB copy of the Ricoh delivery-overview deck) is tracked because AC4/AC5
  present its slides; the repository is private.

### T2 — Solution scaffold — done (pi gpt-5.6-terra:medium)

- `PresenterAi.slnx`, `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors), six `src/`
  projects + three test projects, Serilog console, `GET /health`, `AddOpenApi()`/`MapOpenApi("/openapi/v1.json")`,
  `AddProblemDetails()` + `DomainExceptionHandler` (RFC 9457 body with `code`/`traceId`, generic 500 detail),
  `Contracts/ErrorCodes.cs` catalogue exported as `x-error-codes`, camelCase + string enums, `ValidateOnBuild`.
- Packages: OpenApi 10.0.12 (10.0.0 pulled a vulnerable `Microsoft.OpenApi` under warnings-as-errors),
  Mvc.Testing 10.0.0, Serilog.AspNetCore 10.0.0, TimeProvider.Testing 10.0.0, xunit 2.9.3, Moq 4.20.72,
  FluentAssertions 7.0.0.
- Verified by Claude: `dotnet build -warnaserror` 0 warnings; `dotnet test` 8/8 (Api 6, Application 1,
  Infrastructure 1); `/health` and `/openapi/v1.json` answered on 47913 during the agent's manual check.
- Decision: `generation.job_failed` is catalogued with status 500 (the conventions table lists it as "200 on the
  job resource", a job-status code, not an HTTP status); `OpenApiTests.Every_error_code_has_title_and_status`
  requires 400–599. Revisit when the jobs API lands (phase 1).
- Commit `413b817`.

### T4 — Script parser, writer, chunker — done (pi gpt-5.6-luna:high)

- `Application/Scripts/{PresentationScript,ScriptParser,TextChunker,ScriptWriter}.cs`, `Domain/Errors/ScriptParseException.cs`
  (`presentation.invalid_script`, 400, Node message text), YamlDotNet 11.2.1 for the frontmatter, fixtures copied
  to `tests/PresenterAi.Application.Tests/Fixtures/`.
- Tests: 9 parser scenarios ported one-to-one (parity table in the agent report) + `Round_trips_ricoh` /
  `Round_trips_sample`; Application.Tests 12/12 (verified by Claude); Node oracle `script-parser.test.js` 9/9.
- Commit `3c32aa6`.

### T6 — AudioLevel — done (pi gpt-5.6-luna:high)

- `Application/Presenting/AudioLevel.cs` (`Rms` over `MemoryMarshal.Cast<byte,short>`, `VoiceThreshold = 120`,
  `IsVoiced`), `AudioLevelTests` with the shared `VoicedFrame` fixture: silence, voiced fixture, 119-vs-120
  boundary, odd trailing byte, stride. Commit `01c6f55`.

### T3 — Docker and CI — done (pi gpt-5.6-luna:high)

- `docker-compose.yml` (pgvector/pgvector:pg17 on host 5433, redis:7-alpine on host 6382 — 5432/6380/6381 are
  taken by other containers on this machine; `api` under profile `full` reading `UPSTREAM_ENDPOINT`/`UPSTREAM_KEY`
  from the shell), multi-stage non-root `src/PresenterAi.Api/Dockerfile`, `.dockerignore`,
  `.github/workflows/ci.yml` (steps `secrets-guard`, `compose-config`, `dotnet test`, guarded `web` job with
  `openapi-drift`), README "Run with Docker".
- Agent verification: compose config ok, postgres+redis healthy, `--profile full` image built and `/health`
  answered, everything torn down. This machine has Docker 29.6.2 without the `compose` v2 plugin —
  `docker-compose` 5.3.1 was used locally; CI uses `docker compose`.
- Fixes by Claude before commit: the `web` job's job-level `hashFiles('web/package.json')` guard evaluates
  before checkout and would have skipped the job forever → replaced by a post-checkout step guard
  (`steps.web.outputs.exists`); README wording no longer refers to task numbers. Commit `c2dd9b6`.

### T5 — PromptBuilder — done (pi gpt-5.6-luna:high)

- `Application/Presenting/{PromptBuilder,SlideRef}.cs` (texts copied verbatim, `
` newlines,
  `ContextCharBudget = 48_000`), 7 scenarios ported + `System_instructions_match_node_snapshot` against
  `tests/PresenterAi.Application.Tests/Golden/system-instructions.sample.txt` produced by the Node builder
  (script in the scratchpad, not in the repo). Application.Tests 25/25 (verified by Claude); Node
  `prompt.test.js` 7/7. Commit `6a452c9`.

### T9 — Upstream options, URL/auth resolution — done (pi gpt-5.6-luna:high)

- `Infrastructure/Live/{UpstreamOptions,PresenterOptions,LiveUrlResolver,UpstreamAuth,UpstreamRoute}.cs`,
  `Infrastructure/DependencyInjection.cs` (`AddUpstreamOptions`: bind + `Validate` + `ValidateOnStart`,
  `UpstreamRoutes` singleton), one line in `Program.cs`; 11 config scenarios ported (parity table in the agent
  report); `ApiFactory` supplies non-secret defaults; `StartupTests.Missing_upstream_key_fails_startup`.
- Fixes by Claude: the agent had added a `Microsoft.AspNetCore.App` framework reference to Infrastructure —
  replaced with `Microsoft.Extensions.{DependencyInjection.Abstractions,Logging.Abstractions,Options.ConfigurationExtensions}`
  10.0.0; a missing key crashed the process with an unhandled `OptionsValidationException` (exit
  −532462766) — `Program.cs` now catches it outside the Testing environment, prints
  `Configuration invalid: Missing required setting: Upstream:Endpoint; Missing required setting: Upstream:Key`
  to stderr and exits 1 (verified: exit code 1, one line). Under `WebApplicationFactory` the exception still
  propagates so the startup test observes it.
- Full suite after T9: Application 25, Infrastructure 14, Api 7 — all passing. Commits `7c676fd`, `2f22c58`.

### T7 — LiveSession + fake GPT-Live server — done (pi gpt-5.6-terra:high)

- `Application/Presenting/{ILiveSession,LiveEvents}.cs`, `Infrastructure/Live/{LiveSession,LiveSessionFactory,LiveSessionOptions}.cs`
  (one send loop over a `Channel<OutboundFrame>`, receive loop with fragment assembly, `PeriodicTimer(20 ms,
  timeProvider)` pump enqueuing "fill to now", `GetElapsedTime` accounting, `Finish` via
  `Interlocked.CompareExchange`, one CTS), `tests/PresenterAi.Infrastructure.Tests/Live/{FakeLiveServer,LiveSessionTests}.cs`
  (11 tests incl. the four mandatory pump/startup/finish oracles).
- Node parity over plan wording: 500 ms of silence → 19 frames (the pump keeps `sentMs` ≤ 120 ms behind the
  clock), 300 ms delayed tick → 9 frames; plan §6 T7 wording corrected, approval-log row added.
- Fixes by Claude after a 25-run flake hunt: (1) tests asserted burst counts right after the *first* frame was
  observed — now wait for the whole burst and settle (`SettledSilenceFrameCountAsync`); (2) real race: a
  startup `error` woke `ConnectAsync` (whose catch finishes with `connection_lost`) before
  `Finish("startup_error")` ran — the failure now travels through `Finish(reason, seconds, startupFailure)`.
  25/25 consecutive green runs afterwards. Commit `1610b27`.
- Known minor deviation: `_sentMs` uses integer ms (`bytes / 48`); Node uses float ms. Exact for 960-byte frames.

### Golden JSON for the frozen `/api` endpoints — done (Claude)

- Captured from the Node server (`UPSTREAM_ENDPOINT=https://api.openai.com`, `UPSTREAM_KEY=test`, port 47999,
  stopped afterwards) into `tests/PresenterAi.Api.Tests/Golden/{presentations,presentation-sample,presentation-ricoh,config}.json`.
  Commit `c2d44e9`.

### T8 — Presenter — done (pi gpt-5.6-terra:high)

- `Application/Presenting/{IPresenter,Presenter,PresenterState,PresenterSettings,PresenterSnapshot,PresenterEvents,LiveStartupException}.cs`:
  bounded `Channel<PresenterEvent>` with a single consumer, commands as `TaskCompletionSource`-backed
  `Task<bool>`, timers via `TimeProvider.CreateTimer` that only enqueue (generation counters drop stale
  fires), `WaitUntilIdleAsync` test hook, events raised on the loop task. `LiveSession` now throws
  `LiveStartupException` (code + raw error) on a startup error.
- Tests: 18 Node scenarios + 4 race tests (`FakeSession`, `FakeTimeProvider`); Application.Tests 47/47;
  Claude re-ran the Presenter suite 8× — no flakes. Commit `afa893d`.

### Review round 1 (pi gpt-5.6-terra:medium) — folded in

- Scope `326cab7..2f22c58`; report `docs/review/002-plan-002-impl-review-round-1.md`; ledger row in
  `docs/agentic/review-rounds-ledger.md` (A 0 · B 7 · C 0 · D 2).
- F1 (D, blocker) secrets guard: class widened to any `*key/*secret/*token/*password` setting in JSON/YAML/env/CLI
  spelling, whitespace/case tolerant, boundary stated in the script header, `scripts/secrets-guard.selftest.sh`
  (12 must-fail files + a must-pass repo) runs in CI before the guard. Commits `dee698a`, `9e0705d` (the widened
  guard first reported its own fixtures and the C# constant `ItemKey = "…"` once tracked — bare code identifiers
  are now reported only with the `sk-` prefix; fixture file excluded).
- F2 (D, major) `traceparent`: `Api/Errors/ProblemTrace.cs` is the single trace source (request activity id or a
  generated W3C id, always written to the header) used by `DomainExceptionHandler`, `Problems.Create`, the Dev auth
  challenge and the `/ws` 400. The framework's Problem Details writer overwrites `traceId` with
  `Activity.Current?.Id ?? TraceIdentifier`, so `ProblemTrace.Configure` is registered with `AddProblemDetails` to
  re-apply it after the defaults (found by an intermittent `AuthTests` failure in the full run). Commits `dee698a`,
  `d0a6ce0`.
- F3/F6/F7/F8/F9 and F4/F5 (B): oracles strengthened by pi gpt-5.6-luna:high with a mutation fail/pass
  demonstration per finding (Node-generated prompt goldens for every builder, fractional-RMS frames, subprocess
  startup test, record-level YAML round trips, custom fallback endpoint, independent §6 code→status map, required
  OpenAPI operations). Commits `1310738`, `55552cd`.
- Found while verifying F8: in Production a missing key printed the hosting logger's stack trace before the clean
  line — `Program.cs` now resolves the validated options before `app.Run()` (exit 1 + one stderr line, empty stdout).

### T10 — endpoints, static content, Dev auth, `/ws` bridge — done (pi gpt-5.6-terra:high, 2 rounds)

- First report deferred the mandatory bridge tests; Claude's §4.6 spot-check found five defects (writer loop
  released the slot so a superseded client's disconnect ended the *new* client's session; fire-and-forget
  back-pressure instead of a deterministic 1011; receive loop blocked on `StartAsync`; hand-built 400 without
  `traceparent`; unobserved fire-and-forget tasks) — all fixed in the continuation with tests
  (`BridgeTests` 5 Node scenarios + simultaneous clients, close during send, 1011, ping during connect;
  `BridgeSlotTests`; `BridgeContractTests` incl. exact canonical property sets). Bridge filter 17/17 × 6 runs.
  Claude routed the Dev auth 401 through `Problems.Create` (fourth producer). Commit `25e7863`.
- First real run (Claude, headless client against the .NET API) failed every context load: the content root
  `../../` keeps its trailing separator through `Path.GetFullPath`, so `IsWithinRoot` never matched —
  `FilePresentationRepository`/`AddFileContent` trim it; `FilePresentationRepositoryTests` fail on the old code.
  Commit `ab041a7`.

### T11 — real run through the .NET API (Claude) — server chain done, Chrome pending

- Secrets for the API live in `dotnet user-secrets` (`Upstream:Endpoint`, `Upstream:Key`, `Upstream:Fallback:Key`,
  copied from `.env` by a script, never printed); `dotnet run` uses the Development launch profile so they load.
- Headless run (a `ws` client speaking the frozen browser protocol, script in the session scratchpad) against the
  API on 47913 with the real Azure upstream: `ricoh-delivery-overview` slides 1 → 6 auto-advanced on silence
  (`advance → slide N` at +38.8 s, +101.8 s, +131.8 s, +195.9 s, +265.6 s), transcripts and 2,644 audio frames
  (210.3 s voiced / 54.1 s silent), `usage` every 60 s, `end` → `closed{reason: client_request, seconds: 265.2}`
  → `state idle`. **`usage.seconds = 265.2`** for slides 1–5.
- Operational gotcha: a concurrent `dotnet build`/`dotnet test` of the API project **kills** an API started with
  `dotnet run` from `bin/` (exit −1 = `Process.Kill`) or fails to copy over the locked dll — run the API from a
  `dotnet publish -o <scratch>` copy with `--contentRoot src/PresenterAi.Api` while agents build.
- Chrome run (AC4 proper: page on 47913, `→`/Space/Esc, `__presenterDebug`, usage pill) not yet done — the
  Claude-in-Chrome extension was not connected in this session.

### T12 — `presenter-cli smoke` / `run` (pi gpt-5.6-luna:high, restarted once after a termflow crash) — done

- `src/PresenterAi.Cli/{Program,SmokeCommand,RunCommand}.cs`: in-process `Program.RunAsync(args, IConfiguration,
  out, err, ct)`; configuration from environment, user-secrets (`presenter-ai-cli`) and `--Upstream:*`/`--Presenter:*`/
  `--Content:*` command-line settings; exit codes 0 / 1 (upstream or run failure) / 2 (usage, configuration).
- The `Presenter` factory moved out of `AddPresenterBridge` into `DependencyInjection.AddPresenter()` (Infrastructure);
  the API registers it in `Program.cs` and the CLI composes `AddUpstreamOptions → AddFileContent → AddLiveSessions →
  AddPresenter` — one construction path for both hosts.
- `tests/PresenterAi.Cli.Tests/CliTests.cs` (4): provider selection proven by distinct `Upstream:Model` /
  `Upstream:Fallback:Model` values observed in the fake's `session.start`; `run sample --stop-after-slide 2` against
  `FakeLiveServer`; missing key → one stderr line, exit 2; unknown provider → exit 2. Cli ×3 green, solution
  47 / 30 / 44 / 4. Commit `93f098a`.

### AC3 — live smoke through the .NET CLI (Claude) — done

- CLI user-secrets set from `.env` by a non-echoing script (`Upstream:Endpoint`, `Upstream:Key`,
  `Upstream:Fallback:Key`); the CLI ran from a `dotnet publish` copy so the concurrent T13 agent builds could not
  kill it.
- `smoke --provider azure`: `session.started` +698 ms, 87 audio deltas, 3.1 s voiced (first at +3,954 ms),
  transcript `Presenter AI smoke test successful, one two three four five.`, `closed reason=client_request`,
  **`usage.seconds=9`**, exit 0.
- `smoke --provider openai`: `session.started` +1,525 ms, 85 deltas, 3.4 s voiced (first at +4,082 ms), same
  transcript, `closed reason=close_requested`, **`usage.seconds=8`**, exit 0.
- `run ricoh-delivery-overview --stop-after-slide 2` (content root = repo root, the CLI default): slide 1 → 2 at
  +37.9 s, slide 3 announced at +100.0 s → `stop-after-slide reached; ending`, `MODEL:` transcripts and the `#`/`.`
  audio bars per slide, `usage seconds=59.2` at +60.5 s, `closed reason=client_request seconds=99`, exit 0.
- Note: `--content-root` is the *repository* root for the file store (`Content:RootDir` defaults to `.` in the CLI,
  unlike the API's `../../` relative to its content root) — passing `src/PresenterAi.Api` fails to find
  `presentations/`.

### T13 — `web/` workspaces (pi gpt-5.6-luna:high) — done

- Bun workspaces `shared`/`app`/`admin` (React 19, Vite 6, TS strict, Tailwind 3.4 shared preset, ESLint flat, vitest);
  generated `openapi-typescript` types + `openapi-fetch` client, `errorCodes.ts` from `x-error-codes`, `problem.ts`
  `isProblem`, `errorMessages.ts`; Dev-only zustand auth store `presenter-auth`; InkSpoke kit (`AdminLayout`,
  `AdminRoute`, `ThemeToggle`, `cn`, Tailwind tokens) with `Copied from InkSpoke … @ b83e691f` headers, no branding;
  admin = ComingSoon. Two-hop drift chain: checked-in `web/shared/openapi/v1.json` + `OpenApiTests.Checked_in_document_matches_live_document`
  + `generate:api` reading the snapshot (CI `git diff --exit-code -- web/shared/src/api`). Verified by Claude:
  `bun install --frozen-lockfile`, lint ×3, shared tests 3/3, both builds, `generate:api` with zero drift. Commit `cd93010`.

### Review round 2 (pi gpt-5.6-terra:medium, `2f22c58..906af83`) — folded in

- `docs/review/003-plan-002-impl-review-round-2.md`: F1/F2 D (session disposal class, unobserved shutdown), F3 D minor
  (guard boundary vs escaped JSON names — documented as outside the class), F4 B (stop-after-slide = Node parity, plan
  claim corrected), F5 B (backpressure oracle now fills the real channel). Round-1 classes verified at every site.
  Ledger row added. Fixes by pi gpt-5.6-terra:medium with mutation evidence; commit `795132f`. Solution after the fix:
  Application 51 / Infrastructure 30 / Api 46 / Cli 5, `-warnaserror` clean; Azure `smoke` re-run on the new
  lifecycle: `usage.seconds=9.2`, exit 0.

### T11 — Chrome run on the .NET API (Claude, Claude-in-Chrome) — done

- **Bug found on first load:** `Deck not found: /ricoh/index.html` for every deck — the `/decks/{**path}` 404 endpoint
  was matched by the implicit `UseRouting` at the top of the pipeline, so `StaticFileMiddleware` skipped the file
  ("Static files was skipped as the request already matched an endpoint"). `UseRouting()` now follows the static
  middleware; `PresentationEndpointTests.Deck_file_is_served_from_the_content_root` pins it (`795132f`). The headless
  client never fetched a deck, which is why T11's server run missed it.
- Run (API from a published copy on 47913, real Azure upstream): page loads, `server: connected`, deck adapter
  `showFn` 11 slides; Start → `connecting` → `presenting` at +1 s, microphone ready, 44.1 kHz context; slide 1 → 2
  auto-advanced at +39 s (deck followed, `deckIndex` 1); `→` → slide 3 + deck 3; Space → `paused` (+`pause-3`
  instruction), Space → `presenting` (`resume-3`); usage pill `59.6 s` at the 60 s tick; auto-advance to 4/11;
  Esc → `ending` → `closed: reason=client_request usage=98.6 s` → `idle`; **usage pill `98.6 s`**, 4,490 mic frames sent,
  transcript grouped by turn. AC4 met.
- Operational: Ctrl-C in the termflow terminal did not stop the API process (it kept 47913, the new instance queued);
  stop it by PID (`Stop-Process -Id <pid>` after `netstat -ano | findstr :47913`).

### T14 — React presenter page (pi gpt-5.6-terra:medium, 2 rounds) — done

- 1:1 port of `src/web/*`: `ws/bridgeClient.ts` (auth-first `{"type":"auth","ticket":"dev"}`, frozen commands, 500 ms ×2ⁿ
  reconnect capped at 10 s, idle snapshot on close), `audio/{capture,playback}.ts` + worklets (listen-only when the
  mic is denied), `deck/deckDriver.ts` (showFn / Reveal / sections / none), `store/presenterStore.ts`, four components,
  `routes/Present.tsx` with the same keys and `__presenterDebug`; `web/shared/src/auth/devSignIn.ts` is the single
  source of the Dev user and ticket. vitest: app 11 (mandatory `detects showFn deck`, `start sends presentation id`,
  `sends auth frame first`, `reconnects with backoff`, `continues listen-only when mic denied`, + `SlidePill.spec`).
- First round skipped the manual run; Claude's Chrome run hit "Maximum update depth exceeded" in `<SlidePill>`
  (zustand selector returning a fresh object under React 19). Continuation fixed the class (every selector returns a
  stored reference; `Present` no longer subscribes to the whole store), added `SlidePill.spec.tsx` (fails on the old
  selector with the max-depth error) and reformatted the one-line files. The generated client lacked the `{id}` path
  parameter — an API defect: `[FromRoute]` on `LoadAsync`, snapshot + `generated.d.ts` refreshed (`a4d84a7`), casts
  removed. Commit `d5bbc56`.

### T15 — Chrome run on the React app (Claude, Claude-in-Chrome) — done

- Vite on 47914 proxying to the API on 47913 (published copy incl. the review-2 fixes): Library lists both decks via
  the generated client; Sign in (Dev) shows "Dev user"; `/present/ricoh-delivery-overview` loads the deck (`showFn`,
  11 slides), `connected to server`; `S` → `connecting` → `presenting` at +1 s, transcript turns grouped, audio
  buffered (`bufferedMs` 161–227) with the mic **denied on this origin** (`micReady=false`, listen-only path exercised
  for real); auto-advance 1 → 2; `→` → slide 3 (deck followed); Space → `paused`, Space → `presenting`; Esc →
  `closed: reason=client_request usage=98.4 s` → `idle`; **usage pill `98.4 s`**. AC5 met except the spoken-question
  check, which needs a human voice (the mic was denied for 47914; Chrome automation cannot speak).
- Observations for review round 3 (not fixed): server `log` events appear twice in the React Log panel (`appended
  thinking slide-2-notes` ×2) while the headless client and the Node page receive them once; `deck adapter` logged
  twice (StrictMode double effect); `__presenterDebug().wsOpen` is derived from the presenter state, not the socket;
  the upstream transcribed a spurious `You: a dark` user turn from the server-side silence pump (no mic).

### Review round 3 (pi gpt-5.6-terra:medium, `906af83..41d093a`, new web scope) — folded in

- `docs/review/004-plan-002-impl-review-round-3.md`: F1 D major (route teardown leaves mic/AudioContext alive),
  F2–F4 D minor (StrictMode double deck load = the duplicated `deck adapter` log; detail request ignores errors;
  Library shows `detail` before `errorMessages[code]`), F5–F7 B (backoff schedule, text-message parsing, playback
  start oracles). No blocker on the .NET side; round-2 class fixes verified at every site. The duplicated *server*
  log lines have no client/store path — the fixer adds a stale-socket guard and a test; re-checked in Chrome after.
  Ledger row added. Fixes by pi gpt-5.6-terra:medium (web only).

### T16 — wiring audit (pi gpt-5.6-luna:medium scout) — done, docs/PR pending

- `docs/research/004-plan-002-wiring-audit.md` holds the six tables (DI → resolvers, option keys → readers, bridge
  frames → handlers on both sides, endpoints → callers, entry-point log lines, lifecycle) and the disposition.
  Real gaps fixed: `IDeckStore` had no consumer (the deck static root now comes from it), `Presenter:LogEvents` was
  bound but never read (feeds `LiveSessionOptions.LogEvents`), compose mapped only two of the `.env` keys (now
  `UPSTREAM_MODEL`/`UPSTREAM_VOICE`/`FALLBACK_OPENAI_KEY`/`ADVANCE_SILENCE_MS`/`LOG_EVENTS`; `.env.example` says
  `true/false` for the .NET host). False positives: `Auth:Dev:*` is read by `DevAuthHandler`; `upstream-error` is an
  internal event (wire frame = `error{code}`); `busy` is `error{code:"busy"}` + 1013 by design. `/api/config` and
  `pong` are Node-parity shapes without app callers.
- Fixes landed in `f434b47` (pi gpt-5.6-terra:medium, mutation evidence per finding, app vitest 18). Chrome re-check on
  `/present/sample`: every server log once, `deck adapter` once, Start/Pause/Prev/Next/Mute/End visible (the user
  reported the control bar was invisible — plain buttons on the dark theme — styled in the same commit), End →
  `closed usage=29.6 s` → `idle` with the audio context released (`__presenterDebug().audio === null`). During that run
  the user spoke to the presenter (`You: Can you speak Vietnamese` → answered) — the AC5 spoken-question check is done.

### Dark-theme text colour (Claude, user report "the transcript is dim") — done

- Root cause: neither app root nor any `dark:bg-*` surface set a dark text colour, so the transcript inherited the
  light-theme grey on a near-black card. Fixed as a class: base text on both app roots and on every surface with its
  own background (transcript, pills, cards, admin layout); verified by computed styles in Chrome. Commit `9a7b0ee`.

### T17 — fluid presenter layout + resizable split (user-added; pi gpt-5.6-luna:high) — done

- User survey decisions: `react-resizable-panels` v4 (`Group`/`Panel`/`Separator`, `useDefaultLayout`), horizontal
  split only (deck | transcript-over-log), full viewport height below the header, split remembered per browser.
- Implementation: `App.tsx` drops the `max-w-7xl` main (Library keeps its own `max-w-7xl p-6`); `Present.tsx` is
  `h-[calc(100vh-4rem)]` with 16 px gutters, `Group id="presenter-split"` (horizontal at `lg`, vertical below via a
  `matchMedia` hook), deck `70%` / side `30%`, pixel minimums on desktop (480 / 320) and percentage minimums when
  stacked (35 % / 20 %, so a phone-height viewport can still satisfy both), `Separator` focusable and keyboard
  resizable, `useDefaultLayout({ id: "presenter-split", onlySaveAfterUserInteractions: true })` behind a try/catch
  storage adapter that stores under the plain `presenter-split` key; Transcript and Log are `flex-1 min-h-0` regions.
  New dep `react-resizable-panels@4.13.2`; `vitest` gets `testSetup.ts` (jsdom `ResizeObserver` shim).
- Tests: `Present.layout.spec.tsx` (group id + two panels + `role="separator"`, deck in the first panel and transcript
  in the second, `presenter-split` read from localStorage). Mutation: removing `<Separator/>` fails the first test
  (`expected null not to be null`). App vitest 21, lint clean, both builds, `generate:api` no drift.
- Chrome check on `/present/sample` (viewport 1707×842, dark): section spans the full width with 16 px gutters
  (deck 1289 | separator 8 | side 379 px), section bottom = viewport bottom (no page scroll), deck iframe fills the
  pane height with the control bar below it, heading colour `rgb(243,244,246)`. Keyboard resize (ArrowLeft/Right on
  the separator) and a pointer drag both move the split and persist it (`{"deck":58.8,"side":41.2}`); reload restores
  it. Start → `presenting` (transcript + log flowing in the side pane) → End → `closed: reason=client_request
  usage=25.8 s` → `idle`. Not verified in a browser: the stacked layout below `lg` (the window resize was ignored by
  the maximised Chrome window); the Claude-in-Chrome `left_click_drag` does not emit pointer events, so the drag was
  driven with synthetic `PointerEvent`s.

### `connected via <label>` on every start (Claude, user request) — done

- Node (and the port until now) logged `connected via …` only when a fallback took over, so a live run gave no
  positive signal that the Azure primary answered (the user asked "azure or fallback?"). `Presenter` now logs
  `[info] connected via primary` on the first attempt and keeps `[warn] connected via fallback` for later attempts.
  Oracles: `Successful_primary_start_logs_which_upstream_answered` (fails with the old `attempt > 0` guard — verified
  by mutation) and the fallback test asserts the `warn` line. Application tests 52.

### Push + PR #1 (Claude, after the user's "go ahead") — CI green

- `feature/002-dotnet-core-port` pushed; PR https://github.com/PMTLabs/presenter-ai/pull/1 → `develop`.
- First CI runs: `web` green, `dotnet` red on one test —
  `Loads_context_whether_or_not_the_root_has_a_trailing_separator("\\")`: on the Linux runner a literal `\` is a
  file-name character, so the root became a missing directory. The theory now takes its suffixes from
  `Path.DirectorySeparatorChar` / `Path.AltDirectorySeparatorChar` (still `\` + `/` on Windows); it was the only
  hard-coded backslash separator in the test projects. `51e18d4`: `dotnet` and `web` green on both workflow runs.

## Plan 003 — retire the Node MVP (branch `feature/003-retire-node-mvp`)

### T1–T5 (pi gpt-5.6-luna:high) + orchestrator fixes — done

- Deleted `src/server`, `src/web`, `test/`, `scripts/{headless-run,live-smoke,browser-e2e}.mjs`, root `package*.json`
  (last at `2a0b6a1`). `Content:WebRoot` → `../../web/app/dist`, optional: static middleware + SPA fallback only when
  `index.html` exists, else `[info] web root … not found — API only; run "bun run dev:app" for the UI` and `/` → 404.
  Dockerfile: `oven/bun:1.3.14` stage builds `web/app`, runtime serves it from `/app/web` (`Content__WebRoot`).
  Docs: README, guide 001, `AGENTS.md`, `.env.example`, `docs/reference/002-node-mvp-retired.md`. Commit `a9ef1d6`.
- Tests: `WebRootTests` on a temp web root (present → `/`, `/present/sample`, `/index.html` 200 + `no-cache`; absent →
  `/` and `/present/x` 404, `/health` 200, `/decks/sample/index.html` 200, `/api/nope` non-HTML 404); `SpaFallbackTests`
  moved onto the same fixture. Agent mutations: inverted guard → 500 on `/`; wrong fallback file → 500. Orchestrator
  tightened the oracle: FluentAssertions' `ContainSingle("no-cache")` treated the string as the *reason*, so the header
  value was never checked, and `/` and `/present/sample` both hit the fallback — `/index.html` now covers the static
  middleware; mutating either `no-cache` site to `max-age=0` fails the test. Api tests 48.
- Runbook (agent): dev with `dist` → `/` serves `<div id="root">`, `/present/sample` 200 `Cache-Control: no-cache`;
  without → the info line, `/` 404, `/health` 200, deck 200. Docker image built (`check-dist: ok`). The agent's compose
  run from Git Bash mounted empty `presentations/` (Windows paths against the WSL daemon); rerun by the orchestrator from
  WSL (`/mnt/d/...`, `docker compose` v2.40): `api Up (healthy)`, `/health {"status":"ok"}`, `/` has `id="root"`,
  `/api/presentations` lists `ricoh-delivery-overview` (11) and `sample` (3), deck 200, no "not found" line in the logs.
- **Runbook step 6 found a production-build defect:** in Chrome against the container, Start did nothing —
  `AbortError: Failed to load worklet module script: data:video/mp2t;base64,…`. Vite compiles
  `new URL("./x.ts", import.meta.url)` only inside `new Worker()`; for `audioWorklet.addModule` it inlined the raw `.ts`
  as an asset with the MPEG-TS MIME type. Dev mode serves the worklets as modules, so every earlier Chrome run (Vite on
  47914) passed. Fix: both worklets import their URL with `?worker&url` (Vite 6 docs via Context7) → separate
  `capture-processor-*.js` / `playback-processor-*.js` chunks; `web/app/scripts/check-dist.ts` runs after `vite build`
  and fails the build if a chunk is missing or `data:video/mp2t` reappears (mutation: reverting one import → build exit 1).
  Commit `73ded1b`.
- Step 6 rerun after rebuilding the image: Dev sign-in on 47913, Start → `connected via primary` → `presenting`,
  `micReady: true`, `buf 105 ms`, transcript flowing, auto-advance to slide 2/3 (`deckIndex` 1), Esc →
  `closed: reason=client_request usage=53 s` → `idle`, audio released. Same-origin `/ws` and `/decks` proven.
- Gate at `73ded1b`: `-warnaserror` 0/0; tests 52/48/30/5; web lint clean, shared 3 / app 21, builds + `check-dist: ok`,
  `generate:api` no drift; secrets guard + self-test OK; AC1 greps empty (`web/bun.lock` mentions `vite-node.mjs` /
  `vitest.mjs` — dependency binaries, not MVP code).
- Operational: `docker compose` (v2) lives in WSL, not on the Windows CLI (`docker-compose` v5 standalone there); run compose
  from `wsl -e bash -lc 'cd /mnt/d/sources/demo/presenter-ai && docker compose --profile full …'` so bind mounts resolve.
- CI on PR #2 was red on `PresentationEndpointTests.Static_ui_has_no_cache_header`: the one remaining test that GET `/`
  on the factory default, which resolves to `web/app/dist` — built locally, never in CI (A-class miss of the T3 move).
  Class fix: `ApiFactory` now defaults `Content:WebRoot` to a non-existent path, so no test can pass because a local
  build exists; the test opts into `WebRootFixture`. Mutation: default client on `/` fails with 404 locally as in CI.
- PR https://github.com/PMTLabs/presenter-ai/pull/2 → `develop`: CI green at `524c6cc` (`dotnet` and `web` on both
  workflow runs). Merge awaits the user.

## 2026-09-22 — plan 004 (identity, persistence, auth-state Redis)

- Discovery: two scout agents (inkspoke auth/identity/Redis inventory at commit `b83e691f`; presenter-ai as-built
  auth/content/sessions sweep). Reports kept in the session scratchpad, not the repo — they are inventories of an
  external codebase.
- G1 2026-09-22 after 2 interview rounds (8 questions): scope is research step 0.5 + the auth slice of 0.6;
  presentations + sessions to Postgres with decks left on disk; Dev sign-in becomes a Development-only token
  endpoint and `DevAuthHandler` dies; config allowlist + `Admin:BootstrapEmails`; mandatory `/ws` ticket frame with
  an anonymous upgrade; API keys deferred (the CLI stays in-process); singleton presenter kept; Testcontainers in CI.
- External plan review `pr004-rev-1` (codex, gpt-5.6-sol medium): NO — 9 blockers + 2 improvements, all folded into
  revision 2. Three were factual errors in the draft, each re-verified against the code before fold-in:
  `StartAsync`/`LoadAsync` touch nine test files (not the two production sites cited) and `FilePresentationRepository`
  implements the interface T8 changes; `LoadedPresentation` carries the *content* of the context file
  (`FilePresentationRepository.cs:44-58`) which the schema had no column for; nothing exposes the chosen upstream, so
  `sessions.upstream` was unfillable. See `docs/review/005-plan-004-external-review.md` and the plan's §10 table.
- Design consequences worth remembering: the `/ws` ticket is claimed **before** the single-client CAS slot (an
  unauthenticated socket could otherwise park on the only slot and hand real users `busy`); refresh rotation is a
  conditional revoke with an affected-row check, not just a transaction; `IPresentationRepository` splits into an
  owner-scoped contract plus an ownerless `IPresentationImportSource` for the importer.
- G2 2026-09-22: plan approved at `docs/plan/004-identity-persistence-auth-state.md`, 14 tasks in three PRs.
  Implementation not started — it awaits an explicit instruction.

### PR-A (T1, T2, T3, T12-config) — `7be5bd4`, `1164c7d`, `b9cd186`

- Implemented by an external `pi` agent (`openai-codex/gpt-5.6-luna:high`); orchestrator reviewed the diff and
  verified every claim rather than accepting the report.
- Green at `b9cd186`: `dotnet build -warnaserror` 0/0; `dotnet test` 140 passed across five projects
  (Application 52, Api 48, Infrastructure 30, Integration 5, Cli 5); `secrets-guard: clean`.
- **Testcontainers needs `DOCKER_HOST=tcp://localhost:2375` on this Windows host** even though the `docker` CLI
  works unaided — the daemon is in WSL and Testcontainers does not discover it. With it unset all 5 integration
  tests fail, so the documented `dotnet test PresenterAi.slnx` breaks on a clean Windows checkout without it.
  `AGENTS.md` and the fixture failure message now name that exact value; the agent's original `npipe:` suggestion
  does not work here.
- Orchestrator fixes on top of the agent's work: the agent had silenced the whole transitive NuGet audit
  (`NuGetAuditMode=direct`) to get EF design-time tooling to restore. The real finding is
  `System.Security.Cryptography.Xml` — every stable version carries eight advisories and only 11.0.0 previews
  exist. Replaced with eight `NuGetAuditSuppress` entries by advisory id, so any *other* transitive advisory
  (including a runtime one from Npgsql or StackExchange.Redis) still fails the build; mutation-tested by removing
  one suppression and confirming the restore breaks. Also deduplicated the Docker help message into
  `Support/DockerHelp.cs` and added the admin origin (47915) to the CORS example.
- Verified rather than assumed: the Testcontainers fixtures are load-bearing (a dead `DOCKER_HOST` fails them with
  the intended guidance), the Redis claims are genuinely atomic (`StringGetDeleteAsync` = `GETDEL`,
  `When.NotExists` = `SET NX`), every column of plan §4.3 is mapped including `presentations.context` and
  `external_logins.provider_email_verified`, and ids are `prefix_` + 16 hex chars per conventions §3.
- Known forward risk handed to PR-B: `AddPersistence`/`AddRedis` connect and throw at *registration* time, so
  calling them from `Program.cs` would make all 48 `Api.Tests` need live Postgres and Redis. PR-B's brief
  therefore specifies eager config validation with a lazy connection, plus reachability on `/health`.

### PR-B (T4, T5, T6, T7, T11a) — `9d84159`, `a9eaaf7`, `e81b667`, `4159e7e`

- Implemented by external `pi` agents in two packages (auth stack, then tickets + frontend auth).
- Caught during review: the first auth package tried to add a `TestingDevCompatibility` middleware that
  authenticated every request under `ASPNETCORE_ENVIRONMENT=Testing`, which is a worse `DevAuthHandler`. It was
  stopped mid-flight; `ApiFactory` now mints real signed JWTs instead. The same package shipped **zero** tests for
  T4–T6 until sent back. Writing them exposed a real defect: dev sign-in let `AuthFailureException` escape as 500.
- Mutation-verified guards: the conditional refresh-token revoke, the unverified-email collision refusal, the
  refresh/logout `Origin` check, and ticket-before-CAS ordering on `/ws`.
- **Orchestrator error, fixed in `4159e7e`:** `e81b667` was committed with a required `ApiFactory` registration
  left unstaged, so HEAD was red (`BridgeContractTests.Expired_ticket_closes_4401`: "No service for type
  TestTicketStore") while the reported "176 green" had been observed on the working tree. Found on resume by
  stashing the stray change and re-running at HEAD. The true baseline was 177. Lesson: after committing, confirm
  `git status` is clean before quoting the working tree's test result as the commit's.

### PR-C part 1 (T8, T11b) — `0717d29`

- Two seams the plan left open were settled in the brief before dispatch. (1) `IPresenter` is a singleton and the
  DbContext is scoped under `ValidateOnBuild`/`ValidateScopes`, so the loader opens a scope per call. (2)
  `CliTests` runs the real `run` command off disk with no Testcontainers, so `presenter-cli run` stays
  file-backed.
- Round 1 reproduced (180 passing) but was not committable: four test-only ownerless shims in production types, no
  paging, the API container could still resolve the ownerless reader, and five oracle gaps. The decisive one was
  shown by mutation: passing `"someone-else"` as the owner from the bridge left all 73 API tests green, because
  the test double discarded the owner.
- Round 2 fixed all eight items. The orchestrator re-ran seven mutations across both rounds (owner filter on
  load/list, empty list, the bridge's owner in both directions, a fixed upstream label, `Skip(0)`, and the import
  source registered in the API) and every one fails a test. It also removed two test-harness barriers the agent
  added defensively: `PresenterTests` passed 8/8 runs without them, so the harness now matches HEAD.
- Green at `0717d29`: build 0/0; **183 passed** (Application 52, Infrastructure 30, Api 75, Integration 21, Cli 5);
  Api and Cli pass with no container; web lint/build/test (shared 5, app 22); OpenAPI drift green with no `/api`
  paths left; `secrets-guard: clean`.

### PR-C part 2 (T9, T10, T12-docs) — `e63a393`, `7332e81`, `4ac958d`

- **User decision (D5):** `presenter-cli run --owner <email>` records exactly like `/ws`. Without `--owner`, `run`
  stays file-backed and unrecorded. A local-file run has neither a user row nor a presentation row, and both are
  required foreign keys on `sessions`. **D6:** a failed start writes no row, because `sessions.upstream` is NOT NULL.
- Round 1 (fresh `pi` agent) reproduced 199 passing, but review found two recorder defects:
  - the worker could finalise a row twice. When `Closed` and disconnect cleanup were both queued, the second write
    replaced the upstream's close reason and billed seconds after the slot had been released;
  - an end that arrived between a queued and a processed begin dropped the row of a run that had really started.

  It also found four oracle gaps: waits that were satisfied at begin rather than at finalisation, the real
  `Detach` untested, a fallback test that could not tell session ids apart, and a sleep before a negative
  assertion. The first agent's context was 61% full, so round 2 went to a fresh agent together with `run --owner`.
- Found by the orchestrator while doing T12-docs: both Vite dev proxies still forwarded `/api` rather than `/v1`,
  so `bun run dev:app` could not reach sign-in or any `/v1` call. The gap dated from PR-B. Fixed in `7332e81`.
- Mutations the orchestrator re-ran itself (each file backed up and restored byte for byte by md5); all five fail
  a test:
  - finalise-once removed;
  - the bridge not awaiting the recorder barrier;
  - `run --owner` not awaiting `EndAsync`;
  - the importer never matching an existing row;
  - a disabled owner accepted.

  The agents reported a further nine (fallback label and session id, real and bridge detach, the begin/end race,
  context path stored instead of text, and others).
- Green at `4ac958d` (tree clean after commit): build 0/0; **206 passed** (Application 52, Infrastructure 31,
  Api 78, Integration 36, Cli 9). Api and Cli pass with no container. Web lint/build/test (shared 5, app 22).
  `secrets-guard: clean`.

### T13 wiring audit (orchestrator, direct)

Traced every plan-004 registration, config key and endpoint to a reader or caller, across the API graph
(`src/PresenterAi.Api/Program.cs`) and the three CLI graphs (`src/PresenterAi.Cli/Program.cs`). All three CLI graphs
build under `ValidateScopes` + `ValidateOnBuild`, except the file-backed `BuildServices`, which predates plan 004.

**Registrations → resolvers**

| Registration | Graphs | Resolved by |
|---|---|---|
| `PresenterAiDbContext` (`AddPersistence`) | API, run, import | `TokenService`, `SsoService`, `/health`, `SessionRecorder` (scope per write), `ImportCommand:30`, `RunCommand:37`, `OwnerResolver` |
| `IPresentationRepository` → `PostgresPresentationRepository` | API, run, import | `PresentationEndpoints`, the presenter's scoped loader (`DependencyInjection.cs:135`), `RunCommand:45` (`FindIdBySlugAsync`) |
| `ISessionRecorderFactory` (`TryAddSingleton`) | API, run, import (unused there) | `PresenterBridge` ctor → `PrepareRecorder` (`:264`), `RunCommand:60` |
| `IConnectionMultiplexer` (lazy) | API | `TicketStore`, `SsoCodeStore`, `SsoStateStore`, `/health` |
| `ITicketStore` | API | issued in `SessionEndpoints:31`, claimed in `PresenterBridge:156` |
| `ISsoCodeStore`, `ISsoStateStore` (+ `Lazy<>`) | API | `SsoService` |
| `SignInPolicy`, `TokenService`, `SsoService`, `HttpClient "sso"` | API | `AuthEndpoints`, `SessionEndpoints`; `SsoService:175` (policy), `:198` (client) |
| `IPresentationImportSource` | CLI file-backed, import | file loader in `AddPresenter(fileBacked: true)`, `ImportCommand:27`; `AuthTests:32` asserts the API cannot resolve it |

**`ISessionRecorder` in both `StartAsync` callers:** the bridge attaches in `PrepareRecorder` before
`StartAsync` (`PresenterBridge.cs:264-266`), begins on success (`:318`), and awaits `EndRecorderAsync` in `finally`
(`:102`). `RunCommand` attaches (`:188`), begins (`:196`), then `EndAsync` → `Detach` (`:244-248`). The no-owner `run`
has no recorder (D5).

**Config keys (§4.3) → readers:** `ConnectionStrings:{Postgres,Redis}` → `AddPersistence`/`AddRedis` and the
fail-fast checks (API `Program.cs:209-212`, CLI `Build{Run,Import}Services`). `Jwt:*` → `JwtSettings`
(`TokenService`, JwtBearer, `AuthEndpoints`). `OAuth:*` → `OAuthOptions` (`SsoService`), validated through
`OAuthSettings`. `Auth:Dev:*` → `AuthEndpoints:187`, `TokenService.UpsertDevelopmentUserAsync`.
`Auth:SignIn:*`, `Admin:BootstrapEmails` → `SignInPolicy` (and `TokenService` for the dev user).
`Cors:AllowedOrigins` → the CORS policy and `OriginGuard`. `RateLimiting:Enabled` → `AuthRateLimiting:63`.
`Session:*` → `SessionRedisOptions` (`TicketStore`, `SessionEndpoints`, `PresenterBridge:41-42`).

**Endpoints → callers:** every `/v1` route has a caller in `web/` and/or a test. Sign-in, SSO, refresh and logout
are called from `SignIn.tsx`, `AuthCallback.tsx` and `authStore.ts`. Presentations are called from `Library.tsx`
and `Present.tsx`, and `/ws` from `bridgeClient.ts`. `/v1/config` is covered by tests only; the old
`/api/config` had no web caller either (plan §4.4).

**Flagged and fixed:**
1. `GET /v1/auth/me` had no caller anywhere. The web client never needs it, because sign-in and refresh return the
   user.
2. `POST /v1/sessions/ticket` had a web caller but no test over HTTP. That left F8's ticket-issuance refusal of a
   disabled account unguarded.
   - One new integration test covers both routes:
     `AuthEndpointIntegrationTests.Current_user_and_session_ticket_routes_serve_the_signed_in_user_and_refuse_disabled_accounts`.
     It does a real dev sign-in, then checks `/me`, then issues a ticket and claims it through real Redis exactly
     once, then disables the user and gets 403 `auth.account_disabled` from both routes. An anonymous ticket request
     gets 401.
   - Mutations: the ticket route skipping the user reload, a ticket issued for the wrong user, and `GetUserAsync`
     not enforcing `is_disabled`. Each one fails the test, and both files were restored byte for byte (md5).
3. `OAuth:Microsoft:TenantId` was bound by both OAuth classes but read by nothing. The tenant already lives in the
   Microsoft endpoint URLs (`/common/`). The key is removed from both classes and `appsettings.Example.json`; this is
   plan deviation **D7**.

**Noted, not changed (no caller missing, low risk):**
- `Admin:BootstrapEmails` is normalised in two places: `SignInPolicy` for SSO and `TokenService` for the dev user.
- The dev sign-in reads `Jwt:RefreshTokenDays` raw (default 30), while the other routes use `IOptions<JwtSettings>`.
- `OAuthSettings` (the validator) and `OAuthOptions` (the reader) bind the same section with identical shapes, so a
  field added to only one of them would drift.
- The import graph registers the unused `ISessionRecorderFactory` through `AddPersistence`.

**Not run (needs a live upstream and a browser, so it is for the user):** plan T13's Serilog-at-debug cycle, sign-in
→ import → present → close with one log line per entry point, together with runbook §7 steps 8–16.

Green after T13: build 0/0; **207 passed** (Application 52, Infrastructure 31, Api 78, Integration 37, Cli 9). Api
and Cli pass with no container. `secrets-guard: clean`.

### PR #3 opened (user: "One PR into develop"); first CI run found a bridge race

- `feature/004-identity-persistence` was pushed and https://github.com/PMTLabs/presenter-ai/pull/3 opened against
  `develop`, as AGENTS.md says. Handoff 010 had said `master`, which was wrong.
- **What CI showed:** `dotnet` was red on the `pull_request` run and green on the `push` run of the same commit.
  `BridgeSessionRecorderTests.Two_sequential_runs_do_not_leak_handlers` timed out waiting for the second recorder.
- **Root cause (a production defect, not only test timing):**
  1. The bridge's `finally` awaited `IPresenter.EndAsync`, which returns while the presenter is still `ending`.
  2. `idle` comes one loop hop later, when the upstream close queued behind the End command is handled.
  3. The slot was released inside that hop. A browser reconnecting in that window found `PrepareRecorder` refusing
     (the state was not idle), yet its `start` queued behind the close and presented anyway, **unrecorded**.
  4. The recorder barrier could also finish before the upstream's `Closed` (billed seconds, close reason) reached
     it.
- **Fix:** `ObserveEndAsync` now waits (up to 5 s) for the presenter's `idle` state before the recorder barrier and
  the slot release. That makes the F6 comment true.
- **Regression test:** `Disconnect_holds_the_slot_until_the_presenter_is_idle_so_the_next_run_is_recorded`.
  - It holds the real presenter between `ending` and `idle` through a `Closed` handler. The hold also lets go early
    if the bridge reaches the recorder barrier first.
  - It then asserts: a connect during the hold is told `busy`; the recorder saw `Closed` before its barrier; and the
    next run gets its own recorder.
  - Mutations, both failing it every time and restored by md5: dropping the idle wait (5/5 runs), and ending the
    recorder before the idle wait (3/3).
- **Same pattern fixed in two more tests:** reconnecting straight after a disconnect can legitimately be told
  `busy`, because the slot is released after cleanup. The two recorder tests that reconnect now use
  `BridgeTestSupport.ConnectWhenFreeAsync`; the integration tests already retried.
- **Stability:** the Api suite passed 10/10 consecutive runs (79 tests); the session integration tests passed 3/3.
- **Pre-existing flake, not fixed here (predates plan 004):**
  - `PresenterTests.Last_slide_silence_sends_wrap_up_then_closes` (and once `Wrap_up_without_audio_ends_after_fallback`)
    asserts `idle` after one `Flush()`.
  - The harness's "two barriers and a yield" (`Presenter.WaitUntilIdleAsync`) can run before the close hop.
  - It failed 3/15 runs on this branch and 1/15 on `origin/develop`, checked in a throwaway worktree.
  - **User decision (2026-09-22):** fix it in its own small PR into `develop` after PR #3 merges, not inside PR #3.

### PR #3 external implementation review, round 1 (`docs/review/006`)

- **Reviewers:** two read-only `pi` agents running gpt-5.6-sol medium, one for identity and one for persistence.
- **Findings:** 20 in total (A 3 · B 6 · C 2 · D 9).
- **Triage** (every claimed blocker re-traced in the code):
  - 9 blockers are confirmed;
  - P-02 is lowered to an improvement (**D9**);
  - P-03 is disputed as design (**D8**, best-effort recording);
  - I-10 (trusted forwarded headers) is deferred to the deployment plan.
  The user confirmed D8, D9 and the I-10 deferral.
- **Fixes:** dispatched to two `pi` implementers (gpt-5.6-terra high) in parallel worktrees, both under
  `.claude/worktrees/`:
  - `fix-identity`: I-01–I-04, I-07–I-09 and P-05;
  - `fix-persist`: P-01, I-05, I-06, P-02–P-04 and P-06–P-10.
- **Fixes landed** (cherry-picked onto `feature/004-identity-persistence`): `8f1bb07` bridge, `e2049d6` CLI,
  `9b656b3` schema, `a86d247` API contract, `48cca65` auth validation, `94400e8` web, `00e12eb` config.
  - Each implementer needed a second pass after Claude's verification. The findings Claude sent back are listed
    in `docs/review/006` under "Fixes".
  - After integration, Claude added `ccd2a23`. The new `Jwt_rejects_a_token_not_yet_valid_beyond_the_clock_skew`
    failed 3/30 full-suite runs: `nbf` is written in whole seconds, so `now + 31 s` could validate inside the 30 s
    skew. The margin is now 45 s, and setting `ClockSkew` to the 5-minute default still fails the test.
- **Verification on the integrated branch:**
  - build 0/0;
  - .NET: Application 52, Infrastructure 31 + 3 skipped, Api 119, Cli 11, Integration 39 + 1 skipped;
  - web: lint passes; shared 10 and app 22 tests pass; both builds pass;
  - compose config, the secrets guard and `git diff --check` pass;
  - the Api suite passed 30/30 runs after `ccd2a23`.
- **Observed, not yet explained:** `BridgeSessionRecorderTests.Disconnect_holds_the_slot_until_the_presenter_is_idle_so_the_next_run_is_recorded`
  timed out once in about 21 runs on the *old* bridge code, in the `fix-identity` worktree. The timeout was in
  `ConnectWhenFreeAsync`: the slot was still held more than 5 s after detach.
  - On the integrated bridge it passed 60/60 runs.
  - The bounded writer close in `8f1bb07` is a plausible cure, because `DisposeAsync` could wait on a peer that
    never answers. This is not proven; watch CI.

### PR #3 external implementation review, round 2 (`docs/review/007`)

- **Reviewers:** two fresh read-only `pi` agents running gpt-5.6-sol medium, on the round-1 fix diff
  `032f2eb..06537c3`.
- **Findings:** 16 in total (A 1 · B 10 · C 0 · D 5). Each reviewer claimed 3 blockers; after re-tracing, none
  blocks merging.
  - Three real defects were lowered to improvements and fixed: a sign-out during an in-flight refresh did not
    stick, the 401 interceptor would replay the one-time SSO code, and rate-limit headers were chosen by path
    suffix.
  - R2-P-02 (the 1009 close racing the writer) and R2-I-08 (exact skew pinning) are disputed; four test-hardening
    items are deferred.
- **Fixes landed:** `b7a0753` web, `f6886b2` rate-limit metadata, `1e2f537` tests, `b3f4811` CLI import and the
  bridge comment, by one `pi` implementer (gpt-5.6-terra high) in the `fix-r2` worktree.
  - Claude removed the implementer's production change for R2-I-06 (token-route body checks and a 30 MiB limit),
    and tested the 413 and 415 mappings at `DomainExceptionHandler` instead.
  - Claude found a routing gap: a non-JSON `POST /v1/auth/sso/token` returns 404, not 415, because the global
    `MapFallback` wins when the content-type policy rejects the endpoint. It predates PR #3; follow-up.
  - Claude cut the Api suite from 87 s back to 4 s: a CORS row and a rate-limit row were waiting on the test
    host's unreachable database or code store.
- **Verification:** build 0/0; .NET Application 52, Infrastructure 31 + 3 skipped, Api 137, Cli 11, Integration
  40 + 1 skipped; web shared 18 and app 22, lint and both builds; four orchestrator mutations, each caught.
- **No round 3.** The follow-ups are listed in `docs/review/007` under "Fixes".

## 2026-09-24 — plan 010 T13 live Trainer-mode run (Chrome, React app, Azure `gpt-live-1`, reviser `gpt-6-sol`)

Automated in Chrome: a page script feeds `gpt-audio-1.5` TTS clips (voice `marin`) into a fake microphone and logs
every non-audio bridge frame; the owner listened on headphones. Run on the plan 011 build (010 merged in).

| # | Result |
|---|---|
| 1 | Pass: Script versions lists v1 `import`, current |
| 2 | Pass: `trainer: on (voice: yes)`; switch on |
| 3 | **Failed first** (no `revise_script`: "also mention…" and "please update the script…" were spoken as narration). Fixed `f1ed848`, then pass: "Shall I add that to the script?" |
| 4 | Pass: "yes" → "Got it, slide 2 is being updated", hold, no advance. Two early attempts timed out because the automation's "yes" came late or overlapped the question (test timing, not product) |
| 5 | Pass: v4 `live_edit` in 2.7 s; slide 2 restarted with "The program started in 2020" |
| 6 | Pass: a real question was answered as Q&A (no tool call, no version); an edit request answered "No." → `declined`, "sticking with the current script", no version |
| 7 | Pass: Train on this on that answer → v5 in 2.8 s, slide 2 (current) replayed |
| 8 | Pass: slide 5 feedback on slide 3 → talk continued, v6 in 3.1 s; the retry (v9) was narrated on slide 5 ("every quarter, a roadmap review") |
| 9 | Pass: panel "1 pending edit will apply after this revert: edit_4 (slide 5, processing)"; v8 `revert`, then v9 on top; slide 4 unchanged, so no replay |
| 10 | Pass (Vietnamese deck): "Có" confirmed; v2 summary and narration in Vietnamese ("Biểu bì là lớp ngoài cùng của da và không có mạch máu."); Train on this → v3 in Vietnamese |
| 11 | **Failed first** (no spoken failure; chip kept the last talk's "Updated — v9"). Fixed `c10c867`, `8015a07`, `f09a797`, then pass with `Training__ReviserTimeoutSeconds=10` and a whole-deck "twice as long" edit: "I couldn't apply that change to the script", chip "Couldn't update — timed out", script unchanged |
| 12 | Pass: End, Start → `script_version` 9 |

- **Reviser:** 2.0–3.3 s and 894–1,538 tokens in / 267–373 out for one slide; a whole-deck tone rewrite took 9.7 s
  (3,859 / 1,775); the two timed out at 10.0 s.
- **Usage:** 165 + 622.2 + 5 + 247 + 183.4 (Ricoh) + 161.2 (Vietnamese) ≈ 1,384 s.
- **Defects fixed on the branch:**
  - `f1ed848`: only the backend instructions knew about script edits, so the realtime model answered "also mention
    X" itself. The front instructions now say to delegate a script-change request.
  - `c10c867`: edit ids restart at `edit_1` every talk, but the web store kept the last talk's terminal edits and
    dropped the new frames as regressions. The store clears edits when a talk leaves idle.
  - `8015a07`, `f09a797`: a failed edit that released the held slide appended the notice and then replayed with
    "Stop whatever you are saying now", so the notice was never heard. Any replay due (now, at resume, or after an
    Ask exchange in plan 011) now leads with the notice.
  - `86b6dfb`: the edit-pending notice quoted English words, spoken verbatim in the Vietnamese talk.
- **Not fixed, for the owner:**
  - Train on this in an Ask exchange sent only the question's late last fragment ("lâu"); the answer also starts
    with the filler "One moment." The reviser still got it right from the answer.
  - After "yes", the model calls `revise_script` again; the call is de-duplicated ("running"), so it is harmless.
  - The model paraphrases the confirmation question (Vietnamese: "Mình sẽ thêm ý đó vào nội dung.", a statement).
  - The revert panel keeps "will apply after this revert: edit_4 (applied)" after the edit applied.
- **Cleanup:** Ricoh reverted to v1 text (v11 `revert`), Khóa 2 Bài 1 reverted to v1 (v4 `revert`).
