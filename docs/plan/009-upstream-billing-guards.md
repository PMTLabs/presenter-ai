# 009 — Upstream billing guards

**Date:** 2026-09-23
**Status:** Draft — awaiting approval
**Size:** L (presenter event loop, live session, recorder + migration, `/ws` bridge, CLI, web Present page, docs).
**Area:** `src/PresenterAi.{Application,Infrastructure,Api,Cli,Contracts}`, `web/app`, `docs/`.
**Branch:** `feature/009-billing-guards` from `feature/008-mcp-external-tools` (worktree `.claude/worktrees/p009`);
the PR is stacked on #8.
**Discovery:** `docs/research/008-upstream-session-lifetime-and-billing.md` (every reference re-checked for §3).
**Requirement brief confirmed:** 2026-09-23 (G1)

---

## 1. Goal

Every talk's billed GPT-Live upstream closes within a known, bounded time, whatever the browser, the CLI or the talk
does: a hard length cap, a pause grace that hangs up while paused, an idle cutoff, a `/ws` heartbeat, no orphaned
upstream on errors, start-up or shutdown, a graceful CLI Ctrl+C, and a session row that says why and how long.

## 2. Requirement (as confirmed at G1)

- **Problem:** a GPT-Live session is billed while its upstream is connected, and nothing on the server bounds that.
  Paused, silent, stalled or held talks keep the upstream open indefinitely. A dead or blackholed browser is not
  detected. Several error, start-up and shutdown paths can orphan an upstream. CLI Ctrl+C does not close gracefully.
  Tool and MCP calls outlive End. A close timeout records the billed duration as last-usage-or-zero.
- **Goal:** every talk's billed upstream is closed within a known, bounded time, whatever the browser, CLI or talk
  state does.
- **In scope (all six research gaps):**
  1. Hard max talk length: default 60 min (config); per presentation via script frontmatter `maxMinutes`; per-talk web
     override on the Present page (can only lower); server ceiling 120 min (config) that clamps with a logged
     warning. Ends the talk from any state.
  2. Pause grace: after 2 min paused (config) the upstream closes but the talk stays paused. Resume reconnects a fresh
     upstream and re-sends the current slide's context.
  3. Idle deadline: 5 min (config) with no activity (user speech, model output, slide/control/tool activity) ends the
     talk.
  4. Warning 1 min before either cutoff: web banner with countdown, and the presenter says it once. Activity clears an
     idle warning.
  5. `/ws` heartbeat: the server pings; a missed deadline aborts the socket, then the normal bounded End. A writer
     failure also aborts the receive loop.
  6. Orphan windows closed: unexpected event-loop exception, throw after `_session` assignment, shutdown during
     connect, an `ApplicationStopping` hook, and the bridge must not free the slot while a start may still open an
     upstream.
  7. CLI: Ctrl+C → graceful End with a dispose backstop; presenter-level caps apply; `--max-seconds` kept and clamped
     by the ceiling.
  8. Tool/MCP calls cancelled at End via a per-run token; no retry after End.
  9. Observability: end reason (`max_length`, `idle`, `heartbeat`, …), local start/end wall-clock, a
     confirmed-vs-estimated usage flag, upstream-socket disposal logged with the session id.
- **Out of scope:** per-user daily or monthly usage budgets; provider billing reconciliation; a web editor for stored
  presentations (the override is per talk, not saved); the status-generation-token follow-up; flaky tests; live
  runbooks.
- **Surfaces:** Application `Presenter` (timers, pause-close/reconnect, end reasons, tool cancellation);
  Infrastructure `LiveSession` disposal log, `SessionRecorder` fields + migration, options; API `PresenterBridge`
  (heartbeat, writer abort, slot release, shutdown hook), presentation DTO carries `maxMinutes`; CLI `ImportCommand`
  frontmatter, `RunCommand`/`Program` (Ctrl+C, clamp); `/ws` protocol (start carries an optional override; new
  warning / closed-reason / upstream-suspended frames); web Present page (max-length picker, warning banner,
  paused-disconnected state); a docs guide section on billing guards and config keys.
- **Assumptions (confirmed):**
  - A1 Max length is wall-clock from talk start, including pauses. A paused (upstream-closed) talk still ends at the
    cap, freeing the slot.
  - A2 The web override can only lower the effective cap; minimum 5 min.
  - A3 The idle deadline does not run while paused (pause grace covers it). After pause-close, no billing until Resume.
  - A4 Heartbeat: ping every 15 s; dead after 45 s without a pong or any frame (config). A background tab still
    answers pings, so the idle cap is what bounds it.
  - A5 Invalid `maxMinutes` in frontmatter (≤ 0 or not a number) fails import with a clear error; above-ceiling values
    import and clamp at run time.
  - A6 Branch `feature/009-billing-guards` from `feature/008-mcp-external-tools`; PR stacked on #8.
  - A7 All timing is tested with a fake `TimeProvider`; no test opens a real upstream.
- **Acceptance criteria:**
  1. With a fake clock, a talk ends with reason `max_length` at its effective cap from presenting, paused,
     question-hold and stalled-slide states; the upstream is disposed.
  2. Effective cap = min(frontmatter `maxMinutes` or 60, web override, 120 ceiling); above-ceiling is clamped with a
     warning log.
  3. Paused 2 min → upstream disposed, talk stays paused, no billing. Resume opens a new upstream and the current slide
     continues; the web shows the reconnect.
  4. 5 min without activity (not paused) → ends with reason `idle`; activity just before the deadline resets it.
  5. 60 s before either cutoff the web gets a warning frame with seconds left and shows a countdown; the presenter
     speaks once; activity clears an idle warning.
  6. A browser that stops answering pings is detected within 45 s: socket aborted, End runs, upstream disposed, slot
     freed. A writer failure also ends the receive loop.
  7. Orphan tests: event-handler exception, throw after assignment in Start, shutdown during a hanging connect,
     `ApplicationStopping`, 90 s start-observation expiry — no live upstream remains.
  8. CLI Ctrl+C mid-talk and during start-up closes the upstream without waiting for `--max-seconds`; `--max-seconds`
     above the ceiling is clamped.
  9. End during an in-flight tool/MCP call cancels it; no retry after End.
  10. The session row records end reason, local start/end and a usage-confirmed flag; a close timeout stores the
      estimated duration (not zero); disposal is logged with the session id.
  11. Build has 0 warnings; all suites pass; the guide documents every new config key.
- **Decisions made:** max length 60 per presentation; frontmatter + web override; ceiling 120; pause close after 2 min
  grace with reconnect on Resume; idle 5 min then end; warning on screen + spoken 1 min before; all six gaps; stacked
  on 008.

## 3. Current state (as built on `feature/009-billing-guards`)

**Presenter (one event loop, `src/PresenterAi.Application/Presenting/Presenter.cs`)**
- Timers are `TimeProvider.CreateTimer` callbacks that queue a generation-stamped event (`ArmNudge` `:1945-1954`,
  `SetSilenceTimer` `:1978-1987`); stale generations are dropped in the loop switch (`:346-380`). The only fixed
  bounds are progression timers (`NudgeMs`/`WrapUpFallbackMs`/`QuestionHoldMs` = 15 s, `:32-34`). No length, idle or
  pause cap exists; `_usageSeconds` is only stored (`:68`, `:1127`, `:1853`).
- `RunLoopAsync` catches only lifetime cancellation (`:427-429`): any other handler exception ends the loop with
  `_session` still open. `Shutdown` is the only loop path that disposes `_session` (`:409-423`).
- `DisposeAsync` writes `Shutdown` and waits for it (`:233-237`) before cancelling `_lifetime` (`:244`). Start awaits
  `ConnectAsync(_lifetime.Token)` inside the loop (`:576`) and loads the presentation with `_lifetime.Token` (`:499`),
  so a shutdown during a hanging connect waits behind the connect. `startCts` (`:491`) only cancels the tool load.
- Start: a failed candidate is disposed (`:584-590`), but the retry loop catches every exception, cancellation
  included, and tries the next route. After `_session = session` (`:601`), `AppendInstructions` (`:611`) and
  `PresentSlide` (`:621`) run outside any try; a throw there faults the command (`:438-441`) and leaves `_session`
  open with state `Connecting`.
