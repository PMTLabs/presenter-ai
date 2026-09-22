# 004 — Plan 002 T16 wiring audit (emission → consumption)

**Scout:** `pi --model openai-codex/gpt-5.6-luna:medium`, read-only, 2026-09-21, branch `feature/002-dotnet-core-port` @ `41d093a`.
**Rule applied:** `~/.claude/docs/07-integration-boundary-audit.md` §5 — every new entity traced from emission to consumption; **NO CALLER** rows are the findings.

## Disposition (orchestrator)

| Audit finding | Verdict | Action |
|---|---|---|
| `IDeckStore` registered, never resolved | real | `Program.cs` now takes the deck static root from `IDeckStore.DeckRoot` |
| `Presenter:LogEvents` bound, never read; `LiveSessionOptions.LogEvents` unbound | real | `AddLiveSessions` builds `LiveSessionOptions` from `PresenterOptions.LogEvents` |
| `Auth:Dev:Enabled/UserId/Email` "no binding" | **false** — `DevAuthHandler.cs:20,27,28` reads them via `IConfiguration` (audit grepped for an options class only) | none |
| `.env.example` keys not wired to the .NET host | partly real — compose mapped only `UPSTREAM_ENDPOINT`/`UPSTREAM_KEY` | compose now maps `UPSTREAM_MODEL`, `UPSTREAM_VOICE`, `FALLBACK_OPENAI_KEY`, `ADVANCE_SILENCE_MS`, `LOG_EVENTS` (`true/false` for .NET); `.env.example` documents both; `PORT` stays Node-only (the .NET port is Kestrel's named endpoint) |
| `GET /api/config` tests-only | by design — frozen Node endpoint kept for parity (Node page did not call it either) | none |
| frozen `upstream-error` never emitted | **false** — `upstream-error` is the Presenter's internal event; the wire frame is `error{code}` (plan §4.3 line 13, Node `index.js:129`) | none |
| `busy` frame has no server emitter | by design — the server sends `error{code:"busy"}` + close 1013 (plan §4.3); the React parser's `busy` case is dead code | left for a later cleanup |
| React `pong` handler without a `ping()` caller | parity — the Node page has the same shape | none |
| plan T16 verify: "audit table in the work log with no NO CALLER rows" | this document + the work-log entry are the table; after the actions above the remaining rows are by-design parity items listed here | — |

---

# T16 wiring audit

Scope: branch `feature/002-dotnet-core-port`, commit `41d093a`. Read-only audit; no tests or servers run. `.env` was not opened.

## DI registrations → resolvers

| Registration/emission | Resolver / consumer | Host |
|---|---|---|
| `IOptions<UpstreamOptions>` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:18-26` | `UpstreamRoutes` factory resolves `.Value` at `src/PresenterAi.Infrastructure/DependencyInjection.cs:31-32`; API startup validates at `src/PresenterAi.Api/Program.cs:131`; CLI smoke resolves at `src/PresenterAi.Cli/SmokeCommand.cs:23` | Api / Cli |
| `IOptions<PresenterOptions>` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:28-29` | `IPresenter` factory resolves it at `src/PresenterAi.Infrastructure/DependencyInjection.cs:71`; `/api/config` minimal-API parameter at `src/PresenterAi.Api/Endpoints/ConfigEndpoints.cs:11-12` | Api / Cli (factory) |
| `UpstreamRoutes` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:31-32` | `IPresenter` factory at `src/PresenterAi.Infrastructure/DependencyInjection.cs:70`; `/api/config` parameter at `src/PresenterAi.Api/Endpoints/ConfigEndpoints.cs:11`; CLI smoke at `src/PresenterAi.Cli/SmokeCommand.cs:24` | Api / Cli |
| `IOptions<ContentOptions>` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:42-43` | string-root factory resolves `.Value` at `src/PresenterAi.Infrastructure/DependencyInjection.cs:44-48`; API also resolves it at `src/PresenterAi.Api/Program.cs:59-61` | Api / Cli |
| `string` content root — `src/PresenterAi.Infrastructure/DependencyInjection.cs:44-48` | `IPresentationRepository` factory at `src/PresenterAi.Infrastructure/DependencyInjection.cs:49-50`; `IDeckStore` factory at `src/PresenterAi.Infrastructure/DependencyInjection.cs:51-52` | Api / Cli |
| `IPresentationRepository` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:49-50` | `IPresenter` factory at `src/PresenterAi.Infrastructure/DependencyInjection.cs:68,78`; `ListAsync` minimal-API parameter at `src/PresenterAi.Api/Endpoints/PresentationEndpoints.cs:24`; `LoadAsync` parameter at `src/PresenterAi.Api/Endpoints/PresentationEndpoints.cs:38` | Api / Cli |
| `IDeckStore` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:51-52` | **NO CALLER** — no constructor, `GetRequiredService`, or endpoint parameter found | Api / Cli |
| `TimeProvider` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:58` | `IPresenter` factory at `src/PresenterAi.Infrastructure/DependencyInjection.cs:72`; `LiveSessionFactory` constructor at `src/PresenterAi.Infrastructure/Live/LiveSessionFactory.cs:9-16` | Api / Cli |
| `LiveSessionOptions` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:59` | `LiveSessionFactory` constructor at `src/PresenterAi.Infrastructure/Live/LiveSessionFactory.cs:9-16` | Api / Cli |
| `ILiveSessionFactory` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:60` | `IPresenter` factory at `src/PresenterAi.Infrastructure/DependencyInjection.cs:69`; CLI smoke at `src/PresenterAi.Cli/SmokeCommand.cs:36` | Api / Cli |
| `IPresenter` — `src/PresenterAi.Infrastructure/DependencyInjection.cs:66-80` | `PresenterBridge` constructor at `src/PresenterAi.Api/Realtime/PresenterBridge.cs:22-25`; CLI `RunWithPresenterAsync` parameter supplied by `src/PresenterAi.Cli/RunCommand.cs:24` | Api / Cli |
| `PresenterBridge` — `src/PresenterAi.Api/Realtime/PresenterBridge.cs:338-340` | `/ws` minimal-API parameter at `src/PresenterAi.Api/Realtime/PresenterBridge.cs:328`; tests resolve it at `tests/PresenterAi.Api.Tests/BridgeSlotTests.cs:20` and `BridgeTests.cs:126` | Api |
| `DevAuthHandler` scheme — `src/PresenterAi.Api/Program.cs:35-40` | Authentication middleware at `src/PresenterAi.Api/Program.cs:78-79`; `RequireAuthorization` on API groups (`src/PresenterAi.Api/Endpoints/PresentationEndpoints.cs:12`, `ConfigEndpoints.cs:13`) and `/ws` (`PresenterBridge.cs:329`) | Api |
| Problem Details — `src/PresenterAi.Api/Program.cs:47-48` | `UseExceptionHandler` at `src/PresenterAi.Api/Program.cs:57`; `Problems.Create` is used by bridge at `PresenterBridge.cs:43-44` | Api |
| OpenAPI — `src/PresenterAi.Api/Program.cs:49-54` | `MapOpenApi` at `src/PresenterAi.Api/Program.cs:85`; generator/client tooling reads `/openapi/v1.json` (README.md:149); test fetches it at `tests/PresenterAi.Api.Tests/OpenApiTests.cs:72-74` | Api / web tooling / tests |
| Serilog host registration — `src/PresenterAi.Api/Program.cs:25-28` | framework/application `ILogger<T>` consumers, e.g. `PresenterBridge` at `PresenterBridge.cs:17` and `LiveSession` at `LiveSession.cs:20`; **NO CALLER** for `PresenterOptions.LogEvents` specifically | Api |

## Option keys → readers

| Key | Binding | Property reader / result |
|---|---|---|
| `Upstream:Endpoint`, `Key`, `Model`, `Voice` | `UpstreamOptions` bound at `src/PresenterAi.Infrastructure/DependencyInjection.cs:18-26` | endpoint/key validated there; route construction reads all at `src/PresenterAi.Infrastructure/Live/UpstreamRoute.cs:15-30,48`; `Voice` is exposed via `UpstreamRoutes` and consumed by presenter factory at `DependencyInjection.cs:79` and config endpoint at `ConfigEndpoints.cs:12` |
| `Upstream:Fallback:Endpoint`, `Key`, `Model` | nested `FallbackOptions` bound by the same section | `UpstreamRoutes.From` reads them at `src/PresenterAi.Infrastructure/Live/UpstreamRoute.cs:33-45`; route exists only when fallback key is nonblank |
| `Presenter:AdvanceSilenceMs` | `PresenterOptions` bound at `src/PresenterAi.Infrastructure/DependencyInjection.cs:28-29` | presenter settings at `DependencyInjection.cs:79`; config response at `ConfigEndpoints.cs:12`; snapshot/config UI data flows from there |
| `Presenter:LogEvents` | bound as `PresenterOptions.LogEvents` | **NO CALLER** — property is never read; `LiveSessionOptions.LogEvents` is a separate unbound property |
| `Content:RootDir` | `ContentOptions` bound at `src/PresenterAi.Infrastructure/DependencyInjection.cs:42-43` | root path at `DependencyInjection.cs:46-47`; CLI supplies `.` only when absent at `src/PresenterAi.Cli/Program.cs:66-71`; API derives deck path at `Program.cs:59-65` |
| `Content:WebRoot` | same binding | API static-file provider at `src/PresenterAi.Api/Program.cs:60-72` |
| `Auth:Dev:Enabled`, `UserId`, `Email` | present in `src/PresenterAi.Api/appsettings.json:28-33`, Development and Example files | **NO CALLER / NO BINDING** — `DevAuthHandler` uses its fixed dev identity; no `Auth` options class or configuration read found |
| `Serilog:MinimumLevel` | Serilog reads configuration at `src/PresenterAi.Api/Program.cs:25-28` | Serilog sink/filter configuration; this is consumed |
| `Kestrel:Endpoints:Http:Url` | ASP.NET host configuration in `src/PresenterAi.Api/appsettings.json:2-9`; compose overrides it at `docker-compose.yml:24` | Kestrel host binding, not application options |
| `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS` | compose `docker-compose.yml:22-24` | ASP.NET hosting environment/URL configuration |
| `Upstream__Endpoint`, `Upstream__Key`, `Content__RootDir` | compose `docker-compose.yml:25-27` | normalized environment keys bind to the corresponding options above |
| `UPSTREAM_ENDPOINT`, `UPSTREAM_KEY`, `UPSTREAM_MODEL`, `UPSTREAM_VOICE`, `ADVANCE_SILENCE_MS`, `LOG_EVENTS`, `PORT` | documented in `.env.example:7-24` | **NO CALLER** under the .NET host: CLI reads generic environment variables but these legacy names are not colon-delimited .NET keys; `PORT`, `ADVANCE_SILENCE_MS`, and `LOG_EVENTS` have no reader |
| `POSTGRES_*` | compose `docker-compose.yml:5-12` | **NO CALLER** in the .NET application; only the postgres container consumes them |
| README `UPSTREAM_*` documentation | README.md:45-55 | **NO CALLER** for the legacy names in the .NET configuration path; README also documents the old Node environment convention |
| keys in `appsettings.Example.json` not otherwise listed | `Upstream`, `Presenter`, `Content` bind as above | `Auth.*` is **NO CALLER / NO BINDING**; `Serilog:MinimumLevel` is consumed by Serilog |

## Bridge messages → handlers (both sides)

| Frame | Server emission/handler | Node page (`src/web/app.js`) | React (`bridgeClient.ts` / `Present.tsx` / store) |
|---|---|---|---|
| client `auth` | handler `PresenterBridge.cs:133-135`; ignored except debug log | **NO CALLER** — old page sends no auth frame | emitted at `web/app/src/ws/bridgeClient.ts:69-72`; server ignores it |
| client `start` | handler `PresenterBridge.cs:136-145` → `IPresenter.StartAsync`, implementation `Presenter.cs:92-93` | sender is `src/web/app.js:287-288`; **NO CALLER** for server response beyond state/error | sender `bridgeClient.ts:96-98`; start called `Present.tsx:91` |
| client `next` / `prev` | `PresenterBridge.cs:146-147` → `Presenter.NextAsync`/`PrevAsync`, `Presenter.cs:95-100` | senders `app.js:321`, `app.js:322`; **NO CALLER** row is not applicable | senders `bridgeClient.ts:99-103`; UI `Present.tsx:194,197` |
| client `goto` | `PresenterBridge.cs:148-151` → `Presenter.GotoAsync`, `Presenter.cs:101-102` | `app.js:60` deck navigation sends it | `bridgeClient.ts:105-107`; `Present.tsx:70` |
| client `pause` / `resume` | `PresenterBridge.cs:152-153` → `Presenter.PauseAsync`/`ResumeAsync`, `Presenter.cs:104-108` | `app.js:309-310` | `bridgeClient.ts:108-112`; `Present.tsx:188-191` |
| client `mute` / `unmute` | `PresenterBridge.cs:154-155` → `Presenter.MuteAsync`/`UnmuteAsync`, `Presenter.cs:110-114` | `app.js:315-316` | `bridgeClient.ts:114-118`; `Present.tsx:201-207` |
| client `end` | `PresenterBridge.cs:156` → `Presenter.EndAsync`, `Presenter.cs:119-120` | `app.js:323` | `bridgeClient.ts:120-122`; `Present.tsx:210` |
| client `ping` | `PresenterBridge.cs:157` emits `pong` | handler `app.js:184-185` (no sender) | sender `bridgeClient.ts:123-125`; handler `bridgeClient.ts:168-170`; **NO CALLER** if “pong” is judged against an actual ping call in React (method exists but Present does not call it) |
| client binary audio | `PresenterBridge.cs:99-102` → `Presenter.SendAudioAsync`, `Presenter.cs:116-117` | `app.js:148-155` sends PCM; receive binary enqueues at `app.js:119-122` | `bridgeClient.ts:126-131` sends; `Present.tsx:48` receives/plays |
| server `state` | bridge emitter `PresenterBridge.cs:27`, initial snapshot `:59`; handler `app.js:159-160`; React `bridgeClient.ts:145-148` → `Present.tsx:31-32` → store `presenterStore.ts:21-27` | handled | handled |
| server `slide` | emitter `PresenterBridge.cs:28`; `app.js:162-166`; React `bridgeClient.ts:149-151` → `Present.tsx:33-35` → store message | handled | handled |
| server `transcript` | emitter `PresenterBridge.cs:30`; `app.js:167-169`; React `bridgeClient.ts:152-154` → `Present.tsx:37-45` → store transcript branch `presenterStore.ts:39-58` | handled | handled |
| server `usage` | emitter `PresenterBridge.cs:31`; `app.js:170-172`; React `bridgeClient.ts:155-157` → store `presenterStore.ts:30` | handled | handled |
| server `log` | emitter `PresenterBridge.cs:33`; `app.js:178-180`; React `bridgeClient.ts:158-160` → store log branch `presenterStore.ts:60-73` | handled | handled |
| server upstream `error` | emitter `PresenterBridge.cs:34` uses `{type="error"}`; `app.js:181-183`; React `bridgeClient.ts:161-164` handles both `upstream-error` and `error` → Present/store | handled as `error` | handled as `error` |
| server `upstream-error` | **NO CALLER** — no bridge emitter uses this frozen name (only `error` is emitted at `PresenterBridge.cs:34`) | handler only via default/no dedicated case; React has a parser case but no server emitter | **NO CALLER** on server side |
| server `closed` | emitter `PresenterBridge.cs:32`; `app.js:173-177`; React `bridgeClient.ts:165-167` → `Present.tsx:42-46` | handled | handled |
| server `pong` | emitter `PresenterBridge.cs:157`; `app.js:184-185`; React `bridgeClient.ts:168-170` | handled | handler exists, but no React caller invokes `ping()` |
| server `busy` | **NO CALLER** as named: busy path emits `type="error", code="busy"` at `PresenterBridge.cs:213`, not `type="busy"` | old page handles it as ordinary `error`; no dedicated busy handler | `bridgeClient.ts:171-173` has a `busy` handler, but `Present.tsx` does not subscribe to `busy`; **NO CALLER** |
| server binary audio | emitter `PresenterBridge.cs:29`; old receive `app.js:119-122`; React receive `bridgeClient.ts:133-136` → `Present.tsx:48` | handled | handled |

## Endpoints → callers

| Route | Caller(s) |
|---|---|
| `GET /health` — `src/PresenterAi.Api/Endpoints/HealthEndpoints.cs:7` | compose healthcheck `docker-compose.yml:40`; `tests/PresenterAi.Api.Tests/OpenApiTests.cs:63` and health tests; **NO web application caller** |
| `GET /api/presentations` — `PresentationEndpoints.cs:13` | old page `src/web/app.js:65-67`; React generated client `web/app/src/routes/Library.tsx:14-16`; API endpoint tests `tests/PresenterAi.Api.Tests/PresentationEndpointTests.cs:10` |
| `GET /api/presentations/{id}` — `PresentationEndpoints.cs:16` | old page `src/web/app.js:81-89`; React `web/app/src/routes/Present.tsx:56-58`; endpoint tests `PresentationEndpointTests.cs:11-12` |
| `GET /api/config` — `ConfigEndpoints.cs:11` | API auth/endpoint tests (`tests/PresenterAi.Api.Tests/AuthTests.cs:14,31`, `PresentationEndpointTests.cs:13`); **NO CALLER** in `web/app`, `web/shared`, `src/web/app.js`, or CLI |
| `/ws` — `PresenterBridge.cs:328-331` | old page WebSocket `src/web/app.js:109-112`; React `web/app/src/ws/bridgeClient.ts:64-67`; bridge tests throughout `tests/PresenterAi.Api.Tests/Bridge*.cs` |
| `GET /openapi/v1.json` — `Program.cs:85` | OpenAPI test `tests/PresenterAi.Api.Tests/OpenApiTests.cs:72`; web client generation documented in README.md:149; generated declarations `web/shared/src/api/generated.d.ts` |
| `/decks/{**path}` 404 — `Program.cs:86-91` | **NO CALLER** as an HTTP 404 route; deck URLs are static-file requests produced by old `app.js:92` and React `Present.tsx:74`, which target the static `/decks` files instead |
| fallback SPA — `Program.cs:92-114` | browser navigation/React SPA routes; `tests/PresenterAi.Api.Tests/SpaFallbackTests.cs:10-31` |
| testing-only `/__test/throw`, `/__test/domain-error` — `Program.cs:116-123` | tests only (`ProblemDetailsTests.cs`); not production routes |

## Entry-point log lines

| Entry point | Result |
|---|---|
| `GET /health` (`HealthEndpoints.cs:7`) | **MISSING** entry log |
| `GET /api/presentations` (`PresentationEndpoints.cs:13,24`) | **MISSING** entry log |
| `GET /api/presentations/{id}` (`PresentationEndpoints.cs:16,38`) | **MISSING** entry log |
| `GET /api/config` (`ConfigEndpoints.cs:11`) | **MISSING** entry log |
| `/ws` accept (`PresenterBridge.cs:39,48`) | **MISSING** entry log; busy/non-upgrade paths also have no entry log |
| CLI `smoke` (`Program.cs:37-38`, `SmokeCommand.cs:13`) | **MISSING** structured/log entry line; it writes output only after connect at `SmokeCommand.cs:76` |
| CLI `run` (`Program.cs:38`, `RunCommand.cs:10`) | **MISSING** structured/log entry line; output begins through event callbacks, not command entry |
| `Presenter.StartAsync` (`Presenter.cs:92`) | **MISSING** entry log; first internal log is only ignored-start/error paths (`Presenter.cs:327,338`) |
| `Presenter.EndAsync` (`Presenter.cs:119`) | **MISSING** entry log; close failure log is `Presenter.cs:773` |
| `LiveSession.ConnectAsync` (`LiveSession.cs:75`) | **MISSING** at method entry; first log is after socket connect at `LiveSession.cs:95` |
| `LiveSession.CloseAsync` (`LiveSession.cs:162`) | **MISSING** at method entry; timeout warning at `LiveSession.cs:187` only |

## Events / hosted services / lifecycle

| Chain / check | Audit result |
|---|---|
| `PresenterBridge → Presenter → ILiveSession` | Bridge subscribes to all presenter events in `PresenterBridge.cs:27-34`; bridge `DisposeAsync` disposes presenter at `PresenterBridge.cs:223-230`; presenter shutdown disposes its current session at `Presenter.cs:~234-250` and `DisposeSessionAsync` (`Presenter.cs:~800`); live session implements `IAsyncDisposable` at `LiveSession.cs:200-215` |
| Host shutdown owner | `PresenterBridge` is a singleton (`PresenterBridge.cs:338-340`) and is injected by the mapped endpoint, but no hosted service explicitly disposes it. Generic host disposes registered `IAsyncDisposable` singletons during shutdown; no explicit shutdown hook is present. |
| `ValidateOnStart` | Present only for `UpstreamOptions` at `DependencyInjection.cs:26`; `PresenterOptions`, `ContentOptions`, and `LiveSessionOptions` have no `ValidateOnStart` validators. |
| `ValidateOnBuild` | API enables it at `Program.cs:20-24`; CLI calls `BuildServiceProvider()` without validation at `src/PresenterAi.Cli/Program.cs:80`. |
| Startup test | Exists: `tests/PresenterAi.Api.Tests/StartupTests.cs:10-15`, named `Container_validates_on_build`; it creates the real test client, although it does not directly assert an unresolvable registration failure. |
| OpenAPI test | Exists: `tests/PresenterAi.Api.Tests/OpenApiTests.cs:70-92`, named `Document_lists_all_mapped_endpoints`. It excludes `/ws`, fallback, deck static routes and test routes by design (`:83-86`). |
| Hosted services | No `IHostedService`/`BackgroundService` registration found. Lifecycle is singleton disposal plus presenter/session internal loops. |

## Findings

| Severity | Finding |
|---|---|
| **NO CALLER** | `IDeckStore` is registered at `src/PresenterAi.Infrastructure/DependencyInjection.cs:51-52` and never resolved. |
| **NO CALLER** | `Auth:Dev:Enabled`, `Auth:Dev:UserId`, and `Auth:Dev:Email` are documented/configured in appsettings but have no binding or reader. |
| **NO CALLER** | `Presenter:LogEvents` binds but nobody reads `PresenterOptions.LogEvents`. |
| **NO CALLER** | Legacy `.env.example` keys (`UPSTREAM_*`, `ADVANCE_SILENCE_MS`, `LOG_EVENTS`, `PORT`) are not wired to the .NET colon-delimited configuration path. |
| **NO CALLER** | `GET /api/config` has tests but no web/CLI caller. |
| **NO CALLER** | Frozen `upstream-error` has no server emission; bridge emits `error` instead. |
| **NO CALLER** | Frozen `busy` has no matching server frame; server emits `error` with `code=busy`, and React does not subscribe to its `busy` event. |
| **NO CALLER** | React `pong` has a handler but no React code calls `ping()`. |
| Missing acceptance wiring | The plan explicitly says: “**Verify:** audit table in the work log with no "no caller" rows; PR checks green.” (`docs/plan/002-phase0-dotnet-core-port-api-web.md:516`). This repository has the NO CALLER rows above, and the requested audit is being written to the scratchpad rather than a work-log file. |
| Missing acceptance wiring | The plan names `StartupTests.Container_validates_on_build` and `OpenApiTests.Document_lists_all_mapped_endpoints` as tests that die if T16 breaks (`docs/plan/002-phase0-dotnet-core-port-api-web.md:517-518`); both tests exist, but no test was run in this read-only audit. |

## Files examined

- `docs/plan/002-phase0-dotnet-core-port-api-web.md` (T16 and test strategy)
- `src/PresenterAi.Infrastructure/DependencyInjection.cs`
- `src/PresenterAi.Infrastructure/Live/{UpstreamOptions,UpstreamRoute,PresenterOptions,LiveSession,LiveSessionFactory}.cs`
- `src/PresenterAi.Infrastructure/Content/ContentOptions.cs`
- `src/PresenterAi.Api/Program.cs`
- `src/PresenterAi.Api/Endpoints/{HealthEndpoints,PresentationEndpoints,ConfigEndpoints}.cs`
- `src/PresenterAi.Api/Realtime/PresenterBridge.cs`
- `src/PresenterAi.Api/appsettings.json`, `appsettings.Development.json`, `appsettings.Example.json`
- `src/PresenterAi.Cli/{Program,RunCommand,SmokeCommand}.cs`
- `src/PresenterAi.Application/Presenting/Presenter.cs`
- `src/web/app.js`
- `web/app/src/ws/bridgeClient.ts`, `routes/{Library,Present}.tsx`, `store/presenterStore.ts`
- `web/shared/src/api/generated.d.ts`
- `README.md`, `.env.example`, `docker-compose.yml`
- `tests/PresenterAi.Api.Tests/{StartupTests,OpenApiTests,AuthTests,PresentationEndpointTests,SpaFallbackTests}.cs`

<!-- REPORT COMPLETE -->