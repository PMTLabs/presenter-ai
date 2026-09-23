# 019 — Handoff: plans 007 and 008 approved; waiting for "implement plan 007"

**Written:** 2026-09-23 01:58. Supersedes `018-handoff-plans-007-008.md`.

## Goal

The AI presenter should act like a human presenter. It must obey spoken commands, take turns naturally, and use a
standard, extensible tool protocol that reaches external tools. The work is split into two plans, both approved:

1. **Plan 007:** voice control, turn-taking, and the tool protocol with lazy tool discovery.
   - `docs/plan/007-voice-control-and-tool-protocol.md`, Approved (2026-09-23), revision 1.
2. **Plan 008:** per-user MCP external tools, web search, and the Tools page.
   - `docs/plan/008-mcp-external-tools.md`, Approved (2026-09-23), revision 1.
   - It depends on 007: it starts only after 007 is merged and 007's Task 0 probe gate has passed.

**Nothing is implemented. Implementation starts only when the user says "implement plan 007".** Plan 008 then waits
for its own explicit instruction.

## Done this session (after handoff 018)

- **Plan 008 discovery:**
  - pi luna:high scout `p008-scout-01`, spot-checked, saved as `docs/research/007-mcp-external-tools-scout.md`;
  - Context7 notes on the C# MCP SDK: `ModelContextProtocol.Core` 2.2.0, `HttpClientTransport` takes our own
    `HttpClient`, the SDK's built-in OAuth is not used.
- **Plan 008 draft**, then external review round 1 by pi sol:medium (`p008-plan-rv-01`): A0 B4 C2 D9.
  - Every finding was verified (bridge take-over rule, anonymous SSO token endpoint, MCP spec 2026-07-28 via
    Context7) and folded into revision 1.
  - Outcomes: `docs/review/010-plan-008-review-round-1.md`, plus a ledger row in
    `docs/agentic/review-rounds-ledger.md`.
- **Plan 008 approved (G2)** by the user.
- **Key design decisions in 008 revision 1** (settled; do not relitigate):
  - **Credentials:** AES-256-GCM with the config secret `Tools:CredentialKey`, and associated data bound to the owner
    and the server. Without the key, the credential endpoints return 503.
  - **Outbound guard:** checked at connect time, with a double DNS check (all addresses plus `A` records) and an
    `ISocketConnector` seam. No proxy. Auto-redirect is off; only credential-free metadata GETs follow at most 3
    hops.
  - **OAuth (spec 2026-07-28):** registration order is pre-registered client ID → client ID metadata document (API
    endpoint `GET /v1/tools/oauth/client-metadata.json`) → dynamic registration → ask the user for a client ID.
    Completion happens on the app route `/tools/oauth/callback`, which posts `POST /v1/tools/oauth/complete` as the
    signed-in user; the state's owner must match. Refresh uses a Redis lease plus an `xmin` compare-and-swap.
  - **Confirmation:** a call that needs a "yes" returns `confirmation_required` through the tracker. On a locally heard
    eligible "yes", the presenter runs the captured call itself, then speaks the outcome (commentary) and adds the data
    as thinking. The model can never approve. Retry after 401/404 happens only for tools that need no confirmation.
  - **Sanitising boundary:** stable error codes; the SDK gets `NullLoggerFactory`; `RemoveAllLoggers()` on the named
    clients.
  - **Added beyond the brief** (the user saw and approved): "Remove server", direct connect for servers that need no
    login, web search off by default.

## Environment

- **Main tree:** `D:\sources\demo\presenter-ai` on `develop` @ `0a30aa3`.
  - No worktrees. The only branches are `develop` and `master`.
- **Untracked and uncommitted on `develop`** (do not commit on `develop` without asking):
  - `docs/plan/007-voice-control-and-tool-protocol.md`, `docs/plan/008-mcp-external-tools.md`;
  - `docs/research/006-voice-control-and-tools-scout.md`, `docs/research/007-mcp-external-tools-scout.md`;
  - `docs/review/009-plan-007-review-round-1.md`, `docs/review/010-plan-008-review-round-1.md`;
  - `docs/progress/018-handoff-plans-007-008.md` and this handoff;
  - `docs/agentic/review-rounds-ledger.md` (modified, two new rows).
  - `.mcp.json` and `.playwright-mcp/` are not ours.
- **API:** runs on http://localhost:47913 from the main tree, as background task `bvl72as09`.
  - Its env comes from the scratchpad `env.sh`. **Never echo it.**
  - Restart it with:
    `source "$S/env.sh" && export Jwt__SecretKey="$(openssl rand -base64 48)" && exec dotnet run --project src/PresenterAi.Api > "$S/api.log" 2>&1`,
    where `S` is the session scratchpad.
- **Agents:** the `pi` harness, as the user asked.
  - Scout: `pi --model openai-codex/gpt-5.6-luna:high`. Reviewer: `…gpt-5.6-sol:medium`. Implementer:
    `…gpt-5.6-terra:high`.
  - Use a fresh termflow terminal per task. Write the brief to a scratchpad file, and pre-create the report file.
  - The literal completion marker goes only as the report's last line.
  - Watch for the report with a background `until` loop (25 min timeout).

## Next steps, in order

1. Wait for the user. Do nothing until they say **"implement plan 007"**.
2. When they do:
   - create `feature/007-voice-control-and-tools` from `develop`;
   - commit the docs listed above there: stage explicit files, run `bash scripts/secrets-guard.sh`, no AI
     attribution;
   - then follow plan 007 §5 in order. **Task 0, the live probe, is a gate**: title navigation must delegate at
     least 4 of 5 times, and the tool cycle must complete. Otherwise stop and revisit with the user.
3. Delegate implementation to pi terra:high, one task group at a time. Verify each task (tests and mutations as the
   plan lists), then do a spec-vs-implementation audit and an external review before the PR.
4. Plan 008 starts only after 007 is merged, and on the user's explicit instruction.
5. Every user-facing response ends with a `Task done: …` line.

## Gotchas

- **Standing rules:**
  - **Secrets:** never read, echo or commit secrets (`.env`, `appsettings.Local.json`, user-secrets, `env.sh`). Agent
    briefs must include: "Never read, write or print .env, appsettings.Local.json, user-secrets or any real
    credential. Refer to config keys by name."
  - **Git:** stage explicit files, run `bash scripts/secrets-guard.sh` before commits, no AI attribution, no bare
    `git stash`.
  - **Processes:** never touch port 3000 and never kill all node processes.
  - **Confirmation:** ask before push, PR, merge or delete.
- **Heredocs:** the Bash tool breaks on heredocs that contain apostrophes. Write scripts with the Write tool.
- **Line endings:** the docs in this repo use LF (`review-rounds-ledger.md` has no CR).
- **Mutations:** use the runtime-false guard `DateTime.UtcNow.Year < 0`, because `if (false)` fails `-warnaserror`.
- **Known flakes:** `PresenterTests.Wrap_up_without_audio_ends_after_fallback` and
  `Last_slide_silence_sends_wrap_up_then_closes`. They are intermittent and predate this work.
