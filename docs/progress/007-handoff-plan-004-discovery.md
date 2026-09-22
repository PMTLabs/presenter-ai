# 007 — Handoff: plans 002 + 003 merged; plan 004 (identity + persistence + Redis) in discovery

**Written:** 2026-09-21 23:00 CDT at a clean boundary (ctx 34 %): `develop` = `4150003`, tree clean, no plan-004 file
exists yet; two read-only scouts are running and write to disk. Supersedes `006-handoff-plan-002-after-review-3.md`
(its Rules/Gotchas and 004/005's still apply).

## Goal

Write and get approval for **plan 004 — identity + persistence + Redis** (research `docs/research/002-phase-0-platform-architecture.md`
§2.4–2.7 and migration steps 0.5 + 0.6), then implement it the same way as 002/003 (pi agents, reviews, Chrome runs,
PR to `develop`, merge on the user's OK). The user's standing instruction: "merge PR 2, continue to next plan".

## Environment

- `D:\sources\demo\presenter-ai`, branch **`develop`** (no worktree), tip `4150003` = merge of PR #2. Remote
  `https://github.com/PMTLabs/presenter-ai`. `master` untouched (production).
- Scratchpad `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\121ab6bf-f7d7-40c1-bca4-9b126c3c7052\scratchpad\plan-004\`
  — `scout-inkspoke-brief.md` / `scout-inkspoke-report.md`, `scout-presenter-brief.md` / `scout-presenter-report.md`.
- **Scouts (pi gpt-5.6-luna:medium, read-only):** inkspoke inventory in termflow **`tm-c7f37330f`**, presenter-ai
  as-built map in **`tm-1678217bf`**. Each report ends with `<!-- REPORT COMPLETE -->` when done. No Monitor/cron was
  armed for them (the wake-up reads them directly); if a report is not complete, check the terminal (Working → wait;
  Ready with brief unsubmitted → empty submit `cliType: codex`; crashed → restart pi and resend the brief file).
- inkspoke sources: `D:\sources\work\inkspoke\inkspoke-main` at commit `b83e691f` (`src/InkSpoke.Api/Auth/*`,
  `Services/SsoService.cs`, `Data/Migrations`, `tests/InkSpoke.Api.Tests/{Auth,Support}`) — copy as source with an origin
  header, never as a package (decided in plan 002's brief).
- Still running locally: Vite dev `bun run dev:app` on 47914 in termflow `tm-1b0c232b8` (harmless; stop when convenient).
  Nothing on 47913; compose stack is down. Chrome tab `1324748785` is the automation tab.
- Docker: daemon in WSL (`Ubuntu-24.04`, context `wsl`); `docker compose` v2 only inside WSL —
  `wsl -e bash -lc 'cd /mnt/d/sources/demo/presenter-ai && docker compose --profile full …'`; the Windows CLI has only
  standalone `docker-compose` and mounts empty bind paths.
- Secrets: API/CLI user-secrets hold `Upstream:Endpoint`, `Upstream:Key`, `Upstream:Fallback:Key` — never print.

## Done this session (all merged to `develop`)

| Item | Where |
|---|---|
| Plan 002 T17 fluid layout + resizable split; `connected via <label>` on every start; Linux-only test fix | PR #1 `2a0b6a1` |
| `AGENTS.md` (single agent guide) + `CLAUDE.md` pointing to it | `f70afb9` on develop |
| Plan 003: Node MVP retired, API serves `web/app/dist` (optional), Docker builds the app, temp-web-root tests, **production-build worklet bug fixed** (`?worker&url` + `check-dist.ts`), `ApiFactory` has no web root by default | PR #2 `4150003`; plan `docs/plan/003-retire-node-mvp.md` approval log complete |
| Work log `docs/progress/002-work-log-phase0.md` has sections for all of the above | — |

## In progress

1. **Plan 004 discovery (Phase 1 of the `planning` skill).** Two scout reports (see Environment). Read both when
   complete; do not redo their sweep.

## Next steps

2. **Interview the user (Phase 2, 2–3 rounds, `AskUserQuestion`, proposals first).** Already decided in plan 002's
   brief (do not re-ask): Google + Microsoft SSO via copied `SsoService`; metering + quotas now, Stripe later;
   pgvector extension + schema only; VM + compose; Clean Architecture; public SaaS. Likely open questions, to be
   sharpened by the scout reports: (a) scope split — one plan for 0.5 + 0.6 or two; (b) what moves to Postgres now
   (users/logins/tokens/api keys only, or also decks/presentations/sessions with `presenter-cli import`); (c) Dev
   sign-in stays as the local path + who is admin (`Admin:BootstrapEmails`), allowed domains/invitations policy;
   (d) WebSocket ticket endpoint shape vs the current `auth` frame with `DEV_TICKET` (frozen `/ws` frames — §10 says
   the trio/frames are frozen until the admin plan; a ticket is additive); (e) Redis scope now: tickets/codes with
   TTL + upstream session lease (+ token bucket?) — SSE job progress deferred until a job exists; (f) tests:
   Testcontainers in CI (ubuntu runner has Docker) vs a `ThrowawayPostgresDatabase` pattern like inkspoke's;
   (g) `IFileStore` local disk now, MinIO later.
3. Requirement Brief in chat → G1 (`Confirm / Edit / Rethink`).
4. Plan `docs/plan/004-<kebab>.md` (next free number is **004**; the informal "003 identity / 004 admin" labels in
   plan 002's brief shifted by one — say so in the header as plan 003 did). L size: full template, sequence diagram
   of the SSO flow and the ticketed `/ws` upgrade, wiring-audit task last, offer an external plan review (pi sol:medium)
   before G2. Then G2 (`Approve / Approve and implement / Revise / Reject`) — implement only on an explicit go.
5. Implementation as in 002/003: feature branch `feature/004-…` off `develop`, pi agents per task (luna:high /
   terra:medium|high), reviews with A/B/C/D classes + ledger rows, live checks, PR to `develop`, merge on the user's OK.

## Gotchas / settled decisions (new this session; older ones in 004–006)

- Vite compiles `new URL("./x.ts", import.meta.url)` only inside `new Worker()`; AudioWorklet URLs must use
  `?worker&url` — `web/app/scripts/check-dist.ts` guards it in `bun run build`.
- `ApiFactory` sets `Content:WebRoot` to a non-existent path; tests wanting the SPA opt into `WebRootFixture`.
- FluentAssertions `ContainSingle("text")` — the string is the *reason*, not the expected element.
- A test that passes only because a local build artefact exists (`web/app/dist`) will fail in CI: CI's `dotnet` job
  never builds `web/`.
- Frozen contract (`docs/reference/001-api-and-code-conventions.md` §10): `/api/presentations` trio and `/ws` frames
  stay as they are until the admin plan; new auth endpoints are additive under `/api/v1/auth/*` (check §2 for the
  versioning rule before naming them).
- The user wants: surveys before new requirements, plan gates, no AI attribution, `Task done:` line, never port 3000.
