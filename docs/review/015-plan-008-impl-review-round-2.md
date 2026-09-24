# 015 — Plan 008 implementation review, round 2

**Date:** 2026-09-23. **Reviewer:** `pi` `openai-codex/gpt-6-sol:high`, read-only, request `r008-impl-02`. **Scope:** `b87ab30...64580bb` on `feature/008-mcp-external-tools` (the round 1 fixes: `4f5d6dd` group B, `ba1cbaf` group A). Task 0 live probe and the Task 10 runbook remain deferred. The reviewer's report follows unchanged; the orchestrator's disposition is at the end.

---

# r008-impl-02 — plan 008 implementation review, round 2

Scope: `b87ab30...HEAD`; static review only; no tests/builds run. The deferred live probe and manual runbook are outside this review.

## Findings

1. **A · should-fix — credential-version fencing stops at refresh, not at the other status writers.** `src/PresenterAi.Infrastructure/Tools/Mcp/McpConnector.cs:136-155,264-269`; `src/PresenterAi.Infrastructure/Tools/Mcp/McpSessionToolSource.cs:161-195`; `src/PresenterAi.Api/Endpoints/ToolEndpoints.cs:396-411,456`; `src/PresenterAi.Infrastructure/Tools/Mcp/McpOAuthService.cs:208-213,261`; `src/PresenterAi.Infrastructure/Tools/CredentialProtector.cs:92-105` (unused helper with the same unsafe pattern). An old talk or `/test` reads credential version V; a concurrent owner reconnect saves V+1 and marks connected; the old decryption/list operation then fails and writes `needs_reconnect` or `error` unconditionally, hiding the valid connection. Even an old success can clear a new credential's failure (`connected`). OAuth completion can similarly mark a newer credential as connected/disconnected, and refresh's unconditional success status can overwrite a later failure. The new conditional write in the failed-refresh branch alone does not fence these paths. **Fix:** carry the observed credential version (and auth-kind/no-credential generation where appropriate) through every connection/list outcome, use conditional atomic status updates including success, and ensure new credential saves own the status transition. Add a controlled interleaving test at the *actual endpoint/source* status write, not just the repository helper.

2. **B · should-fix — the failed-refresh race oracle never runs a failed refresh.** `tests/PresenterAi.Integration.Tests/Tools/McpOAuthTests.cs:311-327` (`Failure_status_compare_and_swap_does_not_overwrite_a_new_credential`). It directly saves a replacement and calls the repository conditional setter. A wrong `McpOAuthService.RefreshAsync` that calls unconditional `SetStatusAsync(needs_reconnect)` on token failure still passes this test. **Fix:** pause the token exchange at failure, replace the credential between `FreshAsync` and the failure-status write, resume, and assert winner token returned and connected status preserved through `RefreshAsync`.

3. **B · should-fix — the named-client pipeline test never checks the IP actually dialed.** `tests/PresenterAi.Infrastructure.Tests/Tools/OutboundGuardTests.cs:172-183` (`LoopbackSocketConnector`). Its fake ignores `address` and always connects to loopback. A wrong named-client wiring with a custom callback that rejects blocked DNS but dials an arbitrary different IP for otherwise allowed DNS still receives the fake server's 200; the separate direct `GuardedConnectCallback` unit test checks its own seam, not which callback the named clients actually use. **Fix:** record/assert the exact approved IP, port and cancellation token at the named-client socket seam; check that a mixed public/private DNS result cannot dial either address.

4. **D · should-fix — switching to discovery can turn a formerly valid catalogue into a failed Start.** `src/PresenterAi.Application/Tools/ToolSessionCatalogue.cs:46-73,180-192`; `src/PresenterAi.Application/Presenting/Presenter.cs:536-537`. Take pinned presenter-tool definitions totaling just under 32 KiB (but leaving less than the space for `find_tools`/`call_tool`), plus one session tool that takes the total over 32 KiB. Previously the session tool was omitted and the presenter could start with its pinned tools. Now byte overflow switches to discovery, `AddInline(pinned)` succeeds, then `AddInline([findTools,callTool])` throws `InvalidOperationException`; the presenter does not catch catalogue construction and Start fails entirely. **Fix:** budget pinned and meta definitions together; if pinned definitions cannot coexist with discovery tools, trim/defer nonessential pinned tools or fail gracefully without bringing down the talk. Add a near-limit pinned+session regression fixture.

