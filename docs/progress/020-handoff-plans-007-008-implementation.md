# 020 — Handoff: plans 007 and 008 in implementation

**Written:** 2026-09-23 13:58. Supersedes `019-handoff-plans-007-008-approved.md`.

## Goal

Implement plan 007 (voice control, turn-taking, tool protocol), then plan 008 (per-user MCP external tools). The
user said "implement plan 007, then 008" and asked to **delegate and run in parallel whenever a task is not
dependent**. Both plans are approved (`docs/plan/007-…`, `docs/plan/008-…`).

## Agents (user direction, 2026-09-23; memory `feedback-delegate-implementation-to-pi`)

- Capability rank, least → most: `pi --model openai-codex/gpt-6-luna:high` (cheapest; small/medium tasks) →
  `agy --dangerously-skip-permissions --model "Gemini 3.8 Flash (High)"` → `pi --model openai-codex/gpt-6-sol:medium`
  (hardest: concurrency, protocols, security). Reviews: **sol/high**.
- agy's limit is reached only when the **first** 5h figure hits 100%.
- Observed: sol/medium and luna/high finish in about 5–10 min per task. First passes sometimes stop short (the report
  says "partial" or lists skipped verify items/mutations) — always read "Open issues" and send a follow-up in the same
  terminal. agy needed steering on an ambiguous task.
- Workflow: brief file in the scratchpad, pre-created empty report, terminal per task, `pi` submit with
  `cliType: codex`, agy with `cliType: gemini`; report ends with `IMPLEMENTATION COMPLETE <id>`; watch with a
  background `until` loop / Monitor. Parallel agents get separate worktrees under `.claude/worktrees/` (ignored).
- **Verify every result yourself before committing:** `dotnet build PresenterAi.slnx -warnaserror`, then
  `DOCKER_HOST=tcp://localhost:2375 dotnet test PresenterAi.slnx --no-build`, read the diff against the plan.

## Done

**Plan 007** — branch `feature/007-voice-control-and-tools` (main tree `D:\sources\demo\presenter-ai`), head `18abeb2`:
- `a1793e5` docs (plans, scouts, reviews, handoffs 018/019).
- `b0f65d2` Task 0 live probe — **gate passed** (title navigation 4/5, the tool cycle completes; results in research 006;
  input must be real audio, `session.input_audio.append`).
- `47b978a` Tasks 1–2 (agy): registry, catalogue, GPT-Live tool wire.
- `70db30d` Task 3 (agy): presenter tools, tool-round tracker. Orchestrator fix: `end_presentation` can never end the
  talk by itself.
- `264d349` merge of `feature/007-web-and-matcher` (`d22416c`, pi sol): matcher + Task 6 web.
- `18abeb2` Tasks 4–5 (pi sol): utterance assembly, eligibility, dispatch, paused listening, flush, phases,
  precedence, end confirmation (`confirmed:true` ends only in `AwaitingEndAnswer`). Old tests migrated per plan
  §3.6–§3.8 (pause no longer mutes; new prompts; +700 ms answer→check-in step) — reviewed.
- All suites green at `18abeb2`: Application 233, Infrastructure 48 (+4 skipped), API 146, CLI 11, Integration 40 (+1).

**Plan 008** — branches off the 007 line (none merged into 007 yet):
- `feature/008-protocol` @ `b505781` (worktree `p008-t1`, luna/high): Task 1 tool protocol additions. Based on
  `264d349` (before 007 Tasks 4–5) — small `Presenter.cs` diff in the tool invocation (Resolve + per-tool Timeout).
  Leftover: `PresenterSettings.ToolTimeoutMs` is now unused → remove when merging.
