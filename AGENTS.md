# AGENTS.md — working in presenter-ai

Instructions for any coding agent (Claude Code, Codex, pi, Copilot, …) working in this repository. `CLAUDE.md`
points here; keep this file the single source of truth and keep it short — link to `docs/` for detail.

## What this is

An AI presenter: a Markdown script (`presentations/*.md`) plus an HTML deck (`decks/<name>/`) are narrated live by
a GPT-Live realtime model (Azure AI Foundry primary, OpenAI fallback). The audience hears the narration, sees the
deck advance, and can interrupt with spoken questions. Phase 0 (plan 002) ported the Node MVP to .NET 10 + React 19.

## Repository map

| Path | What lives there |
|---|---|
| `src/PresenterAi.{Domain,Application,Infrastructure,Contracts,Api,Cli}` | Clean Architecture .NET 10 solution (`PresenterAi.slnx`). `Application/Presenting/Presenter.cs` is the single event loop; `Infrastructure/Live/LiveSession.cs` speaks to GPT-Live; `Api` is minimal APIs + the `/ws` bridge; `Cli` is `presenter-cli smoke|run|import`. |
| `tests/PresenterAi.*.Tests` | xUnit + FluentAssertions/Moq per project. Ported Node tests are the parity oracle — do not weaken them. |
| `web/` | bun workspaces: `shared` (generated OpenAPI client, auth store, Tailwind preset), `app` (presenter, Vite 47914), `admin` (Vite 47915). |
| `presentations/`, `decks/` | Content. `docs/guides/001-presenting-a-new-deck.md` explains how to add a deck. |
| `docs/` | `plan` (numbered plans, approval log inside each), `progress` (work logs, handoffs), `review`, `research`, `reference/001-api-and-code-conventions.md` (the API contract), `agentic/review-rounds-ledger.md`. |
| `scripts/secrets-guard.sh` | Fails if anything secret-shaped is tracked; `scripts/secrets-guard.selftest.sh` proves it still catches the known shapes. |
| Node MVP history | Retired; see `docs/reference/002-node-mvp-retired.md`. |

## Build, test, run

```bash
dotnet build PresenterAi.slnx -warnaserror          # warnings are errors in CI too
dotnet test PresenterAi.slnx
dotnet run --project src/PresenterAi.Api            # http://localhost:47913 (health: /health, OpenAPI: /openapi/v1.json)
dotnet run --project src/PresenterAi.Cli -- smoke   # a few seconds of upstream time; run from the repo root
dotnet run --project src/PresenterAi.Cli -- run <slug> --stop-after-slide 2   # reads presentations/<slug>.md, records nothing

# First run with a database (plan 004). Compose publishes Postgres on 5433 and Redis on 6382.
docker compose up -d postgres redis
dotnet user-secrets set ConnectionStrings:Postgres "<connection string>" --project src/PresenterAi.Api  # also ConnectionStrings:Redis
dotnet user-secrets set Jwt:SecretKey "$(openssl rand -base64 48)" --project src/PresenterAi.Api  # generates 48 random bytes without printing them
dotnet tool install --global dotnet-ef              # once
dotnet ef database update --project src/PresenterAi.Infrastructure  # uses ConnectionStrings__Postgres, else the compose default
dotnet run --project src/PresenterAi.Api            # sign in once: dev sign-in creates your users row
# The CLI reads ConnectionStrings:Postgres from its own secret store (--project src/PresenterAi.Cli) or ConnectionStrings__Postgres
dotnet run --project src/PresenterAi.Cli -- import presentations/*.md --owner <your email>  # expands the pattern itself; skips *-context.md
dotnet run --project src/PresenterAi.Cli -- run <slug> --owner <your email>                 # presents the imported copy and records a session

# Integration tests need Docker; Testcontainers starts its own pgvector and Redis.
# On Windows the daemon is in WSL, so DOCKER_HOST must be set or every integration test fails:
DOCKER_HOST=tcp://localhost:2375 dotnet test tests/PresenterAi.Integration.Tests

cd web && bun install --frozen-lockfile && bun run lint && bun run test && bun run build
cd web && bun run dev:app                            # Vite on 47914, proxies /v1,/ws,/decks,/health to 47913
cd web && bun run generate:api                       # regenerates the TS client from web/shared/openapi/v1.json
```

Secrets are never in files: `dotnet user-secrets set Upstream:Endpoint … --project src/PresenterAi.Api` (and
`Upstream:Key`, optional `Upstream:Fallback:Key`; the CLI has its own `presenter-cli` secret store). Docker:
`docker compose --profile full up` maps the upstream, presenter and JWT keys of `.env.example`; its host-URL
keys apply only to a local `dotnet run`. Integration tests start their own `pgvector/pgvector:pg17` and
`redis:7-alpine` containers. On Windows the Docker daemon lives in WSL and is **not** discovered automatically, so
`dotnet test PresenterAi.slnx` fails until you export
`DOCKER_HOST=tcp://localhost:2375` (PowerShell: `$env:DOCKER_HOST='tcp://localhost:2375'`). CI runs on Linux, where
the default socket is found without it.

