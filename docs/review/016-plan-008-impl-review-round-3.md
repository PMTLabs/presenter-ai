# 016 — Plan 008 implementation review, round 3 (confirmation)

**Date:** 2026-09-23. **Reviewer:** `pi` `openai-codex/gpt-6-sol:high`, read-only, request `r008-impl-03`. **Scope:** `64580bb...1095717` on `feature/008-mcp-external-tools` (the round 2 fixes: `79d351f` group D, `50263aa` group C). This is the escalation round. The reviewer's report follows unchanged; the orchestrator's disposition is at the end.

---

# r008-impl-03 — confirmation review

Static inspection of `64580bb...1095717`; no tests or builds run. Task 0 and Task 10 are deferred, not findings.

## Round 2 dispositions

| # | Verdict and evidence | Regression oracle (would fail on removal) |
|---|---|---|
| 1 | **Partial.** All credential-reading *call sites* now invoke the conditional setter (`ToolEndpoints.cs:261-272,319-333,403-422,468`; `McpSessionToolSource.cs:163,171-200`; `McpConnector.cs:137-158,198-201,259-265`; `CredentialProtector.cs:94-101`; `McpOAuthService.cs:212-219,269-280`). But the nullable check cannot fence a credential-less disconnect, and the connection's captured version is obsolete after its own token refresh; see findings below. | `McpToolSourceTests.Talk_start_stale_outcome_cannot_replace_new_credential_status` catches unconditional talk-start outcome writes for both success and failure; `McpOAuthTests.Failure_status_compare_and_swap_does_not_overwrite_a_new_credential` catches unconditional refresh failure. **No test** covers the two remaining sequences below. |
| 2 | **Fixed.** `McpOAuthTests.cs:313-331` starts `RefreshAsync`, pauses a rejecting exchange, writes a replacement, then asserts winner token and connected status. | `Failure_status_compare_and_swap_does_not_overwrite_a_new_credential`: changing the failure path at `McpOAuthService.cs:277` back to unconditional status loses the connected assertion. `Refresh_invalid_grant_with_unchanged_version_marks_reconnect` covers the other branch. |
| 3 | **Fixed for dial address and port.** `OutboundGuardTests.cs:157-195,203-232` observes both named clients' socket seam and rejects a mixed DNS answer without another dial. The requested cancellation-token identity is not asserted, but the review-2 defect (wrong dialed IP passing) is now caught. | `Named_client_actual_pipeline_blocks_dns_refuses_redirect_caps_response_and_disables_proxy`: substituting a different socket-seam IP or port fails the added equality assertions; allowing a mixed answer fails the call-count assertion. |
| 4 | **Accepted as narrowed; fixed invariant test.** Discovery depends on pinned presenter definitions + meta tools, not on session-tool byte size (`ToolSessionCatalogue.cs:46-75`). `ToolSessionCatalogueTests.cs:14-30` builds the actual default presenter registry with a session tool under forced discovery and checks both meta tools and payload size. The contrived near-32-KiB pinned registry from round 2 does not describe this default registry; no new failing default scenario established. | `Default_presenter_registry_plus_session_tools_fits_discovery_inline_budget` fails if default pinned definitions grow beyond the meta-tool budget or the meta tools disappear. |
| 5 | **Rejection accepted.** `ToolSessionCatalogueTests.cs:522-550` uses nine tools with large definitions (below the count threshold), expects both discovery tools, and resolves the omitted session tool. | `Session_tools_exceeding_budget_are_not_inline_but_are_searchable_with_note` fails if byte overflow again silently omits tools without switching to discovery. |
| 6 | **Fixed for the specified transition.** `Tools.tsx:67-86` refetches expanded tools after successful server-level switch; `Tools.spec.tsx:148-181` asserts true → false updates an already displayed read-only tool and makes a second tools request. | `refetches expanded tools after server-level always ask is disabled` fails if that refetch is removed. |
| 7 | **Fixed.** `docs/guides/003-external-tools.md:48-53` now calls it one shared discovery-phase deadline; `McpSessionToolSource.cs:46-58` creates one budget CTS after the reads. | Documentation-only; no executable regression oracle (source's shared CTS is the check). |
| 8 | **Fixed.** `ToolSessionCatalogue.cs:60-63` says “Tools use discovery”, no longer “External tools”. | Documentation-only; no executable regression oracle for exact diagnostic wording. |

## Findings

1. **D · should-fix — nullable credential version is not a generation for no-auth connections.** `src/PresenterAi.Infrastructure/Persistence/PostgresToolConnectionRepository.cs:90-105,137-148`; `src/PresenterAi.Api/Endpoints/ToolEndpoints.cs:312-323`; `src/PresenterAi.Infrastructure/Tools/Mcp/McpSessionToolSource.cs:100-105,163-165`. Root-cause class: an absence predicate (`NOT EXISTS credential`) is reused as an identity for a versioned operation, although several owner transitions leave the absence unchanged. Start a no-auth probe or talk discovery while no credential exists; disconnect that server while its remote list is in flight. Disconnect owns `not_connected` and leaves no credential. The old successful probe/discovery then matches the nullable CAS and restores `connected` (and `LastConnectedAt`; the probe also sets `authKind=none`). A subsequent no-auth failure similarly overwrites the disconnect. **Fix:** add a server connection generation/status-transition token, bumped by disconnect and credential/auth changes; compare that token as well as credential version for no-credential reads. Test an actual paused no-auth probe/discovery → disconnect → resume, asserting `not_connected`. Existing `Talk_start_stale_outcome_cannot_replace_new_credential_status` inserts a credential instead and therefore cannot catch this.

2. **D · should-fix — a connection keeps its pre-refresh version for later status updates.** `src/PresenterAi.Infrastructure/Tools/Mcp/McpConnector.cs:156-174,198-201`; `src/PresenterAi.Infrastructure/Tools/Mcp/McpSessionToolSource.cs:103-105,163-165,171-200`; `src/PresenterAi.Api/Endpoints/ToolEndpoints.cs:389-405,409-422`. Root-cause class: version fencing captures the input credential, not the credential that actually served the operation after an internal refresh. With an OAuth credential expiring within 60 seconds, `ConnectAsync` refreshes it and saves a new version/connected status. If the ensuing initialize/list fails (or the read-only retry fails after a successful refresh), the source/REST failure write and the connection's `SetStatusAsync` use the **old** version and are silently rejected; status remains `connected` despite the failure, so the Tools page does not show reconnect. Likewise a successful list after refresh cannot write its own result, though refresh itself has already set `connected`. **Fix:** return/track the version actually used after refresh and fence subsequent connection/list/call outcomes against that version, without allowing an older operation to overwrite a later independent save. Add an expiring-token → successful refresh → failed list/call interleaving at the source or endpoint and assert the final status/error code. Current `McpOAuthTests.Refresh_invalid_grant_with_unchanged_version_marks_reconnect` covers refresh failure, not this path.

## Status-write enumeration and regression notes

`rg SetStatus src/`: unconditional `PostgresToolConnectionRepository.SetStatusAsync` remains a public repository method (`:74-88`), but **no production call in `src/`** invokes it after a credential read. `SaveCredentialAsync` (`:118-136`) writes the credential, not server status; `DisconnectAsync` (`:137-148`) sets status unconditionally as intended; `AddAsync` (`:32-59`) creates the initial state. All observed post-read status writes call `SetStatusIfCredentialVersionAsync`. Its `authKind` argument updates only when the credential condition matches (`:103-104`); `LastConnectedAt` advances only for matching `connected` (`:101-102`). Header save reads back the saved version before testing (`ToolEndpoints.cs:252-261`), so the first successful header test is not suppressed in the uncontended case. A no-auth server without a credential satisfies the nullable predicate, so its first successful probe/test/discovery is likewise not suppressed; **a subsequent credential-less disconnect is the missing generation boundary** (finding 1). OAuth refresh legitimately updates status itself, but post-refresh outcomes cannot be recorded (finding 2).

Unchanged callers of the changed repository method were inspected in `src/` and the fake repositories; the optional trailing `authKind` preserves their ordinary calls. The `List<PresenterLog>` → `ConcurrentQueue<PresenterLog>` changes (`PresenterTests.cs:1055-1066` and the other changed presenter-test harnesses) leave membership/predicate and `ToArray()` assertions intact; no indexing or order-sensitive assertion against those changed log collections found. The queue retains enqueue order for a single producer and permits enumeration during writes.

**topicsWithNoFindings:** refresh race through the real service and unchanged-version failure branch; named-client approved-address/port and mixed-DNS checks; default presenter pinned-plus-meta budget invariant; searchable byte overflow; server always-ask true → false UI transition; discovery-deadline and budget-note wording; changed harness log assertions and method-call compatibility; authKind and LastConnectedAt on a successful matching conditional update.

## IS THIS BRANCH READY TO MERGE?

**Blockers:** none classified blocker. **Recommendation: not yet:** address both should-fix status-generation defects and add the proposed interleaving tests. **Improvements:** the named-client seam could also assert cancellation-token propagation as requested in the disposition; no separate demonstrated wrong-IP regression was found. This is a read-only static review, not a test result.


## Orchestrator disposition

Round 3: classes A0, B0, C0, D2; no blockers. All eight round 2 dispositions confirmed, including the narrowing of
finding 4 and the rejection of finding 5.

Escalation analysis. Rounds 1, 2 and 3 each raised a race on the same mechanism: fencing writes to the server `Status`
column against concurrent owner actions. Each fix exposed a narrower interleaving (refresh failure → every
credential-reading write → no-credential disconnect and post-refresh outcomes). The root cause is that `Status` is an
advisory, derived field written by several concurrent actors, and making it linearisable needs a server-level
generation token plus tracking of the credential actually used after an internal refresh. Checked impact: `Status`
never gates tool loading (`McpSessionToolSource` tries every enabled server); it drives the Tools page badge and the
reconnect reuse check at `McpOAuthService.cs:81`. Both remaining races are display-only and self-heal on the next Test
or talk.

The user chose to defer both and ship (2026-09-23). No round 4.

| # | Class | Decision | Note |
|---|---|---|---|
| 1 | D | **Deferred (known limitation)** | A no-auth probe or discovery that finishes after a disconnect can restore `connected`. Needs a per-server generation token bumped by disconnect, credential save and auth changes. |
| 2 | D | **Deferred (known limitation)** | After a successful internal token refresh, a later failed list or call is fenced by the pre-refresh version and dropped, leaving `connected`. Needs the connection to track the credential version actually in use. |
| — | improvement | Deferred | Assert cancellation-token propagation at the named-client socket seam. |

Documented in `docs/guides/003-external-tools.md` ("Status and troubleshooting"). Follow-up: fix both with one
generation-token change plus the two interleaving tests the reviewer describes.

Also noted during verification, not review findings: two timing-sensitive tests failed once each and passed on rerun
(`StartupTests.Out_of_range_follow_up_wait_fails_startup("2499")`, `Wrap_up_without_audio_ends_after_fallback`).
