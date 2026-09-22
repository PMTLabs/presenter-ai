# 006 — Handoff: plan 002 implementation complete except T17 (fluid layout) and the push/PR

**Written:** 2026-09-21 20:14 CDT at the user's automation reminder (ctx 40–60 %), at a clean boundary: tip `9a7b0ee`,
everything finished is committed; one `pi` agent (T17) is editing `web/app/**` and writes to a report file.
Supersedes `005-handoff-plan-002-after-t10.md` (its Rules/Gotchas still apply, and 004's before it).

## Goal

Land plan `docs/plan/002-phase0-dotnet-core-port-api-web.md` on `develop`: T1–T16 are done and reviewed (three rounds);
the user added **T17** (fluid full-width presenter layout + resizable split) mid-way; then push the feature branch and
open the PR (needs the user's explicit OK — outward-facing action).

## Environment

- `D:\sources\demo\presenter-ai`, branch **`feature/002-dotnet-core-port`** (no worktree), **never pushed**, 37 commits
  ahead of `origin/develop`.
- Scratchpad `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\121ab6bf-f7d7-40c1-bca4-9b126c3c7052\scratchpad\impl-002\`
  — briefs/reports per task (`T17-report.md` in progress), `headless-remote.mjs`, published API copy `api-run3/`
  (includes the review-2 fixes and `[FromRoute]`), published CLI `cli-run/presenter-cli.dll`.
- **Running processes:** API from `api-run3` on 47913 (started with `Start-Process`, hidden window — stop by PID:
  `Get-NetTCPConnection -LocalPort 47913 -State Listen | % OwningProcess | % { Stop-Process -Id $_ }`); Vite dev server
  `bun run dev:app` on 47914 in termflow terminal `tm-1b0c232b8`; termflow `tm-da3262145` is a stale shell.
- **Claude-in-Chrome is connected** (tab `1324748785`, currently at `http://localhost:47914/`). The user has granted
  the mic on 47914 and spoke to the presenter during a run (AC5 spoken question done).
- Secrets: API and CLI user-secrets hold `Upstream:Endpoint`, `Upstream:Key`, `Upstream:Fallback:Key` — never print.
- Test totals at tip: Application 51 / Infrastructure 30 / Api 46 / Cli 5; web shared 3, app 18; `-warnaserror` clean;
  `generate:api` drift clean; secrets guard + self-test OK.

## Done since handoff 005 (all committed)

| Item | Commit(s) |
|---|---|
| T12 CLI + shared `AddPresenter()`; AC3 live smoke (Azure `usage.seconds=9`, OpenAI `8`, later `9.2`) | `93f098a`, `906af83` |
| T13 web workspaces + OpenAPI drift chain | `cd93010` |
| Review round 2 (`docs/review/003`, ledger row) folded in: session disposal class, observed shutdown, real backpressure oracle, guard boundary, stop-after-slide claim = Node parity; **deck routing bug** found by the Chrome run (`UseRouting` after static files) | `795132f`, `2490669`, `eafb78a` |
| T11 Chrome run on the .NET API (AC4, usage 98.6 s) | work log |
| OpenAPI `{id}` path parameter (`[FromRoute]`), snapshot + client regenerated | `a4d84a7` |
| T14 React presenter page (+ continuation: zustand selector loop fix, reformat) | `d5bbc56` |
| T15 Chrome run on the React app (AC5, usage 98.4 s) | `41d093a` |
| Review round 3 (`docs/review/004`, ledger row): mic/AudioContext release on teardown, StrictMode-safe deck load, `errorMessages` order, stale-socket guard, three web oracles; **control bar styled** (user: buttons invisible) | `9df90e2`, `f434b47`, `7fe27c8` |
| T16 wiring audit (`docs/research/004`): `IDeckStore` consumed, `Presenter:LogEvents` wired, compose maps every `.env` key; README dual-stack quick start, deck guide, plan approval log | `9df90e2`, `221a33a` |
| **Dark theme text colour** (user: transcript unreadable) — base text on both app roots + every self-backgrounded surface; verified by computed styles in Chrome | `9a7b0ee` |
| Confirmed for the user: sessions use the **Azure primary** (no `connected via` fallback lines; ids `live_EQ…` vs OpenAI `live_u2_…`) | — |

## In progress

1. **T17 — fluid layout + resizable split** — `pi luna:high`, terminal **`tm-cb77306ae`**, report `T17-report.md`
   (monitor `blv00kiht`, health cron `ddc4c066` at 20:26 — both die with the session; re-arm). Decisions (user
   survey): `react-resizable-panels` **v4** (`Group`/`Panel`/`Separator`, `useDefaultLayout` id `presenter-split`,
   `onlySaveAfterUserInteractions`), horizontal split only (deck | transcript-over-log), deck pane fills the viewport
   below the 64 px header, split remembered in localStorage (try/catch), panes stack below `lg`, Library keeps a max
   width, dark-theme text colours on every new surface, `Present.layout.spec.tsx` with mutation evidence. Uncommitted
   agent edits so far: `web/app/{package.json,src/App.tsx,src/routes/Library.tsx,src/routes/Present.tsx}`, `web/bun.lock`.
   On completion: verify `cd web && bun install --frozen-lockfile && bun run lint && bun run test && bun run build
   && bun run generate:api && git status --short web` (only `web/app/**` + lockfile), then Chrome on
   `http://localhost:47914/present/sample`: full width, deck fills height, drag the separator, reload → position kept,
   text readable in dark mode, Start/End still work; commit `feat(web): fluid presenter layout with resizable split
   (T17)`; work-log entry; close the terminal.

## Next steps

2. Optional (offered to the user, not yet accepted): log `[info] connected via <label>` on every successful start
   (today only for attempt > 0, `Presenter.cs:367`).
3. Final gate again after T17: `dotnet build -warnaserror`, `dotnet test PresenterAi.slnx`, web CI chain, secrets guard.
4. **Ask the user** before `git push -u origin feature/002-dotnet-core-port` and `gh pr create --base develop`; then
   watch CI (`gh pr checks`, poll ≥ 30 s), fix red jobs, and record the PR URL in the work log.
5. Stop the API (by PID) and Vite (`tm-1b0c232b8`) when done.

## Gotchas / settled decisions (in addition to 004/005)

- Any endpoint mapped before `UseRouting()` shadows static files — keep `UseRouting()` after `UseStaticFiles` in `Program.cs`.
- A React 19 + zustand 5 selector returning a fresh object loops forever ("Maximum update depth exceeded"); select stored references only.
- Dark theme: every surface with `dark:bg-*` needs `dark:text-*` (root sets `text-gray-900 dark:text-gray-100`).
- `stop-after-slide N` ends when slide N+1 is announced (Node parity) — do not "fix" it.
- Ctrl-C in a termflow terminal did not stop a `dotnet` API; the next instance queued behind it — stop by PID.
- Round-3 reviewer found no client path for the "duplicate server log lines" observation; a stale-socket guard was added anyway and the Chrome re-run did not reproduce it.
- The user wants surveys (AskUserQuestion) before new requirements; T17's three decisions are recorded above.
