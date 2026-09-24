# 018 — Handoff: plan 007 approved; write plan 008 (MCP) from its confirmed brief

**Written:** 2026-09-23 01:26. Supersedes `017-handoff-plan-006-take-over.md`.

## Goal

The user wants the AI presenter to act like a human presenter. It must obey spoken commands and use a standard,
extensible tool protocol that can reach external tools. The trigger was a live talk where "Can you stop now" and
"Just end the meeting" were acknowledged in speech but not acted on. The work is split in two:

1. **Plan 007** (voice control, human-like turn-taking, tool protocol with lazy tool discovery):
   - **Approved** 2026-09-23 (revision 1): `docs/plan/007-voice-control-and-tool-protocol.md`.
   - Implementation waits for the user to say "implement plan 007".
2. **Plan 008** (MCP external tools):
   - The requirement brief is **confirmed (G1 passed)**; it is reproduced in full below.
   - **The plan document is not written yet. That is the next step.**

## Environment

- **Main tree:** `D:\sources\demo\presenter-ai`, on `develop` @ `0a30aa3` (PRs #5 and #6 merged).
  - No worktrees exist. Feature branches 002–006 were deleted locally and on origin at the user's request.
- **New files, untracked and uncommitted,** on the `develop` working tree:
  - `docs/plan/007-voice-control-and-tool-protocol.md`;
  - `docs/research/006-voice-control-and-tools-scout.md`;
  - `docs/review/009-plan-007-review-round-1.md`;
  - `docs/agentic/review-rounds-ledger.md` (modified: one new row);
  - this handoff.
  - The untracked `.mcp.json` and `.playwright-mcp/` are not ours.
  - When implementation starts, create `feature/007-voice-control-and-tools` from `develop` and commit these docs
    there. Don't commit on `develop` without asking.
- **API:** runs on http://localhost:47913 from the main tree, as background task `bvl72as09`.
  - Its env comes from the scratchpad `env.sh`. **Never echo it.**
  - Restart it with:
    `source "$S/env.sh" && export Jwt__SecretKey="$(openssl rand -base64 48)" && exec dotnet run --project src/PresenterAi.Api > "$S/api.log" 2>&1`,
    where `S` is the session scratchpad.
  - The web build in `web/app/dist` is current for `develop`.
- **Agents:** the user wants the `pi` harness for agents. Scout: luna:high. Reviewer: sol:medium. Implementer:
  terra:high.
  - Use a fresh termflow terminal per task.
  - Put the literal completion marker only as the report's last line, and tell the agent not to write it into
    drafts. A scout once wrote a placeholder skeleton ending with the marker.

## Done this session (after handoff 017)

- **Plan 006:** implemented by pi terra:high, reviewed and hardened (the disposed-CTS race and reference §8), and
  verified (tests, 4 re-run mutations, a two-tab Playwright check).
  - The user tested it live ("works").
  - PR #6 merged into `develop` (`0a30aa3`) after PR #5 (`bc0941d`). PR #4 was auto-marked merged.
  - All merged branches and worktrees were removed at the user's request.
- **Plan 007:** scout (`docs/research/006`), interview (2 rounds), brief G1 (the user added lazy tool loading), plan,
  external review round 1 by pi sol/medium.
  - The review found A1 B1 C2 D10. All were verified against the code, accepted and folded in (`docs/review/009`,
    plus the ledger row).
  - The user approved the plan (G2).

## Plan 008 — confirmed requirement brief (G1, 2026-09-23)

**Decisions (user):**

| Question | Answer |
|---|---|
| Sources first | All: web search, company documents, any configured MCP server, business data |
| Transport | Remote HTTP only (Streamable HTTP); no stdio |
| Permissions | Read-only tools auto; others need a spoken yes. Read-only comes from MCP `readOnlyHint` plus a user override "always ask" per server or tool |
| Scope | **Per user**: each signed-in user connects their own servers and credentials |
| Server auth | MCP OAuth (authorization spec, Connect-and-approve) **plus** a pasted API key or header for servers without OAuth |
| Allowed hosts | Any public URL, but **block internal addresses**: refuse localhost, private networks and cloud-metadata addresses, including after redirects (SSRF guard). The user first chose "Any URL"; after the risk was explained they chose this |
| Settings UI | A "Tools" page in the presenter app |

**Brief:**
- **Problem:** the presenter only knows its script; it can't look anything up or act on outside systems.
- **Goal:** each user connects their own MCP servers plus web search, and their talks use those tools safely.
- **In scope:**
  - **MCP client in the API:**
    - the official C# MCP SDK;
    - remote Streamable HTTP servers;
    - tools only.
  - **Per-user connections:**
    - add a server by URL and name;
    - connect with MCP OAuth or an API key or header;
    - credentials encrypted per user and never shown again;
    - tokens refreshed automatically;
    - disconnect deletes the credentials.
  - **At Start:**
    - the owner's servers are listed (`tools/list`) within a time budget;
    - their tools join plan 007's per-session catalogue (`ToolSessionCatalogue`) under server-prefixed names;
    - an unreachable server is skipped and logged;
    - above `Tools:MaxInlineTools`, tools are reached through `find_tools` and `call_tool`.
  - **Web search:** GPT-Live's hosted `web_search` tool, with an on/off toggle per user.
  - **Permissions:**
    - `readOnlyHint` → runs at once;
    - otherwise a spoken yes is needed first, using plan 007's confirmation mechanism;
    - "always ask" override per server or tool.
  - **During a call:**
    - a short spoken "one moment";
    - about 10 s timeout per call;
    - results cut to a size budget and passed to the model as data, never as instructions.
  - **A Tools page in the presenter app:**
    - add a server;
    - connect (OAuth or key);
    - list its tools with read-only badges;
    - "always ask" toggles;
    - a web search toggle;
    - test the connection;
    - disconnect.
  - **Logs:** every external call is logged in the page and server logs with server, tool, duration and outcome.
    Arguments and results aren't logged.
- **Out of scope:**
  - stdio servers;
  - per-presentation or admin-managed servers;
  - sharing connections between users;
  - MCP resources, prompts and sampling.
- **Constraints and assumptions:**
  - it depends on plan 007 (registry, catalogue, confirmation, Task 0 probe); if 007's probe shows GPT-Live can't run
    tools, 008 is revisited;
  - tools only in managed mode;
  - tool results and server hints are untrusted (prompt injection; a server can lie about `readOnlyHint`, hence
    "always ask");
  - the SSRF guard applies to every outbound MCP and OAuth request, including redirects and DNS results.
- **Acceptance criteria:**
  1. A user adds a server, connects it with OAuth or a key, and sees its tools with read-only badges.
  2. In their talk, a question the server can answer gets a spoken "checking" and then an answer from the result.
  3. A tool that isn't read-only, or is set to "always ask", runs only after a spoken yes.
  4. With web search on, general questions are searched. With it off, search isn't offered.
  5. Another user's talk never sees this user's servers or credentials.
  6. An unreachable server, expired token or timeout doesn't break the talk. The presenter says it couldn't get the
     answer, the log says why, and the Tools page shows "reconnect".
  7. With more than 16 tools, discovery through `find_tools` works in a live talk.
  8. Credentials are encrypted at rest, never returned by the API or logged, and deleted on disconnect.
  9. Tests pass against an in-process MCP test server, the build passes, and a manual runbook is run with a real
     server.
  10. **Added by the user's host choice:** URLs that resolve to localhost, private, link-local or metadata addresses
      are refused, including after redirects.

## Next steps, in order

1. **Write plan 008** at `docs/plan/008-mcp-external-tools.md`, following the planning skill Phase 4 and the plan 007
   format. Status: Draft.
   - **Discovery first,** delegated to a pi luna:high scout (read-only, report file, marker only at the end):
     - the official C# MCP SDK (package name, Streamable HTTP client, OAuth support in the SDK, `readOnlyHint`
       annotations);
     - this repo's identity and persistence (Postgres entities and EF migrations from plan 004, the JWT user id,
       Data Protection usage if any);
     - the presenter app's routing and auth UI (`web/app/src`);
     - how the bridge knows the talk owner (`ClientConnection.UserId`);
     - how `SessionRequest` reaches `LiveSession`.
   - Use Context7 for SDK documentation.
   - The plan must build on plan 007's `ITool` / `ToolSessionCatalogue` / confirmation phases. Name the dependency
     and the Task 0 gate.
   - It needs a sequence diagram:
     Start → load the owner's connections → `tools/list` with a budget → catalogue → tool call → confirmation if not
     read-only → MCP `tools/call` → result truncation → output.
   - It needs a data model: connections, encrypted secrets, overrides, the web search flag.
   - It needs:
     - the OAuth flow (redirect or callback endpoints, PKCE, dynamic client registration if the server supports it);
     - the SSRF guard (resolve and check every IP, re-check on redirects, block metadata);
     - the REST endpoints;
     - the Tools page;
     - tests (an in-process MCP test server);
     - a wiring-audit task;
     - a manual runbook.
