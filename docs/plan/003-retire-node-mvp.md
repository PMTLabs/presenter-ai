# 003 — Retire the Node MVP; the .NET API serves the built React app

**Date:** 2026-09-21
**Status:** Approved (2026-09-21)
**Size:** S/M (many deletions, one design choice: what `/` serves)
**Area:** delete `src/server`, `src/web`, `test/`, `scripts/*.mjs`, root `package*.json`; edit `src/PresenterAi.Api` (Program, appsettings, Dockerfile), `src/PresenterAi.Infrastructure/Content/ContentOptions.cs`, `tests/PresenterAi.Api.Tests`, README, guide 001, `AGENTS.md`, `.env.example`, `scripts/secrets-guard.selftest.sh`, new `docs/reference/002-node-mvp-retired.md`
**Requirement brief confirmed:** 2026-09-21 (G1) — one survey round (3 questions), brief confirmed as written
**Numbering note:** plan 002's brief informally called the identity plan "003" and the admin plan "004"; those are
the next free numbers when they are written (004, 005). This file takes 003 because it is the next free number now.

---

## 1. Goal

Remove every artefact of the Node MVP now that the .NET + React stack has proven parity (plan 002, PR #1), and give
the .NET API a real static root: the built React app, always present in the Docker image and served in dev when
`web/app/dist` exists. This also fixes the compose `api` service, which cannot start today because its default web
root (`src/web`) was never copied into the image.

## 2. Requirement (as confirmed at G1)

- **Problem:** the Node server/page/scripts are dead weight; the API still defaults `Content:WebRoot` to
  `src/web`; the Docker image has no such folder and `PhysicalFileProvider` throws on a missing root.
- **In scope:**
  - Delete `src/server/**`, `src/web/**`, `test/**`, `scripts/{headless-run,live-smoke,browser-e2e}.mjs`, root
    `package.json`, `package-lock.json` (they last exist at `2a0b6a1`).
  - API: `ContentOptions.WebRoot` default → `../../web/app/dist`; the web static-file middleware and the SPA
    fallback are added **only when the folder exists**; otherwise one startup log line and `/` → 404.
    `/decks`, `/api`, `/ws`, `/openapi`, `/health` unchanged. `appsettings*.json` and `StartupTests` follow.
  - Dockerfile: `oven/bun:1.3.14` stage builds `web/app`; runtime copies `web/app/dist` → `/app/web` and sets
    `Content__WebRoot=/app/web`. Compose unchanged apart from a comment.
  - Same-origin check of the React client (relative `baseUrl`, `ws(s)://${location.host}/ws`) in the runbook.
  - Docs: README (legacy section, line 40, recovery pointer), guide 001 legacy note, new
    `docs/reference/002-node-mvp-retired.md`, `AGENTS.md` map row, `.env.example` `PORT` comment,
    `secrets-guard.selftest.sh` prose fixture path, plan approval log, work log.
- **Out of scope / non-goals:** hosting `web/admin`; real auth; a CI docker-build job (suggested in the PR only);
  anything from the identity or admin plans; `.gitignore` changes (`node_modules/`, `dist/` stay).
- **Users and surfaces:** developers (repo, README, guide, AGENTS.md); the API at `/` in dev and in the container;
  the Docker image / compose `api` service.
- **Behaviour:** dev without a build → API only, one log line, Vite serves the UI. Dev after `bun run build` → the
  API serves the app at `/` and every SPA route with `Cache-Control: no-cache` on `index.html`. Container → always
  serves the app. Unknown `/decks/...` keeps the deck 404 text.
- **Constraints and assumptions:** branch `feature/003-retire-node-mvp` off `develop` → PR to `develop`, merged only
  on the user's say-so; implementation by `pi` (luna:high) including a Docker build + compose run; no CI changes.
- **Acceptance criteria:**
  1. AC1 — `git ls-files` lists no `src/server`, `src/web`, `test/`, `scripts/*.mjs`, root `package*.json`; a grep for
     `src/server|src/web|npm |\.mjs` outside `docs/{plan,progress,review,research,agentic}` returns nothing.
  2. AC2 — `dotnet build -warnaserror` + all tests green; new Api tests: web root present → `GET /` and
     `GET /present/sample` return `index.html` with `no-cache`; web root absent → `GET /` 404, `/health` 200,
     `/decks/sample/index.html` 200 — each with mutation evidence.
  3. AC3 — from the repo root after `cd web && bun run build`, `dotnet run --project src/PresenterAi.Api` serves the
     React index at `/`; without the build it starts API-only with the one log line.
  4. AC4 — `docker compose --profile full build api && … up -d api` → `/health` 200, `/` returns the React index,
     `/api/presentations` lists the decks (observed output in the work log).
  5. AC5 — README, guide, `AGENTS.md`, reference note updated; `scripts/secrets-guard.sh` and its self-test pass.
  6. AC6 — CI green on the PR.
- **Decisions made:** remove everything Node → yes; what `/` serves → built React app baked into the image, optional
  in dev; record → README line + short reference note.

## 3. Current state (as-built)

- `src/PresenterAi.Infrastructure/Content/ContentOptions.cs:7` — `WebRoot = "../../src/web"`; also set in
  `src/PresenterAi.Api/appsettings.json:26`, `appsettings.Development.json:10`, `appsettings.Example.json:19`.
- `src/PresenterAi.Api/Program.cs:60-73` — resolves `webRoot` and unconditionally adds `UseStaticFiles(new
  PhysicalFileProvider(webRoot))` with `no-cache`; `:93-115` `MapFallback` sends `Path.Combine(webRoot,
  "index.html")` for anything that is not `/decks`, `/api/`, `/ws`, `/openapi/`, `/health`. Static files precede
  `UseRouting` on purpose (`:74-76`, T11 bug) — the change must keep that order.
- `src/PresenterAi.Api/Dockerfile` — build stage copies `src/` only; runtime stage copies `/app/publish`. No `web/`,
  no `src/web` → the container's default root `/src/web` does not exist. `.dockerignore` excludes `web/**/dist`
  and `**/node_modules` (fine: the image builds the app itself).
- `docker-compose.yml:29-63` — `api` (profile `full`) sets `Content__RootDir: /app/content`, no `Content__WebRoot`.
- `tests/PresenterAi.Api.Tests/SpaFallbackTests.cs:9-15` — `Deep_link_serves_index` expects `/present/abc` to return
  a page containing "Presenter"; it passes today only because the default root resolves to the repo's `src/web`.
  `StartupTests.cs:55` passes `--Content:WebRoot=<root>/src/web` to the real process. `ApiFactory` (`Infrastructure/
  ApiFactory.cs`) supports per-test settings through `Overrides` / `WithWebHostBuilder`.
- React client is already same-origin: `web/shared/src/api/client.ts:10` `baseUrl ?? VITE_API_URL ?? ''`;
  `web/app/src/ws/bridgeClient.ts:46` `ws(s)://${location.host}/ws`. Vite dev proxies `/api,/ws,/decks,/health`
  (`web/app/vite.config.ts:4-9`). The app's deck iframe loads `/decks/<name>/index.html` from the same origin.
- Node references outside the deleted trees (grep-verified): README `:40,:196-207`, guide 001 `:113-114`,
  `RunCommand.cs:95` (a comment naming `headless-run.mjs` — keep as history or reword), `secrets-guard.selftest.sh:71`
  (prose fixture), `.env.example` `PORT` comment (Node-only knob), `AGENTS.md` map row. CI has no Node job.
- Nothing in `web/` imports from `src/web` (grep `src/web` in `web/` → none).

## 4. Design

### 4.1 Approach

Delete first, then make the web root optional and point it at the React build: `ContentOptions.WebRoot` defaults to
`../../web/app/dist` (relative to the API content root, i.e. the repo's `web/app/dist` in dev). `Program.cs` computes
`webRoot` as today, then:

```
if (Directory.Exists(webRoot))  → UseStaticFiles(webRoot, no-cache) + MapFallback → index.html   (as now)
else                            → log "[info] web root {path} not found — API only (run `bun run dev:app`)";
                                  MapFallback keeps the /decks 404 text and returns 404 for everything else
```

The Dockerfile gains a `web` stage (`oven/bun:1.3.14`: copy `web/package.json web/bun.lock web/*/package.json`,
`bun install --frozen-lockfile`, copy `web/`, `bun run build` for `app` only), and the runtime stage adds
`COPY --from=web /web/app/dist ./web` and `ENV Content__WebRoot=/app/web`. Everything else (decks volume, content
root, health check) stays. Tests get a temp web root fixture so the `dotnet` CI job needs no bun build.

### 4.2 Alternatives considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| A — optional web root + React build in the image | compose serves the UI; dev unchanged; one image | Dockerfile grows a bun stage (≈ +40 s build) | **Chosen** (user decision) |
| B — API only, drop static root + fallback | smallest diff | compose gives no UI; a hosting story is needed later anyway | Rejected — user chose A |
| C — keep `src/web` as the static page | zero API change | keeps dead client code and two UIs to maintain | Rejected — user chose "everything Node" |

### 4.3 Config model

| Key | Before | After |
|---|---|---|
| `Content:WebRoot` | `../../src/web` (required to exist) | `../../web/app/dist` (optional: missing → API only). Container: `/app/web` via `ENV`. |

No other option changes. `ValidateOnStart` stays; the existence check is a runtime branch, not validation, because a
missing dev build is normal.

### 4.4 Chain (caller → side effect) and parallel paths

```
GET /  ─→ StaticFileMiddleware(webRoot)  ─→ file           (only registered when Directory.Exists(webRoot))
GET /present/x ─→ MapFallback ─→ SendFileAsync(webRoot/index.html)   (same guard)
GET /decks/x   ─→ StaticFileMiddleware(DeckRoot)  → file | "/decks/{**path}" 404 text      (unchanged)
GET /api/…, /ws, /openapi/…, /health ─→ endpoints                                             (unchanged)
Parallel paths checked: the CLI has no web root; the React app calls the API with relative URLs only;
                        Vite dev serves index.html itself — none change.
```

### 4.5 Surface list

| Surface | Change | Task |
|---|---|---|
| Node trees, scripts, root `package*.json` | deleted | T1 |
| `ContentOptions.WebRoot`, `appsettings*.json` | new default, optional | T2 |
| `Program.cs` static root + fallback | guarded by `Directory.Exists`, startup log line | T2 |
| `SpaFallbackTests`, new `WebRootTests`, `StartupTests` | temp web root fixture; present/absent oracles | T3 |
| `Dockerfile` (+ compose comment) | bun build stage, `Content__WebRoot=/app/web` | T4 |
| README, guide 001, `AGENTS.md`, `.env.example`, selftest fixture, `RunCommand.cs:95` comment, new reference note | text | T5 |
| Work log, plan approval log | bookkeeping | T6 |

## 5. Impact and risk

| Question | Answer |
|---|---|
| State management | n/a — static files only; no state. |
| Data consistency | n/a. |
| User experience | Root problem fixed: one stack, compose actually serves the UI. Dev sees one extra log line when no build exists. |
| Backward compatibility | Anyone with `Content:WebRoot=../../src/web` in a local override gets the "not found — API only" line, not a crash. Node scripts are gone — the CLI (`smoke`, `run`) is their replacement (README says so). |
| Error recovery | Missing/deleted web root at runtime → static middleware 404s, fallback returns 404 (guard evaluated at startup; document that a build appearing later needs a restart). |
| Logging & debugging | One `[info]` line names the resolved path and the remedy. |
| Edge cases | `web/app/dist` present but without `index.html` → fallback 404 (log at startup if `index.html` is missing too). Trailing separators handled by `Path.GetFullPath`. |

**Risks:**
- R1 — the `dotnet` CI job has no `web/app/dist` → tests must use a temp web root (T3), never the default.
- R2 — bun stage adds build time and a new base image → pin `oven/bun:1.3.14` (matches CI) and copy lockfile first for
  layer caching.
- R3 — deleting `src/web` while the API on 47913 is running from an old publish → irrelevant to the published copy;
  stop it before the manual runbook to avoid confusion.

**Rollback:** revert the PR; the Node code is intact at `2a0b6a1`.

## 6. Tasks

### T1 — Delete the Node MVP  (AC1)
- **Files:** `src/server/**`, `src/web/**`, `test/**`, `scripts/headless-run.mjs`, `scripts/live-smoke.mjs`,
  `scripts/browser-e2e.mjs`, `package.json`, `package-lock.json` (untracked `node_modules/` at the root: delete
  locally, stays ignored).
- **Change:** `git rm -r` the paths above; nothing else in this task.
- **Verify:** `git ls-files | grep -E '^(src/server|src/web|test)/|^scripts/.*\.mjs$|^package(-lock)?\.json$'` → empty.
- **Test that dies:** n/a (deletion); AC1 grep is the oracle.

### T2 — Optional web root pointing at the React build  (AC2, AC3)
- **Files:** `src/PresenterAi.Infrastructure/Content/ContentOptions.cs`, `src/PresenterAi.Api/Program.cs`,
  `src/PresenterAi.Api/appsettings.json`, `appsettings.Development.json`, `appsettings.Example.json`.
- **Change:** default `../../web/app/dist`; in `Program.cs` compute `webRoot`, `var hasWebRoot =
  File.Exists(Path.Combine(webRoot, "index.html"))`; register the web `UseStaticFiles` only when true (keep it before
  `UseRouting`); in `MapFallback` return 404 (no body) when `!hasWebRoot` after the `/decks` and API-path branches; log
  once at startup: `web root {webRoot} not found — API only; run "bun run dev:app" for the UI` (info) when false.
- **Verify:** `dotnet build -warnaserror`; T3 tests; manual: AC3 both ways.
- **Test that dies:** `WebRootTests.Serves_index_when_web_root_exists`, `WebRootTests.Api_only_when_web_root_is_missing`.

### T3 — Tests with a temp web root  (AC2)
- **Files:** `tests/PresenterAi.Api.Tests/SpaFallbackTests.cs`, new `tests/PresenterAi.Api.Tests/WebRootTests.cs`,
  `tests/PresenterAi.Api.Tests/StartupTests.cs`, optionally `Infrastructure/ApiFactory.cs`.
- **Change:** a small fixture writes `index.html` (containing `Presenter`) to a temp directory; `SpaFallbackTests` and
  `WebRootTests` use `factory.WithWebHostBuilder(b => b.UseSetting("Content:WebRoot", tempDir))`; the absent case uses
  a fresh non-existent temp path and asserts `/` 404, `/health` 200, `/decks/sample/index.html` 200, `/present/x` 404.
  `StartupTests` passes the temp dir (or no web root — the test asserts the missing-key exit, not the page).
- **Verify:** `dotnet test tests/PresenterAi.Api.Tests`; mutation: remove the `hasWebRoot` guard → absent-case test
  fails with `DirectoryNotFoundException`/500; point the fallback at a wrong file → present-case test fails.
- **Test that dies:** the two `WebRootTests` above plus `SpaFallbackTests.Deep_link_serves_index`.

### T4 — Docker image builds and serves the app  (AC4)
- **Files:** `src/PresenterAi.Api/Dockerfile`, `docker-compose.yml` (comment on `api` only).
- **Change:** `FROM oven/bun:1.3.14 AS web` → `WORKDIR /web`, copy `web/package.json web/bun.lock`, `web/shared/package.json`,
  `web/app/package.json`, `web/admin/package.json`, `bun install --frozen-lockfile`, copy `web/`, `RUN cd app && bun run
  build`; runtime: `COPY --from=web /web/app/dist ./web`, `ENV Content__WebRoot=/app/web`. Keep the `curl` health check.
- **Verify:** `docker compose --profile full build api` then `up -d api`; `curl -s localhost:47913/health`,
  `curl -s localhost:47913/ | grep -c '<div id="root"'`, `curl -s localhost:47913/api/presentations`; `docker compose
  logs api` shows no "web root … not found"; `down` afterwards. Paste the observed output into the report/work log.
- **Test that dies:** none automated (CI docker job is out of scope) — the runbook step is the oracle.

### T5 — Documentation and stray references  (AC5)
- **Files:** `README.md`, `docs/guides/001-presenting-a-new-deck.md`, `AGENTS.md`, `.env.example`,
  `scripts/secrets-guard.selftest.sh:71`, `src/PresenterAi.Cli/RunCommand.cs:95`, new `docs/reference/002-node-mvp-retired.md`.
- **Change:** README: drop "Legacy Node MVP" section, reword `:40` (API serves `web/app/dist` when built; Vite in dev),
  add one line "The Node MVP was retired in plan 003; its last commit is `2a0b6a1` (`git show 2a0b6a1:src/server/presenter.js`)";
  guide: remove the legacy note; `AGENTS.md`: replace the legacy row with the pointer; `.env.example`: `PORT` comment
  → `ASPNETCORE_URLS` / `Kestrel__Endpoints__Http__Url` for the .NET host; selftest prose → `Content:WebRoot=../../web/app/dist`;
  `RunCommand.cs` comment → "Same semantics as the Node MVP's headless run (retired, see docs/reference/002)". Reference
  note: what was removed, why (parity proven in plan 002), where it lives in history, what replaces each piece
  (`smoke`/`run` CLI, React app, Chrome runs).
- **Verify:** AC1 grep; `bash scripts/secrets-guard.sh && bash scripts/secrets-guard.selftest.sh`.

### T6 — Bookkeeping  (AC6)
- **Files:** `docs/progress/002-work-log-phase0.md`, this plan's approval log.
- **Change:** work-log section with the runbook output and the PR URL; approval-log row "Implementation complete".
- **Verify:** CI green on the PR (`gh pr checks`).

## 7. Test strategy

- **Unit / integration:** `WebRootTests` (present → `/` and `/present/sample` serve `index.html` with `no-cache`;
  absent → `/` 404, `/present/x` 404, `/health` 200, `/decks/sample/index.html` 200), `SpaFallbackTests` moved onto
  the temp fixture, `StartupTests` unchanged in intent. Deliberately not covered: the Docker image (runbook).
- **Manual runbook** (implementer runs 1–6; the user can repeat any step):

| # | Step | Expected |
|---|---|---|
| 1 | Stop any API on 47913 (by PID); `cd web && bun run build` | `web/app/dist/index.html` exists |
| 2 | `dotnet run --project src/PresenterAi.Api`; `curl -s localhost:47913/ \| grep -c 'id="root"'` | `1`; `curl -sI localhost:47913/present/sample` → 200 with `Cache-Control: no-cache` |
| 3 | Stop; `rm -r web/app/dist`; start again | log line `web root … not found — API only`; `curl -si localhost:47913/` → 404; `/health` → 200; `/decks/sample/index.html` → 200 |
| 4 | `docker compose --profile full build api && docker compose --profile full up -d api` | build succeeds; `docker compose logs api` shows no "not found" line |
| 5 | `curl -s localhost:47913/health`; `curl -s localhost:47913/ \| grep -c 'id="root"'`; `curl -s localhost:47913/api/presentations` | 200; `1`; JSON listing `sample` and `ricoh-delivery-overview` |
| 6 | Open `http://localhost:47913/present/sample` in Chrome against the container (Dev sign-in), Start → End | deck loads from the same origin, transcript flows, `closed … usage=…` — proves same-origin `/ws` and `/decks` (Claude, Chrome) |
| 7 | `docker compose --profile full down` | containers stopped |

## 8. Open questions

None.

## 9. Approval log

| Date | Event | Notes |
|---|---|---|
| 2026-09-21 | Requirement brief confirmed (G1) | one survey round (scope / API root / record), brief confirmed as written |
| 2026-09-21 | Plan approved (G2) | approved with "implement" in the same answer; implementation by `pi` gpt-5.6-luna:high on `feature/003-retire-node-mvp` |
