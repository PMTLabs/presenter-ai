# 003 — Implementation review round 2: plan 002 (T7, T8, T10, T12, review-1 fixes)

**Reviewer:** `pi --model openai-codex/gpt-5.6-terra:medium`, review-only (no edits, no tests, no server, no `.env`), 2026-09-21.
**Scope:** commits `2f22c58..906af83` on `feature/002-dotnet-core-port` (LiveSession + fake server, Presenter state machine, endpoints/static/Dev auth/`/ws` bridge, review-round-1 fixes, CLI + shared `AddPresenter()`) against plan 002 §§4.3/4.6/6/7, conventions §§3/5/6/12 and the Node originals. `web/**` (T13, uncommitted at the time) excluded.
**Classification:** F1 D (major), F2 D (major), F3 D (minor), F4 B (minor), F5 B (minor). No A, no C. Round-1 F1/F2 class fixes verified at every site. Ledger: `docs/agentic/review-rounds-ledger.md`.

## Disposition (orchestrator)

| Finding | Class | Fix applied |
|---|---|---|
| F1 sessions never disposed | D | _pending — see work log_ |
| F2 unobserved shutdown tasks | D | _pending_ |
| F3 guard misses `\u`-escaped JSON names | D | _pending_ |
| F4 `stop-after-slide` oracle accepts slide N+1 | B | _pending_ |
| F5 backpressure test drives the seam | B | _pending_ |

---

## Summary

- The round-1 `traceparent` fix is consistently applied to the four actual Problem Details producers found in the committed API range.
- The bridge has the intended CAS ownership and one browser writer, but failed upstream `LiveSession` instances are not disposed during Presenter fallback.
- Two fire-and-forget shutdown paths can fault without observation; this is particularly material for the CLI’s bounded-run shutdown.
- The secrets guard still misses a valid JSON spelling within its stated “any token ending in key” class.
- The CLI `stop-after-slide` oracle explicitly accepts presentation of slide 3 for `--stop-after-slide 2`, so it does not prove the stated two-slide behavior.

## Findings

### F1 — Failed fallback sessions leak their socket/CTS resources [D] — major
- **Where:** `src/PresenterAi.Application/Presenting/Presenter.cs:278-295`; `src/PresenterAi.Infrastructure/Live/LiveSession.cs:31-32,164-180,189-205`; `src/PresenterAi.Application/Presenting/ILiveSession.cs:5-41`.
- **What:** `StartAsyncCore` catches each failed `candidate.ConnectAsync` and immediately tries the next route, but never calls `Terminate()` or disposes the rejected candidate. The concrete candidate owns a `ClientWebSocket` and `CancellationTokenSource`, and releases both only in `DisposeAsync`.
- **Why it matters:** Each failed primary/fallback attempt leaves managed socket/CTS resources for GC instead of deterministic cleanup. Repeated startup failures can accumulate sockets and background loop tasks; this is exactly the fallback lifecycle path the port added.
- **Suggested fix:** Make the session port disposable (or add an explicit async close/dispose operation) and dispose/terminate every candidate in the catch before advancing; add a fake-session assertion that every failed candidate is disposed/terminated once.

### F2 — Presenter and CLI shutdown fire-and-forget tasks are unobserved [D] — major
- **Where:** `src/PresenterAi.Application/Presenting/Presenter.cs:545,583`; `src/PresenterAi.Cli/RunCommand.cs:68-69`.
- **What:** Timer handlers invoke `_ = EndAsyncCore()`, and the CLI slide callback invokes `_ = presenter.EndAsync(...)`; neither task is observed. A close/transport exception becomes an unobserved task exception, while the CLI waits only for `Closed` and reports a generic timeout.
- **Why it matters:** The required bounded shutdown can fail silently in the same conditions it is meant to contain (upstream close failure). It also violates the stated single-loop/lifecycle model by allowing shutdown errors to escape its reporting path.
- **Suggested fix:** Queue/await an end command from the Presenter loop and attach an explicit fault observer that logs and completes the caller with failure. In `RunCommand`, retain the task and include its fault in the command result; test a fake `CloseAsync` failure.

### F3 — Secrets guard misses Unicode-escaped JSON credential names inside its declared class [D] — minor
- **Where:** `scripts/secrets-guard.sh:11-15,48-51,84`; `scripts/secrets-guard.selftest.sh:23-40`.
- **What:** The header promises any JSON token ending in `key` is checked, but the matcher only recognizes literal ASCII letters in the source spelling. Valid JSON such as `{ "Upstream": { "\\u004bey": "abcdefghijklmnopqrstuvwxyz" } }` deserializes to the setting name `Key`, yet neither `name` nor `pattern` matches it.
- **Why it matters:** Configuration binding accepts the decoded key, so a real `Upstream:Key` can be committed under a valid spelling while CI reports clean.
- **Suggested fix:** Parse JSON tracked configuration before evaluating property names (and retain a separate grammar-aware scanner for YAML/env/CLI), or explicitly narrow the documented class. Add this exact escaped-name must-fail fixture.

