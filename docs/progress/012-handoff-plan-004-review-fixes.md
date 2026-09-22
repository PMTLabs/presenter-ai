# 012 — Handoff: plan 004 review round 1, fixes in flight

**Written:** 2026-09-22 17:16. This supersedes `011-handoff-plan-004-impl-review.md`.

## Goal

Land the fixes from the external implementation review round 1 (`docs/review/006`) on PR #3
(`feature/004-identity-persistence` → `develop`). Then run a round-2 re-review of the fix diff, then report to the
user. Their go is required to merge.

## Environment

- **Main tree:** `D:\sources\demo\presenter-ai`, on branch `feature/004-identity-persistence` at **`08ff466`**.
  - **Pushed:** only up to `5b76132`.
  - **Local, not pushed:** `8199b44` (handoff 011), `b714d6c` (review 006 + ledger), `afb35ae` (plan: T12 claim
    corrected, D8/D9), `44fcb5e` (the user's decisions recorded), `08ff466` (work log).
- **Worktree `fix-persist`:** `D:\sources\demo\presenter-ai\.claude\worktrees\fix-persist`, branch
  `feature/004-review-fix-persist`. It is **done and verified by me**, with three commits on top of `b714d6c`:
  - `9b9a5b0` fix(bridge): P-01, I-05, I-06, P-03 comments;
  - `e196158` fix(cli): P-02, P-04, P-09, P-10;
  - `be4fbc8` fix(schema): P-06, P-07, P-08.

  Its `pi` agent is `tm-a7f001947` (terra:high, about 59% context), idle. Keep it for round-2 fixes, or close it.
- **Worktree `fix-identity`:** `D:\sources\demo\presenter-ai\.claude\worktrees\fix-identity`, branch
  `feature/004-review-fix-identity`. Its changes are **uncommitted**, and its agent `tm-d63397e33` (terra:high,
  about 65% context) is running **pass 2** of
  `.claude/agent-reports/plan-004/impl-review-fixes-identity-followup.md`.
- **Watchers:**
  - Monitor `biuv216sq` waits for `## Pass 2` plus a final `<!-- FIXES COMPLETE -->` in
    `impl-review-fixes-identity-report.md`. It lasts 30 min; re-arm it if it expires.
  - Health cron `c971da4c` runs every 14 minutes on both terminals. Delete it once identity pass 2 is complete.
- **Testcontainers:** `DOCKER_HOST=tcp://localhost:2375`. `tcp://localhost:9` is the no-container check.
- **Warning:** a `cd` inside Bash moves this session's primary working directory into a worktree. Use absolute
  paths or `git -C`.

## Decisions the user made (2026-09-22), all recommended options

- **D8:** recording stays best-effort. A database failure is logged and the presentation continues.
- **D9:** import refuses a script or context file that is itself a link, and compares paths case-sensitively on
  Linux. Directory junctions are out of scope.
- **I-10:** trusted forwarded headers are deferred to the deployment plan.
- **The presenter-test flake** (`PresenterTests.Last_slide_silence_sends_wrap_up_then_closes`) gets a separate
  small PR into `develop` **after** PR #3 merges.

## What I verified in fix-persist

- **Build and tests:** build 0/0. Full suite: Application 52 (the known flake failed once, then passed 3/3),
  Infrastructure 31 + 3 Linux-only skipped, Api 86, Cli 11, Integration 39 + 1 skipped.
- **My own mutations, each restored and md5-checked:**
  - unbounded close → backpressure silent-peer test fails;
  - unbounded close plus late permit release → pending-auth test fails;
  - no start-observation wait → 2 recorder tests fail.
- **The Linux-only tests, run in a Linux container:** 34/34 pass. Mutations: link checks disabled → 2 fail;
  case-insensitive comparison → 1 fails.
- **Container recipe:** attaching to stdout does not work over TCP, so use `docker create`, `docker cp`,
  `docker start`, `docker wait`, then `docker logs`. Set `MSYS_NO_PATHCONV=1`, and build the tar of
  `Directory.Build.props PresenterAi.slnx src tests` excluding `bin` and `obj`. The scratch files are in
  `C:\Users\tamtr\.claude\jobs\78e5d02d\tmp` (`src.tar`, `linux-mut.sh`).

## What pass 2 of fix-identity must fix (my findings on its first pass)

1. **I-07.** Its `AuthRateLimiterRegistry` hands the partitioned limiter a limiter that the framework **disposes**
   after about 10 s idle; the next request gets `ObjectDisposedException`, a 500. The registry also grows without
   bound. Replace it: success gets `RateLimit-Limit` only; the 429 gets Remaining 0 and a Reset equal to
   `Retry-After`, from the lease metadata; disabled gets no headers.
2. **Compose.** It changed `ASPNETCORE_URLS` and `Kestrel__Endpoints__Http__Url` to read `.env`, which can bind the
   container to loopback. Revert both, and fix the "maps every key" wording in `AGENTS.md` instead.
3. **The web interceptor's retry** rebuilds from a `Request` whose body is already consumed. Clone it, and add a
   test.
4. A brace-style nit in `client.ts`.

## Next steps, in order

1. **Wait** for identity pass 2; the Monitor and cron cover it. Then **verify it yourself**:
   - review the diff of the four items;
   - `dotnet build PresenterAi.slnx -warnaserror`;
   - `DOCKER_HOST=tcp://localhost:2375 dotnet test PresenterAi.slnx`;
   - Api and Cli with `DOCKER_HOST=tcp://localhost:9`;
   - `cd web && bun run lint && bun run test && bun run build`;
   - `docker-compose --profile full config -q`;
   - your own mutations (for example, headers emitted when disabled; no clone).

   Then commit in the fix-identity worktree as logical commits with explicit `git add` paths, running
   `bash scripts/secrets-guard.sh` first. No AI attribution.
2. **Integrate** in the main tree:
   - `git cherry-pick 9b9a5b0 e196158 be4fbc8`, then the fix-identity commits.
   - Expect conflicts in `docs/reference/001-api-and-code-conventions.md` (§8 from persist, §9 from identity) and
     possibly `tests/PresenterAi.Api.Tests/Infrastructure/ApiFactory.cs`, which both touched.
   - After resolving, re-run build, the full suite and web on the integrated tree.
   - Record the round-1 fixes in the work log and review 006, and add a Status/approval-log line to plan 004.
3. **Push** `feature/004-identity-persistence` to update PR #3; pushing follow-up commits to the open PR is within
   the user's approval. Watch CI with `gh pr checks 3`, polling at most every 30 s.
4. **Round 2:** re-review only the fix diff (`08ff466..HEAD`, code only) with fresh read-only `pi`
   `gpt-5.6-sol:medium` reviewers. Reuse `review-impl-common.md`, adding a note that D8, D9 and I-10 are settled.
   Save the result to `docs/review/007-plan-004-impl-review-round-2.md` and add a ledger row for round 2. Stop at
   round 2 unless new D blockers appear (doctrine 08).
5. **Clean up:**
   - close the agent terminals;
   - `git worktree remove` both worktrees (if that gives "Permission denied", use `rm -rf` followed by
     `git worktree prune`);
   - delete the two local temporary branches once their commits are integrated.
6. **Report to the user:**
   - what was fixed and the verification;
   - whether PR #3 is ready;
   - what is still theirs: the merge (**never merge without their go**), runbook §7 steps 8–16 plus the T13
     Serilog live cycle, and the flake PR after #3.

## Gotchas and settled points (do not relitigate)

- D1–D9 are in plan §10.
- The PR base is `develop`.
- Python on Windows writes CRLF. Strip it with `sed -i 's/\r$//'`.
- Identity's "found, not fixed" note (running the API DLL from the repo root resolves `Content:RootDir` wrongly) is
  out of scope; the documented command is `dotnet run --project`.
- Identity's I-08 `ValidateIssuerSigningKey=false` flag mutation survives, because the handler still verifies
  signatures when a key is set. The behavioural wrong-key test is accepted.
