# 003 — Handoff: implement plan 002 (phase 0a .NET core port, API bridge, React web app)

**Written:** 2026-09-21 16:33 CDT at ctx 37 %, clean boundary. The user's instruction was
"do context-handoff and then implement 002" — **implementation is authorised**; start it after the compaction.

## Goal

Implement the approved plan `docs/plan/002-phase0-dotnet-core-port-api-web.md` (Status: Approved 2026-09-21):
port the Node MVP to .NET 10 + React 19 with parity proven by the ported test suite, live smoke on both upstreams
and a real-Chrome Ricoh run. Sixteen tasks T1–T16; acceptance AC1–AC5 (+ CI green).

## Environment

- Working directory `D:\sources\demo\presenter-ai` — **not yet a git repository**; T1 initialises it and pushes
  to `https://github.com/PMTLabs/presenter-ai.git` (exists, private, empty; `gh` 2.83 is authenticated). Git-flow:
  `master` (initial commit only), `develop`, then `feature/002-dotnet-core-port`. No worktree.
- Toolchain present: .NET SDK 10.0.401, bun 1.3.14, Node 22.23.2, Docker 29.6.2.
- Port 47913 = presenter-ai API. **A Node server instance of mine is still listening on 47913 (pid 103372; older
  pid file `%TEMP%\presenter-ai-47913.pid` says 298179)** — stop pid 103372 before running the .NET API. **Never
  touch port 3000** (another project of the user, pid 55348).
- `.env` holds real secrets `UPSTREAM_ENDPOINT`, `UPSTREAM_KEY`, `FALLBACK_OPENAI_KEY` — never print values; the
  .NET side reads `Upstream:*` via `dotnet user-secrets` (copy values by hand, not into tracked files).