- `PauseCore` (`:1686-1701`) clears timers and sets Paused; the upstream stays open. `ResumeCore` (`:1703-1743`) is
  synchronous and appends to the same session. Navigation leaves pause through `LeavePauseForNavigation`
  (`:2059-2070`). `SendAudioCore` accepts audio while paused (`:1769-1770`).
- End sites (grep `EndAsync(`, `EndAsyncCore(`): bridge `end` command (`PresenterBridge.cs:381`), bridge disconnect
  (`:454`), voice-confirmed end (`Presenter.cs:982`), confirmed end (`:1113`), wrap-up (`:1541`), wrap-up fallback
  (`:1600`), CLI stop-after-slide / max-seconds (`RunCommand.cs:121-137`, `:209-213`). `EndAsyncCore`
  (`:1772-1812`) awaits `session.CloseAsync()`; `OnClosed` (`:1832-1863`) raises `Closed(reason, seconds)` and sets
  Idle. No end *reason* separate from the upstream close reason exists (`PresenterEvents.cs:9`).
- Tools: `InvokeBoundedAsync` links only the tool's own timeout (`:1358-1378`); approved calls use the same
  (`:1441-1456`). End bumps `_runGeneration` so late results are dropped (`:1787`, `:1388-1390`), but the call itself
  keeps running. Tool-set disposal at End is fire-and-forget (`ReleaseSessionTools`, `:1494-1506`).
- Snapshot (`PresenterSnapshot.cs:3-14`) has state, slide, session id, usage; nothing about limits or suspension.
- `IPresenter` (`IPresenter.cs:3-28`) already uses a default event body for `Flush` (`:9`), the pattern for additive
  members. Implementations (grep `: IPresenter`): `Presenter`, `TestQueuedPresenter`
  (`tests/PresenterAi.Api.Tests/Infrastructure/ApiFactory.cs:151`), `ThrowingClosePresenter`
  (`tests/PresenterAi.Cli.Tests/CliTests.cs:214`), `CountingPresenter`
  (`tests/PresenterAi.Infrastructure.Tests/Sessions/SessionRecorderTests.cs:34`), `RecorderPresenter`
  (`tests/PresenterAi.Integration.Tests/Sessions/SessionRecorderTests.cs:319`).

**Live session (`src/PresenterAi.Infrastructure/Live/LiveSession.cs`)**
- `CloseAsync` sends `session.close`, waits `CloseTimeout` (5 s, `LiveSessionOptions.cs:7`), and on timeout logs
  "final usage unconfirmed", aborts and finishes with `seconds = null` (`:245-275`). `DisposeAsync` terminates, joins
  the loops and disposes the socket without any log line (`:283-299`). `Finish` logs the close reason and seconds
  (`:734-748`); `session.closed` carries usage seconds (`:568-570`). The 20 ms silence pump runs while Open
  (`:400-421`). Handshake and close timeouts use the injected `TimeProvider` (`:97`, `:266`).

**Options and DI (`src/PresenterAi.Infrastructure/DependencyInjection.cs`)**
- `PresenterOptions` (`Live/PresenterOptions.cs:5-15`) is bound from `Presenter` with `Validate` +
  `ValidateOnStart` (`:71-77`); the API fails start-up on an out-of-range value
  (`tests/PresenterAi.Api.Tests/StartupTests.cs:38`). `AddPresenter` maps options into `PresenterSettings`
  (`:180-184`); `TimeProvider.System` is `TryAddSingleton` (`:127`). `Session` options (`Redis/SessionRedisOptions.cs`)
  are bound without validation (`:44-45`). The CLI builds its own providers without a host (`Cli/Program.cs:131-166`),
  so validation fires on first `.Value` and is reported as "Configuration invalid" (`Cli/Program.cs:50-54`).

**Bridge (`src/PresenterAi.Api/Realtime/PresenterBridge.cs`, singleton `:833-840`)**
- Presenter events become frames in the constructor (`:58-66`); `closed` carries `reason` and `seconds` only.
- `ReceiveLoopAsync` (`:289-323`) uses `RequestAborted` only; there is no server heartbeat or read deadline. `ping`
  is answered with `pong` (`:382`); nothing else. `docs/reference/001-api-and-code-conventions.md:176` already claims
  "server closes after 45 s of silence" — not implemented.
- The writer catches `WebSocketException` and stops (`:570-573`); backpressure completes the queue and sends a
  bounded 1011 (`:576-579`, `:799-806`); neither wakes the receive loop.
- Disconnect cleanup (`:138-175`) waits for the start observation up to 90 s with real time (`:26`, `:664`), then
  ends the talk if not idle (`:150-153`) and releases the slot. After the 90 s expiry a still-running start can open
  an upstream after the slot is freed. `ObserveEndAsync` waits 5 s for idle (`:439-466`).
- `start` parses `presentation` and `fromIndex` only (`:354-370`). `DisposeAsync` disposes the presenter (`:501-507`);
  there is no `ApplicationStopping` hook (`Api/Program.cs:60-62`, `:160`).

**CLI (`src/PresenterAi.Cli`)**
- `Main` passes `CancellationToken.None` and registers no Ctrl+C handler (`Program.cs:14-18`). `--max-seconds`
  defaults to 300 and accepts any positive integer (`Program.cs:318`, `:355-363`). `RunWithPresenterAsync` waits for
  Closed or the timeout (`RunCommand.cs:205-213`); its `finally` ends only the recorder (`:238-252`); a cancelled
  `StartAsync` (`:193`) leaves the queued start running. `smoke` opens a `LiveSession` directly and disposes it in
  `finally` (`SmokeCommand.cs:82-125`) — a parallel path that does not use the presenter; it only gains the Ctrl+C
  token.

**Scripts, import and the presentation DTO**
- `PresentationMeta` (`Application/Scripts/PresentationScript.cs:5-13`) has no length field. `ScriptParser.ToInt`
  (`ScriptParser.cs:197-209`) is lenient (`"12abc"` → 12), so it cannot enforce A5. `ScriptWriter` round-trips the
  meta scalars (`ScriptWriter.cs:22-23`).
- Import stores the Markdown and a `frontmatter` jsonb snapshot (`Cli/ImportCommand.cs:88-94`, `:123-134`); the
  Postgres loader and the file loader re-parse the Markdown on every load
  (`Infrastructure/Content/PostgresPresentationRepository.cs:79`, `FilePresentationRepository.cs:30,59`), so a new
  meta field reaches the presenter without a data migration.
- `PresentationMetaDto` (`Contracts/Presentations/PresentationDtos.cs:15-23`) is built at
  `Api/Endpoints/PresentationEndpoints.cs:74`; nullable fields are omitted when null (golden files stay valid). The
  web type comes from `web/shared/src/api/generated.d.ts:426-437` (OpenAPI drift chain, `AGENTS.md:88-90`).

**Recorder and schema**
- `sessions` has `started_at`, `ended_at`, `usage_seconds`, `upstream`, `upstream_session_id`, `close_reason`
  (`Persistence/Entities/Session.cs:3-20`, `PresenterAiDbContext.cs:162-186`). `started_at` is the recorder's clock at
  `BeginAsync` (`SessionRecorder.cs:263`); `ended_at` its clock at finalisation (`:357`). A missing final usage stores
  last usage or 0 (`:358`). The bridge's `EndAsync()` defaults the reason to `disconnect` (`:138-167`).
  Migrations live in `Persistence/Migrations/` (latest `20260923183218_AddToolServers`).

**MCP tool (`src/PresenterAi.Infrastructure/Tools/Mcp/McpTool.cs:81-197`)**
- The call links the caller token plus its own timeout. On 401/404 an unconfirmed tool refreshes/reconnects and
  retries once; that retry path does not check the caller token first, and its bare `catch` marks the server
  `needs_reconnect` even when the failure was the caller's cancellation.