## Rules that are not negotiable

- **Secrets.** Never open, print, echo, commit or paste `.env` values or any key — refer to variables by name
  (`UPSTREAM_ENDPOINT`, `UPSTREAM_KEY`, `FALLBACK_OPENAI_KEY`). `Tools:CredentialKey` is a secret; keep its value
  outside the repository. External-tool HTTP endpoints live under `/v1/tools` (see
  [docs/guides/003-external-tools.md](docs/guides/003-external-tools.md)). Tools configuration keys are
  `Tools:CredentialKey`, `Tools:OAuthRedirectUri`, `Tools:Mcp:StartBudgetMs`,
  `Tools:Mcp:CallTimeoutSeconds`, and `Tools:MaxInlineTools`. See the
  [talk limits and billing guide](docs/guides/004-talk-limits-and-billing.md). Run `bash scripts/secrets-guard.sh`
  before every commit. `.env.example` holds placeholders only.
- **Ports.** The API is 47913, Vite 47914/47915. Never touch port 3000 — it belongs to another project on the
  developer's machine. Stop a process by PID, never `kill all node/dotnet`.
- **Git.** `master` is production, `develop` is integration, work happens on `feature/*` branches merged by PR
  into `develop`. Never push to `master`. Stage explicit files (`git add <paths>`), never `git add -A`. No AI
  attribution lines in commits or PRs. Do not amend, rebase or force-push without being asked.
- **Tests.** Never change a test to make it green; fix the cause. A new oracle must come with mutation evidence
  (the wrong implementation it catches). Reviewers do not run tests; implementers do.
- **Parity semantics are deliberate.** `stop-after-slide N` ends when slide N+1 is announced (Node parity).
  `busy` is `error{code:"busy",canTakeOver}` + close 1013; auth may request `takeOver:true`, which closes the
  same user's holder with `taken_over` + 4409; backpressure closes with 1011. The `/ws` frames and `/api` trio
  are frozen until the admin plan except for additive changes per plan 010 (`trainer_mode`, `train_turn`,
  `script_edit`, `script_version`; text commands ≤ 16 KiB, auth ≤ 4 KiB) — see
  `docs/reference/001-api-and-code-conventions.md` §8 and §10. The presenter may emit `{"type":"flush"}` to discard queued playback audio; clients may ignore unknown frames.

## Conventions

- HTTP: RFC 9457 Problem Details with a stable `code` (`area.reason`), bare resources, `{items,page,pageSize,total}`
  lists, `[FromRoute]` on path parameters so OpenAPI stays complete. The UI copy for a code lives in
  `web/shared/src/api/errorMessages.ts` (`errorMessages[code] ?? detail ?? title`).
- OpenAPI drift chain: change an endpoint → `OpenApiTests.Checked_in_document_matches_live_document` fails →
  refresh `web/shared/openapi/v1.json` (`bun run generate:api -- --url http://localhost:47913/openapi/v1.json`) →
  regenerate the client → CI checks `git diff --exit-code -- web/shared/src/api`.
- Pipeline order in `Program.cs`: exception handler → static files (decks, web root) → `UseRouting` → WebSockets →
  auth → endpoints. Routing before static files shadows every deck file.
- React 19 + zustand 5: selectors must return stored references; a fresh object per render loops forever.
  Tailwind dark mode: every surface with `dark:bg-*` sets `dark:text-*`.
- Configuration is bound with `ValidateOnStart`; every option key must have a reader (the T16 wiring audit in
  `docs/research/004-plan-002-wiring-audit.md` shows the tables to keep true).
- Docs are numbered `NNN-kebab-name.md` per folder — take the next free number, never renumber. Small notes go in
  the current work log (`docs/progress/002-work-log-phase0.md`), not in new files.

## How work is organised

- New requirements start with a short interview (the user wants to be surveyed), a requirement brief, then a plan
  in `docs/plan/` with its own approval log; implementation starts only on an explicit go.
- Substantial changes get an independent review (findings classed A/B/C/D, one ledger row per round in
  `docs/agentic/review-rounds-ledger.md`). Fix the class of a finding, not the instance.
- Long sessions hand off through `docs/progress/NNN-handoff-*.md` (goal, done, in progress, next steps,
  environment, gotchas); read the newest one before resuming.
- Live verification matters: a change to the presenter is not done until a real run (CLI smoke or Chrome on the
  React app) has been observed and written into the work log with its `usage` seconds.