5. **B · should-fix — the catalogue budget oracle does not exercise searchable byte overflow.** `tests/PresenterAi.Application.Tests/Tools/ToolSessionCatalogueTests.cs:189-220` (`Exceeding_32KiB_budget_throws_InvalidOperationException`) only supplies oversized *pinned* tools and expects a throw; `:154-185` tests a count overflow with tiny definitions. Restoring the old implementation that chooses discovery only by count and silently omits a session tool for byte overflow still passes both; omitted tools remain undiscoverable. **Fix:** supply <=16 tools with >=32 KiB combined definitions but small pinned/meta definitions, assert `find_tools` and `call_tool` are inline, budget <=32 KiB and a previously omitted tool can be found/resolved; also test the near-budget pinned case in finding 4.

6. **D · should-fix — effective permission is cached as if it were a tool override.** `src/PresenterAi.Api/Endpoints/ToolEndpoints.cs:445-453`; `web/app/src/routes/Tools.tsx:66-75,427-455`. Load tools while server-level Always ask is true and the tool override is false: `/tools` returns effective `alwaysAsk:true` and the page caches it. Switch server-level Always ask off; `updateServer` reloads servers but not the expanded tools. The read-only tool still shows “asks first” and checked even though the next talk runs it without confirmation; the user has to toggle an already-false override or refetch to correct the stale display. **Fix:** keep raw override separate from effective permission, or invalidate/refetch expanded tool lists on server-level permission change. Test the true → false transition with an already expanded list.

7. **C · nit — guide calls a shared post-read deadline per-server.** `docs/guides/003-external-tools.md:48-50` says “Each server's discovery has a 3-second budget ... starting after repository reads”; `src/PresenterAi.Infrastructure/Tools/Mcp/McpSessionToolSource.cs:46-58` creates one shared `budgetCts` for all servers. A slow server launched later does not get an independent three seconds. **Fix:** say the parallel server-discovery phase shares a three-second deadline, starting after repository and override reads.

8. **C · nit — byte-overflow note asserts external tools are involved when there may be none.** `src/PresenterAi.Application/Tools/ToolSessionCatalogue.cs:48-63`. The new note says “External tools use discovery because the inline tool payload exceeded the 32 KiB budget.” A registry of only unpinned presenter tools with large descriptions/schemas (no connected external servers) triggers the same branch and logs this false diagnostic. **Fix:** say “Tools use discovery…” or condition the wording on a nonempty session catalogue.

## Fix verification (round-1 #1–14)