**Web (`web/app/src`)**
- The `/ws` protocol types live in `ws/bridgeClient.ts` (not `web/shared`). `start()` sends presentation and
  `fromIndex` (`:157-159`); `receive` ignores unknown types (`:205-243`); the client defines `ping()` (`:184-186`) but
  the page never calls it. `Present.tsx` routes `closed` etc. to the store (`:129-139`), disconnects on cleanup
  (`:155-160`), starts with `client.start(presentation.id)` (`:256`) and shows a paused warning banner from logs
  (`:313-314`, `:380-384`). The store handles `slide`, `usage`, `transcript`, `log` (`store/presenterStore.ts:54-94`).

**Tests and harnesses**
- Application presenter tests build a `Presenter` over `FakeSession` and `FakeTimeProvider`
  (`tests/PresenterAi.Application.Tests/Presenting/PresenterTests.cs:1000-1033`, `FakeSession.cs:5-132`).
- API tests run the real DI container through `ApiFactory` (`WebApplicationFactory`) with an in-process
  `FakeLiveServer` upstream (`tests/PresenterAi.Api.Tests/BridgeTestSupport.cs:14-23`;
  `tests/PresenterAi.Infrastructure.Tests/Live/FakeLiveServer.cs:31-47`: `StartDelayMs`, `IgnoreClose`,
  `ConnectionCount`). The browser socket is `TestServer`'s in-memory WebSocket (`BridgeTestSupport.cs:37`).
  `ApiFactory` does not replace `TimeProvider` today.
- `LiveSessionTests` has a `RecordingLogger` (`tests/PresenterAi.Infrastructure.Tests/Live/LiveSessionTests.cs:656`);
  the integration recorder tests run against Testcontainers Postgres
  (`tests/PresenterAi.Integration.Tests/Sessions/SessionRecorderTests.cs:20`).

## 4. Design

### 4.1 Approach

**One talk guard, in the presenter loop.** A new `TalkGuard` (Application/Presenting) holds the talk's deadlines as
`TimeProvider` timestamps and one generation-stamped timer that queues `GuardElapsed(generation)` into the presenter
channel, like every other timer. The API and the CLI both drive the same `Presenter` (`AddPresenter`), so both get
the caps; the CLI's `--max-seconds` stays as an extra client-side stop.

- **Max length.** Armed when the Start is accepted (`_talkStartedAt`, before content load and connect, A1) at
  `min(meta.MaxMinutes ?? Presenter:MaxTalkMinutes, override, Presenter:MaxTalkCeilingMinutes)`; until the
  presentation is loaded the deadline uses `min(override, default, ceiling)` and is tightened once `maxMinutes` is
  known. The deadline is fixed for the talk: it keeps running through load, connect, pauses, pause-close and every
  reconnect, and nothing re-arms or extends it. On expiry: `EndAsyncCore(endReason: max_length)` from any state; a
  suspended talk has no session, so End goes straight to `OnClosed` and Idle.
- **Idle.** Runs only while `Presenting`. Activity = voiced model output that is forwarded, a non-empty user
  transcript, `PresentSlide`, any command except `SendAudio`, a tool call or tool completion. Browser mic frames and
  the silence pump are not activity. `Paused` stops it (A3); leaving pause re-arms it from now.
- **Pause grace.** Armed on entering `Paused`; cleared on leaving it. On expiry the presenter *suspends*: it detaches
  `_session`, awaits `CloseAsync()` (bounded 5 s, collects final usage), disposes it, keeps state `Paused`, and
  publishes `Suspended = true`. `SessionClosed` from the old session is ignored because it is no longer `_session`.
- **Resume / navigation while suspended** reconnect through the same connect routine Start uses (extracted from
  `StartAsyncCore :543-599`), re-apply mute, then re-present the current slide (notes + narration from part 1 — a
  fresh upstream has no memory of what was said) after a resume instruction. All routes failing ends the talk with
  `reconnect_failed`.
- **Warnings.** 60 s before the max-length or idle deadline the presenter raises `LimitWarning(kind, secondsLeft)`.
  If an upstream is open and the state is `Presenting` it appends one short instruction to say so (once per warning;
  never while paused). Activity after an idle warning raises `LimitWarning(idle, null)` (cleared).
- **Usage.** Each upstream segment adds its final `seconds` (confirmed) and its local connected wall-clock
  (estimated). `Closed` carries `Seconds` (confirmed total, or null if any segment was unconfirmed),
  `EstimatedSeconds`, `UsageConfirmed`, `EndReason`, and local `StartedAt`/`EndedAt`. `Usage` frames report the
  cumulative figure.

**Orphans and interruptible connects.** Every connect (Start and every reconnect) runs under a per-connect
`CancellationTokenSource` (`_connectCts`). Because the single reader is busy awaiting the connect, nothing that must
stop it may go through the channel. It is cancelled **directly, off the loop**, by: the `TalkGuard` max-length timer
callback (before it queues `GuardElapsed`), the public `EndAsync` (before it queues End), `AbortPendingStart()`,
`DisposeAsync` (before queuing `Shutdown`), and bridge abort. Each canceller first records the requested end reason
(`_pendingEndReason`, first one wins, `Interlocked`). `ConnectUpstreamAsync` also checks `now >= maxDeadline` before
each candidate and right after `ConnectAsync` returns. On cancellation or an elapsed cap it disposes the candidate,
tries no further route, never assigns `_session`, and returns `Cancelled(reason)`; the caller then runs
`EndAsyncCore(_pendingEndReason ?? max_length)`. So no upstream is assigned after the cap, and End, shutdown or a
browser abort interrupts a hanging connect within the handshake's own cancellation latency.
The post-assignment block of Start is wrapped so a throw disposes the session and returns Idle. The loop
catches unexpected handler exceptions, logs them, and runs a fail-safe close (`EndReason = error`); a `finally`
disposes any `_session` if the loop exits.

**Per-run tool token.** A `_runCts` is created per talk and cancelled by End, `OnClosed`, fail-safe close and
Shutdown. `InvokeBoundedAsync` and approved calls link it with the tool timeout. `McpTool` checks the caller token
before and after the token refresh and the reconnect awaits, and wraps both in
`catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`. Caller cancellation before
or during either operation returns the `cancelled` result at once: no retry call and no `_setStatusAsync` (so never
`needs_reconnect`). The bare `catch` blocks at `McpTool.cs:126` and `:172` are narrowed so they no longer see caller
cancellation.

**Bridge.** `ClientConnection` gets one `Abort(reason)` (idempotent) that cancels a CTS linked into `ReceiveAsync` and
aborts the socket. Callers: the heartbeat deadline, writer failure, backpressure after its bounded 1011, shutdown. The
receive loop then exits through the existing `finally`, which passes the reason to `ObserveEndAsync` →
`EndAsync(endReason)`. The heartbeat and the start-observation bound use the injected `TimeProvider`. After the 90 s
start-observation bound expires, the bridge calls `AbortPendingStart()` and waits up to 5 s more for idle before it
frees the slot. A hosted service's `StopAsync` (run by the host on `ApplicationStopping`) aborts the current
connection with reason `shutdown`, aborts a pending start and ends the talk within the same 5 s bound; `DisposeAsync`
stays the backstop.

**CLI.** `Main` owns a CTS: the first Ctrl+C sets `e.Cancel = true` and cancels; a second press is left to the OS.
`RunWithPresenterAsync` on cancellation calls `AbortPendingStart()`, `EndAsync(cli_cancelled)`, waits ≤ 8 s for
Closed; provider disposal remains the backstop. `--max-seconds` is clamped to the ceiling with a one-line warning.

