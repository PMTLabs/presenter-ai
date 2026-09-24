# 021 — Handoff: plan 007 in review, plan 008 integration

**Written:** 2026-09-23 14:51. Supersedes `020-handoff-plans-007-008-implementation.md` (read its "Agents" and
"Gotchas" sections — still valid).

## Goal

Implement plan 007 (voice control, turn-taking, tool protocol), then plan 008 (per-user MCP external tools). The user
said "implement plan 007, then 008" and to **delegate and run in parallel whenever a task is not dependent**. Agents:
`pi --model openai-codex/gpt-6-luna:high` (cheap, small tasks) < `agy --dangerously-skip-permissions --model "Gemini 3.8
Flash (High)"` < `pi --model openai-codex/gpt-6-sol:medium` (hard tasks); reviews `pi … gpt-6-sol:high`. agy's limit
is reached only when the **first** 5h figure is 100% (it was 83% at 14:28). Verify every agent result yourself
(build, full suite, read the diff against the plan, run at least one mutation) before committing.

## Running right now (read these first)

Both agents write to the session scratchpad
`C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\78e5d02d-4e0c-4a11-b2ab-7f64d531cf4b\scratchpad`
(below: `$S`). Background watchers may or may not still be alive; check the report files directly.

1. **Plan 007 review round 3** (confirmation round), `pi` sol/high, termflow terminal `tm-a73d746ba` (the same
   reviewer session as rounds 1–2), read-only, main tree. Report `$S/r007-impl-03-report.md`, ends with
   `REVIEW COMPLETE r007-impl-03`.
2. **Plan 008 Task 6 (REST endpoints)**, agy, terminal `tm-1871dd879`, worktree
   `D:\sources\demo\presenter-ai\.claude\worktrees\p008-t6` (branch `feature/008-rest`, based on Task 4 `ee77ad5`).
   Brief `$S/i008-t6-brief.md`, report `$S/i008-t6-report.md`, ends with `IMPLEMENTATION COMPLETE i008-t6`.

If a report is still empty, look at the terminal screen (`get_terminal_screen`); if the agent died, restart it in a
new terminal with the same brief.

## Done since handoff 020

**Plan 007** — branch `feature/007-voice-control-and-tools`, main tree, head `d1ec0d8`:
- `368220a` Task 7 docs (luna/high): README voice-command table, guide 002, `AGENTS.md` flush frame, reference §8.
- `58acc5e` review round 1 saved as `docs/review/011-…` with the orchestrator's disposition table; `9f25d91` fixes
  (bounded tool timeout via `WaitAsync`, stale outputs keep the barrier moving, spoken range reply, check-in hold,
  immutable catalogue via `SnapshotTool`, off-loop log routed through the channel, real `session.start` budget test).