### F4 — `stop-after-slide 2` test accepts the third slide and cannot prove the advertised stop boundary [B] — minor
- **Where:** `src/PresenterAi.Cli/RunCommand.cs:64-70`; `tests/PresenterAi.Cli.Tests/CliTests.cs:43-70`; plan `docs/plan/002-phase0-dotnet-core-port-api-web.md:538-540`.
- **What:** The implementation only requests end after receiving the slide-3 event (`index + 1 > StopAfterSlide`). The test named `Run_sample_against_fake_server_stops_after_slide_2` explicitly requires `===== SLIDE 3 =====`, so an implementation that always sends slide 3 and its first narration before stopping passes.
- **Why it matters:** This does not establish the plan’s “shows two slides” behavior and can spend/bill model output for an unintended slide.
- **Suggested fix:** Define whether the limit means “do not enter slide N+1” (then request end before advance) or preserve Node’s current post-entry behavior, document that deliberate behavior, and rename the option/test. For the stated two-slide contract, assert no slide-3 frame or slide-3 upstream instruction.

### F5 — Backpressure test bypasses the production queue-full path [B] — minor
- **Where:** `src/PresenterAi.Api/Realtime/PresenterBridge.cs:238-249,260-262`; `tests/PresenterAi.Api.Tests/BridgeTests.cs:119-135`.
- **What:** `Client_that_cannot_drain_is_closed_1011` calls the internal `ForceBackpressureForTest()` seam directly. A wrong `Enqueue` implementation that drops a failed `TryWrite` (or never calls `FailForBackpressure`) still passes because the test invokes `FailForBackpressure` itself.
- **Why it matters:** The test proves that the seam closes 1011, not that a real full bounded channel reaches that seam; the required no-drop backpressure behavior remains unproved.
- **Suggested fix:** Drive enough browser-bound audio/text through a deliberately blocked writer to fill the actual channel, then assert 1011 and no dropped-frame policy. Keep the seam only for a narrow writer-close unit test.

## Round-1 fixes verified

### F1 — secrets-guard class check
- `scripts/secrets-guard.sh:9-28` now states the class, including case/whitespace JSON, YAML/env/CLI separators and the intentional exclusions.
- `scripts/secrets-guard.sh:48-84` applies a case-insensitive, whitespace-tolerant matcher; `scripts/secrets-guard.selftest.sh:23-40` exercises the round-1 whitespace-before-colon and lowercase JSON examples plus env, YAML, CLI, header, and `sk-` code-constant cases.
- This verifies the round-1 literal-`"Key":` bypass is fixed. F3 above is a remaining spelling not included in that stated/tested class implementation.

### F2 — Problem Details `traceparent` class check
- `src/PresenterAi.Api/Errors/DomainExceptionHandler.cs:25-55` uses `ProblemTrace.Apply`.
- `src/PresenterAi.Api/Errors/Problems.cs:10-28` uses `ProblemTrace.Apply`; this covers the Dev-auth challenge at `src/PresenterAi.Api/Auth/DevAuthHandler.cs:37-42` and non-upgrade `/ws` at `src/PresenterAi.Api/Realtime/PresenterBridge.cs:39-43`.
- `src/PresenterAi.Api/Errors/ProblemTrace.cs:24-43` configures the framework Problem Details writer and writes/caches the same header/body ID.
- `tests/PresenterAi.Api.Tests/ProblemDetailsTests.cs:49-128`, `AuthTests.cs:15-23`, and `BridgeContractTests.cs:70-79` exercise the pipeline and both helper paths. The other 4xx sites in `Program.cs:85,93,103` are frozen plain-text/static or empty fallback responses, not Problem Details producers.

## Not verified

- No tests/builds/servers were run, and no `.env` file was opened.
- `web/**`, untracked files, live Azure/OpenAI behavior, Chrome/AC4/AC5, CI execution, and commit remote state were not verified.
- I did not exhaustively mutation-test every test in `tests/**`; I reviewed the ported bridge, presenter, LiveSession, content, API, CLI, and directly related oracle classes.

## IS THIS BRANCH READY TO MERGE?

**Blockers:** F1 and F2 should be fixed before merge because they affect deterministic resource cleanup and error visibility on the new live-session lifecycle.

**Improvements:** F3 and F4 should be addressed before treating the branch as a security/parity gate.

No — not ready to merge until the blockers are resolved.

## Files examined

- `docs/plan/002-phase0-dotnet-core-port-api-web.md` (§§4.3, 4.6, 6, 7)
- `docs/reference/001-api-and-code-conventions.md` (§§3, 5, 6, 12)
- `docs/review/002-plan-002-impl-review-round-1.md`
- `docs/progress/002-work-log-phase0.md`
- Node originals: `src/server/{live-client,presenter,index}.js`; `scripts/{live-smoke,headless-run}.mjs`; `test/{presenter,integration}.test.js`
- Port implementation: `src/PresenterAi.{Application,Infrastructure,Api,Cli}/**` in the committed range, including bridge, live session, Presenter, content, auth, endpoints, errors, DI and commands
- Related committed tests: API bridge/auth/problem/detail/presentation/startup tests; Application Presenter/Audio tests; Infrastructure Live/content tests; CLI tests; secrets guard/self-test

<!-- REPORT COMPLETE -->