### 4.2 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| A — one `TalkGuard` in the presenter loop (max length + idle + pause grace + warnings), bridge heartbeat, orphan fixes | Single owner of deadlines; API and CLI share it; fake-clock testable; no cross-thread races | Presenter grows (~2.2k lines) | **Chosen** — deadlines must see talk state, which only the loop owns |
| B — only a hard cap (simplest), no pause-close, no idle | Tiny change | A paused talk bills for up to 60–120 min; misses AC3/AC4 | Rejected — brief requires all six gaps |
| C — deadlines in the bridge | Bridge already has per-connection state | CLI unguarded; bridge cannot see pause/hold state | Rejected |
| D — separate `IHostedService` watchdog polling snapshots | Decoupled | Polling races with End; CLI has no host | Rejected |
| H1 — WebSocket keep-alive (`WebSocketOptions.KeepAliveInterval` + `KeepAliveTimeout`, ASP.NET Core 9+) | Browser answers protocol pings with no JS; no client change | Real-time timers inside `ManagedWebSocket` (breaks A7); `TestServer`'s in-memory socket bypasses the middleware so `ApiFactory` cannot test it; abort surfaces as a generic `WebSocketException`, so the end reason `heartbeat` cannot be attributed | Rejected |
| H2 — application ping frame + read deadline on any inbound frame, `TimeProvider`-driven | Fake-clock testable through the real DI container; exact 45 s deadline; clear `heartbeat` reason; matches the already documented contract | Client must answer `ping` with `pong` | **Chosen**; audio frames every 20 ms also count, so an old client with a live mic is not cut off |
| S1 — pause-close: dispose only (no `CloseAsync`) | Faster | Final usage never confirmed for the segment | Rejected; `CloseAsync` is bounded at 5 s |

### 4.3 Data, config and protocol