| # | Verdict; evidence and regression oracle |
|---|---|
| 1 | **Fixed for HTTP-handler replay** — `McpConnector.cs:280-317` structurally allows only a single `initialize`/`tools/list` method; `McpAuthHandlerTests.Unauthorized_retries_only_a_single_structurally_safe_method` catches escaped `tools/call`, batches and malformed JSON. `McpTool.cs:108-180` does not retry confirmed 401/404; its reconnect only issues initialize, not the call. No other automatic replay of a confirmed `tools/call` found in the plan-008 code. |
| 2 | **Partial** — `ToolSessionCatalogue.cs:46-74` switches to discovery on bytes as well as count, but there is no test of searchable byte overflow (finding 5) and a new near-budget pinned failure exists (finding 4). |
| 3 | **Fixed for needs_reconnect** — `McpOAuthService.cs:87-103` decrypts and reuses pre-registration; `McpOAuthTests.Reconnect_reuses_protected_pre_registered_client_when_registration_is_unavailable` fails if reuse is removed. |
| 4 | **Fixed** — `McpOAuthService.cs:199-216` connects/lists before reporting connected, rejects failed list; `McpOAuthTests.Registration_paths_exchange_strict_pkce_and_store_encrypted_tokens` asserts list count, `Completion_refuses_a_token_rejected_by_mcp_without_returning_remote_details` asserts refusal. |
| 5 | **Partial** — `McpFailure.cs:5-20`, `ToolEndpoints.cs:264,327,401,456`, `McpSessionToolSource.cs:181` map stable credential/auth codes and the key-change REST integration test fails without those mappings. Other status-writer races noted in finding 1. |
| 6 | **Partial** — `McpOAuthService.cs:264-278` + `PostgresToolConnectionRepository.cs:100-106` fence failed-refresh status by version; repository-only race oracle is finding 2; other unconditional writers in finding 1. |
| 7 | **Fixed for tool/server labels in the reviewed sinks** — `McpTool.cs:201-203`, `McpSessionToolSource.cs:115-117,166,183`, `Presenter.cs:2114-2115` sanitize/cap; `McpToolSourceTests.Hostile_tool_name_is_escaped_and_capped_in_structured_log` and `PresenterExternalToolTests.Hostile_tool_labels_are_escaped_and_capped_in_page_log` catch removal of sanitizing at those sinks. |
| 8 | **Partial** — `ToolEndpoints.cs:442-453` computes effective OR; `ToolEndpointIntegrationTests.Credential_test_tools_and_disconnect_use_persisted_rows` checks persisted false override plus server true; `Tools.spec.tsx` tests locked switch. The changed effective API contract interacts badly with cached tool lists when server permission is switched off (finding 6). |
| 9 | **Fixed** — `ToolEndpoints.cs:305-324` probes and connects no-auth before checking key, after owner lookup; outbound URL/transport guard still applies. `ToolEndpointIntegrationTests.OAuth_start_connects_a_no_auth_server_without_credential_key` and `OAuth_challenge_without_credential_key_returns_503_without_creating_state` cover both sides. |
| 10 | **Partial oracle** — `OutboundGuardTests.Named_client_actual_pipeline_blocks_dns_refuses_redirect_caps_response_and_disables_proxy` sends through both names; missing dial-IP assertion is finding 3. |
| 11 | **Fixed** — `McpToolSourceTests.Session_not_found_404_on_unconfirmed_tool_reconnects_once_and_retries` asserts two requests / one successful call; `Second_404_on_read_only_call_is_not_replayed` asserts bounded attempts. |
| 12 | **Fixed** — `McpOAuthTests.Registration_paths_exchange_strict_pkce_and_store_encrypted_tokens` and `ToolEndpointIntegrationTests.OAuth_start_complete_refresh_and_disconnect_use_real_redis_and_postgres` assert exactly one tools/list; rejected MCP token test checks failure. |
| 13 | **Fixed (agreed badge half)** — `Tools.tsx:427-434`, `Tools.spec.tsx` server-true/tested-false fixture locks the switch. Callback-refresh half explicitly deferred by disposition. |
| 14 | **Fixed in respect of 4 s cap, partial phrasing** — `docs/guides/003-external-tools.md:48-50` distinguishes presenter cap from discovery; per-server claim is finding 7. |

## New/changed oracle check and topics without findings

- **Safe retry:** the structural-method test identifies side-effect requests and denies batched/escaped calls; `McpToolSourceTests` request/effect counts identify both confirmed non-replay and the one read-only 404 retry. A 401/404 from a confirmed call can cause token refresh or a fresh *initialize*, not another `tools/call`. No plan-008 SDK-level or notification replay path found; request-body ambiguity defaults to no handler retry.
- **Probe/connect order:** `StartOAuthAsync` looks up the signed-in owner's server before probing, and the probe plus the no-auth `ConnectAsync` both traverse the guarded named transport. No state is created for no-auth or no-key challenge; challenge with a key still creates owner-bound one-use state. No new SSRF/owner/state issue found in the changed order.
- **Logging:** `UntrustedLogText.Sanitize` is used on the structured tool label and server/tool skip notes; page-log lines pass through `Presenter.LogMessage` including catalogue notes, source, title and upstream-controlled text. The two hostile-label tests cover the changed sinks. No remaining unsanitised log/note *sink* of MCP labels found; catalogue's unsafe wording is finding 8.
- **Other changed tests:** the API integration case checks the persisted-false/server-true permission; UI spec checks the currently loaded server-true lock but does not exercise the true→false transition (finding 6). OAuth success and rejected-token tests identify tool-list completion; reconnect test exercises stored pre-registration with both metadata and dynamic registration unavailable. The new no-key cases assert both connection success and a probed challenge with no state. The catalogue regression-oracle gap is finding 5, pipeline gap finding 3, refresh-race gap finding 2. No further wrong implementation of the specifically named changed tests identified.

