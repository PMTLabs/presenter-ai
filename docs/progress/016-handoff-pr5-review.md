# 016 — Handoff: PR #5 (plan 005 + A1) open; external review running

**Written:** 2026-09-22 21:56. This supersedes `015-handoff-plan-005-a1-audience-questions.md`.

## Goal

Land PR #5 (https://github.com/PMTLabs/presenter-ai/pull/5, `feature/005-echo-and-stall` → `develop`), which covers
plan 005: echo protection, stall recovery, and amendment A1 (audience questions). First triage the external review.
Merge only on the user's explicit go.

## Environment

- **Worktree:** `D:\sources\demo\presenter-ai\.claude\worktrees\feat-005`, branch `feature/005-echo-and-stall`,
  **pushed**; HEAD `06668dd`; clean tree. Work here, not in the main tree.
- **Main tree:** `D:\sources\demo\presenter-ai` is on `develop`. Its untracked `.mcp.json` and `.playwright-mcp/` are
  not ours.
- **PR #4** (`fix/web-auth-gating-and-build`) is still open. Its two commits are inside PR #5. Merge #4 first, or
  close it after #5 lands. That is the user's choice.
- **Running:**
  - API on http://localhost:47913 from the feat-005 worktree: background Bash task `bytwjk5fe`.
    - The env comes from scratchpad `env.sh` (connection strings only). **Never echo it.**
    - `Upstream:*` settings come from user-secrets (`presenter-ai-api`).
    - Log: scratchpad `api.log`.
    - Stop it with TaskStop before `dotnet build`, because it locks DLLs. Restart it the same way afterwards.
  - **Reviewer:** `pi` `openai-codex/gpt-5.6-sol:medium` in termflow terminal **`tm-cbaccf589`** (request
    `p005-rv-01`).
    - Brief: `D:\sources\demo\presenter-ai\.claude\agent-reports\plan-005\review-pr5-brief.md`.
    - Report: `...\plan-005\review-pr5-report.md`, ending with `REPORT COMPLETE`.
    - Monitor task `bvmtxhxqp` watches for it (30 min max; re-arm if it expires).
- termflow MCP works again: the reviewer runs there, and `/compact` can be sent via `execute_command` with
  `terminalId: "me"`.

## Done this session (all on the branch, verified)

- **A1 review fixes** (`b2bec4d`), each mutation-proven:
  - backend filler ("One moment.") is not the answer until that delegation's `response.event` terminal arrives
    (`DelegatedResponseFinished`);
  - the receive loop stops after a delegation rejection, so a server close cannot beat the client-mode retry;
  - a delegation opens the hold only while presenting.
- **Compose / `.env.example`** (`c5dd334`): `UPSTREAM_DELEGATION_MODEL`, `FALLBACK_DELEGATION_MODEL`, using the
  `${VAR-default}` form so an empty value means deck-only.
- **Live run 21:22:** a deck question was answered from the deck, and a backend question went to `gpt-5.6-luna` and
  completed in about 3 s. The resume was correct but mid-sentence.
- **Follow-up window** (`f965ad0`): after answering, the model stays silent. After `FollowUpWaitMs` of quiet, the
  presenter appends `slide-N-resume-K` (bridge, then restart the interrupted sentence). The 15 s timer stops once the
  answer is heard.
- **Configurable** (`d7d3d88`): `Presenter:FollowUpWaitMs`, default 5000, validated 2500–60000 at startup; env
  `FOLLOW_UP_WAIT_MS`. Guide `docs/guides/002-audience-questions.md`. Plan §4.5 notes (`06668dd`).
- **Verification:**
  - build `-warnaserror`: 0 warnings;
  - tests: Application 74, Infrastructure 40 (+3 skipped), Api 139;
  - web: shared 20, app 56 (web unchanged since then);
  - the compose config was checked.
- **Pushed; PR #5 opened** on the user's instruction.

## Next steps, in order

1. **Wait for the review** (monitor `bvmtxhxqp`, or `REPORT COMPLETE` in the report).
   - If the monitor expired, re-arm it.
   - If the report is still empty, check `tm-cbaccf589` with `get_terminal_screen`:
     - `Working`: wait;
     - `Ready` with the prompt unsent: send an empty submit;
     - crashed: close it and restart with the same brief.
2. **Triage every finding against the code yourself.** Don't forward claims uncritically.
   - Fix confirmed ones: the class, not the instance, with a mutation proof per new test.
   - For a finding you reject, give the reason.
   - Reuse `tm-cbaccf589` for a round-2 continuation if needed.
3. **Bookkeeping:**
   - Save a summary under `docs/review/` (next number: check the folder).
   - Append a row to `docs/agentic/review-rounds-ledger.md`: `| 2026-09-22 | PR #5 plan 005 | 1 | A | B | C | D | note |`.
   - Close the terminal after saving.
4. **Verify, commit, push** the fixes to the same branch. The user authorized pushing this branch for the PR.
5. **Report** blockers and improvements to the user. **Ask before merging** PR #5 or PR #4.
6. **Live-check the follow-up window and the sentence restart** (not yet re-checked live). That's the user's runbook.
   Point them at the guide.
7. Follow-ups to mention:
   - the page ignores server `error` frames;
   - `LiveSessionTests` close race and a presenter-test flake;
   - 415→404 routing;
   - review 007 deferred items.

## Gotchas

- The Bash tool breaks on heredocs that contain apostrophes. Write Python scripts with the Write tool, then run them.
- A runtime-false guard (`DateTime.UtcNow.Year < 0`) is needed in mutations. `if (false)` fails `-warnaserror`.
- Don't use a bare `git stash`. Stage explicit files and run `bash scripts/secrets-guard.sh`. No AI attribution.
- Never touch port 3000, and never kill all node processes.
- On Windows, Python `open(..., 'w')` without `newline=''` writes CRLF. Git normalises it, but prefer `newline=''`.
