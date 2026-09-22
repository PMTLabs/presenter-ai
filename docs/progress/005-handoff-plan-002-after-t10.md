# 005 — Handoff: plan 002 implementation, after T10/review 1 (T12 and T13 in flight)

**Written:** 2026-09-21 18:48 CDT at the user's automation reminder (ctx 40–60 %), at a clean boundary: everything
finished is committed (tip `e74eb82`); two `pi` agents are working in their own terminals and write to report files.
Supersedes `004-handoff-plan-002-after-t8.md` (its Rules/Gotchas still apply; read its "Rules" section).

## Goal

Finish plan `docs/plan/002-phase0-dotnet-core-port-api-web.md`: T12 (CLI) and T13 (web workspaces) are running;
remaining T11-Chrome, live smoke AC3, review round 2, T14, T15, review round 3, T16.

## Environment

- `D:\sources\demo\presenter-ai`, branch **`feature/002-dotnet-core-port`** (no worktree), not pushed yet.
- Scratchpad: `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\121ab6bf-f7d7-40c1-bca4-9b126c3c7052\scratchpad\impl-002\`
  — briefs `T12-brief.md`, `T13-brief.md`; reports `T12-report.md`, `T13-report.md` (in progress); headless client
  `headless-remote.mjs` (usage: `node headless-remote.mjs ws://localhost:47913/ws <id> --stop-after-slide N`).
- API secrets are in `dotnet user-secrets` for the Api project (`Upstream:Endpoint`, `Upstream:Key`,
  `Upstream:Fallback:Key`) — never print; the CLI has its own UserSecretsId `presenter-ai-cli` (not set yet — set the
  same three with `dotnet user-secrets set … --project src/PresenterAi.Cli` by copying from `.env` via a script that
  never echoes values, as done for the Api).
- **Run the API from a published copy** while agents build: `dotnet publish src/PresenterAi.Api -c Debug -o <scratch>/api-run`
  then in a termflow terminal `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet <scratch>\api-run\PresenterAi.Api.dll
  --contentRoot D:\sources\demo\presenter-ai\src\PresenterAi.Api` (a concurrent `dotnet build` kills a `dotnet run`
  instance with exit −1). Terminal `tm-dfd9efa2d` was the API terminal; it is a fresh shell after the termflow crash.
- **termflow crashed once at ~18:40** (all terminals became fresh shells, agents died); the agent-research monitors and
  crons are session-only and died with the Claude session too — after any restart, check terminal screens first.
- Claude-in-Chrome extension was **not connected** in this session (T11 browser part and T15 need it; the user was told).
- Test totals at `e74eb82`: Application 47 / Infrastructure 30 / Api 44; `dotnet build -warnaserror` clean; Node 52.

## Done since handoff 004 (all committed)

| Item | Commit(s) |
|---|---|
| Review round 1 folded in (F1 guard class + selftest in CI, F2 traceparent via `ProblemTrace` + `ProblemTrace.Configure`, F3–F9 oracles with mutation evidence) | `dee698a`, `9e0705d`, `d0a6ce0`, `1310738`, `55552cd`; report `docs/review/002-plan-002-impl-review-round-1.md`, ledger row |
| T10 endpoints/static/Dev auth/`/ws` bridge (after a continuation fixing 5 bridge defects; Bridge tests 17 × 6 green) | `25e7863` |
| Content-root trailing-separator bug (every context load failed in a real run) | `ab041a7` |
| Work log through T10 + T11 headless run; scout report | `be06525`, `e74eb82` (`docs/research/003-inkspoke-web-kit-inventory.md`) |
| T11 server chain verified with the real Azure upstream (headless client): Ricoh slides 1→6, 2,644 frames, `usage.seconds = 265.2`, `closed client_request` | work log |

## In progress (check the terminal screens first — Working / Ready-with-summary / fresh shell = crashed)