- `feature/008-persistence` @ `6f46ce7` (worktree `p008-t2`): Task 2 entities, migration `AddToolServers`,
  repository, `CredentialProtector` (`credential_unreadable` vs `credential_key_changed`), `ExternalToolsOptions`
  (bound to `Tools`, separate from 007's `ToolsOptions`).
- `feature/008-guard` @ `d560fdd` (worktree `p008-t3`): Task 3 outbound guard, real HTTPS redirect tests, logger test.
  Orchestrator fix: Teredo `2001::/32` blocked.
- `feature/008-mcp-external-tools` @ `ddc02bc` (worktree `p008-t5`) = Task 2 + Task 3 merged + Task 5 OAuth
  (`McpOAuthService`, `McpOAuthStateStore`, refresh lease + xmin CAS, anonymous
  `GET /v1/tools/oauth/client-metadata.json`, OpenAPI snapshot updated, reusable
  `tests/PresenterAi.Integration.Tests/Tools/StrictFakeAuthServer.cs`, 23 OAuth scenarios, all 6 mutations). It also
  added a guarded initialize probe for Task 4 to call.

## Next steps, in order

1. **Plan 007 Task 7 (docs)** → luna/high in the main tree: README voice-command table (= matcher phrase table),
   `docs/guides/002-audience-questions.md`, `AGENTS.md` flush frame, `docs/reference/001-api-and-code-conventions.md`
   §8, research 006. **In parallel:** start 008 work (step 4) in its worktree.
2. **Plan 007 Task 8:** wiring audit (07-integration-boundary-audit §5) and the manual runbook. The runbook needs the
   API: restart it (it was stopped because it locked build outputs) from the main tree:
   `source "$S/env.sh" && export Jwt__SecretKey="$(openssl rand -base64 48)" && exec dotnet run --project src/PresenterAi.Api > "$S/api.log" 2>&1`
   as a background task (S = session scratchpad; never echo env.sh). Ask the user to run the live runbook steps
   (voice) or do the parts that can be scripted; the Task 0 audio WAVs are in `$S/probe-audio`.
3. **Plan 007 review + PR:** spec-vs-implementation audit, external review by **sol/high** (read
   `~/.claude/docs/08-why-reviews-take-too-many-rounds.md` first; A/B/C/D classification; ledger row), fix, then
   **ask the user** before pushing and opening the PR into `develop`. Do not merge without the user.
4. **Plan 008 integration branch:** merge `feature/008-protocol` into `feature/008-mcp-external-tools`, then merge the
   007 head (`feature/007-voice-control-and-tools`) into it (resolve the `Presenter.cs` tool-invocation hunk: keep 007
   Tasks 4–5 and apply Task 1's Resolve + Timeout; remove `ToolTimeoutMs`). Verify full suite.
5. **Plan 008 Task 4** (MCP client + session tool source; needs T1, T2, T3, T5) → agy or sol/medium. Then Task 6 REST
   (luna/high or agy), Task 7 presenter integration (sol/medium — confirmation gate, hosted web search, Start
   budget), Task 8 web Tools page (luna/high), Task 9 docs (luna/high), Task 0 probe (needs 007 merged — orchestrator
   runs it live), Task 10 verification. External review sol/high, then ask before PR.
6. Every user-facing response ends with a `Task done: …` line.

## Environment

- Main tree `D:\sources\demo\presenter-ai` on `feature/007-voice-control-and-tools` @ `18abeb2`; clean except
  `.mcp.json` and `.playwright-mcp/` (not ours).
- Worktrees (all committed, nothing pending): `.claude/worktrees/p007-pi` (merged, can be removed), `p008-t1`,
  `p008-t2`, `p008-t3` (merged into `p008-t5`), `p008-t5` (`feature/008-mcp-external-tools`). Ask before deleting
  branches.
- API on 47913 is **stopped** (task `bvl72as09` stopped deliberately).
- Docker for Testcontainers: `DOCKER_HOST=tcp://localhost:2375`.
- No agent terminals are open.

## Gotchas

- Standing rules: never read/echo secrets (`.env`, `appsettings.Local.json`, user-secrets, scratchpad `env.sh`); agent
  briefs carry the no-secrets line; stage explicit files; `bash scripts/secrets-guard.sh` before commits; no AI
  attribution; no bare `git stash`; never touch port 3000 or kill all node processes; ask before push/PR/merge/delete.
- A running API locks `src/PresenterAi.Api/bin` → the build fails with MSB3021/3027 copy errors (not compile errors).
  Tests run with `--no-build` after a failed build use stale binaries — always check the build line first.
- Mutations use the runtime guard `DateTime.UtcNow.Year < 0` (`if (false)` fails `-warnaserror`).
- Heredocs with apostrophes break the Bash tool; write files with the Write tool or python.
- Known flakes: `Wrap_up_without_audio_ends_after_fallback`, `Last_slide_silence_sends_wrap_up_then_closes`.