- Reference repo for conventions to copy (read-only): `D:\sources\work\inkspoke\inkspoke-main`.
- Memory dir: `C:\Users\tamtr\.claude\projects\D--sources-demo-presenter-ai\memory\` (see MEMORY.md — includes
  the two feedback rules below).

## Rules that govern this implementation (user instructions, verbatim intent)

1. **Delegate coding to external `pi` agents through the `agent-research` skill, strictly** (load the skill first):
   pre-create an empty report file per task in the scratchpad, one terminal per role
   (`mcp__termflow__create_terminal`, cwd = project), launch, verify the TUI shows the model and is ready, submit
   one message (`cliType: "codex"`, `useBracketedPaste: true`), confirm it appears as one message and status
   `Working`, then wait with a `Monitor` on the report file (require the completion tag **and** real content —
   agents write placeholder skeletons with the tag; the first monitor this session fired on one) plus a terminal
   health check at least every 15 min; never poll; save results under the project; close the terminal after.
   Tiers per plan §8: scout `pi --model openai-codex/gpt-5.6-luna:medium`; implement moderate
   `pi --model openai-codex/gpt-5.6-luna:high` (T3, T4, T5, T6, T9, T12, T13); implement complex
   `pi --model openai-codex/gpt-5.6-terra:medium` (`:high` for T7/T8/T10) (T2, T7, T8, T10, T14); review in a
   separate terminal `pi --model openai-codex/gpt-5.6-terra:medium` with the A/B/C/D classification (after T9,
   after T12, after T15), ledger row in `docs/agentic/review-rounds-ledger.md`. Claude does T1 (git), T11 and T15
   (Chrome runs via the browser MCP; page DPR 1.5 → click via `javascript_tool`), T16, and trivial build-blocking
   fixes directly. Implementer briefs end with "run `dotnet test` / `bun run test` and write the result to the
   report file"; reviewers do not run tests.
2. **Every implementer brief must cite** the plan task section, `docs/reference/001-api-and-code-conventions.md`
   (Problem Details + `code`, camelCase, `/v1`, frozen `/api` trio, optional `auth` WS frame in 002) and the
   Node source files being ported; agents must never open `.env`, never commit, never push.
3. Global rules: no AI attribution in commits; never commit `.env`; end every user-facing response with
   `Task done: …`; the user wants to be surveyed (planning skill) on **new** requirements — implementation of an
   approved plan needs no survey. If implementation shows the plan is wrong: stop, update the plan, re-confirm
   with the user.

## Done

- Docs: plan 002 approved (with external review folded in — `docs/review/001-plan-002-external-review.md`),
  conventions `docs/reference/001-api-and-code-conventions.md`, research 001/002, guides 001, work log 001.
- Node MVP: 52/52 tests, live-verified; it is the parity reference and stays until plan 004.

## In progress

Nothing. No code for plan 002 exists yet.

## Next steps (in plan order; each task's files, change, verify step and dying test are in the plan)

1. Stop the stray Node server (pid 103372) — `Stop-Process -Id 103372` — and delete `%TEMP%\presenter-ai-47913.pid`.
2. **T1 (Claude):** `git init`, `.gitignore`/`.gitattributes`/`.editorconfig`, `scripts/secrets-guard.sh`, first
   commit of the current tree (verify `.env` is not tracked), remote `origin`, push `master`, create/push
   `develop`, branch `feature/002-dotnet-core-port`. Commit message convention: `type: description`
   (e.g. `chore: initial commit of the Node MVP and docs`).
3. **T2 (pi terra:medium):** solution scaffold per plan T2 + conventions (ProblemDetails handler, `ErrorCodes`,
   OpenAPI, Moq, `ValidateOnBuild`). Then **T3 (pi luna:high)** compose + Dockerfile + CI.
4. **T4, T5, T6, T9 (pi luna:high, may run in parallel terminals — disjoint files)**, then review round 1
   (pi terra:medium) after T9; fix findings; ledger row.
5. **T7 then T8 (pi terra:high)** — LiveSession + fake server, Presenter; the pump and race oracles from the plan
   are mandatory.
6. **T10 (pi terra:high)** API + bridge + Dev auth + contract/golden tests. **T11 (Claude):** Node page on the .NET
   API in Chrome (AC4). **T12 (pi luna:high)** CLI; then review round 2; then live smoke on Azure + OpenAI (AC3).
7. **T13 (pi luna:high)**, **T14 (pi terra:medium)**, **T15 (Claude)** Chrome run of the React app (AC5, incl. the
   spoken-question check AC6 of plan 001); review round 3; **T16 (Claude)** wiring audit, README, PR to `develop`.
8. Keep `docs/progress/002-work-log-phase0.md` (new file, per plan T15) updated after each task; record
   `usage.seconds` of live runs; update the plan's approval log only if the plan changes.

## Gotchas / settled decisions (do not relitigate)

- Protocol is frozen to the Node frames in plan §4.3 (`start` carries `presentation`, required); HTTP trio
  shapes frozen; everything else per the conventions doc. Concurrency model plan §4.6 (one presenter loop, one
  upstream send loop, monotonic `TimeProvider`, no drop-oldest audio).
- Live-service facts: output paced by input audio (silence pump 20 ms / 120 ms slack / 960-byte frames), RMS
  threshold 120, paragraph pauses ≈2.3 s (advance 3000 ms, part gap 2500/0.8×), parts one at a time, close reason
  `client_request`, 500-token append cap, 16,384-token instructions cap.
- Browser: device-rate `AudioContext` (24 kHz forced hangs on this machine); Start must not block on the mic.
- Windows: large heredocs fail (`ENAMETOOLONG`) — use the Write tool or a script file for big content; Python
  heredocs with quotes can break — write `.py` to the scratchpad and run it.
- SSE plumbing and the Sessions screen are deferred (phase 1 / plan 003) — do not build them in 002.
- Scratchpad for agent report files:
  `C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\121ab6bf-f7d7-40c1-bca4-9b126c3c7052\scratchpad\`
  (subfolder `impl-002/`).
