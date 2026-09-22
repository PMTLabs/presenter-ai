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

### Review round 3 (pi gpt-5.6-terra:medium, `906af83..41d093a`, new web scope) — in progress

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