**Config (all bound with `Validate` + `ValidateOnStart`; the API fails start-up; the CLI prints "Configuration
invalid"). None can disable a guard; the ceiling always applies.**

| Key | Default | Allowed | Reader |
|---|---|---|---|
| `Presenter:MaxTalkMinutes` | 60 | 5 … ceiling | `PresenterSettings` → `TalkGuard` |
| `Presenter:MaxTalkCeilingMinutes` | 120 | 5 … 240 | `TalkGuard`; CLI `--max-seconds` clamp |
| `Presenter:PauseGraceSeconds` | 120 | 30 … 900 | `TalkGuard` |
| `Presenter:IdleTimeoutSeconds` | 300 | 120 … 1800 | `TalkGuard` (≥ 120 keeps the 60 s warning inside it) |
| `Session:HeartbeatIntervalSeconds` | 15 | 5 … 60 | `PresenterBridge` |
| `Session:HeartbeatTimeoutSeconds` | 45 | ≥ 2 × interval, ≤ 300 | `PresenterBridge` |

The warning lead (60 s) is a constant (`TalkGuard.WarningLead`), not a key. New keys go in `appsettings.json` and
`appsettings.Example.json` next to the existing `Presenter` block. `docker-compose.yml` and `.env.example` are not
changed (dropped by the user before approval).

**Script frontmatter:** `maxMinutes: <positive integer>`. Parsed strictly (integer ≥ 1, no fraction, no trailing
text); otherwise `ScriptParseException("maxMinutes must be a positive whole number of minutes")`, so import fails for
that file (A5). Values above the ceiling import and clamp at run time with a `warn` log. `PresentationMeta` gains
`int? MaxMinutes = null` (last positional, existing `new PresentationMeta(…)` calls compile). `ScriptWriter` and the
import `frontmatter` snapshot include it. `PresentationMetaDto` gains `maxMinutes` (omitted when null).

**Contracts (Application):**
- `EndReasons` string constants (closed vocabulary; any other value is a bug): `user`, `completed`, `max_length`,
  `idle`, `heartbeat`, `writer_failed`, `backpressure`, `disconnect`, `takeover`, `shutdown`, `error`,
  `upstream_lost`, `reconnect_failed`, `cli_cancelled`, `cli_max_seconds`, `stop_after_slide`.
- `EndReason` is separate from the existing `Reason` (the provider's free-text close reason, kept as is for logs).
  Every close path sets `EndReason` from the table below; `OnClosed` takes it as a required argument (no default at
  the call sites), and a `Debug.Assert` plus a test check it is in the vocabulary.

| Close path | `EndReason` | `Reason` (provider/diagnostic) |
|---|---|---|
| Browser/voice/tool End, End button | `user` | provider close reason |
| Wrap-up finished | `completed` | provider close reason |
| Max length, open session / suspended | `max_length` | provider reason / `suspended` |
| Idle deadline | `idle` | provider close reason |
| Bridge abort: heartbeat / writer failure / backpressure | `heartbeat` / `writer_failed` / `backpressure` | provider close reason |
| Browser closed or dropped (no abort reason) | `disconnect` | provider close reason |
| Take-over | `takeover` | provider close reason |
| `ApplicationStopping` or `DisposeAsync` | `shutdown` | `disposed` |
| Close timeout (any of the above) | unchanged (the requested reason) | `close_timeout`; usage unconfirmed |
| Upstream socket lost / server `session.closed` unrequested | `upstream_lost` | provider reason |
| Reconnect found no route | `reconnect_failed` | last connect error |
| Handler exception fail-safe | `error` | exception type |
| CLI Ctrl+C / `--max-seconds` / `--stop-after-slide` | `cli_cancelled` / `cli_max_seconds` / `stop_after_slide` | provider close reason |
- `PresenterClosed(Reason, Seconds, EndReason = "upstream_lost", bool UsageConfirmed = false,
  double EstimatedSeconds = 0, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null)`.
- `PresenterStartResult` gains `DateTimeOffset? ConnectedAt = null`.
- `PresenterSnapshot` gains `bool Suspended` (last, defaulted) so a reloaded page shows the right banner.
- `IPresenter` gains, all with default bodies so the four test fakes compile unchanged:
  `StartAsync(id, fromIndex, ownerId, int? maxMinutes, CancellationToken)`,
  `EndAsync(string endReason, bool resumable = false, CancellationToken)`, `void AbortPendingStart()`,
  `event Action<PresenterLimitWarning>? LimitWarning`, `event Action<PresenterUpstreamStatus>? UpstreamStatus`.

**`/ws` frames (additive; clients ignore unknown frames — `AGENTS.md:81`):**

| Direction | Frame | When |
|---|---|---|
| C→S | `{"type":"start","presentation":"…","fromIndex"?:n,"maxMinutes"?:n}` | `maxMinutes` integer ≥ 5; lower than the cap or ignored; otherwise `error{code:"protocol"}` and no start |
| S→C | `{"type":"ping"}` | every `HeartbeatIntervalSeconds` |
| C→S | `{"type":"pong"}` | reply to `ping` (any inbound frame also counts as alive) |
| S→C | `{"type":"limit_warning","kind":"max_length"\|"idle","secondsLeft":60}` | 60 s before a cutoff |
| S→C | `{"type":"limit_warning","kind":"idle","secondsLeft":null}` | idle warning cleared by activity |
| S→C | `{"type":"upstream","status":"suspended"\|"reconnecting"\|"live"}` | pause-close, Resume, reconnected |
| S→C | `closed` gains `endReason`, `usageConfirmed`, `estimatedSeconds` | every close |
| S→C | `state` gains `suspended` | every snapshot |

The existing client `ping` → `pong` stays.

**Migration `SessionBillingGuards`** (`dotnet ef migrations add`, never auto-applied): `sessions.end_reason text null`,
`sessions.usage_confirmed boolean null` (null = recorded before this plan), `sessions.estimated_seconds integer null`.
`started_at` / `ended_at` now come from the presenter's clock (`ConnectedAt`, `Closed.EndedAt`), falling back to the
recorder's clock. `usage_seconds` = confirmed seconds when confirmed, else `estimated_seconds` (never 0 for a talk
that ran). A recorder `EndAsync` with no `Closed` (bridge fallback) stores `end_reason = disconnect`,
`usage_confirmed = false`, `estimated_seconds = ended_at − started_at`.

**Log lines:** `LiveSession.DisposeAsync` →
`Upstream socket disposed: session={Id} route={Route} state={State}` (Information). Presenter page log:
`limit: max length {n} min (script {a}, override {b}, ceiling {c})`, `limit: clamped {x} → {c} min`,
`limit: {kind} warning, {s} s left`, `pause: upstream closed after {n} s paused`, `resume: reconnected via {label}`,
`closed: reason=… end=… usage=… (estimated …)`.

### 4.4 Sequences

**(a) Cutoff timer → End**
```
TalkGuard timer (TimeProvider) → QueueFromProducer(GuardElapsed(gen))
  → RunLoopAsync: gen == _guardGeneration?
    ├─ warning due (deadline − 60 s) → LimitWarning(kind, 60) → bridge frame limit_warning / CLI log line
    │    └─ Presenting && _session open → AppendInstructions(PromptBuilder.LimitWarningInstruction(kind)) [once]
    └─ deadline reached → EndAsyncCore(endReason: max_length | idle)
         ├─ _session != null → CloseAsync (≤ 5 s) → SessionClosed → OnClosed → DisposeSessionAsync
         └─ suspended (null)  → OnClosed(reason: "suspended", endReason) → Idle
         → _runCts.Cancel() (tools) → Closed{EndReason, UsageConfirmed, EstimatedSeconds, StartedAt, EndedAt}
           → bridge `closed` frame · SessionRecorder ClosedWork · CLI closed line
Max-length timer callback also cancels _connectCts first (off-loop) so a connect in progress cannot delay it.
Parallel paths checked: every close path passes an EndReason from the §4.3 table.
```

**(b) Pause-close → Resume-reconnect**
```
PauseCore → SetState(Paused) → TalkGuard.ArmPauseGrace (idle stopped)
PauseGraceElapsed → SuspendUpstreamAsync: s = _session; _session = null; await s.CloseAsync(); segment usage += …
  → DisposeSessionAsync(s) → LiveSession.DisposeAsync logs "Upstream socket disposed: session=…"
  → Suspended = true → PublishSnapshot + UpstreamStatus(suspended) → web banner "Paused — disconnected"
Resume | Next | Prev | Goto (web) → ProcessCommandAsync → Suspended?
  ├─ yes → UpstreamStatus(reconnecting) → ConnectUpstreamAsync(presentation, attempt loop, _connectCts, maxDeadline)
  │     ├─ cancelled (End / max length / abort / shutdown, off-loop) → candidate disposed, _session never assigned
  │     │        → EndAsyncCore(_pendingEndReason)
  │     ├─ ok  → _session = new; re-apply mute; Suspended = false; UpstreamStatus(live); max deadline unchanged
  │     │        → SetState(Presenting) → PresentSlide(current or target) with resume preface → idle re-armed
  │     └─ all failed → EndAsyncCore(reconnect_failed)
  └─ no  → existing ResumeCore / navigation
Parallel paths checked: voice commands and model tools cannot arrive while suspended (no upstream);
ConfirmEnd while suspended is ignored; Mute/Unmute store the flag only.
```

**(c) Heartbeat death → End**
```
Bridge ownership acquired → connection.StartHeartbeat(TimeProvider):
  ping timer every 15 s → EnqueueText({type:"ping"})
  deadline timer at lastInbound + 45 s → lastInbound moved? re-arm : connection.Abort("heartbeat")
ReceiveLoopAsync: every frame → connection.MarkAlive(); ReceiveAsync(linked token) throws on Abort
  → finally: WaitForStartObservationAsync (TimeProvider bound; on expiry AbortPendingStart)
  → ObserveEndAsync(endReason: connection.AbortReason ?? takeover|disconnect) → presenter End (≤ 5 s to idle)
  → recorder EndAsync → slot released (Released.TrySetResult)
Writer WebSocketException / backpressure 1011 → connection.Abort("writer_failed" | "backpressure") → same path.
Parallel paths checked: take-over abort (`AbortAfterTakeOverCloseAsync`) keeps its own reason `takeover`.
```

### 4.5 Surface list

| Surface | Change | Task |
|---|---|---|
| `Presenter` / `TalkGuard` / `PromptBuilder` | max length, idle, pause grace, warnings, end reasons, usage totals | T3, T4 |
| `Presenter` start/loop/dispose, tool invocation | orphan windows, per-run token | T5 |
| `IPresenter`, `PresenterEvents`, `PresenterSnapshot`, `PresenterStartResult`, `PresenterSettings` | additive contracts | T1 |
| `PresenterOptions`, `SessionRedisOptions`, `DependencyInjection`, appsettings | new keys + validation | T1 |
| `ScriptParser`, `PresentationMeta`, `ScriptWriter`, `ImportCommand` | `maxMinutes` | T2 |
| `PresentationMetaDto`, `PresentationEndpoints`, `web/shared/openapi/v1.json`, generated client | `maxMinutes` | T2 |
| `McpTool` | no retry after caller cancellation | T5 |
| `LiveSession` | disposal log | T6 |
| `Session` entity, DbContext, migration, `SessionRecorder` | end reason, confirmed flag, estimate, local times | T6 |
| `PresenterBridge` + new `PresenterShutdownService`, `Program.cs` registration | heartbeat, abort, frames, override, slot, shutdown | T7 |
| CLI `Program`, `RunCommand` | Ctrl+C, clamp, end reasons | T8 |
| `web/app` `bridgeClient.ts`, `presenterStore.ts`, `Present.tsx` | pong, frames, picker, banner, suspended state | T9 |
| `docs/guides/004-talk-limits-and-billing.md`, `docs/reference/001…md` §8 | docs | T10 |

## 5. Impact and risk

| Question | Answer |
|---|---|
| State management — what survives a crash mid-operation? | Nothing new is persisted mid-talk. A process kill cannot close the upstream (OS closes the socket); the row keeps `ended_at` null as today. |
| Data consistency — orphans, races, double-apply? | Deadlines are generation-stamped loop events, so a late timer after End is a no-op. `Abort(reason)` and `AbortPendingStart()` are idempotent. Old-session `SessionClosed` after suspend is ignored by reference check. Recorder keeps its first-finaliser-wins rule. |
| User experience — root problem or symptom; any surprise? | Root cause: the server now owns the upstream lifetime. Surprises: a talk ends at 60 min unless the script says otherwise; Resume after 2 min restarts the current slide from its first part and takes ~1–3 s to reconnect. Both are announced (banner, spoken warning, guide). |
| Backward compatibility — existing data / sessions / configs? Migration? | Additive columns, nullable; old rows read `usage_confirmed = null`. Frames and DTO fields are additive; nullable DTO field is omitted so goldens stay. Scripts without `maxMinutes` use the default. Existing config keeps working. One migration, applied with `dotnet ef database update`. |
| Error recovery — what happens on failure; can it recover? | Reconnect failure ends the talk (`reconnect_failed`); a handler exception fail-safe closes and the loop keeps serving the next Start; a close timeout aborts and records the estimate. |
| Logging & debugging — enough to diagnose in the field? | End reason on the frame, log line and row; disposal log with session id and route; clamp and warning lines; heartbeat abort logged by the bridge with user id and reason. |
| Edge cases — empty, huge, repeated, concurrent, interrupted? | A slow load/connect counts against the cap (armed at Start acceptance); a cap that elapses during connect cancels it. Override ≥ cap is ignored; < 5 rejected. Idle warning cleared then re-armed repeatedly is bounded to one spoken line per warning. End, max length, shutdown or a browser abort during a connect cancel it off-loop (§4.1); no upstream is assigned after the cap. Pause at T−30 s of the cap still ends at the cap. Two Ctrl+C presses: the second kills the process (provider disposal not guaranteed). |

**Risks:**
- R1 — Reconnect on Resume re-sends the full system instructions: extra prompt cost and 1–3 s latency → only on an
  explicit Resume/navigation after a ≥ 2 min pause; logged with the route; documented.
- R2 — The spoken warning adds billed output → one short sentence per warning, never while paused; the banner carries
  the countdown.
- R3 — Timer races with End (warning fires while `Ending`) → the guard handler checks state `Presenting|Paused` and
  generation; End and `OnClosed` clear the guard. Test: `Deadline_elapsing_while_ending_is_ignored`.
- R4 — Fake-clock flakiness (timer callbacks queue asynchronously) → tests advance the clock then
  `WaitUntilIdleAsync()` (existing two-barrier hook, `Presenter.cs:210-217`); API tests poll frames with the existing
  `ReceiveUntilAsync` bound rather than sleeping.
- R5 — A frozen background tab stops answering pings and is ended by the heartbeat → intended for billing; the guide
  says so. Mic frames count as alive, so an active talk is unaffected.
- R6 — Injecting a fake clock into `ApiFactory` would stall presenter narration timers in other bridge tests → the
  override is opt-in. `ApiFactory` today has no clock seam (`tests/PresenterAi.Api.Tests/Infrastructure/
  ApiFactory.cs:12-31`); T7 adds `ApiFactory.UseFakeClock()`, which replaces the `TimeProvider` singleton
  (registered with `TryAddSingleton` at `DependencyInjection.cs:127`) through `ConfigureTestServices`, and a
  helper `AdvanceAndSettleAsync(TimeSpan)` that advances the clock, then awaits the presenter's
  `WaitUntilIdleAsync()` and the bridge's current connection cleanup (its `Released` task). Every guard and
  heartbeat API test uses that helper, never a bare `Advance`.
- R7 — `ApplicationStopping` work exceeding `HostOptions.ShutdownTimeout` (30 s default) → bounded at 5 s plus the
  cancelled connect.
- R8 — Presenter.cs size (review signal ~1,500 lines) → the guard's arithmetic lives in `TalkGuard`; the presenter only
  wires events.

**Rollback:** limits can be raised within the allowed ranges (for example `Presenter:MaxTalkMinutes` up to the
ceiling, `Presenter:MaxTalkCeilingMinutes` up to 240, `Presenter:IdleTimeoutSeconds` up to 1800), but no key turns a
guard off — the ceiling always applies. Anything else is a revert of the PR; the migration's `Down` drops the three
columns.

## 6. Tasks

Order and parallelism: **T1 first.** Then T2, T6, T7 (code), T8 (code), T9 can run in parallel. T3 → T4 → T5 are
sequential (same file, `Presenter.cs`); T5's `McpTool` part may run in parallel. T7's and T8's integration tests need
T3–T5. T10 after the shapes settle; T11 last. Every "Test that dies" is new unless marked (existing).

### T1 — Contracts and configuration  (AC2, AC11)
- **Files:** `src/PresenterAi.Application/Presenting/{IPresenter,PresenterEvents,PresenterSnapshot,
  PresenterStartResult,PresenterSettings}.cs`, new `Presenting/EndReasons.cs`,
  `src/PresenterAi.Application/Scripts/PresentationScript.cs`,
  `src/PresenterAi.Infrastructure/Live/PresenterOptions.cs`,
  `src/PresenterAi.Infrastructure/Redis/SessionRedisOptions.cs`,
  `src/PresenterAi.Infrastructure/DependencyInjection.cs`,
  `src/PresenterAi.Api/appsettings.json`, `appsettings.Example.json`.
- **Change:** the §4.3 contract additions with default interface bodies; the six keys with defaults, bounds,
  `Validate` + `ValidateOnStart` (add validation to the `Session` binding); map presenter keys into
  `PresenterSettings` in `AddPresenter`.
- **Verify:** `dotnet build PresenterAi.slnx -warnaserror`; `dotnet test tests/PresenterAi.Api.Tests --filter Startup`.
- **Test that dies if this breaks:** `tests/PresenterAi.Api.Tests/StartupTests.cs`
  `Out_of_range_talk_limit_fails_startup` (theory over all six keys, incl. ceiling < max),
  `Talk_limit_settings_reach_the_presenter`.

### T2 — `maxMinutes` in scripts, import and the presentation DTO  (AC2)
- **Files:** `src/PresenterAi.Application/Scripts/{ScriptParser,ScriptWriter}.cs`,
  `src/PresenterAi.Cli/ImportCommand.cs`, `src/PresenterAi.Contracts/Presentations/PresentationDtos.cs`,
  `src/PresenterAi.Api/Endpoints/PresentationEndpoints.cs`,
  `web/shared/openapi/v1.json`, `web/shared/src/api/generated.d.ts`.
- **Change:** strict parse (§4.3); writer round-trip; `frontmatter` jsonb includes `maxMinutes`; DTO field; refresh
  OpenAPI and regenerate the client (drift chain, `AGENTS.md:88-90`).
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests --filter Script`; `OpenApiTests`;
  `cd web && bun run generate:api` then `git diff --exit-code -- web/shared/src/api` after commit.
- **Test that dies if this breaks:** `tests/PresenterAi.Application.Tests/Scripts/ScriptParserTests.cs`
  `Parses_max_minutes`, `Rejects_non_positive_or_non_integer_max_minutes` (`0`, `-5`, `"abc"`, `"12abc"`, `1.5`),
  `Keeps_above_ceiling_max_minutes`; `ScriptWriterTests` `Round_trips_max_minutes`;
  `tests/PresenterAi.Integration.Tests/Cli/ImportCommandTests.cs`
  `Invalid_max_minutes_fails_that_file_with_a_clear_error`;
  `tests/PresenterAi.Api.Tests/PresentationEndpointTests.cs` `Meta_carries_max_minutes`; existing
  `OpenApiTests.Checked_in_document_matches_live_document`.

### T3 — Talk guard: max length, idle, warnings, end reasons  (AC1, AC2, AC4, AC5)
- **Files:** new `src/PresenterAi.Application/Presenting/TalkGuard.cs`, `Presenter.cs`, `PromptBuilder.cs`;
  `tests/PresenterAi.Application.Tests/Presenting/FakeSession.cs` (close seconds knob).
- **Change:** guard per §4.1; max deadline armed at Start acceptance and tightened after load, clamp log;
  activity hooks at the sites listed in §4.1;
  `LimitWarning` event + one spoken line; every End site (§3 list) passes an `EndReasons` value; `Closed` carries the
  new fields; `EndAsync(endReason, …)` on `Presenter`.
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests`.
- **Test that dies if this breaks:** new `tests/PresenterAi.Application.Tests/Presenting/PresenterTalkGuardTests.cs`:
  `Max_length_ends_from_presenting|paused|question_hold|stalled_slide` (theory; after `AdvanceAndSettle` to exactly
  the deadline asserts `EndReason == max_length`, `Closed.EndedAt == startedAt + cap`, `CreatedSessions == 1`,
  `DisposeCount == 1`, and after a further settle no new session was created),
  `Slow_load_and_connect_count_against_the_cap`, `End_reasons_are_in_the_vocabulary` (every close path in the §4.3
  table, asserting both `EndReason` and `Reason`), `Effective_cap_is_min_of_script_override_and_ceiling` (theory),
  `Above_ceiling_cap_is_clamped_with_a_warning_log`, `Override_above_the_cap_is_ignored`,
  `Idle_ends_after_five_minutes_without_activity`, `Activity_just_before_the_idle_deadline_resets_it`,
  `Mic_frames_are_not_activity`, `Idle_does_not_run_while_paused`,
  `Warning_sixty_seconds_before_max_length_is_raised_and_spoken_once`, `Idle_warning_is_cleared_by_activity`,
  `No_spoken_warning_while_paused`, `Deadline_elapsing_while_ending_is_ignored`.

### T4 — Pause-close and reconnect on Resume  (AC1, AC3)
- **Files:** `Presenter.cs`, `TalkGuard.cs`, `PresenterSnapshot.cs` (from T1), `FakeSession.cs`.
- **Change:** extract `ConnectUpstreamAsync` from `StartAsyncCore :543-599` (Start and Resume share it), taking
  `_connectCts` and the max deadline (§4.1: off-loop cancellation, cap check before each candidate and after connect);
  `SuspendUpstreamAsync`; Resume/Next/Prev/Goto reconnect while suspended; `UpstreamStatus` event; `Suspended` in the
  snapshot; usage accumulation across segments; max length ends a suspended talk.
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests`.
- **Test that dies if this breaks:** new `tests/PresenterAi.Application.Tests/Presenting/PresenterPauseCloseTests.cs`:
  `Two_minutes_paused_closes_and_disposes_the_upstream_but_stays_paused`, `Suspended_talk_sends_no_audio`,
  `Resume_after_suspend_opens_a_new_upstream_and_re_presents_the_current_slide`,
  `Next_after_suspend_reconnects_then_presents_the_target_slide`, `Mute_is_reapplied_after_reconnect`,
  `Reconnect_failure_ends_with_reconnect_failed`, `Max_length_ends_a_suspended_talk_without_an_upstream`,
  `Usage_totals_both_segments_and_is_unconfirmed_if_one_segment_timed_out`,
  `Late_closed_event_of_the_suspended_session_is_ignored`, `Resume_within_grace_keeps_the_same_upstream`,
  `Reconnect_held_across_the_cap_is_cancelled_and_ends_with_max_length` (resume at `cap − 1 s`, connect gate held
  past the deadline: candidate disposed, `_session` never assigned, `CreatedSessions` unchanged after settle,
  `EndReason == max_length`, `EndedAt == deadline`), `End_during_a_hanging_reconnect_cancels_it_and_keeps_user_reason`,
  `Two_pause_close_resume_cycles_do_not_extend_the_cap` (original deadline and `max_length` still hold).

### T5 — Orphan windows in the presenter and per-run tool cancellation  (AC7, AC9)
- **Files:** `Presenter.cs`, `src/PresenterAi.Infrastructure/Tools/Mcp/McpTool.cs`, `FakeSession.cs` (connect gate
  honouring the token, throw-on-append knob).
- **Change:** per-start CTS for load + connect; `AbortPendingStart()`; `DisposeAsync` cancels it before `Shutdown`;
  cancellation stops the route loop; post-assignment try/dispose; loop-level catch + fail-safe close + `finally`;
  `_runCts` linked into `InvokeBoundedAsync` and approved calls; `McpTool` retry guard (§4.1).
- **Verify:** `dotnet test tests/PresenterAi.Application.Tests tests/PresenterAi.Infrastructure.Tests`.
- **Test that dies if this breaks:** new `tests/PresenterAi.Application.Tests/Presenting/PresenterLifetimeTests.cs`:
  `Handler_exception_closes_the_upstream_and_the_next_start_works`,
  `Throw_after_session_assignment_disposes_it_and_returns_idle`,
  `Dispose_during_a_hanging_connect_disposes_the_candidate_without_trying_the_fallback`,
  `Abort_pending_start_cancels_the_connect`, `End_during_a_hanging_start_connect_cancels_it`,
  `End_cancels_an_in_flight_tool_call`,
  `End_cancels_an_approved_tool_call`; new
  `tests/PresenterAi.Infrastructure.Tests/Tools/McpToolCancellationTests.cs`:
  `Caller_cancellation_during_refresh_does_not_retry_or_mark_needs_reconnect`,
  `Caller_cancellation_during_reconnect_does_not_retry` (both assert retry call count 0 and status mutation count 0),
  `Caller_cancellation_before_retry_returns_cancelled`.

### T6 — Disposal log, session fields and migration  (AC10)
- **Files:** `src/PresenterAi.Infrastructure/Live/LiveSession.cs`, `Persistence/Entities/Session.cs`,
  `Persistence/PresenterAiDbContext.cs`, new `Persistence/Migrations/<timestamp>_SessionBillingGuards.cs` (+ Designer,
  snapshot), `Sessions/SessionRecorder.cs`.
- **Change:** §4.3 log line and columns; recorder maps `Closed` fields and `ConnectedAt`; fallback rules for
  `EndAsync` without `Closed`.
- **Verify:** `DOCKER_HOST=tcp://localhost:2375 dotnet test tests/PresenterAi.Integration.Tests`;
  `dotnet test tests/PresenterAi.Infrastructure.Tests`.
- **Test that dies if this breaks:** `tests/PresenterAi.Infrastructure.Tests/Live/LiveSessionTests.cs`
  `Dispose_logs_upstream_disposal_with_session_id`, existing `Close_times_out_and_aborts_when_server_is_silent`;
  `tests/PresenterAi.Integration.Tests/Sessions/SessionRecorderTests.cs`
  `Records_end_reason_local_times_and_confirmed_usage`, `Close_timeout_stores_the_estimated_seconds_not_zero`,
  `Bridge_end_without_closed_stores_disconnect_and_unconfirmed`; existing
  `MigrationsApplyToPostgresTests.Migrations_apply_the_required_postgres_schema` (extend to the three columns).

### T7 — Bridge: heartbeat, abort, frames, override, slot and shutdown  (AC2, AC3, AC5, AC6, AC7)
- **Files:** `src/PresenterAi.Api/Realtime/PresenterBridge.cs`,
  new `src/PresenterAi.Api/Realtime/PresenterShutdownService.cs`,
  `src/PresenterAi.Api/Program.cs` (register hosted service); `tests/PresenterAi.Api.Tests/Infrastructure/ApiFactory.cs`
  (opt-in `UseFakeClock()` + `AdvanceAndSettleAsync`, see R6), `BridgeTestSupport.cs`.
- **Change:** inject `TimeProvider` and heartbeat options; `ClientConnection.Abort(reason)`, `MarkAlive`, heartbeat
  timers; `pong` command; `maxMinutes` parse; `limit_warning`, `upstream`, extended `closed`/`state` frames; end
  reason into `ObserveEndAsync`; start-observation expiry → `AbortPendingStart()` + bounded wait; shutdown service.
- **Verify:** `dotnet test tests/PresenterAi.Api.Tests`.
- **Test that dies if this breaks:** new `tests/PresenterAi.Api.Tests/BridgeBillingGuardTests.cs` (real DI,
  `FakeLiveServer`, `FakeTimeProvider`): `Silent_browser_is_aborted_at_45_s_ends_the_talk_and_frees_the_slot`
  (asserts presenter `Closed.EndReason`, the `closed.endReason` frame sent before the abort where writable, and the
  recorder row are all `heartbeat`; no upstream connection is created after the abort; the slot is released only
  after the presenter is idle; a new client then connects), `Frames_keep_the_connection_alive`,
  `Writer_failure_ends_the_receive_loop_with_writer_failed`, `Backpressure_abort_records_backpressure`,
  `Start_max_minutes_override_lowers_the_cap`,
  `Start_max_minutes_below_five_is_a_protocol_error`, `Limit_warning_frame_carries_seconds_left`,
  `Pause_close_sends_upstream_suspended_and_resume_sends_live`, `Closed_frame_carries_end_reason`,
  `Start_observation_expiry_aborts_the_start_before_freeing_the_slot` (no connection created after the abort;
  presenter idle before release),
  `Application_stopping_closes_the_live_upstream`; existing `BridgeTests.Ping_is_answered_while_start_is_connecting`,
  `BridgeTests.Backpressure_against_a_silent_peer_releases_the_slot_after_the_bounded_close`,
  `BridgeSessionRecorderTests` (all).

### T8 — CLI Ctrl+C, clamp and end reasons  (AC8)
- **Files:** `src/PresenterAi.Cli/Program.cs`, `src/PresenterAi.Cli/RunCommand.cs`.
- **Change:** `Console.CancelKeyPress` → CTS passed to `RunAsync`; cancellation path in `RunWithPresenterAsync`
  (`AbortPendingStart`, `EndAsync(cli_cancelled)`, bounded wait); `--max-seconds` clamp to the ceiling with a warning
  line; `stop_after_slide` / `cli_max_seconds` reasons; print `end=` on the closed line.
- **Verify:** `dotnet test tests/PresenterAi.Cli.Tests`.
- **Test that dies if this breaks:** `tests/PresenterAi.Cli.Tests/CliTests.cs`
  `Cancel_mid_talk_closes_the_upstream_before_max_seconds`, `Cancel_during_startup_leaves_no_upstream`
  (`FakeLiveServer.StartDelayMs`), `Max_seconds_above_the_ceiling_is_clamped_with_a_warning`; existing
  `Run_sample_against_fake_server_stops_after_slide_2`.

### T9 — Web: pong, picker, warning banner, suspended state  (AC2, AC3, AC5)
- **Files:** `web/app/src/ws/bridgeClient.ts`, `web/app/src/store/presenterStore.ts`, `web/app/src/routes/Present.tsx`.
- **Change:** answer `ping` with `pong`; `start(presentation, fromIndex?, maxMinutes?)`; `limit_warning` and `upstream`
  events into the store; a "Length" select next to Start (Default = script or server cap, then 5/10/15/20/30/45/60/90
  minutes; shown only while idle); a banner with a local 1 s countdown cleared on `secondsLeft:null`/`closed`; a
  "Paused — disconnected to save cost; Resume reconnects" banner while `suspended`, "Reconnecting…" while
  reconnecting. Every `dark:bg-*` surface sets `dark:text-*`; selectors return stored references (`AGENTS.md:92-93`).
- **Verify:** `cd web && bun run lint && bun run test && bun run build`.
- **Test that dies if this breaks:** `web/app/src/ws/bridgeClient.spec.ts` `answers server ping with pong`,
  `start sends maxMinutes only when chosen`, `emits limit_warning and upstream`; `web/app/src/routes/Present.spec.tsx`
  `shows a countdown banner on limit_warning and clears it`, `shows the disconnected banner while suspended`,
  `length picker sends the chosen override`.

### T10 — Docs: guide and protocol reference  (AC11)
- **Files:** new `docs/guides/004-talk-limits-and-billing.md`, `docs/reference/001-api-and-code-conventions.md` §8,
  `AGENTS.md` (one line linking the guide).
- **Change:** guide: what each guard does, the effective-cap formula, frontmatter `maxMinutes`, the picker, the pause
  reconnect behaviour and its cost, heartbeat, CLI Ctrl+C and clamp, end reasons, the `sessions` columns, and a table
  of all six keys (default, range, env name). Fix §8's ping line to the real protocol and list the new frames.
- **Verify:** every key in §4.3 appears in the guide table (`grep` for each key name).
- **Test that dies if this breaks:** T11's key-to-doc grep check.

### T11 — Wiring audit and full verification  (AC1–AC11)
- Trace: each new config key → its reader (`PresenterSettings`/`TalkGuard`/bridge/CLI); `LimitWarning` and
  `UpstreamStatus` → bridge frame and CLI log; `EndReasons` → every End site and the recorder column;
  `AbortPendingStart`
  → bridge expiry, shutdown service, CLI; `PresenterShutdownService` registered and its `StopAsync` reached;
  `maxMinutes` → parser → DTO → web picker → `start` frame → presenter; `ConnectedAt`/`EndedAt` → recorder; web
  `pong` → bridge `MarkAlive`. Flag anything without a caller.
- **Verify:** `dotnet build PresenterAi.slnx -warnaserror` (0 warnings);
  `DOCKER_HOST=tcp://localhost:2375 dotnet test PresenterAi.slnx`; `cd web && bun run lint && bun run test && bun run
  build`; `bash scripts/secrets-guard.sh`; each §4.3 log line is seen once in a local CLI run against
  `FakeLiveServer`-backed tests' output or the runbook.

### AC → task matrix

| AC | Tasks |
|---|---|
| 1 max length from every state | T3, T4 |
| 2 effective cap and clamp | T1, T2, T3, T7, T9 |
| 3 pause-close and reconnect | T4, T7, T9 |
| 4 idle | T3 |
| 5 warning frame, countdown, spoken once | T3, T7, T9 |
| 6 heartbeat and writer failure | T7 |
| 7 orphan windows | T5, T7 |
| 8 CLI Ctrl+C and clamp | T8 (T5 for `AbortPendingStart`) |
| 9 tool cancellation | T5 |
| 10 row fields, estimate, disposal log | T6 (fields from T3) |
| 11 build, suites, guide | T1, T10, T11 |

## 7. Test strategy

- **Unit (fake clock, `FakeSession`):** every guard, pause-close and orphan case in T3–T5. No test opens a real
  upstream (A7); `FakeLiveServer` is an in-process loopback fake. Not covered: real provider billing, browser tab
  freezing, OS-level socket teardown on process kill.
- **Integration through the real DI container:** `ApiFactory` + `FakeLiveServer` + opt-in `FakeTimeProvider`
  (`BridgeBillingGuardTests`, T7) for heartbeat death → End → slot free, pause-close frames, start-observation expiry
  and `ApplicationStopping`; CLI `RunWithPresenterAsync` against `FakeLiveServer` (T8); recorder against
  Testcontainers Postgres (T6).
- **Mutation evidence (repo rule, `AGENTS.md:76-77`):** for each new oracle, note in the PR the wrong implementation
  it catches (for example: idle counting mic frames; max length armed only while Presenting; heartbeat re-arm on the
  ping timer instead of the last frame; retry after caller cancellation).
- **Manual runbook** (run later with the user; unchecked items block merge):

| # | Step | Expected |
|---|---|---|
| 1 | Start a talk with the picker at 5 min (web) | "limit: max length 5 min" in the log; banner at 4:00 with countdown; one spoken warning; ends `max_length` at 5:00 |
| 2 | Pause, wait 2 min | Banner "Paused — disconnected"; server log "Upstream socket disposed: session=…"; usage stops |
| 3 | Resume | "Reconnecting…", then the current slide is narrated again; `resume: reconnected via …` |
| 4 | Say nothing, answer "no" to carry on, wait 5 min | Idle warning at 4:00; ends `idle` |
| 5 | Kill the browser's network (DevTools offline) mid-talk | Within 45 s the server logs a heartbeat abort; row `end_reason = heartbeat` |
| 6 | CLI `run <slug>`, Ctrl+C on slide 2 | "closed … end=cli_cancelled" within a few seconds, exit 1 |
| 7 | Inspect the latest `sessions` rows | `end_reason`, `usage_confirmed`, `estimated_seconds` filled; usage not 0 |

## 8. Rollout / phasing

One PR stacked on #8, merged after #8. Ship order inside the branch: T1 → (T2, T6, T3 → T4 → T5) → T7, T8, T9 →
T10 → T11. The migration must be applied (`dotnet ef database update`) before the API with this build records
sessions; the API never auto-migrates. No feature flag: the guards are the point; the ranges in §4.3 are the only
tuning.

## 9. Open questions

None block approval. Decisions this plan made that the brief did not settle — confirm or change at G2:
1. **Heartbeat mechanism:** an application `{"type":"ping"}` / `{"type":"pong"}` with a read deadline on any inbound
   frame, not WebSocket-protocol keep-alive (§4.2 H1/H2).
2. ~~Max length starts at connect~~ — reversed after review r-p009 (D1): it starts when the Start is accepted, as
   the brief's A1 says, and a slow load/connect counts against it.
3. **Resume after pause-close re-narrates the current slide from its first part** (the new upstream has no memory).
   Navigation while suspended also reconnects.
4. **Spoken warning only while presenting**; while paused the banner alone warns.
5. **Override below 5 is rejected** with a protocol error; an override at or above the cap is ignored.
6. **Config key names and ranges** in §4.3 (ceiling up to 240, pause grace 30–900 s, idle 120–1800 s, heartbeat
   interval 5–60 s); the 60 s warning lead is fixed.
7. **"Local start/end"** reuses `started_at` / `ended_at`, now taken from the presenter's clock; new columns are
   `end_reason`, `usage_confirmed`, `estimated_seconds`. `usage_seconds` holds the estimate when unconfirmed.
8. **"Closed-reason frame"** is the existing `closed` frame with `endReason` added, not a new frame type.
9. **End reason vocabulary and mapping table** as listed in §4.3 (adds `writer_failed`, `backpressure` after
   review r-p009).
10. ~~Compose / `.env.example` placeholders~~ dropped by the user before approval.

## 10. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-23 | Requirement brief confirmed (G1) | all six research gaps; decisions in §2 |
| 2026-09-23 | External plan review r-p009 (codex gpt-6-sol/high, read-only) | 2 blockers (cap armed at connect; reconnect not cancellable by End/cap), vocabulary gap, MCP cancellation during refresh/reconnect, weak test oracles, missing `ApiFactory` clock seam; all folded into §4.1, §4.3, §4.4, §5, T3–T7, §9 |
| | Plan approved (G2) | |
