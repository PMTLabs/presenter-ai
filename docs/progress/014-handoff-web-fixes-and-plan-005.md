# 014 — Handoff: web fixes committed, plan 005 server half committed, web half next

**Written:** 2026-09-22 ~20:25. This supersedes `013-handoff-plan-004-review-round-2.md`. PR #3 has merged into
`develop` (`25a3f1b`).

## Goal

1. Land the four web fixes that a Playwright run found (branch `fix/web-auth-gating-and-build`).
2. Implement plan 005 (`docs/plan/005-echo-protection-and-stall-recovery.md`, approved 2026-09-22, "Approve and
   implement"): an echo-canceller reference, an echo gate with barge-in, stall recovery and diagnostics.

## Environment

- **Main tree:** `D:\sources\demo\presenter-ai` on `develop` at `25a3f1b`, clean apart from the user's untracked
  `.mcp.json` and the Playwright tool's `.playwright-mcp/`. Neither is ours to commit or delete.
- **Worktree `D:\sources\demo\presenter-ai\.claude\worktrees\fix-web`**, branch `fix/web-auth-gating-and-build`:
  - commit **`e20c5fc`**, on top of `develop`, verified;
  - **not pushed**, no PR.
- **Worktree `D:\sources\demo\presenter-ai\.claude\worktrees\feat-005`**, branch `feature/005-echo-and-stall`:
  - `721566b` plan 005;
  - `e9e8178` server half (T4, T6);
  - this handoff;
  - **not pushed**.
- **Running for the Playwright checks:**
  - the API on 47913: background Bash task `bwwo7ogq6`, `dotnet run` from the main tree;
  - Postgres (5433) and Redis (6382) through `docker-compose` in the main tree, with `DOCKER_HOST=tcp://localhost:2375`;
  - the env is in scratchpad `.../scratchpad/env.sh`, which reads the compose dev placeholder; a random JWT key is
    passed to the process;
  - **never echo either**; the user-secrets are untouched.
- **What the API serves:** the main tree's `web/app/dist`, rebuilt with `NODE_ENV=production` at 19:33. It is the
  `develop` code, not the fixes.
- **Imported for `dev@presenter-ai.local`:** `sample` and `ricoh-delivery-overview`.
- **Playwright:** the production bundle has no dev sign-in button, by design (`import.meta.env.DEV`). Sign in by
  calling `fetch('/v1/auth/dev/sign-in', {method:'POST', credentials:'include'})` from the page, then reload.
- **Agents:** no terminals and no monitors are open.
- **The user's live run:** started 19:30 and was still live at about 19:40. Tell them if it is still running.

## Done this session

- **PR #3:** merged into `develop` (`25a3f1b`), and the three review branches deleted, both on the user's go.
- **Playwright findings on `develop`:**
  1. sign-out left `/ws` authenticated and Start enabled;
  2. pages made pre-refresh 401s;
  3. a development React bundle was shipped, because this machine has `NODE_ENV=development`;
  4. a busy slot caused a reconnect storm: the backoff reset on `open`, and the server sends `busy` then closes
     1013.
- **Web fixes `e20c5fc`** (by `pi` terra:high, in 2 passes):
  - **First-pass defect I found:** the effects keyed on the `user` object, so every refresh would have torn down a
    live run. They now key on `userId`.
  - **Build scripts:** reverted to plain `vite build`; `vite.config` forces production.
  - **Verified by me:** lint; shared 20 and app 29 tests; build with and without `NODE_ENV=development`; mutations
    (busy backoff off, sign-out teardown off) caught and md5-restored.
- **Plan 005:**
  - **G1 decisions:** layered echo fix; flush after 300 ms of barge-in; pause and warn after the second failed nudge.
  - **G2:** approved.
- **Server half `e9e8178`** (T4, T6):
  - **My fix on top:** a resume after a stall restarts the diagnostics count (new test).
  - **Build:** `-warnaserror` 0/0; Application 57/57.
  - **Mutations, all caught:** resume diagnostics, the stall `PauseCore`, the second-nudge re-arm.
- **Stall evidence (19:30 run):** nudge at 19:30:42; unvoiced output streamed at about 10 deltas/s for more than
  10 min; one nudge only, so it waited forever. This drove T4.
- **Echo research:** Chromium's echo canceller only reliably references WebRTC-received audio; our Web Audio
  playback may be excluded. Sources are in the chat and in plan 005 §1.

## Next steps, in order

1. **Playwright-verify `e20c5fc`:**
   - build `fix-web/web/app` and copy its `dist` over the main tree's `web/app/dist` (gitignored build output);
   - check: reload → one refresh and no 401s; sign-out on `/present` → the bridge closes, the sign-in prompt shows
     and Start is disabled; a signed-out deep link → no ticket request; a second tab while the slot is held → the
     busy notice and ≥ 5 s backoff.
2. **Ask the user** before pushing `fix/web-auth-gating-and-build` and opening a PR into `develop`.
3. **Plan 005 web half:**
   - in `feat-005`, `git merge fix/web-auth-gating-and-build` (both edit `Present.tsx`);
   - dispatch one `pi` terra:high implementer for T1, T2, T3 and T5 (plan §4.1, §4.2, §6), with the `server-brief.md`
     style of rules (worktree only, no commit, report + `REPORT COMPLETE`);
   - brief path: `.claude/agent-reports/plan-005/web-brief.md`;
   - key points: the `MessageChannel` between the worklets, the pure `echoGate.ts` bundled into the worklet chunk
     (check-dist), `?echoGate=off`, the fallback to `context.destination`, and the barge-in flush once per open
     period.
4. **Verify the web half:** tests; my own mutations; the full .NET suite with `DOCKER_HOST=tcp://localhost:2375`;
   Playwright smoke of the Present page (the loopback falls back cleanly headless).
5. **Report to the user:**
   - the live check on loudspeakers is theirs (plan §7);
   - ask to push and open the PRs;
   - pre-existing follow-ups: the `LiveSessionTests` close race and the presenter-test flake for a flake PR, the
     415→404 routing fallback, and the review 007 deferred items.

## Gotchas

- `PresenterTests.No_output_audio…` was rewritten deliberately; the user approved the new escalation.
- Don't use a bare `git stash`: the stash stack is shared across worktrees.
- A `cd` inside Bash moves the session's directory; use absolute paths.
- Never touch port 3000, and never kill all node processes.