**topicsWithNoFindings:** confirmed-call 401/404 non-replay and JSON-RPC allowlist; no-auth probe SSRF/owner/state ordering; initial OAuth PKCE/owner handling; OAuth completion successful listing and rejected-token handling; stable-code-to-problem/status mapping on non-racing REST failures; sanitising the checked structured/page log sinks; read-only 404 request-count oracle; no-key API test oracles; effective-ask API response for a fresh read; OAuth callback refresh (explicitly deferred); presenter page-log 1024-character cap on ordinary events (reviewed; no separate correctness failure established).

## IS THIS BRANCH READY TO MERGE?

**Blockers:** none identified in this round.

**Merge recommendation:** not yet. Address the stale status writes (1), catalogue Start failure and missing oracle (4–5), and stale effective-permission UI (6) before merging. **Further improvements:** harden the refresh interleaving and named-client test oracles (2–3), and correct the budget/note claims (7–8). This is a static assessment only; the deferred Task 0 probe and Task 10 runbook are not counted as findings.


## Orchestrator disposition

Round 2: classes A1, B3, C2, D2; no blockers. Findings were checked against the code before disposition. Fixes are
split by file area between two agents (group C: status writers and the refresh-race oracle; group D: guard oracle,
catalogue invariant test, Tools page refetch, wording, and a test-harness race found while verifying round 1).

| # | Class | Decision | Group | Note |
|---|---|---|---|---|
| 1 | A | Fix (class) | C | Status does not gate tool loading at talk start (every enabled server is tried), so the effect is a stale badge and the reconnect path keyed on `needs_reconnect`. Fixed as a class anyway: every write that follows a credential-reading attempt becomes conditional on the credential version it observed, success included; saves, disconnects and creates own their transitions. |
| 2 | B | Fix | C | The race test goes through `McpOAuthService.RefreshAsync` with a paused, failing exchange. |
| 3 | B | Fix | D | The fake socket seam records and asserts the dialed address for both named clients, including a mixed public/private DNS answer. |
| 4 | D | Narrowed to a test | D | The throw depends only on the presenter's own pinned tools plus `find_tools`/`call_tool`, never on session tools' size, and the count-overflow path had the same precondition before round 1. A test pins the invariant on the real default registry. |
| 5 | B | **Rejected** | — | `Session_tools_exceeding_budget_are_not_inline_but_are_searchable_with_note` (`ToolSessionCatalogueTests.cs:503`) covers byte overflow with at most 16 tools and asserts both discovery tools and resolution of an omitted tool; the orchestrator's mutation (count-only overflow) made it fail. |
| 6 | D | Fix | D | Refetch the expanded tool list after the server-level switch changes; spec covers true → false. |
| 7 | C | Fix | D | The guide says the discovery phase shares one 3-second deadline. |
| 8 | C | Fix | D | The note no longer claims external tools are involved. |

Also fixed in group D (not a review finding): `PresenterTests` harness log list appended from the event-loop task
while tests enumerate it ("Collection was modified", seen once in `Slide_diagnostics_count_output_frames_and_user_transcript_characters`).

Escalation check (review-rounds doctrine): the next round is round 3, the escalation trigger. Round 3 is a
confirmation round only; any further finding is handled by root-cause analysis of its class, not another patch round.
