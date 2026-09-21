# 004 — Handoff: plan 002 implementation, after T8 (T10 and review round 1 in flight)

**Written:** 2026-09-21 17:41 CDT at the user's automation reminder (ctx 40–60 %), at a clean boundary:
everything finished is committed; two `pi` agents are still working in their own terminals and write their
results to report files on disk. Supersedes `003-handoff-implement-plan-002.md` (its rules still apply).

## Goal

Finish plan `docs/plan/002-phase0-dotnet-core-port-api-web.md` (Approved 2026-09-21): tasks T10–T16 remain, plus
folding in review round 1 and running review rounds 2 and 3. Acceptance AC1–AC5 (+ partial AC13).

## Environment

- Working directory `D:\sources\demo\presenter-ai`, git branch **`feature/002-dotnet-core-port`** (no worktree).
  Remote `origin` = `https://github.com/PMTLabs/presenter-ai.git`; `master` (initial commit) and `develop` are
  pushed; the feature branch is **not pushed yet** (push it when T3's CI should run — before/at T16).
- Toolchain: .NET SDK 10.0.401, bun 1.3.14, Node 22, Docker 29.6.2 **without** the `compose` v2 plugin
  (`docker-compose` 5.3.1 works locally; CI uses `docker compose`).
- Port 47913 = presenter-ai API (free unless an agent's manual check is running). **Never touch port 3000.**
- `.env` holds real secrets (`UPSTREAM_ENDPOINT`, `UPSTREAM_KEY`, `FALLBACK_OPENAI_KEY`) — never print; the .NET
  side is configured via `--Upstream:Endpoint=… --Upstream:Key=…` / `dotnet user-secrets` (not yet set up).
- Scratchpad (briefs + reports): `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\121ab6bf-f7d7-40c1-bca4-9b126c3c7052\scratchpad\impl-002\`
  — `T{2..9}-report.md` complete; `T10-brief.md`, `T10-report.md` (in progress), `review1-report.md` (in progress).
- Memory dir: `C:\Users\tamtr\.claude\projects\D--sources-demo-presenter-ai\memory\`.

## Rules (unchanged from handoff 003 — read its "Rules" section)

Delegate coding to `pi` via the `agent-research` skill (scout luna:medium; implement luna:high for T12/T13,
terra:medium for T14, terra:high for T10; review terra:medium with A/B/C/D + "IS THIS BRANCH READY TO MERGE?",
ledger row in `docs/agentic/review-rounds-ledger.md`); Claude does T11, T15, T16, trivial fixes, commits per task
(explicit `git add` of task files, `scripts/secrets-guard.sh` before each commit, no AI attribution). Agents
never open `.env`, commit or push. Every user-facing response ends with `Task done: …`. Monitors on report files
must require the completion tag **and** real content (agents write placeholder skeletons first).

## Done (all committed on the feature branch; `dotnet test PresenterAi.slnx` = Application 47, Infrastructure 25, Api 7)

| Task | Commit | Notes |
|---|---|---|
| T1 git init/push | `326cab7` | master + develop pushed |
| T2 scaffold | `413b817` | Problem Details, ErrorCodes → `x-error-codes`, OpenAPI, ValidateOnBuild |
| T4 scripts | `3c32aa6` | YamlDotNet, 9 scenarios + round-trips |
| T6 AudioLevel | `01c6f55` | |
| T3 docker/CI | `c2dd9b6` | compose ports 5433/6382; CI web-job guard fixed by Claude |
| T5 PromptBuilder | `6a452c9` | Node golden snapshot |
| T9 upstream options | `7c676fd`, `2f22c58` | Infrastructure kept free of ASP.NET; missing key → exit 1 (filter `!isTesting`) |
| T7 LiveSession + fake | `1610b27` | de-flaked by Claude (startup-error race, burst-count waits) |
| golden JSON | `c2d44e9` | `tests/PresenterAi.Api.Tests/Golden/*.json` from the Node server |
| T8 Presenter | `afa893d` | 22 tests, single loop, `LiveStartupException` |
| work log | `4205a71` | `docs/progress/002-work-log-phase0.md` through T8 |

## In progress (two terminals — check them first)

1. **Review round 1** — `pi --model openai-codex/gpt-5.6-terra:medium`, termflow terminal **`tm-a8ecd08fb`**,
   scope `326cab7..2f22c58` (T2–T6, T9). Report: `…\impl-002\review1-report.md` (partially written at 17:40:
   first finding = a secrets-guard bypass — a JSON `"Key" : "…"` with whitespace before the colon is not matched;
   "Problem Details do not …" was being written). Wait for `<!-- REPORT COMPLETE -->` + "READY TO MERGE";
   monitor task `bl41ubdlw` may have expired (30 min) — re-arm a Monitor on the file if needed.
   Then: read `~/.claude/docs/08-why-reviews-take-too-many-rounds.md`, fix the class of each finding (grep with
   more than one pattern), add the ledger row `| 2026-09-21 | feature/002-dotnet-core-port | 1 | A | B | C | D | … |`
   to `docs/agentic/review-rounds-ledger.md`, save the report to `docs/review/002-plan-002-impl-review-round-1.md`,
   commit, close the terminal.
2. **T10** — `pi --model openai-codex/gpt-5.6-terra:high`, terminal **`tm-6fe2d41bb`**, brief
   `…\impl-002\T10-brief.md` (endpoints, file content, Dev auth, `/ws` bridge, golden/contract/bridge tests).
   Report `…\impl-002\T10-report.md`. On completion: verify independently (`dotnet build -warnaserror`,
   `dotnet test PresenterAi.slnx`, run the Bridge tests several times for flakes), spot-check
   `Api/Realtime/PresenterBridge.cs` against plan §4.6, commit `feat(api): … (T10)`, close the terminal.
   A one-shot health-check cron (`f3a579cd`, 17:44) covers both terminals; re-arm ~15 min checks while they run.

## Next steps (plan order)

3. **T11 (Claude, Chrome, AC4):** stop nothing on 3000; run the .NET API alone on 47913
   (`dotnet run --project src/PresenterAi.Api` with real upstream values supplied via `dotnet user-secrets set
   Upstream:Endpoint …` / `Upstream:Key …` — copy them by hand from `.env` without printing; or pass
   `--Upstream:Endpoint=… --Upstream:Key=…` on the command line); open `http://localhost:47913/` (the Node page
   served from `src/web`) in Chrome via the browser MCP (`tabs_context_mcp` first; page DPR 1.5 → click via
   `javascript_tool`), present Ricoh slides 1–5 (auto-advance, → key, Space pause/resume, deck › button, Esc);
   check `window.__presenterDebug()` (`wsOpen`, `framesSent` rising, usage pill) and the server log
   (`session.closed`, `client_request`); record `usage.seconds` in the work log.
4. **T12 (pi luna:high):** CLI `presenter-cli smoke --provider azure|openai` and `run <id> [--max-seconds N]
   [--stop-after-slide N]` sharing the DI registrations; `CliTests.Smoke_selects_provider_and_reports_usage_seconds`
   and `CliTests.Run_sample_against_fake_server_stops_after_slide_2` against `FakeLiveServer`. Then **review
   round 2** (terra:medium) over T7/T8/T10/T12; then the **live smoke on Azure + OpenAI (AC3)**.
5. **T13 (pi luna:high)** web workspaces (`web/{shared,app,admin}`, bun, generated client from
   `/openapi/v1.json`, `errorCodes.ts`, `isProblem`, `errorMessages.ts`, InkSpoke kit copies, Vite 47914 proxy);
   **T14 (pi terra:medium)** presenter page port (WS client sends the optional `auth` frame; vitest suites);
   **T15 (Claude)** real-Chrome run of the React app (AC5 incl. spoken-question check) + work log; **review round
   3**; **T16 (Claude)** wiring audit, README, push the feature branch, PR to `develop` (CI must be green).
6. Keep `docs/progress/002-work-log-phase0.md` updated after each task.

## Gotchas / settled decisions

- Frozen wire formats: plan §4.3 (HTTP trio + WebSocket frames; `start.presentation` required; `transcript`
  uses `start_ms`/`end_ms`); conventions doc §5/§6 for everything else; `generation.job_failed` catalogued as 500.
- Concurrency model plan §4.6 is implemented in `LiveSession` (one send loop, pump enqueues, `Finish` CAS) and
  `Presenter` (one event loop); the bridge must follow the same rule (single writer task, bounded channel,
  1011 when the client cannot drain, no drop-oldest).
- Pump math: silence frames = (elapsed − 120 ms slack) / 20 ms (plan wording corrected on 2026-09-21).
- `Program.cs` catches `OptionsValidationException` only when `!isTesting` (captured before `Run()`; the host
  is disposed by the time the filter runs) — do not "simplify" it.
- Application.Tests uses plain xUnit asserts (no FluentAssertions reference); Infrastructure.Tests and Api.Tests
  use FluentAssertions 7.0.0 + Moq 4.20.72. Infrastructure.Tests has the `Microsoft.AspNetCore.App` framework
  reference (for `FakeLiveServer`); Infrastructure itself must stay free of ASP.NET.
- Windows: big heredocs fail — write `.py`/`.md` files with the Write tool; `Get-NetTCPConnection` for ports;
  `docker-compose` (v1 CLI) locally.
- Browser: device-rate `AudioContext`; Start must not block on the mic; Claude-in-Chrome clicks via `javascript_tool`.
