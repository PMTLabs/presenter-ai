# 011 — Handoff: plan 004 external implementation review in flight

**Written:** 2026-09-22 16:28. Everything is committed and pushed, and the tree is clean. Two review agents are
running. This supersedes `010-handoff-plan-004-close-out.md`.

## Goal

Get PR #3 (plan 004: identity, persistence and auth-state Redis) through an independent implementation review,
then have the user merge it into `develop`.

## Environment

- **Working directory:** `D:\sources\demo\presenter-ai`. There is **no worktree**; the temporary
  `.claude/worktrees/flake-develop` was removed.
- **Branch:** `feature/004-identity-persistence` at **`5b76132`**, pushed.
- **PR:** https://github.com/PMTLabs/presenter-ai/pull/3 → `develop` (the user chose one PR). CI is green on
  `5b76132`, on both the `push` and `pull_request` runs.
- **Testcontainers:** set `DOCKER_HOST=tcp://localhost:2375`. `tcp://localhost:9` is the no-container check.

## Done since 010

| Commit | What |
|---|---|
| `a139d20` | T13: new integration test for `GET /v1/auth/me` and `POST /v1/sessions/ticket` over HTTP (disabled account → 403); dropped the unread `OAuth:Microsoft:TenantId` (**D7**) |
| `c844639` | T13 wiring-audit checklist in the work log; plan 004 status → Implemented; D7 and an approval-log row |
| `7a177d2` | **Bridge race fix**, found by the first CI run: the slot was released while the presenter was still `ending`, so a quick reconnect's start ran **unrecorded**. `ObserveEndAsync` now waits up to 5 s for `idle`. New test `Disconnect_holds_the_slot_until_the_presenter_is_idle_so_the_next_run_is_recorded`, plus `BridgeTestSupport.ConnectWhenFreeAsync` |
| `5b76132` | Work log for PR #3 and the race |

- **Verified by me:** build 0/0; **208 tests** (Application 52, Infrastructure 31, Api 79, Integration 37, Cli 9);
  the Api suite 10/10 in a row; mutations killed.
- **Pre-existing flake, not fixed:** `PresenterTests.Last_slide_silence_sends_wrap_up_then_closes`. It fails 3/15
  here and 1/15 on `origin/develop`, because the harness's `Flush()` uses two barriers and a yield. I asked the user
  whether to fix it in a separate PR; there is **no answer yet**.

## In progress: the external implementation review

The user asked for it ("start the two reviewers, with `pi` harness"). Both reviewers run
`pi --model openai-codex/gpt-5.6-sol:medium` and are read-only.

| Reviewer | Terminal | Brief | Report |
|---|---|---|---|
| Identity: JWT, refresh, SSO, dev sign-in, rate limits, tickets, `/ws` auth, web auth client | `tm-5d655e211` (rev-identity) | `.claude/agent-reports/plan-004/review-impl-identity-brief.md` | `.claude/agent-reports/plan-004/review-impl-identity-report.md` |
| Persistence: schema, DI lifetimes, owner scoping, recorder, bridge barrier, CLI, config/CI/docs | `tm-21553a4cb` (rev-persistence) | `.claude/agent-reports/plan-004/review-impl-persistence-brief.md` | `.claude/agent-reports/plan-004/review-impl-persistence-report.md` |

- The shared rules are in `review-impl-common.md`: A/B/C/D classification, blocker vs improvement,
  CONFIRMED/PLAUSIBLE, and "IS THIS BRANCH READY TO MERGE?".
- Each report's last line is `<!-- REVIEW COMPLETE -->`.
- **Watchers:**
  - a Monitor on both report files (task `busmbeadh`, 30 min; re-arm it if it expires);
  - a recurring health cron `b98632e9` every 14 minutes that checks both terminals. Delete it once both reports
    are complete.

## Next steps, in order

1. **Wait** for both reports. Do not poll the terminals: the Monitor and cron cover that. Do not do the review work
   yourself.
2. **Save the results:**
   - Save the findings to `docs/review/006-plan-004-impl-review-round-1.md`: a summary, the findings by reviewer,
     and the files examined.
   - Append one row per reviewer (or one combined row) to `docs/agentic/review-rounds-ledger.md` in the
     `| date | branch | round | A | B | C | D | note |` format.
   - Close both terminals only after the results are saved.
3. **Triage:** check each blocker yourself against the code before acting on it. Some will be PLAUSIBLE, and
   agent claims need verifying. Then:
   - send the confirmed blockers and worthwhile improvements to a **fresh `pi` implementer** (luna:high or
     terra:high) with a brief in `.claude/agent-reports/plan-004/impl-review-fixes-brief.md`;
   - verify its work yourself: re-run the suites, and mutation-test every new oracle with backups and an md5
     restore;
   - commit, push to PR #3 (it is already open, so pushing follow-up commits is within the user's approval), and
     watch CI.
   Fix the class, not the instance, following doctrine `~/.claude/docs/08-why-reviews-take-too-many-rounds.md`.
4. **Round 2:** re-review the fix diff only, with the same reviewers or fresh ones. Stop at round 2 unless new D
   blockers appear (see the escalation triggers in doctrine 08).
5. **Report to the user:** what the review found, what was fixed, whether PR #3 is ready, and what is still theirs:
   - merging (**never merge without their go**);
   - manual runbook steps 8–16 in plan §7, plus the T13 Serilog live cycle;
   - their answer on the presenter-test flake.

## Gotchas and settled decisions (do not relitigate)

- D1–D7 are in plan §10; D5 was the user's decision.
- The PR base is **`develop`**, per AGENTS.md. Handoff 010 wrongly said `master`.
- Never merge, deploy or delete without asking. Pushing fix commits to the open PR branch is fine.
- Python on Windows writes CRLF. The repo is LF (`.gitattributes` `eol=lf`), so run `sed -i 's/\r$//'` after
  editing a doc with Python, or edit with the Edit tool.
- A bash heredoc containing backticks inside `$(...)` broke once. Use the Write tool for long briefs.
- The work log's plan-004 section ends with "PR #3 opened …". Add new entries after it.
