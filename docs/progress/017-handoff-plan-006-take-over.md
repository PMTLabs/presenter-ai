# 017 — Handoff: implement plan 006 (take over the presenter); PR #5 waiting on the user

**Written:** 2026-09-22 23:45. Supersedes `016-handoff-pr5-review.md`.

## Goal

1. **Plan 006 (current work).** Implement `docs/plan/006-take-over-presenter.md`, which is approved and includes
   revision 1. It adds a **Take over** button when the presenter is busy in another tab.
   - Same user only.
   - A take-over ends the old tab's talk as *interrupted*, so Start in the new tab resumes at that slide.
   - The old tab stops silently, with no reconnect.
   - A backlog doc records "move the live talk between tabs".
   - The user said "implement plan 006".
2. **PR #5** (plan 005 + A1) is open and review round 1 is fixed and pushed (`21cab99`). It is waiting on the user's
   live check and their go to merge. PR #4 is folded into #5: close it after #5 merges, or merge it first. That is the
   user's choice.

## Environment

- **Plan 006 worktree:** `D:\sources\demo\presenter-ai\.claude\worktrees\feat-006`, branch
  `feature/006-take-over-presenter`, created from `feature/005-echo-and-stall` @ `21cab99`. Not pushed yet.
  `CLAUDE.local.md` was copied in (git ignores it).
- **PR #5 worktree:** `D:\sources\demo\presenter-ai\.claude\worktrees\feat-005`, branch
  `feature/005-echo-and-stall`, pushed, clean.
- **Main tree** `D:\sources\demo\presenter-ai` is on `develop`. Its untracked `.mcp.json` and `.playwright-mcp/` are
  not ours.
- **API:** runs on http://localhost:47913 **from the feat-005 worktree**, background task `bxj409x4w`.
  - The env comes from scratchpad `env.sh`. **Never echo it.** `Upstream:*` comes from user-secrets.
  - Stop it with TaskStop before building. To test plan 006 live, restart it from **feat-006** with the same command:
    `source "$S/env.sh" && export Jwt__SecretKey="$(openssl rand -base64 48)" && exec dotnet run --project src/PresenterAi.Api > "$S/api.log" 2>&1`,
    where `S` is the session scratchpad.
- termflow MCP works: use it for `pi` agents and for `/compact` (`terminalId: "me"`).

## Done this session

- **PR #5 review round 1:** pi sol/medium review, triaged against the code.
  - Fixed:
    - the echo-gate coupling warm-up;
    - the client answer-now is sent only while presenting;
    - a top-level `backend_error` ends the pending backend answer;
    - a recovered delegation rejection is no longer raised as an error;
    - the loopback fallback race;
    - the timestamp test oracle;
    - the README nudge row.
  - Kept by design: the hold across navigation, and a second question waiting behind a pending backend answer.
  - Every fix is mutation-proven. Summary `docs/review/008-…`, and a row in the ledger.
  - Verified:
    - build: 0 warnings;
    - .NET tests: 76, 40 (+3 skipped), 140;
    - web: lint, shared 20, app 60, build and check-dist.
- **Plan 006:** interview, brief (G1), plan (G2), then revision 1 (resume at the slide).

## Next steps, in order

1. Commit the plan (`docs/plan/006-take-over-presenter.md`) and this handoff on `feature/006-take-over-presenter`.
2. **Delegate the implementation to `pi`** through the `agent-research` skill: a fresh termflow terminal in the
   feat-006 worktree, and a brief that points at plan 006.
   - Model: luna:high or terra:high, per the memory "Delegate implementation to pi".
   - Rules for the brief:
     - never read `.env`, `appsettings.Local.json` or user-secrets;
     - don't touch ports 47913 or 3000;
     - no commits;
     - run the tests;
     - write a report file ending `REPORT COMPLETE`.
   - The tasks are plan §5, 1 → 1b → 2 → 3 → 4, with a mutation proof per new test (the plan names them).
   - Monitor the report with a background job; check the terminal every 15 minutes.
3. **Review the agent's diff yourself** against plan §3 and the acceptance criteria. Key risks:
   - the close item really goes through A's writer;
   - `Released` is completed on every path;
   - the 1 s abort when the holder's writer has failed;
   - the resumable flag is reset on each Start;
   - old clients stay compatible.
   Then re-run the mutations yourself.
4. **Full verification:**
   - `dotnet build PresenterAi.slnx -warnaserror`;
   - the 3 .NET test projects;
   - web: `bun run lint`, `bun run test`, and `cd app && bun run build`.
5. **Manual runbook** (plan §5 task 5): restart the API from feat-006 and use two tabs.
   - Playwright MCP can drive two tabs, but a real talk needs the upstream.
   - At minimum, check busy → Take over → the old tab's notice with a synthetic idle presenter.
   - Leave the live-talk step to the user.
6. **Commit with explicit files** after `bash scripts/secrets-guard.sh`. No AI attribution.
   **Ask the user before pushing or opening a PR.** The PR base is `develop` once #5 has merged; until then, stack it
   on `feature/005-echo-and-stall`.
7. **Report to the user.** Also remind them of the PR #5 live check and the merge decision.

## Gotchas

- The Bash tool breaks on heredocs that contain apostrophes. Write Python scripts with the Write tool, then run them.
  Keep `newline=''`, and preserve CRLF where the file already uses it.
- In mutations, guard with `DateTime.UtcNow.Year < 0`, because `if (false)` fails `-warnaserror`. A nullable-string
  argument can break a mutant's compile.
- A git `index.lock` can appear briefly (another git process); retry after a moment.
- No bare `git stash`. Stage explicit files. Never touch port 3000, and never kill all node processes.
- Start currently always sends `fromIndex: 0` (`Present.tsx:244`). Plan 006 revision 1 changes that; don't
  "fix" it back.