- `1bfa7dc` review round 2 saved as `docs/review/012-…` with disposition (D-01 deferred: only the six built-ins reach
  `IPresenter`; D-04 rejected: refusal only on a closed session); `088ecf1` fixes (`WaitingOnSlide` after check-in
  "no", no audio in `Ending` + flush, navigation-generation map, range reply keeps the hold, serialised 4 KiB cap in
  `ToolResult`, registry lock, same-slide `go_to_slide`/already-presenting resume go through the queued command);
  `d1ec0d8` correction note (the final backend answer keeps plan 005's 15 s re-arm — my brief was wrong).
- Ledger rows for rounds 1 and 2 are in `docs/agentic/review-rounds-ledger.md`.
- All suites green at `088ecf1`: Application 262, Infrastructure 49 (+4 skipped), API 146, CLI 11, Integration 40 (+1).
  Known flake `Wrap_up_without_audio_ends_after_fallback` fails about 2 runs in 5; passes on rerun.

**Plan 008** — integration branch `feature/008-mcp-external-tools`, worktree `.claude\worktrees\p008-t5`, head
`d68e516` = Tasks 1, 2, 3, 5 + the 007 line up to `ef8e06c` + Task 4 + Task 7 + an `ApiFactory` fix:
- `b7e2713`/`21b09b9` merges, `4f4b329` removed `PresenterSettings.ToolTimeoutMs` (per-tool `Timeout`).
- Task 4 (agy) `ee77ad5` on `feature/008-mcp-client`: `McpConnector`, `McpTool`, `McpSessionToolSource`,
  `TestMcpServer`, `StrictFakeAuthServer` moved to `tests/PresenterAi.Infrastructure.Tests/Tools/`. Two mutations run by
  me, both caught. Note: tool calls are recognised by the text `"tools/call"` in the request body (fragile, mention
  in the 008 review).
- Task 7 (sol/medium) `5d7e593` on `feature/008-presenter-integration`; I moved its prompts into
  `PromptBuilder.ExternalToolsSystemRules/BackendRules`; gate-off mutation → 35 failures.
- `d68e516`: API tests default to an `EmptySessionToolSource` (the real source reads Postgres, which the API tests do
  not run; before this, 15 bridge tests timed out).
- Suites green at `d68e516`: Application 282, Infrastructure 143 (+4), API 153, CLI 12, Integration 66 (+1).

## Next steps, in order

1. **Read the round 3 review** (`$S/r007-impl-03-report.md`). Save it as `docs/review/013-plan-007-impl-review-round-3.md`
   with a disposition table and a ledger row. Round 3 is the escalation trigger in
   `~/.claude/docs/08-why-reviews-take-too-many-rounds.md`: fix only genuine blockers (do them directly if small), do
   not start round 4 for improvements — list them as follow-ups. Close terminal `tm-a73d746ba` after.
2. **Plan 007 Task 8:** wiring audit is covered by the reviews; the manual runbook (plan 007 §5 Task 8, steps 1–6)
   needs the API and the user's voice. Restart the API from the main tree as a background task:
   `source "$S/env.sh" && export Jwt__SecretKey="$(openssl rand -base64 48)" && exec dotnet run --project src/PresenterAi.Api > "$S/api.log" 2>&1`
   (never echo `env.sh`). Then **ask the user** to run the voice steps (or whether to skip them), and **ask before
   pushing and opening the PR into `develop`**.
3. **Task 6 result:** verify (build, full suite, `bun run lint/test/build` in `web/`, read `ToolEndpoints.cs` and
   `SecretHygieneTests`, run one mutation yourself), commit on `feature/008-rest`, merge into
   `feature/008-mcp-external-tools` (worktree `p008-t5`), verify the full suite there.
4. **Merge the 007 fixes into the 008 line:** merge `feature/007-voice-control-and-tools` (`d1ec0d8` or later) into
   `feature/008-mcp-external-tools`. Expect conflicts in `Presenter.cs`, `ToolSessionCatalogue.cs`, `ToolResult.cs`.
   **Critical:** 007's `SnapshotTool` wrapper (`ToolSessionCatalogue.cs`) must forward `RequiresConfirmation`,
   `Timeout` and `Source`, or every external tool loses its confirmation gate — add a test. Also apply 007's bounded
   timeout (`WaitAsync`, cloned arguments, late-fault observation) to Task 7's approved-run path
   (`ApproveToolConfirmation`) and use `tool.Timeout`. Task 7's confirmation question uses the tool name, not
   `{title} on {server}` (plan §3.8) — fix while there.
5. **Then in parallel:** Task 8 web Tools page (luna/high; needs Task 6's generated client), Task 9 docs (luna/high).
   Then Task 0 live probe (orchestrator runs it after 007 is merged), Task 10 verification and wiring audit, external
   review sol/high, then **ask the user** before any PR.
6. Every user-facing response ends with a `Task done: …` line.

## Environment

- Main tree `D:\sources\demo\presenter-ai` on `feature/007-voice-control-and-tools` @ `d1ec0d8`; clean except
  `.mcp.json` and `.playwright-mcp/` (not ours).
- Worktrees: `p008-t5` (integration, `d68e516`), `p008-t6` (Task 6, agent working, uncommitted), `p008-t4`, `p008-t7`
  (merged), `p008-t1/t2/t3` (merged), `p007-pi` (merged). Ask before deleting branches or worktrees.
- API on 47913 is **stopped**. Docker for Testcontainers: `DOCKER_HOST=tcp://localhost:2375`.
- Open agent terminals: `tm-a73d746ba` (007 reviewer), `tm-1871dd879` (Task 6 agy).

## Gotchas

- All of handoff 020's gotchas still apply (secrets, explicit staging, `bash scripts/secrets-guard.sh`, no AI
  attribution, no bare `git stash`, port 3000, ask before push/PR/merge/delete, running API locks build outputs,
  `DateTime.UtcNow.Year < 0` mutation guard, write files with Write/python rather than apostrophe heredocs).
- A brief is a claim surface: when a fix list restates a rule, check it against the plan (round 2's "final answer
  does not re-arm" contradicted plan 005).
- Agents' "mutation" tables may name tests that merely assert the right behaviour; run one mutation yourself.