1. **T12 CLI** — `pi luna:high`, terminal **`tm-88ab62b59`**, brief `T12-brief.md`, report `T12-report.md`.
   Owns `src/PresenterAi.Cli/**`, new `tests/PresenterAi.Cli.Tests/**`, and a shared `AddPresenter()` registration
   (Infrastructure/Application) that the API must also use. On completion: verify (`dotnet build -warnaserror`,
   `dotnet test PresenterAi.slnx`, Cli tests ×3), check the API still uses the shared registration and Api tests are
   green, commit `feat(cli): smoke and run commands sharing the API registrations (T12)`, close the terminal.
2. **T13 web workspaces** — `pi luna:high`, terminal **`tm-baa3d8db1`**, brief `T13-brief.md` (decisions: admin =
   ComingSoon only; Dev-only zustand auth store; `generated.d.ts` + openapi-fetch client; shared Tailwind preset;
   two-hop OpenAPI drift chain = checked-in `web/shared/openapi/v1.json` + Api test `Checked_in_document_matches_live_document`
   + `generate:api` reading the snapshot; ports 47914/47915), report `T13-report.md`. Owns `web/**`, `README.md`
   (append), `tests/PresenterAi.Api.Tests/OpenApiTests.cs` (one test). On completion: verify `cd web && bun install
   && bun run lint && bun run test && bun run build && bun run generate:api && git status --short web` (must be clean),
   `dotnet test tests/PresenterAi.Api.Tests`, spot-check copied files carry `Copied from InkSpoke … @ b83e691f`
   headers and no InkSpoke branding, commit `feat(web): … (T13)` (include `web/bun.lock`), close the terminal.
   If a report is complete but its terminal is a fresh shell, the agent may have died right after — verify anyway.

## Next steps (plan order)

3. **Live smoke AC3 (Claude):** after T12 — set the CLI user-secrets (see Environment), run
   `dotnet run --project src/PresenterAi.Cli -- smoke --provider azure` and `--provider openai`; both must speak and
   close with `usage.seconds`; record in the work log (never print secrets). Also `run ricoh-delivery-overview --stop-after-slide 2`.
4. **Review round 2** — `pi terra:medium`, review-only, over `2f22c58..<tip>` (T7/T8/T10/T12 + review-1 fixes), A/B/C/D
   classification + "IS THIS BRANCH READY TO MERGE?"; read `~/.claude/docs/08-why-reviews-take-too-many-rounds.md`
   before dispatch; save to `docs/review/003-plan-002-impl-review-round-2.md`, ledger row, fold in by class.
5. **T14** (`pi terra:medium`) presenter page port into `web/app` (brief from plan §6 T14 + `src/web/**` as the source;
   WS client sends the optional `auth` frame first; vitest suites `deckDriver`, `bridgeClient`, `startAudio`).
6. **T11 Chrome + T15 Chrome** (Claude) once the extension connects: Node page on 47913 (AC4) and React app on 47914
   (AC5, spoken-question check); record `usage.seconds` and findings in the work log.
7. **Review round 3**, then **T16** (wiring audit, README, push the feature branch, PR to `develop`, CI green).
8. Keep `docs/progress/002-work-log-phase0.md` updated per task; `Task done:` line on every response.

## Gotchas / settled decisions (in addition to handoff 004)

- The secrets guard scans tracked files only: run it **after** `git add` (or re-run on the staged set) — it once
  passed before the self-test fixtures were tracked and would have failed CI.
- Framework Problem Details writer overwrites `traceId`; keep `AddProblemDetails(ProblemTrace.Configure)`.
- `Program.cs` resolves `IOptions<UpstreamOptions>.Value` before `app.Run()` on purpose (clean exit 1 without the
  hosting stack trace); `StartupTests.Missing_upstream_key_exits_cleanly_in_production` pins it.
- T13 `generate:api` must not need a running server in CI (reads the snapshot); the dotnet job pins the snapshot.
- Windows/Node: `node --test test/` fails; use `node --test test/*.test.js`. ESM imports of repo files from the
  scratchpad need `file:///D:/...` URLs and CJS default import for `ws`.
