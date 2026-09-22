# 013 — Handoff: plan 004 review round 2 fixes committed, not pushed

**Written:** 2026-09-22 18:26. This supersedes `012-handoff-plan-004-review-fixes.md`.

## Goal

Land PR #3 (`feature/004-identity-persistence` → `develop`, plan 004) ready to merge, then report to the user.
The user's go is required to merge. **Never merge without it.**

## Environment

- **Main tree:** `D:\sources\demo\presenter-ai`, on branch `feature/004-identity-persistence` at **`b3f4811`**.
  Not a worktree.
  - **Pushed:** up to `06537c3`. CI was green on it for `web` and `dotnet`, on both the pull-request and the push
    runs.
  - **Local, not pushed:**
    - `d526203`: docs, review 007 and the ledger rows;
    - `b7a0753`: fix(web);
    - `f6886b2`: fix(api), rate-limit metadata;
    - `1e2f537`: test(api);
    - `b3f4811`: fix(cli) and the bridge comment.
- **Worktrees, all clean with their commits integrated, to remove:**
  - `.claude/worktrees/fix-identity` (branch `feature/004-review-fix-identity`);
  - `.claude/worktrees/fix-persist` (`feature/004-review-fix-persist`);
  - `.claude/worktrees/fix-r2` (`feature/004-review-r2-fix`).
- **Agents:** all terminals are closed. No agent is running, and there are no crons or monitors.
- **Testcontainers:** `DOCKER_HOST=tcp://localhost:2375`. `tcp://localhost:9` is the no-container check.
- **Warning:** a `cd` inside Bash moves the session's primary directory. Use absolute paths or `git -C`.

## Done this session

1. **Round-1 fixes** (`docs/review/006`, section "Fixes"):
   - verified, integrated and pushed (`8f1bb07`..`06537c3`), plus my `ccd2a23` (the JWT `nbf` flake);
   - CI green.
2. **Round-2 re-review** (`docs/review/007`, ledger rows added):
   - two fresh `pi` sol:medium reviewers on the fix diff `032f2eb..06537c3` gave 16 findings: A 1, B 10, D 5;
   - my re-trace found **no blocker**. Three real defects were lowered to improvements and fixed:
     - a sign-out during an in-flight refresh did not stick;
     - the 401 interceptor replayed the SSO code;
     - rate-limit headers were chosen by path suffix;
   - the rest are fixed, deferred or disputed; see 007's Triage table.
3. **Round-2 fixes** (`b7a0753`..`b3f4811`), by a `pi` terra:high implementer and then corrected by me:
   - **Rejected** its production change for R2-I-06: a path-matched middleware and a 30 MiB token-request limit.
     413 and 415 are now pinned at `DomainExceptionHandler` by a theory.
   - **Found:** a non-JSON POST to `/v1/auth/sso/token` returns **404, not 415**. The global `MapFallback` stays a
     candidate when the content-type policy rejects the endpoint. This predates PR #3; the SPA always sends JSON.
     **Not fixed; add it to the follow-ups.**
   - `refreshAuth` now checks the generation **after** `response.json()`.
   - **Fixed an 87 s test:** the CORS ticket row, authenticated, waited on the unreachable test DB; it is now
     anonymous. Also an 8 s rate-limit row: the token row now sends malformed JSON. The Api suite is back to 4 s.
   - **Verified by me:**
     - build 0/0;
     - Application 52, Infrastructure 31 + 3 skipped, Api 137, Cli 11, Integration 40 + 1 skipped;
     - Api and Cli without a container;
     - web shared 18, app 22, lint and both builds;
     - the secrets guard and `git diff --check`.
   - **My mutations, each restored and md5-checked:**
     - both 413 and 415 handler arms deleted;
     - the `signOut` wait removed;
     - the old refresh-only retry rule put back;
     - the token limit set to 3.

     The agent's report has the rest: `.claude/agent-reports/plan-004/impl-review-r2-fixes-report.md`.

## Next steps, in order

1. **Docs**, then commit with explicit paths and run `bash scripts/secrets-guard.sh` first:
   - add a "Fixes" section to `docs/review/007` listing the commits, my corrections above, the verification and
     the follow-up list;
   - add a short entry to `docs/progress/002-work-log-phase0.md`;
   - add a round-2 row to the plan 004 approval log (§10) and update its Status line.
2. **Push** `feature/004-identity-persistence`; follow-up commits to the open PR are within the user's approval.
   Watch CI with `gh pr checks 3`, polling at most every 30 s (one Monitor).
3. **Clean up:**
   - `git worktree remove` all three worktrees; if that gives "Permission denied", `rm -rf` then
     `git worktree prune`;
   - `git branch -d` the three fully merged temporary branches.
4. **No round 3.** Doctrine 08 applies: no D blocker remains after re-tracing.
5. **Report to the user:**
   - PR #3 is ready for their review and merge decision;
   - what is theirs: the merge, runbook §7 steps 8–16 plus the T13 Serilog live cycle, and the presenter-test
     flake PR after #3;
   - the deferred follow-ups, not blocking:
     - R2-I-04: a 500 and streaming header oracle;
     - R2-P-04: a full catalog oracle;
     - R2-P-05: a causal failed-start barrier;
     - R2-P-06: a real connectivity CLI test;
     - R2-P-02: optionally, a single writer that owns sends and closes;
     - the 415→404 routing fallback;
     - I-10: forwarded headers, deferred to the deployment plan.

## Gotchas and settled points (do not relitigate)

- D1–D9 are in plan §10. The user confirmed D8 (best-effort recording), D9 (links and case only), the I-10
  deferral, and the flake PR after #3.
- R2-P-02 is disputed: `ManagedWebSocket` serialises sends through `_sendMutex`, checked in the .NET 10.0.12
  runtime. R2-I-08 is disputed: pinning the exact skew would need a validation clock.
- The bridge test `Disconnect_holds_the_slot…` flaked once in about 21 runs on the *old* bridge code, and 0 in 60
  on the integrated code. Watch CI.
- `dotnet ef migrations has-pending-model-changes` reports no drift.
- Python on Windows writes CRLF; strip it with `sed -i 's/\r$//'`.
