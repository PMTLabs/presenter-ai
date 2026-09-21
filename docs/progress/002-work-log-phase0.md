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

### Review round 1 (pi gpt-5.6-terra:medium) and T10 (pi gpt-5.6-terra:high) — in progress
