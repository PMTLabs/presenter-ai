# 015 — Handoff: plan 005 web half done, amendment A1 (audience questions) being implemented

**Written:** 2026-09-22 20:50. This supersedes `014-handoff-web-fixes-and-plan-005.md`.

## Goal

Finish plan 005 (`docs/plan/005-echo-protection-and-stall-recovery.md`) including **amendment A1** (§4.5, tasks
T8–T10, approved 2026-09-22): answer audience questions (deck first, GPT-Live-managed Responses delegation to
`gpt-5.6-luna` for the rest), hold the slide until the answer is spoken (15 s fallback), and a deck-only safety net.

## Environment

- **Main tree:** `D:\sources\demo\presenter-ai` on `develop` (`25a3f1b`). Its untracked `.mcp.json` and
  `.playwright-mcp/` are not ours. Its gitignored `web/app/dist` holds the **feat-005** web build (copied 20:23).
- **Worktree `D:\sources\demo\presenter-ai\.claude\worktrees\fix-web`**, branch `fix/web-auth-gating-and-build`
  (`e20c5fc`, `d3d8e93`): **pushed; PR #4 into `develop` open** (https://github.com/PMTLabs/presenter-ai/pull/4).
  Not merged — merge only on the user's go.
- **Worktree `D:\sources\demo\presenter-ai\.claude\worktrees\feat-005`**, branch `feature/005-echo-and-stall`, **not
  pushed**. Commits since 014: merge of the fix branch (`da97d1e`), web half T1/T2/T3/T5 (`5253749`), auto-scroll
  (`6d51512`), A1 draft + research 005 (`66b1927`), A1 approved (`c155fdb`), this handoff.
- **Running:**
  - API on 47913: background Bash task `bwwo7ogq6` (`dotnet run` from the main tree = `develop` server code), with
    compose Postgres 5433 / Redis 6382 (`DOCKER_HOST=tcp://localhost:2375`). Log: scratchpad `api.log`.
  - **A1 implementer:** headless `pi` (openai-codex/gpt-5.6-terra:high, session id `p005-a1-01`), background Bash task
    `bnaav0ntx`, stdout in scratchpad `pi-a1.log`. Brief `.claude/agent-reports/plan-005/a1-brief.md`; report
    `.claude/agent-reports/plan-005/a1-report.md` (ends with `REPORT COMPLETE`). It was editing `src/` at 20:49
    (uncommitted changes in feat-005 are **its work in progress** — do not revert or commit them before the report).
- **termflow MCP is down** (ECONNREFUSED): agents run headless via `pi -p` / `agy -p` from Bash (`--session-id`
  lets `pi` continue a session). An idle `pi` TUI is left in termflow pane `tm-019eb095e` (user may close it).
- **Playwright:** the prod bundle has no dev sign-in button; sign in with
  `fetch('/v1/auth/dev/sign-in',{method:'POST',credentials:'include'})` then reload. Browser currently signed in.

## Done this session

- Playwright-verified the web fixes; found and fixed the busy notice being wiped by the client's synthetic idle state
  (`d3d8e93`, accepted-only clear, Start disabled while busy). Pushed; PR #4 opened on the user's go.
- Plan 005 web half by `pi` terra:high; my review fixes (all mutation-proven): barge-in counts speech frames only (not
  hangover); gate compares against the loudest far level of the last 250 ms (echo delay); paused banner only shows a
  server warn logged after the latest `state →` line; SDP fmtp inserted after its rtpmap. Plan §4.2 records the first
  two as "as built". Lint, shared 20 + app 56 tests, both builds; Playwright: `reference: "loopback"`, Start/End clean.
- Auto-scroll hook `web/app/src/components/useStickToBottom.ts` for Transcript and LogPanel (user request).
- Q&A root cause: client delegation ignored by `Presenter.OnDelegation`, then the silence timer advanced 5 s later.
  Research by `agy` + `pi` sol saved as `docs/research/005-gpt-live-delegation-audience-questions.md`; key facts
  re-verified on Microsoft's delegation page. User decisions: tiered, managed Responses delegation, `gpt-5.6-luna`,
  low effort / priority / low verbosity, hold until answered (15 s).
- Observation: a 21 s Playwright run got no model speech (nudge at +15 s) — same upstream stall as the 19:30 run;
  T4 on this branch handles it (the running API is `develop`, so only one nudge there).

## Next steps, in order

1. **Wait for the A1 report** (task `bnaav0ntx` notification or `REPORT COMPLETE` in `a1-report.md`). If the process
   died without a report, read `pi-a1.log`, then continue it with
   `pi --model "openai-codex/gpt-5.6-terra:high" --session-id p005-a1-01 -p "<continue …>"` from the feat-005 dir.
2. **Review the A1 diff myself** against plan §4.5 and the brief: the hold gates part gap, ordinary silence, voiced
   wrap-up close and the wrap-up fallback; blank deltas ignored; audio before the latest user delta is not the answer;
   `delegation.created` resets `answerVoiced`; 15 s release never advances synchronously; pause/navigation/end clear
   it; stale timers dropped by generation; the safety net retries **once**, same upstream, only for delegation-related
   startup errors; only documented `delegation.responses` fields are sent (check the report's sources).
3. **Verify:** `dotnet build PresenterAi.slnx -warnaserror`; Application, Infrastructure and Api tests
   (`DOCKER_HOST=tcp://localhost:2375`); my own mutations on the hold guards and the safety net; web tests still green.
4. **Commit** A1 with explicit files after `bash scripts/secrets-guard.sh`; no AI attribution.
5. **Optional live check prep:** restart the API from the feat-005 worktree so the server half + A1 are live (stop
   `bwwo7ogq6` with TaskStop first; env via scratchpad `env.sh`, never echo it). Tell the user the live runbook
   (plan §7, steps 1–6) is theirs, and that on Azure `gpt-5.6-luna` must exist as a deployment or the session runs
   deck-only (logged); `Upstream:DelegationModel` in user-secrets overrides it.
6. **Ask the user** before pushing `feature/005-echo-and-stall` / opening its PR, and before merging PR #4.
7. Follow-ups to report: the page ignores server `error` frames (upstream errors never reach the log panel);
   `LiveSessionTests` close race + presenter-test flake (flake PR); 415→404 routing fallback; review 007 deferred items.

## Gotchas

- Don't use a bare `git stash` (shared across worktrees). Stage explicit files.
- This Bash tool breaks on heredocs containing apostrophes; write scripts with the Write tool and run them.
- Never touch port 3000; never kill all node processes.
- `PresenterTests.No_output_audio…` was rewritten deliberately (T4, user-approved).
