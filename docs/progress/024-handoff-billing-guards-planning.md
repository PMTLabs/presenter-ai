# 024 — Handoff: plan the upstream billing guards (critical)

**Written:** 2026-09-23 17:15. Supersedes `023-handoff-plans-007-008-prs-open.md`, whose State, Decisions and Open
follow-ups still apply.

## Goal

The user asked to **plan** billing guards, calling them critical: the server must guarantee that an idle, paused,
stalled or abandoned talk disconnects the billed GPT-Live upstream completely. This is a new requirement, so run the
`planning` skill: discovery (already done, see below) → interview (AskUserQuestion) → Requirement Brief (G1) → plan
document → approval (G2). **Do not implement** until the user separately says to after G2.

## Discovery already done (do not redo)

`docs/research/008-upstream-session-lifetime-and-billing.md`, committed as `2d2f63e` on `feature/008-mcp-external-tools`
(worktree `D:\sources\demo\presenter-ai\.claude\worktrees\p008-t5`; not pushed). The scout's ranked gaps:

1. **High:** no server-side idle or maximum-duration cap. `PauseCore` (`Presenter.cs:1686-1701`) keeps the upstream
   open; stalled slides, question holds and silent background tabs also stay billed. Only the CLI has `--max-seconds`
   (300 s). The orchestrator verified this.
2. **High:** no server heartbeat on `/ws`. A dead or blackholed browser is not detected (`PresenterBridge.cs:289-324`),
   and a writer failure does not abort the receive loop (`:540-565`).
3. **Medium:** orphan windows: an unexpected event-loop exception, shutdown during connect, a throw after `_session` is
   assigned in Start, and the bridge freeing the slot after 90 s start observation while a start may still open an
   upstream.
4. **Medium:** CLI Ctrl+C sends no graceful close (`Cli/Program.cs:13-18`, `RunCommand.cs:186-256`).
5. **Medium:** tool and MCP calls in flight are not cancelled at End (they run until their own 5–10 s timeout).
6. **Low:** billed duration is unconfirmed on close timeout; the recorder falls back to last usage or zero.

What I told the user I'd recommend (proposals to bring to the interview, not decisions):
- a hard maximum talk length (configurable) that ends the talk from any state;
- a pause/idle deadline (e.g. 2–5 min) that closes the upstream; resume reconnects;
- a server heartbeat on `/ws` that ends the talk when the browser stops answering;
- smaller follow-ups: cancel tool calls at End; a clean close on CLI Ctrl+C.

Interview topics worth asking (intent, not facts): default values for the maximum length and idle deadline;
whether pause should close the upstream immediately or after a grace period (resume then means a reconnect and a
fresh session, with its latency and the context re-sent); whether the user gets a spoken or on-screen warning
before a cutoff; which gaps are in scope for this plan (all six, or 1–3 plus a follow-up plan); whether the guards
also apply to the CLI; the branch base (see Environment); a per-user daily usage budget (probably out of scope).

## Environment

- Main tree `D:\sources\demo\presenter-ai` on `feature/007-voice-control-and-tools` @ `0f8dc63` (pushed), plus this
  handoff commit (unpushed). Unstaged `src/PresenterAi.Api/appsettings.json` change and untracked `.mcp.json`,
  `.playwright-mcp/` are not ours; leave them.
- Worktree `.claude\worktrees\p008-t5` on `feature/008-mcp-external-tools` @ `2d2f63e`: PR #8 plus two local commits
  (`1a4075c`, a merge of the 007 handoff docs, and `2d2f63e`, research 008), not pushed. It contains all the code from
  plans 007 and 008, so it is the natural base for the billing-guards branch (for example `feature/009-billing-guards`
  in a new worktree under `.claude\worktrees\`). Confirm the base with the user.
- **Servers are stopped** (the user asked, to avoid charges). API 47913 and Vite 47914 are free, and no PresenterAi
  process is running. To run again: source the scratchpad `env.sh` (connection strings; never echo it), then
  `dotnet run --project src/PresenterAi.Api` and `cd web && bun run dev:app`. User-secrets now also hold a generated
  `Tools:CredentialKey` and `Jwt:SecretKey` (values never printed). The `AddToolServers` migration is applied to the
  local database.
- Next plan number: check `docs/plan/` (`ls | sort -V | tail`); expected 009.
- Every user-facing response ends with a `Task done: …` line.