2. Run the self-review checklist, then present a summary of 15 lines or fewer and ask
   **Approve / Approve + external review / Revise / Reject**. It's size L, so offer the external review (pi sol:medium,
   classification A/B/C/D, `docs/review/010-…`, a ledger row).
3. After approval, stop. Implementation of 007 (then 008) waits for the user's explicit "implement plan 007".
4. Every user-facing response ends with a `Task done: …` line.

## Gotchas

- Standing rules:
  - **Secrets:** never read, echo or commit secrets (`.env`, `appsettings.Local.json`, user-secrets, `env.sh`).
    Agent briefs must say so.
  - **Git:** stage explicit files, run `bash scripts/secrets-guard.sh` before commits, no AI attribution, no bare
    `git stash`.
  - **Processes:** never touch port 3000 and never kill all node processes.
  - **Confirmation:** ask before push, PR, merge or delete.
- **Heredocs:** the Bash tool breaks on heredocs that contain apostrophes. Write Python scripts with the Write tool,
  keep `newline=''` and preserve CRLF.
- **Mutations:** use the runtime-false guard `DateTime.UtcNow.Year < 0`, because `if (false)` fails `-warnaserror`.
- **Known flake:** `PresenterTests.Wrap_up_without_audio_ends_after_fallback` and
  `Last_slide_silence_sends_wrap_up_then_closes` are known intermittent failures (they assert idle after one
  `Flush()`). They predate this work.
